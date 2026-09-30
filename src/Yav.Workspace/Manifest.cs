using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Yav.Core.Runs;

namespace Yav.Workspace;

public enum EntryKind
{
    File,
    Symlink,
}

/// <param name="Path">Relative to the workspace root, with forward slashes, in the letter case found on disk.</param>
/// <param name="Hash">SHA-256 of the exact bytes on disk.</param>
/// <param name="LastWriteTicks">Used only to skip re-hashing unchanged files. Never part of the fingerprint.</param>
public sealed record ManifestEntry(string Path, long Length, string Hash, EntryKind Kind, long LastWriteTicks);

/// <summary>
/// The complete list of files in a workspace state with the hash of each. Its fingerprint identifies
/// that state exactly: two states have the same fingerprint only when every path and every byte is the same.
/// </summary>
public sealed class Manifest
{
    private const string Header = "yav-manifest";
    private const int FormatVersion = 1;

    private Manifest(Dictionary<string, ManifestEntry> entries, string fingerprint)
    {
        Entries = entries;
        Fingerprint = fingerprint;
    }

    public IReadOnlyDictionary<string, ManifestEntry> Entries { get; }

    public string Fingerprint { get; }

    public int Count => Entries.Count;

    public static Manifest From(IEnumerable<ManifestEntry> entries)
    {
        var map = new Dictionary<string, ManifestEntry>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            map[entry.Path] = entry;
        }

        return new Manifest(map, ComputeFingerprint(map.Values));
    }

    /// <summary>Finds an entry the way the file system would: without regard to letter case.</summary>
    public ManifestEntry? FindIgnoringCase(string path)
    {
        if (Entries.TryGetValue(path, out var exact))
        {
            return exact;
        }

        foreach (var entry in Entries.Values)
        {
            if (string.Equals(entry.Path, path, StringComparison.OrdinalIgnoreCase))
            {
                return entry;
            }
        }

        return null;
    }

    public void Save(string path)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        using (var writer = new StreamWriter(temporary, append: false, new UTF8Encoding(false)))
        {
            writer.NewLine = "\n";
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{Header} {FormatVersion} {Fingerprint} {Entries.Count}"));
            foreach (var entry in Entries.Values.OrderBy(e => e.Path, StringComparer.Ordinal))
            {
                writer.WriteLine(string.Create(
                    CultureInfo.InvariantCulture,
                    $"{entry.Hash}\t{entry.Length}\t{entry.LastWriteTicks}\t{(entry.Kind == EntryKind.Symlink ? 'l' : 'f')}\t{entry.Path}"));
            }
        }

        File.Move(temporary, path, overwrite: true);
    }

    public static Manifest Load(string path)
    {
        using var reader = new StreamReader(path, new UTF8Encoding(false));
        var header = reader.ReadLine()?.Split(' ');
        if (header is not { Length: 4 } || header[0] != Header || header[1] != FormatVersion.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException($"'{path}' is not a YAV manifest this version can read.");
        }

        var entries = new List<ManifestEntry>();
        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var parts = line.Split('\t', 5);
            if (parts.Length != 5)
            {
                throw new InvalidDataException($"'{path}' contains a malformed entry.");
            }

            entries.Add(new ManifestEntry(
                parts[4],
                long.Parse(parts[1], CultureInfo.InvariantCulture),
                parts[0],
                parts[3] == "l" ? EntryKind.Symlink : EntryKind.File,
                long.Parse(parts[2], CultureInfo.InvariantCulture)));
        }

        var manifest = From(entries);
        if (!string.Equals(manifest.Fingerprint, header[2], StringComparison.Ordinal)
            || manifest.Count != int.Parse(header[3], CultureInfo.InvariantCulture))
        {
            throw new InvalidDataException($"'{path}' does not match its own fingerprint. It was changed after it was written.");
        }

        return manifest;
    }

    /// <summary>The files that differ between two states, ordered by path.</summary>
    /// <param name="isBinary">Tells whether the content with the given hash is binary.</param>
    public static ChangeSet Compare(Manifest baseline, Manifest candidate, Func<string, bool> isBinary)
    {
        var changes = new List<ChangedFile>();
        foreach (var entry in candidate.Entries.Values)
        {
            if (!baseline.Entries.TryGetValue(entry.Path, out var before))
            {
                changes.Add(new ChangedFile(entry.Path, ChangeKind.Added, null, entry.Hash, null, entry.Length, isBinary(entry.Hash), null));
            }
            else if (!string.Equals(before.Hash, entry.Hash, StringComparison.Ordinal) || before.Kind != entry.Kind)
            {
                changes.Add(new ChangedFile(
                    entry.Path, ChangeKind.Modified, before.Hash, entry.Hash, before.Length, entry.Length,
                    isBinary(entry.Hash) || isBinary(before.Hash), null));
            }
        }

        foreach (var entry in baseline.Entries.Values)
        {
            if (!candidate.Entries.ContainsKey(entry.Path))
            {
                changes.Add(new ChangedFile(entry.Path, ChangeKind.Deleted, entry.Hash, null, entry.Length, null, isBinary(entry.Hash), null));
            }
        }

        // An added file with exactly the content of a deleted file is reported as its new location.
        var deletedByHash = changes
            .Where(c => c.Kind == ChangeKind.Deleted && c.BaselineLength > 0)
            .GroupBy(c => c.BaselineHash!, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => new Queue<string>(g.Select(c => c.Path).OrderBy(p => p, StringComparer.Ordinal)), StringComparer.Ordinal);

        for (var i = 0; i < changes.Count; i++)
        {
            var change = changes[i];
            if (change.Kind == ChangeKind.Added
                && change.CandidateHash is not null
                && deletedByHash.TryGetValue(change.CandidateHash, out var sources)
                && sources.Count > 0)
            {
                changes[i] = change with { RenamedFrom = sources.Dequeue() };
            }
        }

        changes.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        return new ChangeSet(changes);
    }

    private static string ComputeFingerprint(IEnumerable<ManifestEntry> entries)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var separator = new byte[] { 0 };
        var newline = new byte[] { (byte)'\n' };
        foreach (var entry in entries.OrderBy(e => e.Path, StringComparer.Ordinal))
        {
            // Every field is terminated, so no two different entries produce the same bytes.
            hash.AppendData(Encoding.UTF8.GetBytes(entry.Path));
            hash.AppendData(separator);
            hash.AppendData(entry.Kind == EntryKind.Symlink ? "l"u8 : "f"u8);
            hash.AppendData(separator);
            hash.AppendData(Encoding.ASCII.GetBytes(entry.Length.ToString(CultureInfo.InvariantCulture)));
            hash.AppendData(separator);
            hash.AppendData(Encoding.ASCII.GetBytes(entry.Hash));
            hash.AppendData(newline);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }
}
