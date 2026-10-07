namespace Yav.Console.Install;

/// <summary>
/// A list of directories as Windows keeps it in PATH: parts separated by semicolons. It is changed as text:
/// parts are neither reordered nor rewritten, and empty parts and %VARIABLES% stay as they are.
/// </summary>
public static class PathList
{
    /// <summary>True when one of the parts names the directory, however it is spelled.</summary>
    public static bool Contains(string path, string directory)
    {
        var wanted = Comparable(directory);
        return path.Split(';').Any(part => Comparable(part) == wanted);
    }

    /// <summary>The text with the directory added at the end, unless one of the parts names it already.</summary>
    public static string WithEntry(string path, string directory)
    {
        if (Contains(path, directory))
        {
            return path;
        }

        if (path.Length == 0)
        {
            return directory;
        }

        return path.EndsWith(';') ? path + directory : path + ";" + directory;
    }

    /// <summary>The text without the parts that name the directory. Every other part stays exactly as it was.</summary>
    public static string WithoutEntry(string path, string directory)
    {
        // The parts that stay are joined as they were split, empty ones included: a text without the
        // directory comes back unchanged.
        var wanted = Comparable(directory);
        return string.Join(';', path.Split(';').Where(part => Comparable(part) != wanted));
    }

    /// <summary>How Windows would find a directory by a part: without quotes, blanks and closing backslashes, in any case.</summary>
    internal static string Comparable(string part)
    {
        var expanded = Environment.ExpandEnvironmentVariables(part.Trim().Trim('"'));
        return expanded.Length == 0 ? string.Empty : expanded.TrimEnd('\\').ToLowerInvariant();
    }
}
