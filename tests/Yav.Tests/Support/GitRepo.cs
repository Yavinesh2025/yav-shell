using Yav.Core.Ports;
using Yav.Platform.Processes;

namespace Yav.Tests.Support;

/// <summary>A real Git repository in a temporary directory.</summary>
public sealed class GitRepo : IDisposable
{
    private static readonly ProcessRunner Runner = new();
    private readonly TempDirectory _directory;

    private GitRepo(TempDirectory directory)
    {
        _directory = directory;
    }

    public string Path => _directory.Path;

    public static GitRepo Create(string label = "repo", bool autoCrlf = false)
    {
        var repo = new GitRepo(new TempDirectory(label));
        repo.Git("init", "--quiet", "--initial-branch=main");
        repo.Git("config", "user.email", "test@example.invalid");
        repo.Git("config", "user.name", "YAV Tests");
        repo.Git("config", "commit.gpgsign", "false");
        repo.Git("config", "core.autocrlf", autoCrlf ? "true" : "false");
        repo.Git("config", "core.longpaths", "true");
        return repo;
    }

    /// <summary>A repository with one commit containing the given files.</summary>
    public static GitRepo WithFiles(params (string Path, string Content)[] files)
    {
        var repo = Create();
        foreach (var (path, content) in files)
        {
            repo.Write(path, content);
        }

        repo.CommitAll("initial");
        return repo;
    }

    public string File(string relative) => _directory.File(relative);

    public string Write(string relative, string content) => _directory.Write(relative, content);

    public string WriteBytes(string relative, byte[] content) => _directory.WriteBytes(relative, content);

    public string Read(string relative) => _directory.Read(relative);

    public bool Exists(string relative) => _directory.Exists(relative);

    public void Delete(string relative) => System.IO.File.Delete(File(relative));

    public string Git(params string[] arguments)
    {
        var result = Runner.RunAsync(
            new ProcessSpec("git", arguments, Path, new Dictionary<string, string?>
            {
                ["GIT_TERMINAL_PROMPT"] = "0",
                ["GIT_CONFIG_NOSYSTEM"] = "1",
            }),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(60)),
            CancellationToken.None).GetAwaiter().GetResult();
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException($"git {string.Join(' ', arguments)} failed ({result.ExitCode}): {result.StandardError}{result.StandardOutput}");
        }

        return result.StandardOutput;
    }

    public void CommitAll(string message)
    {
        Git("add", "-A");
        Git("commit", "--quiet", "--no-verify", "-m", message);
    }

    public string Head => Git("rev-parse", "HEAD").Trim();

    /// <summary>Everything that describes the state of the repository the user would notice changing.</summary>
    public string Snapshot()
    {
        var status = Git("status", "--porcelain=v2", "--untracked-files=all");
        var refs = Git("for-each-ref", "--format=%(refname) %(objectname)");
        var stash = Git("stash", "list");
        var branch = Git("rev-parse", "--abbrev-ref", "HEAD");
        return $"HEAD {Head}\nBRANCH {branch}STATUS\n{status}REFS\n{refs}STASH\n{stash}";
    }

    /// <summary>The content of every file in the working tree, keyed by relative path.</summary>
    public Dictionary<string, string> WorkingTree() => ReadTree(Path);

    public static Dictionary<string, string> ReadTree(string root)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            var relative = System.IO.Path.GetRelativePath(root, file).Replace('\\', '/');
            if (relative == ".git" || relative.StartsWith(".git/", StringComparison.Ordinal))
            {
                continue;
            }

            result[relative] = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.IO.File.ReadAllBytes(file)));
        }

        return result;
    }

    public void Dispose()
    {
        try
        {
            // Registered worktrees would otherwise keep references into directories that are about to disappear.
            Git("worktree", "prune");
        }
        catch (Exception)
        {
        }

        _directory.Dispose();
    }
}
