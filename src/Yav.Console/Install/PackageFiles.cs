namespace Yav.Console.Install;

/// <summary>
/// The files that are installed next to yav.exe: the license, the notices and the full license texts of the
/// components that are part of it, the documentation and the examples. They are resources of yav.exe itself, so
/// that the program is one file.
/// </summary>
public static class PackageFiles
{
    private const string Prefix = "package/";

    public static IReadOnlyList<PackageFile> All { get; } = Read();

    private static List<PackageFile> Read()
    {
        var assembly = typeof(PackageFiles).Assembly;
        return assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(Prefix, StringComparison.Ordinal))
            .Select(name => new PackageFile(
                // A directory below examples is named with the separator of the machine that built the program.
                name[Prefix.Length..].Replace('\\', '/'),
                () => assembly.GetManifestResourceStream(name)!))
            .OrderBy(file => file.Path, StringComparer.Ordinal)
            .ToList();
    }
}
