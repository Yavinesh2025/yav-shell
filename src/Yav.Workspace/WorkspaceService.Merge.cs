using Yav.Core.Ports;
using Yav.Core.Runs;

namespace Yav.Workspace;

public sealed partial class WorkspaceService
{
    /// <summary>
    /// Brings edits made in the project since the baseline into the isolated workspace and makes the
    /// project's current state the new baseline. Either everything merges or the workspace is left as it was.
    /// </summary>
    public async Task<MergeResult> MergeConcurrentEditsAsync(IsolatedWorkspace workspace, Candidate candidate, CancellationToken cancellationToken)
    {
        if (workspace.Mode == WorkspaceMode.InPlace)
        {
            var inPlace = await ProjectManifestAsync(workspace, BlobPolicy.Baseline, cancellationToken).ConfigureAwait(false);
            SaveManifest(workspace, inPlace);
            SaveRecord(LoadRecord(workspace.WorkspaceId) with { BaselineFingerprint = inPlace.Fingerprint, FileCount = inPlace.Count });
            return new MergeResult(true, [], [], inPlace.Fingerprint);
        }

        var baseline = LoadManifest(workspace, workspace.BaselineFingerprint);
        var agent = LoadManifest(workspace, candidate.Fingerprint);
        var project = await ProjectManifestAsync(workspace, BlobPolicy.Baseline, cancellationToken).ConfigureAwait(false);

        var unresolved = new List<string>();
        var merged = new List<string>();
        var plan = new List<Action>();
        var scratch = Path.Combine(workspace.MetadataPath, "merge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(scratch);

        try
        {
            foreach (var change in Manifest.Compare(baseline, project, _ => false).Files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var path = change.Path;
                var target = ResolveInside(workspace.RootPath, path);
                if (target is null)
                {
                    unresolved.Add(path);
                    continue;
                }

                baseline.Entries.TryGetValue(path, out var before);
                agent.Entries.TryGetValue(path, out var theirs);
                project.Entries.TryGetValue(path, out var mine);

                var agentChanged = !string.Equals(before?.Hash, theirs?.Hash, StringComparison.Ordinal);
                if (!agentChanged)
                {
                    // Only the user changed this path: the workspace takes the user's version.
                    if (mine is null)
                    {
                        plan.Add(() =>
                        {
                            if (File.Exists(target))
                            {
                                DeleteFile(target);
                            }
                        });
                    }
                    else if (_blobs.Contains(mine.Hash))
                    {
                        var hash = mine.Hash;
                        plan.Add(() => _blobs.RestoreTo(hash, target));
                    }
                    else
                    {
                        var source = ResolveInside(workspace.OriginalRoot, path)!;
                        plan.Add(() =>
                        {
                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            CopyFile(source, target);
                        });
                    }

                    continue;
                }

                if (string.Equals(theirs?.Hash, mine?.Hash, StringComparison.Ordinal))
                {
                    // Both sides ended up with the same content, for example after an apply.
                    continue;
                }

                if (before is null || theirs is null || mine is null
                    || !_blobs.Contains(before.Hash) || !_blobs.Contains(theirs.Hash) || !_blobs.Contains(mine.Hash)
                    || IsBinaryContent(before.Hash) || IsBinaryContent(theirs.Hash) || IsBinaryContent(mine.Hash)
                    || !_git.IsAvailable)
                {
                    // One side deleted or added the file, the content is binary, or the earlier content is gone.
                    unresolved.Add(path);
                    continue;
                }

                var id = Guid.NewGuid().ToString("N")[..8];
                var baseFile = Path.Combine(scratch, id + ".base");
                var agentFile = Path.Combine(scratch, id + ".agent");
                var userFile = Path.Combine(scratch, id + ".user");
                _blobs.RestoreTo(before.Hash, baseFile);
                _blobs.RestoreTo(theirs.Hash, agentFile);
                _blobs.RestoreTo(mine.Hash, userFile);

                var (text, conflicts) = await _git.MergeFileAsync(scratch, agentFile, baseFile, userFile, cancellationToken).ConfigureAwait(false);
                if (conflicts != 0)
                {
                    unresolved.Add(path);
                    continue;
                }

                var result = Path.Combine(scratch, id + ".merged");
                await File.WriteAllTextAsync(result, text, Utf8NoBom, cancellationToken).ConfigureAwait(false);
                merged.Add(path);
                plan.Add(() =>
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    CopyFile(result, target);
                });
            }

            if (unresolved.Count > 0)
            {
                unresolved.Sort(StringComparer.Ordinal);
                return new MergeResult(false, unresolved, [], workspace.BaselineFingerprint);
            }

            foreach (var step in plan)
            {
                step();
            }

            SaveManifest(workspace, project);
            var record = LoadRecord(workspace.WorkspaceId);
            var references = record.RepositoryReferences;
            string? baseCommit = record.BaseCommit;
            if (workspace.Mode == WorkspaceMode.GitWorktree && _git.IsAvailable)
            {
                references = await ReadReferencesAsync(workspace.OriginalRoot, cancellationToken).ConfigureAwait(false);
                baseCommit = await _git.ResolveHeadAsync(workspace.OriginalRoot, cancellationToken).ConfigureAwait(false) ?? baseCommit;
            }

            SaveRecord(record with
            {
                BaselineFingerprint = project.Fingerprint,
                FileCount = project.Count,
                RepositoryReferences = references,
                BaseCommit = baseCommit,
            });

            merged.Sort(StringComparer.Ordinal);
            return new MergeResult(true, [], merged, project.Fingerprint);
        }
        finally
        {
            DeleteDirectory(scratch);
        }
    }

    public Task<MechanicalEditPreview> PreviewMechanicalEditAsync(IsolatedWorkspace workspace, MechanicalEditRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(Plan(workspace, request).Preview);

    public Task ApplyMechanicalEditAsync(IsolatedWorkspace workspace, MechanicalEditRequest request, CancellationToken cancellationToken)
    {
        var (preview, target, updated, preamble) = Plan(workspace, request);
        if (!preview.Valid || target is null || updated is null)
        {
            throw new InvalidOperationException("The edit was not made: " + (preview.Problem ?? "it is not valid."));
        }

        var temporary = target + ".yav-" + Guid.NewGuid().ToString("N")[..8] + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            stream.Write(preamble);
            stream.Write(Utf8NoBom.GetBytes(updated));
        }

        File.Move(temporary, target, overwrite: true);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Validates a literal replacement without changing anything. An edit is only valid when the target
    /// is unambiguous; anything else is a task for Model A.
    /// </summary>
    private static (MechanicalEditPreview Preview, string? Target, string? Updated, byte[] Preamble) Plan(IsolatedWorkspace workspace, MechanicalEditRequest request)
    {
        MechanicalEditPreview Invalid(string problem, int count = 0, IReadOnlyList<int>? lines = null) =>
            new(false, problem, count, lines ?? [], string.Empty);

        if (string.IsNullOrEmpty(request.ExpectedText))
        {
            return (Invalid("The text to replace is empty."), null, null, []);
        }

        var target = ResolveInside(workspace.RootPath, request.Path);
        if (target is null)
        {
            return (Invalid($"'{request.Path}' is not a path inside the workspace."), null, null, []);
        }

        if (!File.Exists(target))
        {
            return (Invalid($"'{request.Path}' does not exist."), null, null, []);
        }

        if (new FileInfo(target).LinkTarget is not null)
        {
            return (Invalid($"'{request.Path}' is a link."), null, null, []);
        }

        var bytes = File.ReadAllBytes(target);
        if (UnifiedDiff.IsBinary(bytes))
        {
            return (Invalid($"'{request.Path}' is a binary file."), null, null, []);
        }

        byte[] preamble = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? [0xEF, 0xBB, 0xBF] : [];
        string text;
        try
        {
            text = StrictUtf8.GetString(bytes.AsSpan(preamble.Length));
        }
        catch (System.Text.DecoderFallbackException)
        {
            return (Invalid($"'{request.Path}' is not UTF-8 text, so it cannot be edited safely."), null, null, []);
        }

        var positions = new List<int>();
        var searchFrom = 0;
        while (searchFrom <= text.Length)
        {
            var found = text.IndexOf(request.ExpectedText, searchFrom, StringComparison.Ordinal);
            if (found < 0)
            {
                break;
            }

            positions.Add(found);
            searchFrom = found + request.ExpectedText.Length;
        }

        var lines = positions.Select(p => 1 + text.AsSpan(0, p).Count('\n')).ToList();
        if (positions.Count == 0)
        {
            return (Invalid("The text to replace was not found."), null, null, []);
        }

        if (request.ExpectedMatches is { } expected && expected != positions.Count)
        {
            return (Invalid($"Expected {expected} match(es) but found {positions.Count}.", positions.Count, lines), null, null, []);
        }

        if (!request.AllOccurrences && positions.Count > 1)
        {
            return (Invalid($"The text occurs {positions.Count} times, so the edit is ambiguous.", positions.Count, lines), null, null, []);
        }

        var updated = text.Replace(request.ExpectedText, request.ReplacementText, StringComparison.Ordinal);
        var label = request.Path.Replace('\\', '/');
        var diff = UnifiedDiff.Create("a/" + label, "b/" + label, text, updated);
        return (new MechanicalEditPreview(true, null, positions.Count, lines, diff), target, updated, preamble);
    }
}
