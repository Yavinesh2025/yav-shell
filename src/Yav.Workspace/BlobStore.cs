using System.Security.Cryptography;

namespace Yav.Workspace;

public sealed record GarbageReport(int Files, long Bytes);

/// <summary>
/// Stores file content under the SHA-256 of its bytes. The same content is stored once no matter how many
/// runs refer to it, and content is verified against its hash whenever it is restored.
/// </summary>
public sealed class BlobStore
{
    private readonly string _root;

    public BlobStore(string root)
    {
        _root = Path.GetFullPath(root);
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    public static string HashFile(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan);
        return Convert.ToHexStringLower(SHA256.HashData(stream));
    }

    public static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public string PathOf(string hash)
    {
        if (hash is not { Length: 64 })
        {
            throw new ArgumentException("A content hash has 64 hexadecimal characters.", nameof(hash));
        }

        foreach (var c in hash)
        {
            if (!(char.IsAsciiDigit(c) || c is >= 'a' and <= 'f'))
            {
                throw new ArgumentException("A content hash has 64 lower-case hexadecimal characters.", nameof(hash));
            }
        }

        return Path.Combine(_root, hash[..2], hash);
    }

    public bool Contains(string hash) => File.Exists(PathOf(hash));

    /// <summary>Copies a file into the store and returns the hash of what was actually copied.</summary>
    public string StoreFile(string sourcePath)
    {
        var temporary = Path.Combine(_root, "incoming-" + Guid.NewGuid().ToString("N"));
        try
        {
            string hash;
            using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 128 * 1024, FileOptions.SequentialScan))
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    target.Write(buffer, 0, read);
                }

                hash = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }

            Commit(temporary, hash);
            return hash;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public string StoreBytes(ReadOnlySpan<byte> bytes)
    {
        var hash = HashBytes(bytes);
        if (Contains(hash))
        {
            return hash;
        }

        var temporary = Path.Combine(_root, "incoming-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var target = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                target.Write(bytes);
            }

            Commit(temporary, hash);
            return hash;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    public byte[] ReadAllBytes(string hash)
    {
        var bytes = File.ReadAllBytes(PathOf(hash));
        if (!string.Equals(HashBytes(bytes), hash, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Stored content {hash[..12]} is damaged: it no longer matches its hash.");
        }

        return bytes;
    }

    /// <summary>The first bytes of the content, for telling text from binary without reading all of it.</summary>
    public byte[] ReadPrefix(string hash, int count)
    {
        var path = PathOf(hash);
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var buffer = new byte[(int)Math.Min(count, stream.Length)];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// Writes the content to the target path. The content is verified first and the target is replaced
    /// in one step, so a reader never sees a partly written file.
    /// </summary>
    public void RestoreTo(string hash, string targetPath)
    {
        var source = PathOf(hash);
        if (!File.Exists(source))
        {
            throw new FileNotFoundException($"Stored content {hash[..12]} is missing.", source);
        }

        var directory = Path.GetDirectoryName(Path.GetFullPath(targetPath))!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, ".yav-" + Guid.NewGuid().ToString("N")[..12] + ".tmp");
        try
        {
            string actual;
            using (var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.Read, 128 * 1024, FileOptions.SequentialScan))
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 128 * 1024))
            using (var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
            {
                var buffer = new byte[128 * 1024];
                int read;
                while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                {
                    hasher.AppendData(buffer, 0, read);
                    output.Write(buffer, 0, read);
                }

                output.Flush(flushToDisk: true);
                actual = Convert.ToHexStringLower(hasher.GetHashAndReset());
            }

            if (!string.Equals(actual, hash, StringComparison.Ordinal))
            {
                throw new InvalidDataException($"Stored content {hash[..12]} is damaged: it no longer matches its hash.");
            }

            if (File.Exists(targetPath))
            {
                var attributes = File.GetAttributes(targetPath);
                if ((attributes & FileAttributes.ReadOnly) != 0)
                {
                    File.SetAttributes(targetPath, attributes & ~FileAttributes.ReadOnly);
                }
            }

            File.Move(temporary, targetPath, overwrite: true);
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    /// <summary>Removes content that nothing refers to any more.</summary>
    public GarbageReport CollectGarbage(IReadOnlySet<string> referenced, TimeSpan? minimumAge = null)
    {
        var files = 0;
        long bytes = 0;
        var cutoff = DateTime.UtcNow - (minimumAge ?? TimeSpan.Zero);
        foreach (var directory in Directory.EnumerateDirectories(_root))
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.Length != 64 || referenced.Contains(name))
                {
                    continue;
                }

                var info = new FileInfo(file);
                if (minimumAge is not null && info.LastWriteTimeUtc > cutoff)
                {
                    continue;
                }

                try
                {
                    var length = info.Length;
                    info.Attributes = FileAttributes.Normal;
                    info.Delete();
                    files++;
                    bytes += length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // In use by another YAV instance. It is collected next time.
                }
            }
        }

        return new GarbageReport(files, bytes);
    }

    private void Commit(string temporary, string hash)
    {
        var final = PathOf(hash);
        if (File.Exists(final))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(final)!);
        try
        {
            File.Move(temporary, final, overwrite: false);
            // Stored content is never edited in place.
            File.SetAttributes(final, FileAttributes.ReadOnly);
        }
        catch (IOException) when (File.Exists(final))
        {
            // Another writer stored the same content first.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
