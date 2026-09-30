using Yav.Core;
using Yav.Core.Ports;
using Yav.Core.Runs;

namespace Yav.Workspace;

public sealed partial class WorkspaceService
{
    public async Task<ApplyPreflight> PreflightApplyAsync(IsolatedWorkspace workspace, Candidate candidate, CancellationToken cancellationToken)
    {
        var conflicts = new List<ApplyConflict>();
        foreach (var file in candidate.Changes.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var conflict = Check(workspace, file);
            if (conflict is not null)
            {
                conflicts.Add(conflict);
            }
        }

        var unrelated = new List<string>();
        if (workspace.Mode != WorkspaceMode.InPlace)
        {
            var baseline = LoadManifest(workspace, workspace.BaselineFingerprint);
            var touched = new HashSet<string>(candidate.Changes.Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
            var current = await ProjectManifestAsync(workspace, BlobPolicy.None, cancellationToken).ConfigureAwait(false);
            foreach (var change in Manifest.Compare(baseline, current, _ => false).Files)
            {
                if (!touched.Contains(change.Path))
                {
                    unrelated.Add(change.Path);
                }
            }
        }

        return new ApplyPreflight(conflicts.Count == 0, candidate.Changes.Files, conflicts, unrelated);
    }

    private ApplyConflict? Check(IsolatedWorkspace workspace, ChangedFile file)
    {
        var target = ResolveInside(workspace.OriginalRoot, file.Path);
        if (target is null)
        {
            return new ApplyConflict(file.Path, ApplyConflictKind.UnsafePath, "The path is not inside the project.", false);
        }

        if (Directory.Exists(target))
        {
            return new ApplyConflict(file.Path, ApplyConflictKind.AlreadyExists, "A directory with this name exists in the project.", false);
        }

        var exists = File.Exists(target);
        if (exists && new FileInfo(target).LinkTarget is not null)
        {
            return new ApplyConflict(file.Path, ApplyConflictKind.UnsafePath, "The project file is a link. Links are never written through.", false);
        }

        if (file.CandidateHash is not null && !_blobs.Contains(file.CandidateHash))
        {
            return new ApplyConflict(file.Path, ApplyConflictKind.Missing, "The stored content of the candidate is missing.", false);
        }

        string? current;
        try
        {
            current = exists ? BlobStore.HashFile(target) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new ApplyConflict(file.Path, ApplyConflictKind.Locked, "The file is in use or cannot be read: " + ex.Message, false);
        }

        if (workspace.Mode == WorkspaceMode.InPlace)
        {
            // The agent already wrote into the project. The project must still hold exactly the candidate.
            return string.Equals(current, file.CandidateHash, StringComparison.Ordinal)
                ? null
                : new ApplyConflict(file.Path, ApplyConflictKind.ConcurrentEdit, "The file changed after the candidate was frozen.", false);
        }

        switch (file.Kind)
        {
            case ChangeKind.Added:
                if (current is null || string.Equals(current, file.CandidateHash, StringComparison.Ordinal))
                {
                    return null;
                }

                return new ApplyConflict(
                    file.Path, ApplyConflictKind.AlreadyExists,
                    "The candidate adds this file, but a file with different content now exists in the project.", false);

            case ChangeKind.Modified:
                if (current is null)
                {
                    return new ApplyConflict(
                        file.Path, ApplyConflictKind.Missing, "The candidate changes this file, but it no longer exists in the project.", false);
                }

                if (string.Equals(current, file.BaselineHash, StringComparison.Ordinal)
                    || string.Equals(current, file.CandidateHash, StringComparison.Ordinal))
                {
                    return null;
                }

                return new ApplyConflict(
                    file.Path, ApplyConflictKind.ConcurrentEdit,
                    "The file was edited in the project after the task started.", AutoMergePossible: !file.IsBinary);

            default:
                if (current is null || string.Equals(current, file.BaselineHash, StringComparison.Ordinal))
                {
                    return null;
                }

                return new ApplyConflict(
                    file.Path, ApplyConflictKind.ConcurrentEdit,
                    "The candidate deletes this file, but it was edited in the project after the task started.", false);
        }
    }

    public async Task<ApplyResult> ApplyAsync(IsolatedWorkspace workspace, Candidate candidate, string runId, CancellationToken cancellationToken)
    {
        if (candidate.Changes.IsEmpty)
        {
            return new ApplyResult(false, null, [], "The candidate contains no changes to apply.", false);
        }

        var preflight = await PreflightApplyAsync(workspace, candidate, cancellationToken).ConfigureAwait(false);
        if (!preflight.CanApply)
        {
            // Nothing is written when any file conflicts.
            return new ApplyResult(
                false, null, preflight.Conflicts,
                $"{preflight.Conflicts.Count} file(s) conflict with edits made in the project. Nothing was written.", false);
        }

        var inPlace = workspace.Mode == WorkspaceMode.InPlace;
        var entries = new List<JournalEntry>();
        var index = 0;
        foreach (var file in candidate.Changes.Files)
        {
            var target = ResolveInside(workspace.OriginalRoot, file.Path)!;
            string? preImage;
            if (inPlace)
            {
                preImage = file.BaselineHash;
            }
            else
            {
                // What is in the project right now is kept, so the apply can be rolled back and undone exactly.
                preImage = File.Exists(target) ? _blobs.StoreFile(target) : null;
            }

            entries.Add(new JournalEntry(index++, file.Path, file.Kind, preImage, file.CandidateHash, Done: inPlace));
        }

        var now = _clock.GetUtcNow();
        var journal = new ApplyJournal(
            Ids.NewId("j"), runId, candidate.CandidateId, candidate.Fingerprint, workspace.OriginalRoot,
            inPlace ? JournalState.Committed : JournalState.Prepared, entries, now, now);
        _journals.Save(journal);
        if (inPlace)
        {
            return new ApplyResult(true, journal, [], null, false);
        }

        journal = journal with { State = JournalState.InProgress };
        _journals.Save(journal);

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var target = ResolveInside(workspace.OriginalRoot, entry.Path)!;
            try
            {
                Perform(entry, target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                var rolledBack = RollBack(workspace.OriginalRoot, entries);
                journal = journal with { Entries = entries.ToList(), State = rolledBack ? JournalState.RolledBack : JournalState.Interrupted };
                _journals.Save(journal);
                return new ApplyResult(
                    false, journal,
                    [new ApplyConflict(entry.Path, ApplyConflictKind.Locked, ex.Message, false)],
                    rolledBack
                        ? $"'{entry.Path}' could not be written ({ex.Message}). Everything already written was rolled back."
                        : $"'{entry.Path}' could not be written ({ex.Message}) and the rollback was incomplete. Run /doctor.",
                    rolledBack);
            }

            entries[i] = entry with { Done = true };
            journal = journal with { Entries = entries.ToList() };
            _journals.Save(journal);
        }

        journal = journal with { State = JournalState.Committed };
        _journals.Save(journal);
        return new ApplyResult(true, journal, [], null, false);
    }

    private void Perform(JournalEntry entry, string target)
    {
        if (entry.Kind == ChangeKind.Deleted)
        {
            if (File.Exists(target))
            {
                _files.Delete(target);
            }

            return;
        }

        _files.Write(_blobs, entry.PostImageHash!, target);
    }

    /// <summary>Puts back what was in the project before the apply, for every file that still holds what the apply wrote.</summary>
    private bool RollBack(string projectRoot, List<JournalEntry> entries)
    {
        var complete = true;
        for (var i = entries.Count - 1; i >= 0; i--)
        {
            var entry = entries[i];
            var target = ResolveInside(projectRoot, entry.Path);
            if (target is null)
            {
                complete = false;
                continue;
            }

            try
            {
                var current = File.Exists(target) ? BlobStore.HashFile(target) : null;
                if (string.Equals(current, entry.PreImageHash, StringComparison.Ordinal))
                {
                    // Never written, or already put back.
                    entries[i] = entry with { Done = false };
                    continue;
                }

                if (!string.Equals(current, entry.PostImageHash, StringComparison.Ordinal))
                {
                    // Someone edited the file since. It is left alone.
                    complete = false;
                    continue;
                }

                Restore(entry, target, projectRoot);
                entries[i] = entry with { Done = false };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                complete = false;
            }
        }

        return complete;
    }

    private void Restore(JournalEntry entry, string target, string projectRoot)
    {
        if (entry.PreImageHash is null)
        {
            if (File.Exists(target))
            {
                _files.Delete(target);
                RemoveEmptyParents(target, projectRoot);
            }

            return;
        }

        _files.Write(_blobs, entry.PreImageHash, target);
    }

    private static void RemoveEmptyParents(string path, string projectRoot)
    {
        var root = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar);
        var directory = Path.GetDirectoryName(path);
        while (directory is not null
            && directory.Length > root.Length
            && directory.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(directory)
            && !Directory.EnumerateFileSystemEntries(directory).Any())
        {
            Directory.Delete(directory);
            directory = Path.GetDirectoryName(directory);
        }
    }

    public Task<UndoResult> UndoAsync(ApplyJournal journal, bool skipConflicts, CancellationToken cancellationToken)
    {
        if (journal.State == JournalState.Undone)
        {
            return Task.FromResult(new UndoResult(false, [], [], "This apply was already undone."));
        }

        if (journal.State != JournalState.Committed)
        {
            return Task.FromResult(new UndoResult(
                false, [], [], $"Only a completed apply can be undone; this one is {journal.State}."));
        }

        var conflicts = new List<ApplyConflict>();
        var unsafePath = false;
        var plan = new List<(JournalEntry Entry, string Target)>();
        foreach (var entry in journal.Entries.Where(e => e.Done))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = ResolveInside(journal.ProjectPath, entry.Path);
            if (target is null)
            {
                unsafePath = true;
                conflicts.Add(new ApplyConflict(entry.Path, ApplyConflictKind.UnsafePath, "The path is not inside the project.", false));
                continue;
            }

            string? current;
            try
            {
                current = File.Exists(target) ? BlobStore.HashFile(target) : null;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                conflicts.Add(new ApplyConflict(entry.Path, ApplyConflictKind.Locked, ex.Message, false));
                continue;
            }

            if (!string.Equals(current, entry.PostImageHash, StringComparison.Ordinal))
            {
                var detail = entry.Kind == ChangeKind.Deleted
                    ? "A file with this name was created after the apply deleted it."
                    : current is null ? "The file was deleted after the apply." : "The file was edited after the apply.";
                conflicts.Add(new ApplyConflict(
                    entry.Path, entry.Kind == ChangeKind.Deleted ? ApplyConflictKind.AlreadyExists : ApplyConflictKind.ConcurrentEdit, detail, false));
                continue;
            }

            if (entry.PreImageHash is not null && !_blobs.Contains(entry.PreImageHash))
            {
                conflicts.Add(new ApplyConflict(entry.Path, ApplyConflictKind.Missing, "The stored earlier content is missing.", false));
                continue;
            }

            plan.Add((entry, target));
        }

        if (unsafePath)
        {
            // A journal that names a path outside the project is not trusted for any of its entries.
            return Task.FromResult(new UndoResult(false, [], conflicts, "The journal names a path outside the project. Nothing was changed."));
        }

        if (conflicts.Count > 0 && !skipConflicts)
        {
            return Task.FromResult(new UndoResult(
                false, [], conflicts,
                $"{conflicts.Count} file(s) were edited after the apply. Nothing was changed."));
        }

        var restored = new List<string>();
        var failures = new List<ApplyConflict>(conflicts);
        foreach (var (entry, target) in Enumerable.Reverse(plan))
        {
            try
            {
                Restore(entry, target, journal.ProjectPath);
                restored.Add(entry.Path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
            {
                failures.Add(new ApplyConflict(entry.Path, ApplyConflictKind.Locked, ex.Message, false));
            }
        }

        _journals.Save(journal with { State = JournalState.Undone });
        return Task.FromResult(new UndoResult(true, restored, failures, null));
    }

    public Task<IReadOnlyList<ApplyJournal>> ReconcileJournalsAsync(CancellationToken cancellationToken)
    {
        var handled = new List<ApplyJournal>();
        foreach (var journal in _journals.FindUnfinished())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (journal.State == JournalState.Prepared || !Directory.Exists(journal.ProjectPath))
            {
                var closed = journal with { State = journal.State == JournalState.Prepared ? JournalState.RolledBack : JournalState.Interrupted };
                _journals.Save(closed);
                handled.Add(closed);
                continue;
            }

            // The entry that was being written when the process stopped is not marked done. The files
            // themselves say what happened, so every entry is examined.
            var entries = journal.Entries.ToList();
            var complete = RollBack(journal.ProjectPath, entries);
            var result = journal with { Entries = entries, State = complete ? JournalState.RolledBack : JournalState.Interrupted };
            _journals.Save(result);
            handled.Add(result);
        }

        return Task.FromResult<IReadOnlyList<ApplyJournal>>(handled);
    }

    /// <summary>The state of the project as it is right now.</summary>
    private async Task<Manifest> ProjectManifestAsync(IsolatedWorkspace workspace, BlobPolicy blobs, CancellationToken cancellationToken)
    {
        var listing = workspace.Mode == WorkspaceMode.GitWorktree ? null : Path.Combine(workspace.MetadataPath, "source-list.git");
        var insideRepository = await _git.FindRepositoryRootAsync(workspace.OriginalRoot, cancellationToken).ConfigureAwait(false) is not null;
        var files = await ListFilesAsync(workspace.OriginalRoot, insideRepository ? null : listing, cancellationToken).ConfigureAwait(false);
        return await BuildManifestAsync(workspace.OriginalRoot, files, blobs, cancellationToken).ConfigureAwait(false);
    }
}
