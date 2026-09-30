using System.Text;
using Yav.Core;
using Yav.Core.Ports;
using Yav.Core.Settings;

namespace Yav.Workspace;

/// <summary>
/// Workspace protection. Agents work in an isolated copy; the project itself is written only by an
/// explicit, journaled apply or undo. Isolation separates the work from the project; it is not a
/// security sandbox.
/// </summary>
public sealed partial class WorkspaceService : IWorkspaceService
{
    /// <summary>The optional project configuration file. Changes to it always need explicit approval.</summary>
    public const string ConfigurationFileName = "yav.project.json";

    // Content of baseline files larger than this is not copied into YAV storage.
    private const long BaselineBlobLimit = 64L * 1024 * 1024;

    // Text files larger than this are reported as changed without printing the diff.
    private const long DiffTextLimit = 2L * 1024 * 1024;

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly string[] VersionControlDirectories = [".git", ".hg", ".svn"];

    private readonly YavPaths _paths;
    private readonly IJournalStore _journals;
    private readonly TimeProvider _clock;
    private readonly IFileOperations _files;
    private readonly GitClient _git;
    private readonly BlobStore _blobs;

    public WorkspaceService(YavPaths paths, IProcessRunner runner, IJournalStore journals, TimeProvider clock, IFileOperations? files = null)
    {
        _paths = paths;
        _journals = journals;
        _clock = clock;
        _files = files ?? new FileOperations();
        _git = new GitClient(runner);
        _blobs = new BlobStore(paths.Blobs);
        Directory.CreateDirectory(paths.Workspaces);
    }

    public BlobStore Blobs => _blobs;

    public bool GitAvailable => _git.IsAvailable;

    private string WorkspaceDirectory(string workspaceId)
    {
        if (!IsWorkspaceId(workspaceId))
        {
            throw new ArgumentException($"'{workspaceId}' is not a workspace identifier.", nameof(workspaceId));
        }

        return Path.Combine(_paths.Workspaces, workspaceId);
    }

    private static bool IsWorkspaceId(string? text)
    {
        if (text is not { Length: >= 2 and <= 32 } || text[0] != 'w')
        {
            return false;
        }

        foreach (var c in text)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c == '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static string RecordPath(string workspaceDirectory) => Path.Combine(workspaceDirectory, "meta", "workspace.json");

    private static string ManifestPath(string metadataPath, string fingerprint) =>
        Path.Combine(metadataPath, "manifests", fingerprint + ".manifest");

    private IsolatedWorkspace ToWorkspace(WorkspaceRecord record, bool reused)
    {
        var directory = WorkspaceDirectory(record.WorkspaceId);
        var subpath = Path.GetRelativePath(record.OriginalRoot, record.OriginalPath);
        var agentDirectory = subpath is "." or "" ? record.RootPath : Path.Combine(record.RootPath, subpath);
        return new IsolatedWorkspace(
            WorkspaceId: record.WorkspaceId,
            TaskId: record.TaskId,
            Mode: record.Mode,
            OriginalPath: record.OriginalPath,
            OriginalRoot: record.OriginalRoot,
            RootPath: record.RootPath,
            AgentDirectory: agentDirectory,
            MetadataPath: Path.Combine(directory, "meta"),
            EvidencePath: Path.Combine(directory, "evidence"),
            BaselineId: record.BaselineFingerprint,
            BaselineFingerprint: record.BaselineFingerprint,
            BaseCommit: record.BaseCommit,
            FileCount: record.FileCount,
            Reused: reused,
            Notes: record.Notes);
    }

    private WorkspaceRecord LoadRecord(string workspaceId)
    {
        var path = RecordPath(WorkspaceDirectory(workspaceId));
        return WorkspaceRecord.FromJson(File.ReadAllText(path, Utf8NoBom));
    }

    private void SaveRecord(WorkspaceRecord record)
    {
        var path = RecordPath(WorkspaceDirectory(record.WorkspaceId));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, record.ToJson(), Utf8NoBom);
        File.Move(temporary, path, overwrite: true);
    }

    public Task<IsolatedWorkspace?> FindAsync(string workspaceId, CancellationToken cancellationToken)
    {
        if (!IsWorkspaceId(workspaceId))
        {
            return Task.FromResult<IsolatedWorkspace?>(null);
        }

        var recordPath = RecordPath(WorkspaceDirectory(workspaceId));
        if (!File.Exists(recordPath))
        {
            return Task.FromResult<IsolatedWorkspace?>(null);
        }

        var record = LoadRecord(workspaceId);
        return Task.FromResult<IsolatedWorkspace?>(Directory.Exists(record.RootPath) ? ToWorkspace(record, reused: true) : null);
    }

    /// <summary>
    /// Turns a workspace-relative path into a full path, or returns null when the path would leave the root.
    /// </summary>
    internal static string? ResolveInside(string root, string relative, bool allowVersionControl = false)
    {
        if (string.IsNullOrWhiteSpace(relative))
        {
            return null;
        }

        var normalized = relative.Replace('\\', '/');
        if (normalized.StartsWith('/') || normalized.Contains(':') || normalized.Contains('\0'))
        {
            return null;
        }

        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment is ".." or ".")
            {
                return null;
            }
        }

        if (!allowVersionControl && segments.Length > 0 && VersionControlDirectories.Contains(segments[0], StringComparer.OrdinalIgnoreCase))
        {
            return null;
        }

        var rootFull = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar);
        var full = Path.GetFullPath(Path.Combine(rootFull, Path.Combine(segments)));
        return full.StartsWith(rootFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>Lists the files that belong to a directory's source state: tracked files plus untracked files that are not ignored.</summary>
    private async Task<List<string>> ListFilesAsync(string root, string? privateGitDirectory, CancellationToken cancellationToken)
    {
        if (_git.IsAvailable)
        {
            var insideRepository = await _git.FindRepositoryRootAsync(root, cancellationToken).ConfigureAwait(false) is not null;
            if (insideRepository)
            {
                var listed = await _git.RunNullSeparatedAsync(
                    root, ["ls-files", "-z", "--cached", "--others", "--exclude-standard"], cancellationToken).ConfigureAwait(false);
                return Existing(root, listed);
            }

            if (privateGitDirectory is not null)
            {
                // A Git directory kept in YAV storage gives ignore-file handling for a project that is not a repository,
                // without creating anything inside the project.
                if (!Directory.Exists(privateGitDirectory))
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(privateGitDirectory)!);
                    await _git.RunAsync(
                        Path.GetDirectoryName(privateGitDirectory)!, ["init", "--quiet", "--bare", privateGitDirectory], cancellationToken).ConfigureAwait(false);
                }

                var listed = await _git.RunNullSeparatedAsync(
                    root,
                    ["--git-dir", privateGitDirectory, "--work-tree", root, "-c", "core.bare=false", "ls-files", "-z", "--others", "--exclude-standard"],
                    cancellationToken).ConfigureAwait(false);
                return Existing(root, listed);
            }
        }

        return Walk(root);
    }

    private static List<string> Existing(string root, IEnumerable<string> relativePaths)
    {
        var result = new List<string>();
        foreach (var relative in relativePaths)
        {
            var normalized = relative.Replace('\\', '/');
            if (normalized.EndsWith('/'))
            {
                // A nested repository or submodule is one entry; its content is not part of this state.
                continue;
            }

            var full = ResolveInside(root, normalized);
            if (full is not null && File.Exists(full))
            {
                result.Add(normalized);
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static List<string> Walk(string root)
    {
        var result = new List<string>();
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            {
                var name = Path.GetFileName(entry);
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.Directory) != 0)
                {
                    // A link to a directory is never followed: it could lead outside the project or back into it.
                    if ((attributes & FileAttributes.ReparsePoint) == 0 && !VersionControlDirectories.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        pending.Push(entry);
                    }

                    continue;
                }

                result.Add(Path.GetRelativePath(root, entry).Replace('\\', '/'));
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }

    /// <summary>Hashes every listed file. Every byte is read each time: a file that kept its size and time is still noticed.</summary>
    private async Task<Manifest> BuildManifestAsync(string root, IReadOnlyList<string> relativePaths, BlobPolicy blobs, CancellationToken cancellationToken)
    {
        var entries = new System.Collections.Concurrent.ConcurrentBag<ManifestEntry>();
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(relativePaths, options, (relative, _) =>
        {
            var full = ResolveInside(root, relative);
            if (full is null)
            {
                return ValueTask.CompletedTask;
            }

            var entry = Describe(full, relative, blobs);
            if (entry is not null)
            {
                entries.Add(entry);
            }

            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        return Manifest.From(entries);
    }

    private enum BlobPolicy
    {
        None,

        /// <summary>Store content up to the baseline size limit.</summary>
        Baseline,

        /// <summary>Store all content that is not stored yet, whatever its size.</summary>
        All,
    }

    private ManifestEntry? Describe(string fullPath, string relative, BlobPolicy blobs)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var info = new FileInfo(fullPath);
                if (!info.Exists)
                {
                    return null;
                }

                if (info.LinkTarget is { } target)
                {
                    // A link is recorded by where it points, and never followed.
                    var bytes = Encoding.UTF8.GetBytes(target);
                    var linkHash = blobs == BlobPolicy.None ? BlobStore.HashBytes(bytes) : _blobs.StoreBytes(bytes);
                    return new ManifestEntry(relative, 0, linkHash, EntryKind.Symlink, info.LastWriteTimeUtc.Ticks);
                }

                var hash = BlobStore.HashFile(fullPath);
                var store = blobs == BlobPolicy.All || (blobs == BlobPolicy.Baseline && info.Length <= BaselineBlobLimit);
                if (store && !_blobs.Contains(hash))
                {
                    var stored = _blobs.StoreFile(fullPath);
                    if (!string.Equals(stored, hash, StringComparison.Ordinal))
                    {
                        // The file changed between the two reads. What was stored is what counts.
                        hash = stored;
                    }
                }

                info.Refresh();
                return new ManifestEntry(relative, info.Length, hash, EntryKind.File, info.LastWriteTimeUtc.Ticks);
            }
            catch (FileNotFoundException)
            {
                return null;
            }
            catch (DirectoryNotFoundException)
            {
                return null;
            }
            catch (IOException) when (attempt < 4)
            {
                Thread.Sleep(50 * (attempt + 1));
            }
        }
    }

    private Manifest LoadManifest(IsolatedWorkspace workspace, string fingerprint)
    {
        var path = ManifestPath(workspace.MetadataPath, fingerprint);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"The recorded state {Short(fingerprint)} of workspace {workspace.WorkspaceId} is missing.", path);
        }

        var manifest = Manifest.Load(path);
        if (!string.Equals(manifest.Fingerprint, fingerprint, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The recorded state {Short(fingerprint)} does not match its content.");
        }

        return manifest;
    }

    private static void SaveManifest(IsolatedWorkspace workspace, Manifest manifest) => SaveManifest(workspace.MetadataPath, manifest);

    private static void SaveManifest(string metadataPath, Manifest manifest)
    {
        var path = ManifestPath(metadataPath, manifest.Fingerprint);
        if (!File.Exists(path))
        {
            manifest.Save(path);
        }
    }

    private static string? PrivateGitDirectory(WorkspaceMode mode, string metadataPath) =>
        mode == WorkspaceMode.GitWorktree ? null : Path.Combine(metadataPath, "list.git");

    private bool IsBinaryContent(string hash) =>
        _blobs.Contains(hash) && UnifiedDiff.IsBinary(_blobs.ReadPrefix(hash, 8000));

    private static string Short(string fingerprint) => fingerprint.Length > 12 ? fingerprint[..12] : fingerprint;

    private static void DeleteDirectory(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                // Links are skipped, never followed: a junction inside the workspace may point at the user's own files.
                var options = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
                foreach (var file in Directory.EnumerateFiles(path, "*", options))
                {
                    var attributes = File.GetAttributes(file);
                    if ((attributes & FileAttributes.ReadOnly) != 0)
                    {
                        File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                    }
                }

                Directory.Delete(path, recursive: true);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && attempt < 4)
            {
                Thread.Sleep(150 * (attempt + 1));
            }
        }
    }

    private static string NewWorkspaceId() => "w" + Ids.RandomSuffix(8);
}
