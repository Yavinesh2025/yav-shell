using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Platform.Processes;

namespace Yav.Bench;

/// <summary>How one acceptance check ended.</summary>
public sealed record CheckOutcome(string Id, bool Passed, int ExitCode, long DurationMs, string Output);

/// <summary>
/// A copy of the project of a task, in a directory of its own, as a Git repository with one commit. Every
/// run gets a new one, so no run sees what another one left behind.
/// </summary>
public sealed class Workbench : IDisposable
{
    private static readonly ProcessRunner Runner = new();

    private Workbench(string directory)
    {
        Directory = directory;
        Project = Path.Combine(directory, "project");
    }

    /// <summary>The directory of this run: the project and, next to it, what the run needs.</summary>
    public string Directory { get; }

    public string Project { get; }

    public static async Task<Workbench> CreateAsync(BenchTask task, string root, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(root, $"{task.Id}-{Guid.NewGuid().ToString("N")[..8]}");
        var bench = new Workbench(directory);
        Copy(task.ProjectDirectory, bench.Project);

        await bench.GitAsync(["init", "--quiet", "--initial-branch=main"], cancellationToken).ConfigureAwait(false);
        await bench.GitAsync(["config", "user.email", "bench@example.invalid"], cancellationToken).ConfigureAwait(false);
        await bench.GitAsync(["config", "user.name", "YAV Bench"], cancellationToken).ConfigureAwait(false);
        await bench.GitAsync(["config", "commit.gpgsign", "false"], cancellationToken).ConfigureAwait(false);
        await bench.GitAsync(["config", "core.autocrlf", "false"], cancellationToken).ConfigureAwait(false);
        await bench.GitAsync(["add", "-A"], cancellationToken).ConfigureAwait(false);
        await bench.GitAsync(["commit", "--quiet", "--no-verify", "-m", "The project before the task"], cancellationToken).ConfigureAwait(false);
        return bench;
    }

    /// <summary>Puts the files of a solution over the project, as someone would who solved the task by hand.</summary>
    public void Overlay(string solutionDirectory) => Copy(solutionDirectory, Project);

    public async Task<IReadOnlyList<CheckOutcome>> RunChecksAsync(IReadOnlyList<GateDefinition> checks, CancellationToken cancellationToken)
    {
        var outcomes = new List<CheckOutcome>();
        foreach (var check in checks)
        {
            var executable = Runner.Resolve(check.Command)
                ?? throw new FileNotFoundException($"'{check.Command}', which the check '{check.Id}' needs, was not found on PATH.");
            var result = await Runner.RunAsync(
                new ProcessSpec(executable, check.Arguments, Project, new Dictionary<string, string?> { ["PYTHONDONTWRITEBYTECODE"] = "1" }),
                new CaptureOptions(Timeout: TimeSpan.FromSeconds(check.TimeoutSeconds), MaxCapturedCharacters: 64 * 1024),
                cancellationToken).ConfigureAwait(false);
            var output = (result.StandardOutput + result.StandardError).Trim();
            outcomes.Add(new CheckOutcome(
                check.Id,
                result.Succeeded && check.SuccessExitCodes.Contains(result.ExitCode),
                result.ExitCode,
                (long)result.Duration.TotalMilliseconds,
                output.Length <= 2000 ? output : output[^2000..]));
        }

        return outcomes;
    }

    public async Task<string> DiffAsync(CancellationToken cancellationToken)
    {
        await GitAsync(["add", "-A", "--intent-to-add"], cancellationToken).ConfigureAwait(false);
        return await GitAsync(["diff", "--no-color"], cancellationToken).ConfigureAwait(false);
    }

    public void Dispose()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    foreach (var file in System.IO.Directory.EnumerateFiles(Directory, "*", SearchOption.AllDirectories))
                    {
                        var attributes = File.GetAttributes(file);
                        if ((attributes & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                        }
                    }

                    System.IO.Directory.Delete(Directory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in System.IO.Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            System.IO.Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private async Task<string> GitAsync(IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        var git = Runner.Resolve("git") ?? throw new FileNotFoundException("Git was not found on PATH. The benchmark needs it.");
        var result = await Runner.RunAsync(
            new ProcessSpec(git, arguments, Project, new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" }),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(60)),
            cancellationToken).ConfigureAwait(false);
        return result.Succeeded
            ? result.StandardOutput
            : throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.StandardError}");
    }
}
