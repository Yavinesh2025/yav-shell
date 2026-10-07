using Yav.Console.Install;

namespace Yav.Tests.Packaging;

/// <summary>
/// yav.exe carries the files that are installed beside it. They have to be the ones of the repository, as they
/// were when the program was built: the license, the notices, the README, the documentation and the examples.
/// </summary>
public class PackageFilesTests
{
    private static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
            {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }

    private static IEnumerable<string> Expected()
    {
        foreach (var file in new[] { "LICENSE.txt", "THIRD-PARTY-NOTICES.md", "README.md" })
        {
            yield return file;
        }

        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repository, "docs"), "*.md"))
        {
            yield return "docs/" + Path.GetFileName(file);
        }

        // The full license texts of what is part of yav.exe go with it.
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repository, "licenses"), "*.txt"))
        {
            yield return "licenses/" + Path.GetFileName(file);
        }

        var examples = Path.Combine(Repository, "examples");
        foreach (var file in Directory.EnumerateFiles(examples, "*", SearchOption.AllDirectories))
        {
            yield return "examples/" + Path.GetRelativePath(examples, file).Replace('\\', '/');
        }
    }

    [Fact]
    public void The_files_installed_beside_the_program_are_those_of_the_repository_named_with_forward_slashes()
    {
        var expected = Expected().Order(StringComparer.Ordinal).ToArray();

        Assert.Equal(expected, PackageFiles.All.Select(f => f.Path).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains("examples/dotnet/yav.project.json", expected);
        Assert.Contains("docs/user-guide.md", expected);
        Assert.Contains("licenses/dotnet-runtime.txt", expected);
        Assert.DoesNotContain(PackageFiles.All, f => f.Path.Contains('\\', StringComparison.Ordinal) || f.Path.EndsWith(".docx", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Each_file_holds_what_the_file_of_the_repository_holds()
    {
        foreach (var file in PackageFiles.All)
        {
            using var stream = file.Open();
            using var copy = new MemoryStream();
            stream.CopyTo(copy);

            Assert.True(
                File.ReadAllBytes(Path.Combine(Repository, file.Path.Replace('/', Path.DirectorySeparatorChar))).AsSpan().SequenceEqual(copy.ToArray()),
                $"{file.Path} in yav.exe differs from the file in the repository.");
        }
    }

    [Fact]
    public void A_file_can_be_read_more_than_once()
    {
        var license = PackageFiles.All.Single(f => f.Path == "LICENSE.txt");

        using var first = new StreamReader(license.Open());
        using var second = new StreamReader(license.Open());

        Assert.Equal(first.ReadToEnd(), second.ReadToEnd());
    }
}
