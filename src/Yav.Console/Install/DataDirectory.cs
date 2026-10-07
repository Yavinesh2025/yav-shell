using Yav.Core.Settings;

namespace Yav.Console.Install;

/// <summary>
/// The data directory of the user as 'yav uninstall --remove-data' sees it. YAV_HOME can name any directory, so
/// before anything is removed it has to be a directory of YAV and nothing else: it holds yav.db, which YAV creates
/// on every start, and nothing YAV does not create.
/// </summary>
public static class DataDirectory
{
    private static readonly Environment.SpecialFolder[] Folders =
    [
        Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
        Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Desktop,
        Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolder.ApplicationData,
        Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.Programs,
    ];

    /// <summary>The directories YAV creates in its data directory (see <see cref="YavPaths"/>).</summary>
    private static readonly string[] OwnDirectories = ["logs", "workspaces", "blobs", "exports", "schemas"];

    /// <summary>
    /// The files YAV creates in its data directory: the database with the files SQLite keeps beside it, the settings
    /// with what saving them (settings.json.tmp-*) and setting an unreadable one aside (settings.unreadable-*.json)
    /// leaves, and the history of the prompt.
    /// </summary>
    private static bool IsOwnFile(string name) =>
        name is "yav.db" or "yav.db-wal" or "yav.db-shm" or "yav.db-journal" or "settings.json"
        || name.StartsWith("settings.json.tmp-", StringComparison.Ordinal)
        || (name.StartsWith("settings.unreadable-", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
        || name.StartsWith("history.txt", StringComparison.Ordinal);

    /// <summary>Why the directory must not be removed as a data directory of YAV. Null when it may be, and when it does not exist.</summary>
    public static string? Problem(string home)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(home));
        if (string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(full) ?? string.Empty), full, StringComparison.OrdinalIgnoreCase))
        {
            return $"{home} is the root of a drive, not a data directory of YAV.";
        }

        foreach (var folder in Folders)
        {
            var special = Environment.GetFolderPath(folder);
            if (special.Length > 0 && string.Equals(Path.TrimEndingDirectorySeparator(special), full, StringComparison.OrdinalIgnoreCase))
            {
                return $"{home} is a folder of Windows or of your user account, not a data directory of YAV.";
            }
        }

        if (!Directory.Exists(full))
        {
            return null;
        }

        if (!File.Exists(Path.Combine(full, "yav.db")))
        {
            return $"{home} holds no yav.db, so it is not taken for a data directory of YAV.";
        }

        // Hidden and system entries count as well. Through a directory that is itself a link, its contents are listed:
        // they are what would be removed.
        foreach (var entry in new DirectoryInfo(full).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            var own = entry is DirectoryInfo
                ? OwnDirectories.Contains(entry.Name, StringComparer.OrdinalIgnoreCase)
                : IsOwnFile(entry.Name.ToLowerInvariant());
            if (!own)
            {
                return $"{home} also holds {entry.Name}, which YAV did not put there, so it is not taken for a data directory of YAV alone. "
                    + "Delete the folder yourself if all of it may go.";
            }
        }

        if (InUse(Path.Combine(full, "yav.db")) is { } use)
        {
            return use;
        }

        return null;
    }

    /// <summary>
    /// Removes the directory with everything in it, after <see cref="Problem"/> was asked again. Files Git keeps read-only
    /// are made writable first. A link to another place is removed as a link: what it points to is never entered. The
    /// database is held while the rest goes, so that no YAV starts on it meanwhile, and it and the settings go last: a
    /// removal that stops part-way leaves a directory that is still recognised as one of YAV, and can be removed again.
    /// </summary>
    /// <exception cref="InstallException">The directory is not one of YAV alone, or another YAV uses it. Nothing was removed then.</exception>
    public static void Remove(string home)
    {
        if (!Directory.Exists(home))
        {
            return;
        }

        if (Problem(home) is { } problem)
        {
            throw new InstallException(problem + " Nothing was removed.");
        }

        var root = new DirectoryInfo(Path.GetFullPath(home));
        var database = Path.Combine(root.FullName, "yav.db");
        var settings = Path.Combine(root.FullName, "settings.json");
        FileStream held;
        try
        {
            held = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new InstallException($"{database} could not be opened alone: {ex.Message} Another YAV Shell may use it; leave it with /exit. Nothing was removed.", ex);
        }

        using (held)
        {
            foreach (var entry in root.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                if (!string.Equals(entry.FullName, database, StringComparison.OrdinalIgnoreCase) && !string.Equals(entry.FullName, settings, StringComparison.OrdinalIgnoreCase))
                {
                    RemoveEntry(entry);
                }
            }
        }

        foreach (var last in new[] { settings, database })
        {
            if (File.Exists(last))
            {
                RemoveEntry(new FileInfo(last));
            }
        }

        root.Attributes &= ~FileAttributes.ReadOnly;
        root.Delete();
    }

    /// <summary>Why the database cannot be removed now. Null when this process can open it alone, so that no other YAV uses it.</summary>
    private static string? InUse(string database)
    {
        try
        {
            using var probe = new FileStream(database, FileMode.Open, FileAccess.Read, FileShare.None);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"{database} is in use or cannot be opened ({ex.Message}). Another YAV Shell may use it; leave it with /exit and try again.";
        }
    }

    private static void RemoveEntry(FileSystemInfo entry)
    {
        if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
        {
            // A link is removed as a link: what it leads to is no data of YAV, and it may lead back into it.
            entry.Delete();
        }
        else if (entry is DirectoryInfo inner)
        {
            RemoveTree(inner);
        }
        else
        {
            entry.Attributes &= ~FileAttributes.ReadOnly;
            entry.Delete();
        }
    }

    private static void RemoveTree(DirectoryInfo directory)
    {
        // Hidden and system entries are part of the data as well.
        foreach (var entry in directory.EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
        {
            RemoveEntry(entry);
        }

        directory.Attributes &= ~FileAttributes.ReadOnly;
        directory.Delete();
    }
}
