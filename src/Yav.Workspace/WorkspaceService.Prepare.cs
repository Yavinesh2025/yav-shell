using Yav.Core.Ports;
using Yav.Core.Runs;

namespace Yav.Workspace;

public sealed partial class WorkspaceService
{
    public async Task<IsolatedWorkspace> PrepareAsync(
        WorkspaceRequest request,
        WorkspaceInspection inspection,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        if (request.ExistingWorkspaceId is not null)
        {
            var existing = await FindAsync(request.ExistingWorkspaceId, cancellationToken).ConfigureAwait(false);
            if (existing is not null)
            {
                progress?.Report($"Reusing isolated workspace {existing.WorkspaceId}");
                return existing;
            }

            progress?.Report($"Workspace {request.ExistingWorkspaceId} no longer exists; creating a new one");
        }

        if (inspection.Unsupported.Count > 0)
        {
            throw new WorkspaceUnsupportedException(
                "This project cannot be isolated: " + string.Join(" ", inspection.Unsupported), inspection.Unsupported);
        }

        if (inspection.EquivalenceGaps.Count > 0 && !request.EquivalenceGapsAcknowledged)
        {
            throw new WorkspaceUnsupportedException(
                "The isolated workspace would not be an equivalent reproduction of the project: "
                + string.Join(" ", inspection.EquivalenceGaps)
                + " Acknowledge this to continue, or configure what to replicate.",
                inspection.EquivalenceGaps);
        }

        var mode = request.Mode;
        if (mode == WorkspaceMode.GitWorktree && (!inspection.IsGitRepository || inspection.HeadCommit is null))
        {
            // A worktree needs a commit to check out.
            mode = WorkspaceMode.Snapshot;
        }

        var project = Path.GetFullPath(request.ProjectPath);
        var originalRoot = inspection.RepositoryRoot ?? project;
        var workspaceId = NewWorkspaceId();
        var directory = WorkspaceDirectory(workspaceId);
        var metadata = Path.Combine(directory, "meta");
        var evidence = Path.Combine(directory, "evidence");
        var root = mode == WorkspaceMode.InPlace ? originalRoot : Path.Combine(directory, "w");
        Directory.CreateDirectory(metadata);
        Directory.CreateDirectory(evidence);

        var notes = new List<string>(inspection.Warnings);
        var references = new List<string>();
        var replicated = new List<string>();

        try
        {
            switch (mode)
            {
                case WorkspaceMode.GitWorktree:
                    references = await ReadReferencesAsync(originalRoot, cancellationToken).ConfigureAwait(false);
                    progress?.Report("Creating an isolated worktree");
                    await _git.RunAsync(
                        originalRoot, ["worktree", "add", "--detach", "--quiet", root, inspection.HeadCommit!], cancellationToken, TimeSpan.FromMinutes(30)).ConfigureAwait(false);
                    if (inspection.IsDirty)
                    {
                        progress?.Report($"Carrying over {inspection.DirtyEntries.Count} uncommitted change(s)");
                        Overlay(originalRoot, root, inspection.DirtyEntries, notes);
                    }

                    replicated = Replicate(originalRoot, root, inspection.IgnoredAssets, request.Configuration, notes);
                    break;

                case WorkspaceMode.Snapshot:
                    progress?.Report("Creating a protected copy");
                    Directory.CreateDirectory(root);
                    var listing = Path.Combine(metadata, "source-list.git");
                    var files = await ListFilesAsync(originalRoot, inspection.IsGitRepository ? null : listing, cancellationToken).ConfigureAwait(false);
                    CopyFiles(originalRoot, root, files, cancellationToken);
                    if (inspection.IsGitRepository)
                    {
                        replicated = Replicate(originalRoot, root, inspection.IgnoredAssets, request.Configuration, notes);
                    }

                    break;

                case WorkspaceMode.InPlace:
                    notes.Add("The agent works directly in the project. Changes are not isolated; they can be reversed with /undo while the files stay unedited.");
                    if (inspection.IsGitRepository)
                    {
                        references = await ReadReferencesAsync(originalRoot, cancellationToken).ConfigureAwait(false);
                    }

                    break;
            }

            progress?.Report("Recording the baseline");
            var listed = await ListFilesAsync(root, PrivateGitDirectory(mode, metadata), cancellationToken).ConfigureAwait(false);
            var baseline = await BuildManifestAsync(root, listed, BlobPolicy.Baseline, cancellationToken).ConfigureAwait(false);
            SaveManifest(metadata, baseline);

            var tooLarge = baseline.Entries.Values.Count(e => e.Length > BaselineBlobLimit);
            if (tooLarge > 0)
            {
                notes.Add($"{tooLarge} file(s) are larger than {BaselineBlobLimit / (1024 * 1024)} MB; their original content is not kept, so concurrent edits to them cannot be merged.");
            }

            var record = new WorkspaceRecord
            {
                WorkspaceId = workspaceId,
                TaskId = request.TaskId,
                Mode = mode,
                OriginalPath = project,
                OriginalRoot = originalRoot,
                RootPath = root,
                BaselineFingerprint = baseline.Fingerprint,
                BaseCommit = inspection.HeadCommit,
                CreatedAt = _clock.GetUtcNow(),
                FileCount = baseline.Count,
                Notes = notes,
                RepositoryReferences = references,
                ReplicatedIgnored = replicated,
            };
            SaveRecord(record);
            return ToWorkspace(record, reused: false);
        }
        catch
        {
            // Nothing half-made is left behind.
            await RemoveWorkspaceFilesAsync(mode, originalRoot, root, directory, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Copies the user's uncommitted work into the worktree. The project itself is only read.</summary>
    private static void Overlay(string originalRoot, string root, IReadOnlyList<DirtyEntry> entries, List<string> notes)
    {
        foreach (var entry in entries)
        {
            var source = ResolveInside(originalRoot, entry.Path);
            var target = ResolveInside(root, entry.Path);
            if (source is null || target is null)
            {
                notes.Add($"'{entry.Path}' was not carried over because its path is not inside the project.");
                continue;
            }

            if (entry.RenamedFrom is not null)
            {
                var oldSource = ResolveInside(originalRoot, entry.RenamedFrom);
                var oldTarget = ResolveInside(root, entry.RenamedFrom);
                if (oldSource is not null && oldTarget is not null && !File.Exists(oldSource) && File.Exists(oldTarget))
                {
                    DeleteFile(oldTarget);
                }
            }

            if (Directory.Exists(source))
            {
                // A submodule or nested repository.
                notes.Add($"'{entry.Path}' is a directory entry (a submodule or nested repository) and was not carried over.");
                continue;
            }

            if (File.Exists(source))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyFile(source, target);
            }
            else if (File.Exists(target))
            {
                DeleteFile(target);
            }
        }
    }

    /// <summary>Copies the ignored files the project configuration asks for. Likely secrets need separate, explicit permission.</summary>
    private static List<string> Replicate(
        string originalRoot,
        string root,
        IReadOnlyList<IgnoredAsset> ignored,
        ProjectConfiguration configuration,
        List<string> notes)
    {
        var copied = new List<string>();
        if (configuration.ReplicateIgnored.Count == 0)
        {
            return copied;
        }

        foreach (var asset in ignored)
        {
            var path = asset.Path.TrimEnd('/');
            if (!Glob.MatchesAny(configuration.ReplicateIgnored, path))
            {
                continue;
            }

            var source = ResolveInside(originalRoot, path);
            var target = ResolveInside(root, path);
            if (source is null || target is null)
            {
                continue;
            }

            if (asset.IsDirectory)
            {
                if (!Directory.Exists(source) || (File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
                {
                    notes.Add($"'{path}' is a link and was not copied.");
                    continue;
                }

                foreach (var file in Directory.EnumerateFiles(source, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint }))
                {
                    var relative = path + "/" + Path.GetRelativePath(source, file).Replace('\\', '/');
                    if (CopyReplicated(file, ResolveInside(root, relative), relative, configuration, notes))
                    {
                        copied.Add(relative);
                    }
                }
            }
            else if (CopyReplicated(source, target, path, configuration, notes))
            {
                copied.Add(path);
            }
        }

        return copied;
    }

    private static bool CopyReplicated(string source, string? target, string relative, ProjectConfiguration configuration, List<string> notes)
    {
        if (target is null || !File.Exists(source))
        {
            return false;
        }

        if (SecretPaths.LooksLikeSecret(relative) && !Glob.MatchesAny(configuration.AllowSecrets, relative))
        {
            notes.Add($"'{relative}' looks like it holds credentials and was not copied. List it under allowSecrets to copy it.");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        CopyFile(source, target);
        return true;
    }

    private static void CopyFiles(string sourceRoot, string targetRoot, IReadOnlyList<string> relativePaths, CancellationToken cancellationToken)
    {
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = cancellationToken };
        Parallel.ForEach(relativePaths, options, relative =>
        {
            var source = ResolveInside(sourceRoot, relative);
            var target = ResolveInside(targetRoot, relative);
            if (source is null || target is null || !File.Exists(source))
            {
                return;
            }

            if (new FileInfo(source).LinkTarget is not null)
            {
                // A link is not copied: its target may be outside the project.
                return;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            CopyFile(source, target);
        });
    }

    private static void CopyFile(string source, string target)
    {
        if (File.Exists(target))
        {
            var attributes = File.GetAttributes(target);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(target, attributes & ~FileAttributes.ReadOnly);
            }
        }

        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
        using var output = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, 128 * 1024);
        input.CopyTo(output);
    }

    private static void DeleteFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.ReadOnly) != 0)
        {
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        }

        File.Delete(path);
    }

    public async Task DiscardAsync(IsolatedWorkspace workspace, CancellationToken cancellationToken)
    {
        var directory = WorkspaceDirectory(workspace.WorkspaceId);
        var prefix = directory.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var isolated = workspace.Mode != WorkspaceMode.InPlace;

        // The record is data. Before anything is deleted, every path must be inside YAV's own storage.
        foreach (var path in new[] { workspace.MetadataPath, workspace.EvidencePath }.Concat(isolated ? [workspace.RootPath] : []))
        {
            if (!Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Refusing to discard workspace {workspace.WorkspaceId}: '{path}' is outside YAV's workspace storage.");
            }
        }

        await RemoveWorkspaceFilesAsync(workspace.Mode, workspace.OriginalRoot, workspace.RootPath, directory, cancellationToken).ConfigureAwait(false);
    }

    private async Task RemoveWorkspaceFilesAsync(WorkspaceMode mode, string originalRoot, string root, string directory, CancellationToken cancellationToken)
    {
        if (mode == WorkspaceMode.GitWorktree && _git.IsAvailable && Directory.Exists(originalRoot))
        {
            // Unregisters the worktree from the shared repository. Branches, the index and the project's files are not touched.
            await _git.TryRunAsync(originalRoot, ["worktree", "remove", "--force", root], cancellationToken).ConfigureAwait(false);
        }

        DeleteDirectory(directory);

        if (mode == WorkspaceMode.GitWorktree && _git.IsAvailable && Directory.Exists(originalRoot))
        {
            await _git.TryRunAsync(originalRoot, ["worktree", "prune"], cancellationToken).ConfigureAwait(false);
        }
    }
}
