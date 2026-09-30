namespace Yav.Platform.Processes;

/// <summary>
/// Finds executables the way Windows does, with one deliberate difference: the working directory is
/// never searched implicitly. A repository that contains a file named like a common tool must not be
/// able to replace that tool. A path written explicitly (for example <c>.\build.cmd</c>) is honored.
/// </summary>
public static class ExecutableResolver
{
    private static readonly string[] DefaultExtensions = [".COM", ".EXE", ".BAT", ".CMD"];

    public static string? Resolve(string command, string? workingDirectory = null, string? pathVariable = null, string? pathExtVariable = null)
    {
        if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return null;
        }

        var extensions = GetExtensions(pathExtVariable ?? Environment.GetEnvironmentVariable("PATHEXT"));
        var explicitPath = command.Contains('\\') || command.Contains('/') || Path.IsPathRooted(command);
        if (explicitPath)
        {
            var basePath = Path.IsPathRooted(command)
                ? command
                : Path.Combine(workingDirectory ?? Environment.CurrentDirectory, command);
            return Probe(Path.GetFullPath(basePath), extensions);
        }

        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var raw in path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var directory = raw.Trim('"');
            if (directory.Length == 0 || !Path.IsPathFullyQualified(directory))
            {
                // A relative PATH entry (including ".") would resolve against the project directory.
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(directory, command);
            }
            catch (ArgumentException)
            {
                continue;
            }

            var found = Probe(candidate, extensions);
            if (found is not null)
            {
                return found;
            }
        }

        return null;
    }

    public static bool IsBatchFile(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) || extension.Equals(".bat", StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsPowerShellScript(string path) =>
        Path.GetExtension(path).Equals(".ps1", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// True for a program of a package (Microsoft Store, MSIX): its app execution alias, or the program itself,
    /// which both lie below a folder named WindowsApps. Windows runs such a program in the job of its package,
    /// and takes it out of the job of the program that started it.
    /// </summary>
    public static bool IsPackaged(string path) =>
        (Path.GetDirectoryName(path) ?? string.Empty)
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Contains("WindowsApps", StringComparer.OrdinalIgnoreCase);

    private static string? Probe(string candidate, string[] extensions)
    {
        var extension = Path.GetExtension(candidate);
        if (extension.Length > 0 && File.Exists(candidate))
        {
            return Path.GetFullPath(candidate);
        }

        foreach (var ext in extensions)
        {
            var withExtension = candidate + ext;
            if (File.Exists(withExtension))
            {
                return Path.GetFullPath(withExtension);
            }
        }

        return null;
    }

    private static string[] GetExtensions(string? pathExt)
    {
        if (string.IsNullOrWhiteSpace(pathExt))
        {
            return DefaultExtensions;
        }

        var parts = pathExt.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(p => p.StartsWith('.'))
            // Only file types CreateProcess or cmd.exe can actually start.
            .Where(p => DefaultExtensions.Contains(p, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        return parts.Length == 0 ? DefaultExtensions : parts;
    }
}
