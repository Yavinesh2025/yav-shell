namespace Yav.Platform.Install;

/// <summary>
/// The full form of a path, in which every part that exists is named by its long name. Windows can name a file by
/// its short name as well (C:\Users\LONGNA~1\...), and the directory of temporary files is often given that way:
/// two spellings of one directory have to be taken for the same directory, or an installation would not recognize
/// itself, or the data directory, under the other name. Path.GetFullPath gives the long names: where the full path
/// has a '~', .NET asks Windows for the long name of the part that exists and keeps the rest as it was written.
/// The tests that use short names hold it to that.
/// </summary>
public static class LongPath
{
    /// <summary>The full path in its long form, without a closing separator. A volume root such as C:\ keeps its separator.</summary>
    public static string Of(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary>True when both paths name the same file or directory, however each was spelled.</summary>
    public static bool Same(string a, string b) => string.Equals(Of(a), Of(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is <paramref name="directory"/> or lies below it, however each was spelled.</summary>
    public static bool IsSameOrInside(string path, string directory)
    {
        var full = Of(path);
        var parent = Of(directory);
        return string.Equals(full, parent, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(parent.EndsWith(Path.DirectorySeparatorChar) ? parent : parent + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
