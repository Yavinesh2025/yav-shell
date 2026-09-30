using Yav.Core.Ports;

namespace Yav.Workspace;

public sealed class GitException(string command, int exitCode, string detail)
    : InvalidOperationException($"git {command} failed (exit {exitCode}): {detail.Trim()}")
{
    public int ExitCode { get; } = exitCode;
}

/// <summary>
/// Runs the Git command-line tool for repository operations. Every call passes its arguments as a list,
/// never takes optional locks (so reading a repository does not rewrite its index), and never prompts.
/// </summary>
public sealed class GitClient
{
    private static readonly IReadOnlyDictionary<string, string?> Environment = new Dictionary<string, string?>
    {
        ["GIT_TERMINAL_PROMPT"] = "0",
        // Without this, "git status" refreshes and rewrites the index of the repository it only reads.
        ["GIT_OPTIONAL_LOCKS"] = "0",
        ["GIT_PAGER"] = "cat",
        ["LC_ALL"] = "C",
        ["GIT_EDITOR"] = "true",
        ["GIT_ASKPASS"] = "echo",
    };

    private static readonly string[] CommonOptions =
    [
        "-c", "core.quotepath=false",
        "-c", "core.longpaths=true",
        "-c", "advice.detachedHead=false",
    ];

    private readonly IProcessRunner _runner;
    private readonly Lazy<string?> _executable;

    public GitClient(IProcessRunner runner)
    {
        _runner = runner;
        _executable = new Lazy<string?>(() => runner.Resolve("git"));
    }

    public bool IsAvailable => _executable.Value is not null;

    public string? ExecutablePath => _executable.Value;

    public async Task<ProcessResult> TryRunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var executable = _executable.Value ?? throw new InvalidOperationException("Git was not found on PATH.");
        return await _runner.RunAsync(
            new ProcessSpec(executable, [.. CommonOptions, .. arguments], workingDirectory, Environment, Label: "git"),
            new CaptureOptions(Timeout: timeout ?? TimeSpan.FromMinutes(5), MaxCapturedCharacters: 64 * 1024 * 1024),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> RunAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken, TimeSpan? timeout = null)
    {
        var result = await TryRunAsync(workingDirectory, arguments, cancellationToken, timeout).ConfigureAwait(false);
        if (result.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.ExitCode != 0 || result.TimedOut)
        {
            var detail = result.TimedOut ? "timed out" : result.StandardError.Length > 0 ? result.StandardError : result.StandardOutput;
            throw new GitException(arguments.Count > 0 ? arguments[0] : string.Empty, result.ExitCode, detail);
        }

        return result.StandardOutput;
    }

    /// <summary>Runs a command whose output is a list of entries separated by null characters.</summary>
    public async Task<string[]> RunNullSeparatedAsync(string workingDirectory, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        // Read as bytes: this output is one long record list, not lines, and paths are UTF-8.
        var executable = _executable.Value ?? throw new InvalidOperationException("Git was not found on PATH.");
        var process = _runner.Start(new ProcessSpec(executable, [.. CommonOptions, .. arguments], workingDirectory, Environment, Label: "git"));
        await using (process.ConfigureAwait(false))
        {
            process.CloseInput();
            using var output = new MemoryStream();
            using var error = new MemoryStream();
            var readOutput = process.StandardOutput.CopyToAsync(output, cancellationToken);
            var readError = process.StandardError.CopyToAsync(error, cancellationToken);
            try
            {
                await Task.WhenAll(readOutput, readError).WaitAsync(TimeSpan.FromMinutes(5), cancellationToken).ConfigureAwait(false);
                var exitCode = await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromMinutes(1), cancellationToken).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    throw new GitException(arguments.Count > 0 ? arguments[0] : string.Empty, exitCode, System.Text.Encoding.UTF8.GetString(error.ToArray()));
                }
            }
            catch (TimeoutException)
            {
                process.Terminate();
                throw new GitException(arguments.Count > 0 ? arguments[0] : string.Empty, -1, "timed out");
            }

            var text = System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, (int)output.Length);
            return text.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        }
    }

    /// <summary>The root of the repository that contains the directory, or null when it is not inside one.</summary>
    public async Task<string?> FindRepositoryRootAsync(string directory, CancellationToken cancellationToken)
    {
        if (!IsAvailable)
        {
            return null;
        }

        var result = await TryRunAsync(directory, ["rev-parse", "--show-toplevel"], cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return null;
        }

        var root = result.StandardOutput.Trim();
        return root.Length == 0 ? null : Path.GetFullPath(root);
    }

    public async Task<string?> ResolveHeadAsync(string repository, CancellationToken cancellationToken)
    {
        var result = await TryRunAsync(repository, ["rev-parse", "--verify", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false);
        var head = result.StandardOutput.Trim();
        return result.ExitCode == 0 && head.Length >= 40 ? head : null;
    }

    /// <summary>
    /// Three-way merge of one file. Returns the merged text and the number of conflicts; a negative number
    /// means the merge could not be attempted.
    /// </summary>
    public async Task<(string Text, int Conflicts)> MergeFileAsync(string workingDirectory, string current, string baseFile, string other, CancellationToken cancellationToken)
    {
        var executable = _executable.Value ?? throw new InvalidOperationException("Git was not found on PATH.");
        var output = Path.Combine(workingDirectory, "merge-" + Guid.NewGuid().ToString("N")[..8] + ".out");
        try
        {
            // The result is written into a copy of the current version, so it is read back byte for byte.
            File.Copy(current, output, overwrite: true);
            File.SetAttributes(output, FileAttributes.Normal);
            var result = await _runner.RunAsync(
                new ProcessSpec(executable, ["merge-file", "--quiet", output, baseFile, other], workingDirectory, Environment, Label: "git merge-file"),
                new CaptureOptions(Timeout: TimeSpan.FromMinutes(2)),
                cancellationToken).ConfigureAwait(false);
            if (result.ExitCode is < 0 or > 127 || result.TimedOut || result.Cancelled)
            {
                return (string.Empty, -1);
            }

            return (await File.ReadAllTextAsync(output, cancellationToken).ConfigureAwait(false), result.ExitCode);
        }
        finally
        {
            if (File.Exists(output))
            {
                File.Delete(output);
            }
        }
    }
}
