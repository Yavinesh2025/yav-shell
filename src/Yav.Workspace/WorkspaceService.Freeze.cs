using System.Diagnostics;
using System.Text;
using Yav.Core.Ports;
using Yav.Core.Runs;

namespace Yav.Workspace;

public sealed partial class WorkspaceService
{
    public async Task<string> FingerprintAsync(IsolatedWorkspace workspace, CancellationToken cancellationToken)
    {
        var files = await ListFilesAsync(workspace.RootPath, PrivateGitDirectory(workspace.Mode, workspace.MetadataPath), cancellationToken).ConfigureAwait(false);
        var manifest = await BuildManifestAsync(workspace.RootPath, files, BlobPolicy.None, cancellationToken).ConfigureAwait(false);
        return manifest.Fingerprint;
    }

    public async Task<CandidateSnapshot> FreezeAsync(IsolatedWorkspace workspace, ProjectConfiguration configuration, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var baseline = LoadManifest(workspace, workspace.BaselineFingerprint);
        var files = await ListFilesAsync(workspace.RootPath, PrivateGitDirectory(workspace.Mode, workspace.MetadataPath), cancellationToken).ConfigureAwait(false);

        // The content of every file is stored, so the candidate can be reproduced exactly even when the
        // workspace changes afterwards. Content that is already stored is not copied again.
        var candidate = await BuildManifestAsync(workspace.RootPath, files, BlobPolicy.All, cancellationToken).ConfigureAwait(false);
        SaveManifest(workspace, candidate);

        var changes = Manifest.Compare(baseline, candidate, IsBinaryContent);
        var protectedPatterns = configuration.ProtectedPaths.Append(ConfigurationFileName).ToList();
        var protectedTouched = changes.Files
            .Where(f => Glob.MatchesAny(protectedPatterns, f.Path))
            .Select(f => f.Path)
            .ToList();
        var testsTouched = changes.Files
            .Where(f => f.Kind is ChangeKind.Modified or ChangeKind.Deleted)
            .Where(f => Glob.MatchesAny(configuration.TestPaths, f.Path))
            .Select(f => f.Path)
            .ToList();

        return new CandidateSnapshot(
            Fingerprint: candidate.Fingerprint,
            ManifestId: candidate.Fingerprint,
            Changes: changes,
            ProtectedPathsTouched: protectedTouched,
            ExistingTestsTouched: testsTouched,
            FileCount: candidate.Count,
            Duration: Stopwatch.GetElapsedTime(started));
    }

    public async Task<IReadOnlyList<string>> RestoreCandidateAsync(IsolatedWorkspace workspace, Candidate candidate, CancellationToken cancellationToken)
    {
        if (workspace.Mode == WorkspaceMode.InPlace)
        {
            const string Reason = "This run works directly in the project. Its files are never rewritten to match a candidate.";
            throw new WorkspaceUnsupportedException(Reason, [Reason]);
        }

        var wanted = LoadManifest(workspace, candidate.Fingerprint);
        var files = await ListFilesAsync(workspace.RootPath, PrivateGitDirectory(workspace.Mode, workspace.MetadataPath), cancellationToken).ConfigureAwait(false);
        var current = await BuildManifestAsync(workspace.RootPath, files, BlobPolicy.None, cancellationToken).ConfigureAwait(false);
        var differences = Manifest.Compare(wanted, current, _ => false).Files;

        // Everything is verified before anything is written, so a candidate is never half restored.
        var plan = new List<(string Path, string Target, string? Hash)>();
        foreach (var difference in differences)
        {
            var target = ResolveInside(workspace.RootPath, difference.Path)
                ?? throw new IOException($"'{difference.Path}' is not inside the isolated workspace.");

            // Seen from the candidate, a file that was "added" is one the candidate does not have.
            var hash = difference.Kind == ChangeKind.Added ? null : wanted.Entries[difference.Path].Hash;
            if (hash is not null && !_blobs.Contains(hash))
            {
                throw new IOException($"The stored content of '{difference.Path}' in candidate {Short(candidate.Fingerprint)} is missing.");
            }

            plan.Add((difference.Path, target, hash));
        }

        foreach (var (_, target, hash) in plan)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (hash is null)
            {
                if (File.Exists(target))
                {
                    DeleteFile(target);
                }
            }
            else
            {
                _blobs.RestoreTo(hash, target);
            }
        }

        return plan.Select(p => p.Path).ToList();
    }

    public Task<DiffResult> DiffAsync(IsolatedWorkspace workspace, Candidate candidate, int maxCharacters, CancellationToken cancellationToken)
    {
        var builder = new StringBuilder();
        var binary = new List<string>();
        foreach (var file in candidate.Changes.Files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (file.RenamedFrom is not null)
            {
                builder.Append("# ").Append(file.Path).Append(" has the same content as deleted ").Append(file.RenamedFrom).Append('\n');
            }

            if (file.IsBinary)
            {
                binary.Add(file.Path);
                builder.Append("Binary file ").Append(file.Path).Append(' ').Append(Describe(file)).Append('\n');
                continue;
            }

            if ((file.BaselineLength ?? 0) > DiffTextLimit || (file.CandidateLength ?? 0) > DiffTextLimit)
            {
                builder.Append("Large file ").Append(file.Path).Append(' ').Append(Describe(file)).Append("; the diff is not printed\n");
                continue;
            }

            var before = ReadText(file.BaselineHash);
            var after = ReadText(file.CandidateHash);
            if (before is null || after is null)
            {
                builder.Append("File ").Append(file.Path).Append(' ').Append(Describe(file)).Append("; its content is not available for a diff\n");
                continue;
            }

            var oldLabel = file.Kind == ChangeKind.Added ? "/dev/null" : "a/" + file.Path;
            var newLabel = file.Kind == ChangeKind.Deleted ? "/dev/null" : "b/" + file.Path;
            builder.Append(UnifiedDiff.Create(oldLabel, newLabel, before, after));
        }

        var full = builder.ToString();
        Directory.CreateDirectory(workspace.EvidencePath);
        var fullPath = Path.Combine(workspace.EvidencePath, $"diff-{Short(candidate.Fingerprint)}.patch");
        File.WriteAllText(fullPath, full, Utf8NoBom);

        if (full.Length <= maxCharacters)
        {
            return Task.FromResult(new DiffResult(full, false, fullPath, candidate.Changes.Files.Count, binary));
        }

        var cut = full.LastIndexOf('\n', Math.Max(0, maxCharacters - 1));
        var shown = cut > 0 ? full[..(cut + 1)] : full[..maxCharacters];
        shown += $"[... shortened: {full.Length - shown.Length} more characters in the complete diff ...]\n";
        return Task.FromResult(new DiffResult(shown, true, fullPath, candidate.Changes.Files.Count, binary));

        static string Describe(ChangedFile file) => file.Kind switch
        {
            ChangeKind.Added => $"added ({file.CandidateLength} bytes)",
            ChangeKind.Deleted => $"deleted ({file.BaselineLength} bytes)",
            _ => $"changed ({file.BaselineLength} -> {file.CandidateLength} bytes)",
        };
    }

    public Task<Func<string, bool>> GetPathLookupAsync(IsolatedWorkspace workspace, Candidate candidate, CancellationToken cancellationToken)
    {
        var known = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in LoadManifest(workspace, workspace.BaselineFingerprint).Entries.Keys)
        {
            known.Add(path);
        }

        foreach (var path in LoadManifest(workspace, candidate.Fingerprint).Entries.Keys)
        {
            known.Add(path);
        }

        return Task.FromResult<Func<string, bool>>(path => known.Contains(path.Replace('\\', '/')));
    }

    public Task<ExecutionCopy> PrepareExecutionCopyAsync(
        IsolatedWorkspace workspace,
        Candidate candidate,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken)
    {
        if (string.Equals(configuration.ValidationExecution, ProjectConfiguration.ExecutionCandidate, StringComparison.OrdinalIgnoreCase)
            || workspace.Mode == WorkspaceMode.InPlace)
        {
            return Task.FromResult(new ExecutionCopy(
                workspace.RootPath,
                IsCandidateWorkspace: true,
                candidate.Fingerprint,
                ["Checks run in the candidate workspace. It is verified against the candidate fingerprint afterwards."]));
        }

        var manifest = LoadManifest(workspace, candidate.Fingerprint);
        return Task.FromResult(Materialize(workspace, manifest, "x", cancellationToken));
    }

    public Task<ExecutionCopy> PrepareBaselineCopyAsync(IsolatedWorkspace workspace, ProjectConfiguration configuration, CancellationToken cancellationToken)
    {
        var manifest = LoadManifest(workspace, workspace.BaselineFingerprint);
        return Task.FromResult(Materialize(workspace, manifest, "b", cancellationToken));
    }

    /// <summary>
    /// Makes a directory hold exactly the given state. Files that are not part of any recorded state, such as
    /// build output and installed dependencies, are left alone so later checks can reuse them.
    /// </summary>
    private ExecutionCopy Materialize(IsolatedWorkspace workspace, Manifest manifest, string name, CancellationToken cancellationToken)
    {
        var directory = Path.Combine(WorkspaceDirectory(workspace.WorkspaceId), name);
        var statePath = Path.Combine(workspace.MetadataPath, name + ".manifest");
        Directory.CreateDirectory(directory);
        var notes = new List<string>();

        if (File.Exists(statePath))
        {
            Manifest previous;
            try
            {
                previous = Manifest.Load(statePath);
            }
            catch (InvalidDataException)
            {
                previous = Manifest.From([]);
            }

            foreach (var entry in previous.Entries.Values)
            {
                if (!manifest.Entries.ContainsKey(entry.Path))
                {
                    var stale = ResolveInside(directory, entry.Path);
                    if (stale is not null && File.Exists(stale))
                    {
                        DeleteFile(stale);
                    }
                }
            }
        }

        var missing = new System.Collections.Concurrent.ConcurrentBag<string>();
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8), CancellationToken = cancellationToken };
        Parallel.ForEach(manifest.Entries.Values, options, entry =>
        {
            var target = ResolveInside(directory, entry.Path);
            if (target is null || entry.Kind == EntryKind.Symlink)
            {
                return;
            }

            // The content is compared, not the time stamp: a check may have rewritten a source file.
            if (File.Exists(target) && new FileInfo(target).Length == entry.Length
                && string.Equals(BlobStore.HashFile(target), entry.Hash, StringComparison.Ordinal))
            {
                return;
            }

            if (_blobs.Contains(entry.Hash))
            {
                _blobs.RestoreTo(entry.Hash, target);
                return;
            }

            // Large baseline files are not stored. They are taken from the workspace when it still holds them unchanged.
            var source = ResolveInside(workspace.RootPath, entry.Path);
            if (source is not null && File.Exists(source) && string.Equals(BlobStore.HashFile(source), entry.Hash, StringComparison.Ordinal))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                CopyFile(source, target);
                return;
            }

            missing.Add(entry.Path);
        });

        if (!missing.IsEmpty)
        {
            throw new IOException(
                $"The copy for checking is incomplete: the content of {missing.Count} file(s) is no longer available ({missing.First()}).");
        }

        manifest.Save(statePath);
        notes.Add($"Checks run in a separate copy of state {Short(manifest.Fingerprint)}.");
        return new ExecutionCopy(directory, IsCandidateWorkspace: false, manifest.Fingerprint, notes);
    }

    private string? ReadText(string? hash)
    {
        if (hash is null)
        {
            return string.Empty;
        }

        if (!_blobs.Contains(hash))
        {
            return null;
        }

        return Decode(_blobs.ReadAllBytes(hash));
    }

    private static string Decode(byte[] bytes)
    {
        ReadOnlySpan<byte> span = bytes;
        if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        try
        {
            return StrictUtf8.GetString(span);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(span);
        }
    }
}
