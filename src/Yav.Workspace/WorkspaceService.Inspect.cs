using Yav.Core.Ports;
using Yav.Core.Runs;

namespace Yav.Workspace;

public sealed partial class WorkspaceService
{
    public async Task<WorkspaceInspection> InspectAsync(string projectPath, ProjectConfiguration configuration, CancellationToken cancellationToken)
    {
        var project = Path.GetFullPath(projectPath);
        if (!Directory.Exists(project))
        {
            throw new DirectoryNotFoundException($"The project directory does not exist: {project}");
        }

        var root = await _git.FindRepositoryRootAsync(project, cancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            var warnings = new List<string>();
            if (!_git.IsAvailable)
            {
                warnings.Add("Git was not found, so ignore files are not honored and merges of concurrent edits are unavailable.");
            }

            return new WorkspaceInspection(
                ProjectPath: project,
                IsGitRepository: false,
                RepositoryRoot: null,
                HeadCommit: null,
                Branch: null,
                DirtyEntries: [],
                HasSubmodules: false,
                UsesLfs: false,
                LfsAvailable: false,
                Symlinks: [],
                CaseCollisions: [],
                IgnoredAssets: [],
                EquivalenceGaps: [],
                Warnings: warnings,
                Unsupported: []);
        }

        var unsupported = new List<string>();
        var gaps = new List<string>();
        var notes = new List<string>();

        // Every question is a process of its own and none of them changes anything, so they are asked at
        // the same time. They are answered in the order in which they were asked one after the other.
        var askingBare = _git.TryRunAsync(root, ["rev-parse", "--is-bare-repository"], cancellationToken);
        var askingHead = _git.ResolveHeadAsync(root, cancellationToken);
        var askingStatus = ReadStatusAsync(root, cancellationToken);
        var askingDirectory = _git.RunAsync(root, ["rev-parse", "--absolute-git-dir"], cancellationToken);
        var askingIndex = ReadIndexAsync(root, cancellationToken);
        var askingLfsUse = UsesLfsAsync(root, cancellationToken);
        var askingLfs = _git.IsAvailable ? LfsAvailableAsync(root, cancellationToken) : Task.FromResult(false);
        var askingIgnored = ReadIgnoredAsync(root, cancellationToken);
        try
        {
            await Task.WhenAll(askingBare, askingHead, askingStatus, askingDirectory, askingIndex, askingLfsUse, askingLfs, askingIgnored).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // All of them have ended. The first that went wrong is reported where its answer is read.
            _ = ex;
        }

        var bare = await askingBare.ConfigureAwait(false);
        if (bare.StandardOutput.Trim() == "true")
        {
            unsupported.Add("The repository is bare: it has no working tree to isolate.");
        }

        var head = await askingHead.ConfigureAwait(false);
        var (branch, dirty, conflicted) = await askingStatus.ConfigureAwait(false);
        if (conflicted > 0)
        {
            unsupported.Add($"The repository has {conflicted} file(s) with unresolved merge conflicts. Resolve them before starting a task.");
        }

        var gitDirectory = (await askingDirectory.ConfigureAwait(false)).Trim();
        foreach (var (marker, operation) in new[]
        {
            ("rebase-merge", "a rebase"), ("rebase-apply", "a rebase"), ("MERGE_HEAD", "a merge"),
            ("CHERRY_PICK_HEAD", "a cherry-pick"), ("REVERT_HEAD", "a revert"), ("BISECT_LOG", "a bisect"),
        })
        {
            var path = Path.Combine(gitDirectory, marker);
            if (File.Exists(path) || Directory.Exists(path))
            {
                notes.Add($"The repository is in the middle of {operation}.");
                break;
            }
        }

        var (symlinks, collisions, hasGitlinks) = await askingIndex.ConfigureAwait(false);
        var hasSubmodules = hasGitlinks || File.Exists(Path.Combine(root, ".gitmodules"));
        if (hasSubmodules)
        {
            gaps.Add("The repository has submodules. Their content is not checked out in the isolated workspace.");
        }

        var usesLfs = await askingLfsUse.ConfigureAwait(false);
        var lfsAvailable = await askingLfs.ConfigureAwait(false);
        if (usesLfs && !lfsAvailable)
        {
            gaps.Add("The repository uses Git LFS but git-lfs is not installed, so large files would be checked out as pointer files.");
        }

        if (symlinks.Count > 0)
        {
            gaps.Add($"The repository contains {symlinks.Count} symbolic link(s). Links are recorded but never followed or applied.");
        }

        if (collisions.Count > 0)
        {
            gaps.Add($"The repository contains paths that differ only by letter case ({collisions[0]}). Windows can hold only one of them.");
        }

        var ignored = await askingIgnored.ConfigureAwait(false);
        var notReplicated = ignored.Where(a => !Glob.MatchesAny(configuration.ReplicateIgnored, a.Path.TrimEnd('/'))).ToList();
        if (notReplicated.Count > 0)
        {
            var sample = string.Join(", ", notReplicated.Take(4).Select(a => a.Path));
            var more = notReplicated.Count > 4 ? $" and {notReplicated.Count - 4} more" : string.Empty;
            gaps.Add($"The project has ignored files that will be absent from the isolated workspace: {sample}{more}.");
        }

        if (head is null)
        {
            notes.Add("The repository has no commit yet, so it is isolated as a protected copy instead of a worktree.");
        }

        return new WorkspaceInspection(
            ProjectPath: project,
            IsGitRepository: true,
            RepositoryRoot: root,
            HeadCommit: head,
            Branch: branch,
            DirtyEntries: dirty,
            HasSubmodules: hasSubmodules,
            UsesLfs: usesLfs,
            LfsAvailable: lfsAvailable,
            Symlinks: symlinks,
            CaseCollisions: collisions,
            IgnoredAssets: ignored,
            EquivalenceGaps: gaps,
            Warnings: notes,
            Unsupported: unsupported);
    }

    private async Task<(string? Branch, List<DirtyEntry> Entries, int Conflicted)> ReadStatusAsync(string root, CancellationToken cancellationToken)
    {
        var records = await _git.RunNullSeparatedAsync(
            root, ["status", "--porcelain=v2", "-z", "--branch", "--untracked-files=all", "--ignored=no"], cancellationToken).ConfigureAwait(false);

        string? branch = null;
        var entries = new List<DirtyEntry>();
        var conflicted = 0;
        for (var i = 0; i < records.Length; i++)
        {
            var record = records[i];
            if (record.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                var name = record["# branch.head ".Length..];
                branch = name == "(detached)" ? null : name;
                continue;
            }

            if (record.Length < 2 || record[0] == '#')
            {
                continue;
            }

            switch (record[0])
            {
                case '1':
                {
                    // 1 XY sub mH mI mW hH hI path
                    var parts = record.Split(' ', 9);
                    if (parts.Length == 9)
                    {
                        entries.Add(Entry(parts[8], parts[1], null));
                    }

                    break;
                }

                case '2':
                {
                    // 2 XY sub mH mI mW hH hI Xscore path, followed by the original path as its own record.
                    var parts = record.Split(' ', 10);
                    var original = i + 1 < records.Length ? records[++i] : null;
                    if (parts.Length == 10)
                    {
                        entries.Add(Entry(parts[9], parts[1], original));
                    }

                    break;
                }

                case 'u':
                {
                    var parts = record.Split(' ', 11);
                    conflicted++;
                    if (parts.Length == 11)
                    {
                        entries.Add(new DirtyEntry(parts[10], parts[1], Staged: true, Unstaged: true, Untracked: false, RenamedFrom: null));
                    }

                    break;
                }

                case '?':
                    entries.Add(new DirtyEntry(record[2..], "??", Staged: false, Unstaged: false, Untracked: true, RenamedFrom: null));
                    break;
            }
        }

        entries.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return (branch, entries, conflicted);

        static DirtyEntry Entry(string path, string status, string? renamedFrom) => new(
            path,
            status,
            Staged: status.Length > 0 && status[0] != '.',
            Unstaged: status.Length > 1 && status[1] != '.',
            Untracked: false,
            RenamedFrom: renamedFrom);
    }

    private async Task<(List<string> Symlinks, List<string> Collisions, bool HasGitlinks)> ReadIndexAsync(string root, CancellationToken cancellationToken)
    {
        var records = await _git.RunNullSeparatedAsync(root, ["ls-files", "-z", "--stage"], cancellationToken).ConfigureAwait(false);
        var symlinks = new List<string>();
        var seen = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var collisions = new List<string>();
        var gitlinks = false;
        foreach (var record in records)
        {
            // <mode> <object> <stage>\t<path>
            var tab = record.IndexOf('\t');
            if (tab < 0)
            {
                continue;
            }

            var path = record[(tab + 1)..];
            var mode = record.Length >= 6 ? record[..6] : string.Empty;
            if (mode == "120000")
            {
                symlinks.Add(path);
            }
            else if (mode == "160000")
            {
                gitlinks = true;
            }

            if (seen.TryGetValue(path, out var existing))
            {
                if (!string.Equals(existing, path, StringComparison.Ordinal))
                {
                    collisions.Add($"{existing} / {path}");
                }
            }
            else
            {
                seen[path] = path;
            }
        }

        return (symlinks, collisions, gitlinks);
    }

    private async Task<bool> UsesLfsAsync(string root, CancellationToken cancellationToken)
    {
        var records = await _git.RunNullSeparatedAsync(
            root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard", "--", ".gitattributes", "**/.gitattributes"], cancellationToken).ConfigureAwait(false);
        foreach (var relative in records)
        {
            var path = ResolveInside(root, relative);
            if (path is null || !File.Exists(path))
            {
                continue;
            }

            foreach (var line in File.ReadLines(path))
            {
                if (line.Contains("filter=lfs", StringComparison.Ordinal) && !line.TrimStart().StartsWith('#'))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private async Task<bool> LfsAvailableAsync(string root, CancellationToken cancellationToken)
    {
        var result = await _git.TryRunAsync(root, ["lfs", "version"], cancellationToken, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        return result.ExitCode == 0;
    }

    private async Task<List<IgnoredAsset>> ReadIgnoredAsync(string root, CancellationToken cancellationToken)
    {
        // Ignored directories are reported as one entry each, so a dependency folder is not listed file by file.
        var records = await _git.RunNullSeparatedAsync(
            root, ["status", "--porcelain=v2", "-z", "--ignored=matching", "--untracked-files=normal"], cancellationToken).ConfigureAwait(false);
        var assets = new List<IgnoredAsset>();
        foreach (var record in records)
        {
            if (!record.StartsWith("! ", StringComparison.Ordinal))
            {
                continue;
            }

            var path = record[2..];
            var isDirectory = path.EndsWith('/');
            long? size = null;
            if (!isDirectory)
            {
                var full = ResolveInside(root, path);
                if (full is not null && File.Exists(full))
                {
                    size = new FileInfo(full).Length;
                }
            }

            assets.Add(new IgnoredAsset(path, isDirectory, !isDirectory && SecretPaths.LooksLikeSecret(path), size));
        }

        assets.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return assets;
    }

    private async Task<List<string>> ReadReferencesAsync(string root, CancellationToken cancellationToken)
    {
        var references = new List<string>();
        var head = await _git.TryRunAsync(root, ["symbolic-ref", "--quiet", "HEAD"], cancellationToken).ConfigureAwait(false);
        var headCommit = await _git.ResolveHeadAsync(root, cancellationToken).ConfigureAwait(false);
        references.Add($"HEAD {(head.ExitCode == 0 ? head.StandardOutput.Trim() : "(detached)")} {headCommit ?? "(none)"}");

        var listed = await _git.RunAsync(root, ["for-each-ref", "--format=%(refname) %(objectname)"], cancellationToken).ConfigureAwait(false);
        foreach (var line in listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            references.Add(line);
        }

        references.Sort(StringComparer.Ordinal);
        return references;
    }

    public async Task<IReadOnlyList<string>> VerifyRepositoryUntouchedAsync(IsolatedWorkspace workspace, CancellationToken cancellationToken)
    {
        var record = LoadRecord(workspace.WorkspaceId);
        if (record.RepositoryReferences.Count == 0 || !_git.IsAvailable)
        {
            return [];
        }

        var now = await ReadReferencesAsync(record.OriginalRoot, cancellationToken).ConfigureAwait(false);
        var before = record.RepositoryReferences.ToDictionary(Name, Value, StringComparer.Ordinal);
        var after = now.ToDictionary(Name, Value, StringComparer.Ordinal);
        var differences = new List<string>();

        foreach (var (name, value) in after)
        {
            if (!before.TryGetValue(name, out var earlier))
            {
                differences.Add($"{name} was created ({Abbreviate(value)}).");
            }
            else if (!string.Equals(earlier, value, StringComparison.Ordinal))
            {
                differences.Add($"{name} moved from {Abbreviate(earlier)} to {Abbreviate(value)}.");
            }
        }

        foreach (var (name, value) in before)
        {
            if (!after.ContainsKey(name))
            {
                differences.Add($"{name} was deleted (was {Abbreviate(value)}).");
            }
        }

        differences.Sort(StringComparer.Ordinal);
        return differences;

        static string Name(string line) => line.Split(' ', 2)[0];
        static string Value(string line) => line.Contains(' ') ? line.Split(' ', 2)[1] : string.Empty;
        static string Abbreviate(string value) => value.Length > 60 ? value[..60] : value;
    }
}
