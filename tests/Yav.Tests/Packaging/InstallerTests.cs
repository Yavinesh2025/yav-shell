using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Yav.Console.Install;
using Yav.Platform.Install;
using Yav.Tests.Support;

namespace Yav.Tests.Packaging;

/// <summary>
/// Where the stand-ins for Windows let an installation go: only into the tests' own directory for temporary files. An
/// installation that would reach anything else - above all %LOCALAPPDATA%\Programs\YavShell of the user who runs the
/// tests - is stopped when it asks whether its program runs, which it does before it writes its first file.
/// </summary>
internal static class TestPlaces
{
    private static readonly string Root = LongPath.Of(Path.Combine(Path.GetTempPath(), "yav-tests"));

    /// <summary>True when the path lies inside the tests' own directory.</summary>
    public static bool Inside(string path) =>
        LongPath.Of(path).StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Records and throws when the path lies elsewhere. Recorded as well, because the start-up offer turns every failure
    /// into a message: the setup asserts at its end that nothing was recorded.
    /// </summary>
    public static void Require(string path, string what, List<string> violations)
    {
        if (!Inside(path))
        {
            var violation = $"{what} {path} lies outside of {Root}.";
            violations.Add(violation);
            throw new InvalidOperationException("Tripwire: " + violation);
        }
    }

    /// <summary>The PATH entries an installation added, each of which must lie inside the tests' own directory.</summary>
    public static void RequireAdded(string before, string after, List<string> violations)
    {
        var earlier = before.Split(';').ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var part in after.Split(';').Where(part => part.Length > 0 && !earlier.Contains(part)))
        {
            Require(part.Trim('"'), "The PATH entry", violations);
        }
    }
}

/// <summary>
/// Stands for the registry and the process list of Windows, so that no test reads or changes the PATH or the
/// entries under "Installed apps" of the user who runs the tests. Its default directory is one of the test's own,
/// and nothing outside the tests' directory may be installed (see <see cref="TestPlaces"/>).
/// </summary>
internal sealed class FakeInstallSystem : IInstallSystem
{
    private readonly string _defaultDirectory = string.Empty;

    /// <summary>Where an installation that names no directory goes, when none is registered: a directory of the test.</summary>
    public required string DefaultDirectory
    {
        get => _defaultDirectory;
        init
        {
            TestPlaces.Require(value, "The default directory", Violations);
            _defaultDirectory = value;
        }
    }

    /// <summary>What the tripwires caught. Empty at the end of every test.</summary>
    public List<string> Violations { get; } = [];

    public UserPath CurrentPath { get; set; } = new(@"C:\Windows\System32;%USERPROFILE%\bin", Expandable: true);

    /// <summary>Thrown when the PATH is read, as Windows would for a PATH it cannot give as text.</summary>
    public Exception? PathFailure { get; set; }

    public int PathReads { get; private set; }

    public List<UserPath> PathWrites { get; } = [];

    public int Announcements { get; private set; }

    public InstallRegistration? Registration { get; set; }

    public (InstallRegistration Registration, string Executable, long Bytes)? Registered { get; private set; }

    public int RegistrationDeletes { get; private set; }

    /// <summary>The processes that run a program file, by the long form of its path.</summary>
    public Dictionary<string, int[]> Running { get; } = new(StringComparer.OrdinalIgnoreCase);

    public UserPath ReadUserPath()
    {
        PathReads++;
        return PathFailure is { } failure ? throw failure : CurrentPath;
    }

    public void WriteUserPath(UserPath path)
    {
        TestPlaces.RequireAdded(CurrentPath.Value, path.Value, Violations);
        PathWrites.Add(path);
        CurrentPath = path;
    }

    public void AnnounceEnvironmentChange() => Announcements++;

    public InstallRegistration? ReadRegistration() => Registration;

    public void WriteRegistration(InstallRegistration registration, string executable, long installedBytes)
    {
        TestPlaces.Require(registration.InstallLocation, "The registered directory", Violations);
        TestPlaces.Require(executable, "The registered program", Violations);
        Registration = registration;
        Registered = (registration, executable, installedBytes);
    }

    public void DeleteRegistration()
    {
        Registration = null;
        RegistrationDeletes++;
    }

    public IReadOnlyList<int> ProcessesRunning(string executable)
    {
        // Asked before an installation or a removal changes anything: the place it is about to change is checked here.
        TestPlaces.Require(executable, "The program", Violations);
        return Running.TryGetValue(LongPath.Of(executable), out var ids) ? ids : [];
    }
}

/// <summary>
/// A program to install, the files that go with it, a data directory and an installation directory, all in a
/// temporary directory whose name has blanks, brackets and letters of other alphabets.
/// </summary>
internal sealed class InstallSetup : IDisposable
{
    private readonly TempDirectory _root = new("install [1] ünï");

    public InstallSetup()
    {
        Directory = _root.File("Programs [2] 日本/YavShell");
        Data = _root.CreateDirectory("data");
        Program = WriteProgram("download/yav.exe", "yav " + Fixtures.ProductVersion);
        System = new FakeInstallSystem { DefaultDirectory = _root.File("Default Programs/YavShell") };
        Installer = new Installer(System, Fixtures.ProductVersion, Data, (exe, _) => Task.FromResult(
            File.Exists(exe) ? new VersionAnswer(File.ReadAllText(exe)) : new VersionAnswer(null, "could not be started: it is not there")));
    }

    public FakeInstallSystem System { get; }

    public Installer Installer { get; }

    /// <summary>Where the tests install. It does not exist before the first installation.</summary>
    public string Directory { get; }

    public string Data { get; }

    /// <summary>The program to install. What it says when it is asked for its version is its whole content.</summary>
    public string Program { get; }

    public List<PackageFile> Files { get; } =
    [
        Text("LICENSE.txt", "license"),
        Text("docs/user-guide.md", "guide"),
        Text("examples/dotnet/yav.project.json", "{ }"),
    ];

    public string Installed(string relative) => Path.Combine(Directory, relative.Replace('/', Path.DirectorySeparatorChar));

    public string Root(string relative) => _root.File(relative);

    public string WriteProgram(string relative, string content) => _root.Write(relative, content);

    public static PackageFile Text(string path, string content) => new(path, () => new MemoryStream(Encoding.UTF8.GetBytes(content)));

    public Task<InstallOutcome> InstallAsync(InstallOptions? options = null, string? program = null) =>
        Installer.InstallAsync(program ?? Program, Files, options ?? new InstallOptions { Directory = Directory }, CancellationToken.None);

    public InstallManifest Manifest() =>
        JsonSerializer.Deserialize<InstallManifest>(File.ReadAllText(Installed(Installer.ManifestName)), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

    public void Dispose()
    {
        _root.Dispose();
        Assert.Empty(System.Violations);
    }
}

/// <summary>The installation of YAV Shell for the current user, and its removal.</summary>
public class InstallerTests
{
    private static string Sha256(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    public async Task A_new_installation_puts_the_program_and_its_files_into_the_directory_and_names_them_in_a_manifest()
    {
        using var setup = new InstallSetup();

        var outcome = await setup.InstallAsync();

        Assert.Equal(File.ReadAllBytes(setup.Program), File.ReadAllBytes(setup.Installed("yav.exe")));
        Assert.Equal("license", File.ReadAllText(setup.Installed("LICENSE.txt")));
        Assert.Equal("guide", File.ReadAllText(setup.Installed("docs/user-guide.md")));
        Assert.Equal("{ }", File.ReadAllText(setup.Installed("examples/dotnet/yav.project.json")));

        var manifest = setup.Manifest();
        Assert.Equal("YAV Shell", manifest.Product);
        Assert.Equal(Fixtures.ProductVersion, manifest.Version);
        Assert.Equal(
            ["LICENSE.txt", "docs/user-guide.md", "examples/dotnet/yav.project.json", "yav.exe"],
            manifest.Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToArray());
        foreach (var file in manifest.Files)
        {
            Assert.Equal(Sha256(setup.Installed(file.Path)), file.Sha256);
            Assert.Equal(new FileInfo(setup.Installed(file.Path)).Length, file.Bytes);
        }

        // Nothing is left of the temporary files a file was written through.
        Assert.DoesNotContain(Directory.EnumerateFiles(setup.Directory, "*", SearchOption.AllDirectories), f => f.Contains(".partial", StringComparison.Ordinal));
        Assert.Equal(LongPath.Of(setup.Directory), outcome.Directory);
        Assert.Equal(Path.Combine(LongPath.Of(setup.Directory), "yav.exe"), outcome.Executable);
        Assert.Null(outcome.EarlierVersion);
        Assert.True(outcome.ProgramCopied);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_directory_is_added_at_the_end_of_the_path_which_keeps_its_kind_and_windows_is_told(bool expandable)
    {
        using var setup = new InstallSetup();
        setup.System.CurrentPath = new UserPath(@"C:\Windows;%USERPROFILE%\bin;", expandable);

        var outcome = await setup.InstallAsync();

        Assert.Equal(PathChange.Added, outcome.Path);
        var written = Assert.Single(setup.System.PathWrites);
        Assert.Equal(@"C:\Windows;%USERPROFILE%\bin;" + LongPath.Of(setup.Directory), written.Value);
        Assert.Equal(expandable, written.Expandable);
        Assert.Equal(1, setup.System.Announcements);
    }

    [Fact]
    public async Task A_directory_that_is_on_the_path_already_is_neither_added_again_nor_announced()
    {
        using var setup = new InstallSetup();
        // In other letters, in quotes and with a closing backslash, it is still the same directory to Windows.
        setup.System.CurrentPath = new UserPath(@"C:\Windows;""" + LongPath.Of(setup.Directory).ToUpperInvariant() + @"\""", Expandable: true);

        var outcome = await setup.InstallAsync();

        Assert.Equal(PathChange.WasThere, outcome.Path);
        Assert.Empty(setup.System.PathWrites);
        Assert.Equal(0, setup.System.Announcements);
    }

    [Fact]
    public async Task The_entry_under_installed_apps_names_the_directory_the_version_the_program_and_the_size()
    {
        using var setup = new InstallSetup();

        var outcome = await setup.InstallAsync();

        Assert.True(outcome.Registered);
        var (registration, executable, bytes) = setup.System.Registered!.Value;
        Assert.Equal(LongPath.Of(setup.Directory), registration.InstallLocation);
        Assert.Equal(Fixtures.ProductVersion, registration.DisplayVersion);
        Assert.Equal(outcome.Executable, executable);
        Assert.Equal(setup.Manifest().Files.Sum(f => f.Bytes), bytes);
    }

    [Fact]
    public async Task Without_the_path_and_without_installed_apps_neither_is_read_nor_changed()
    {
        using var setup = new InstallSetup();
        setup.System.PathFailure = new InvalidOperationException("The PATH was read although nobody asked for it to be changed.");

        var outcome = await setup.InstallAsync(new InstallOptions { Directory = setup.Directory, AddToPath = false, Register = false });

        Assert.Equal(PathChange.NotAsked, outcome.Path);
        Assert.False(outcome.Registered);
        Assert.Equal(0, setup.System.PathReads);
        Assert.Null(setup.System.Registered);
        Assert.True(File.Exists(setup.Installed("yav.exe")));
    }

    [Fact]
    public async Task Without_a_directory_the_one_installed_apps_names_is_used()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();

        var outcome = await setup.InstallAsync(new InstallOptions());

        Assert.Equal(LongPath.Of(setup.Directory), outcome.Directory);
        Assert.Equal(Fixtures.ProductVersion, outcome.EarlierVersion);
    }

    [Fact]
    public async Task Installing_again_replaces_a_damaged_or_read_only_file_and_keeps_the_files_and_folders_of_the_user()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        File.WriteAllText(setup.Installed("docs/user-guide.md"), "damaged");
        File.WriteAllText(setup.Installed("LICENSE.txt"), "changed and made read-only");
        File.SetAttributes(setup.Installed("LICENSE.txt"), FileAttributes.ReadOnly);
        File.WriteAllText(setup.Installed("my notes [1].txt"), "mine");
        File.WriteAllText(setup.Installed("docs/mine.md"), "mine too");
        System.IO.Directory.CreateDirectory(setup.Installed("my empty folder"));

        await setup.InstallAsync();

        Assert.Equal("guide", File.ReadAllText(setup.Installed("docs/user-guide.md")));
        Assert.Equal("license", File.ReadAllText(setup.Installed("LICENSE.txt")));
        Assert.Equal("mine", File.ReadAllText(setup.Installed("my notes [1].txt")));
        Assert.Equal("mine too", File.ReadAllText(setup.Installed("docs/mine.md")));
        Assert.True(System.IO.Directory.Exists(setup.Installed("my empty folder")));
        Assert.DoesNotContain(setup.Manifest().Files, f => f.Path.Contains("mine", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Installing_a_new_version_removes_what_only_the_earlier_one_had_and_says_which_version_that_was()
    {
        using var setup = new InstallSetup();
        setup.Files.Add(InstallSetup.Text("docs/old/only-in-the-old-version.md", "old"));
        setup.Files.Add(InstallSetup.Text("examples/node/yav.project.json", "{ }"));
        await setup.InstallAsync();
        File.WriteAllText(setup.Installed("examples/node/mine.json"), "mine");
        var manifest = setup.Manifest();
        File.WriteAllText(setup.Installed(Installer.ManifestName), JsonSerializer.Serialize(manifest with { Version = "0.1.9" }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        setup.Files.RemoveAll(f => f.Path.Contains("old", StringComparison.Ordinal) || f.Path.Contains("node", StringComparison.Ordinal));

        var outcome = await setup.InstallAsync();

        Assert.Equal("0.1.9", outcome.EarlierVersion);
        Assert.False(File.Exists(setup.Installed("docs/old/only-in-the-old-version.md")));
        Assert.False(System.IO.Directory.Exists(setup.Installed("docs/old")));
        Assert.False(File.Exists(setup.Installed("examples/node/yav.project.json")));

        // The folder holds a file of the user, so it stays.
        Assert.Equal("mine", File.ReadAllText(setup.Installed("examples/node/mine.json")));
        Assert.Equal(4, setup.Manifest().Files.Count);
    }

    [Theory]
    [InlineData("a file")]
    [InlineData("an empty folder")]
    public async Task A_directory_that_holds_something_else_is_refused_and_left_as_it_was(string what)
    {
        using var setup = new InstallSetup();
        System.IO.Directory.CreateDirectory(setup.Directory);
        if (what == "a file")
        {
            File.WriteAllText(setup.Installed("yav.exe"), "a program of the user");
        }
        else
        {
            System.IO.Directory.CreateDirectory(setup.Installed("something"));
        }

        var refused = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync());

        Assert.Contains("was not created by the installation of YAV Shell. Nothing was changed", refused.Message, StringComparison.Ordinal);
        Assert.Single(System.IO.Directory.EnumerateFileSystemEntries(setup.Directory));
        if (what == "a file")
        {
            Assert.Equal("a program of the user", File.ReadAllText(setup.Installed("yav.exe")));
        }

        Assert.Equal(0, setup.System.PathReads);
        Assert.Null(setup.System.Registered);
    }

    [Theory]
    [InlineData("this is not JSON")]
    [InlineData("{\"product\":\"Another Program\",\"version\":\"1.0.0\",\"installedAt\":\"2026-10-01T00:00:00+00:00\",\"files\":[]}")]
    [InlineData("{\"product\":\"YAV Shell\",\"version\":\"0.1.1\",\"installedAt\":\"2026-10-01T00:00:00+00:00\"}")]
    public async Task A_manifest_that_cannot_be_read_is_not_taken_for_an_installation(string content)
    {
        using var setup = new InstallSetup();
        System.IO.Directory.CreateDirectory(setup.Directory);
        File.WriteAllText(setup.Installed(Installer.ManifestName), content);
        File.WriteAllText(setup.Installed("yav.exe"), "someone's program");

        var refused = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync());
        var notRemoved = Assert.Throws<InstallException>(() => setup.Installer.Uninstall(setup.Directory, setup.Program));

        Assert.Contains("cannot be read, so it is not known which files", refused.Message, StringComparison.Ordinal);
        Assert.Contains("does not hold an installation of YAV Shell", notRemoved.Message, StringComparison.Ordinal);
        Assert.Equal("someone's program", File.ReadAllText(setup.Installed("yav.exe")));
        Assert.Equal(content, File.ReadAllText(setup.Installed(Installer.ManifestName)));
    }

    [Theory]
    [InlineData("data")]
    [InlineData("data/programs")]
    [InlineData("")]
    public async Task The_data_directory_can_neither_be_the_installation_directory_nor_lie_in_it_or_around_it(string relative)
    {
        using var setup = new InstallSetup();
        var directory = relative.Length == 0 ? setup.Root(string.Empty) : setup.Root(relative);

        var refused = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync(new InstallOptions { Directory = directory }));

        Assert.Contains("is where YAV keeps your data, or holds it. Nothing was changed", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(directory, "yav.exe")));
        Assert.Equal(0, setup.System.PathReads);
    }

    [ShortNamesFact]
    public async Task The_data_directory_is_recognized_by_its_short_name_as_well()
    {
        using var setup = new InstallSetup();
        var shortName = WindowsNames.Short(setup.Data);

        // The path of the directory for temporary files may be a short one itself: it is the long form that differs.
        Assert.NotEqual(WindowsNames.Long(setup.Data), shortName, StringComparer.OrdinalIgnoreCase);

        await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync(new InstallOptions { Directory = shortName }));
        await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync(new InstallOptions { Directory = Path.Combine(shortName, "inside") }));

        Assert.False(File.Exists(Path.Combine(setup.Data, "yav.exe")));
    }

    [ShortNamesFact]
    public async Task An_installation_named_by_its_short_name_is_kept_under_its_long_name()
    {
        using var setup = new InstallSetup();
        System.IO.Directory.CreateDirectory(setup.Directory);
        var shortName = WindowsNames.Short(setup.Directory);

        var outcome = await setup.InstallAsync(new InstallOptions { Directory = shortName });

        Assert.Equal(WindowsNames.Long(setup.Directory), outcome.Directory);
        Assert.Equal(outcome.Directory, setup.System.Registration!.InstallLocation);
        Assert.EndsWith(";" + outcome.Directory, setup.System.CurrentPath.Value, StringComparison.Ordinal);
        Assert.True(setup.Installer.IsInstalledProgram(Path.Combine(shortName, "yav.exe")));
    }

    [Fact]
    public async Task Nothing_is_installed_or_removed_while_yav_runs_from_the_directory()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        File.WriteAllText(setup.Installed("docs/user-guide.md"), "damaged");
        setup.System.Running[LongPath.Of(setup.Installed("yav.exe"))] = [4242];
        var writes = setup.System.PathWrites.Count;

        var notInstalled = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync());
        var notRemoved = Assert.Throws<InstallException>(() => setup.Installer.Uninstall(setup.Directory, setup.Program));

        Assert.Contains("(process 4242), so it cannot be installed now. Nothing was changed", notInstalled.Message, StringComparison.Ordinal);
        Assert.Contains("(process 4242), so it cannot be removed now. Nothing was changed", notRemoved.Message, StringComparison.Ordinal);
        Assert.Equal("damaged", File.ReadAllText(setup.Installed("docs/user-guide.md")));
        Assert.True(File.Exists(setup.Installed("yav.exe")));
        Assert.Equal(writes, setup.System.PathWrites.Count);
        Assert.NotNull(setup.System.Registration);
    }

    [Fact]
    public async Task The_process_that_installs_does_not_count_as_one_that_runs_the_program()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        setup.System.Running[LongPath.Of(setup.Installed("yav.exe"))] = [Environment.ProcessId];

        await setup.InstallAsync(program: setup.Installed("yav.exe"));
        var outcome = setup.Installer.Uninstall(setup.Directory, setup.Program);

        Assert.True(outcome.DirectoryRemoved);
    }

    [Fact]
    public async Task Started_from_the_installed_program_the_installation_is_repaired_without_copying_the_program()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        var program = File.ReadAllBytes(setup.Installed("yav.exe"));
        File.Delete(setup.Installed("docs/user-guide.md"));
        setup.System.CurrentPath = new UserPath(@"C:\Windows", Expandable: true);
        setup.System.Registration = null;

        var outcome = await setup.InstallAsync(new InstallOptions { Directory = setup.Directory }, program: setup.Installed("yav.exe"));

        Assert.False(outcome.ProgramCopied);
        Assert.Equal(program, File.ReadAllBytes(setup.Installed("yav.exe")));
        Assert.Equal("guide", File.ReadAllText(setup.Installed("docs/user-guide.md")));
        Assert.Equal(PathChange.Added, outcome.Path);
        Assert.NotNull(setup.System.Registration);
        Assert.Equal(Sha256(setup.Installed("yav.exe")), setup.Manifest().Files.Single(f => f.Path == "yav.exe").Sha256);
    }

    [Fact]
    public async Task A_program_that_does_not_start_after_it_was_copied_changes_neither_the_path_nor_installed_apps()
    {
        using var setup = new InstallSetup();
        var broken = setup.WriteProgram("download/broken/yav.exe", "this is no program");

        var failed = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync(program: broken));

        Assert.Contains("--version said 'this is no program' instead of 'yav " + Fixtures.ProductVersion + "'", failed.Message, StringComparison.Ordinal);
        Assert.Contains($"'yav uninstall --dir \"{LongPath.Of(setup.Directory)}\"' removes them. The PATH and \"Installed apps\" were not changed.", failed.Message, StringComparison.Ordinal);
        Assert.Empty(setup.System.PathWrites);
        Assert.Null(setup.System.Registered);

        // What was written can be removed.
        var removed = setup.Installer.Uninstall(setup.Directory, setup.Program);
        Assert.True(removed.DirectoryRemoved);
    }

    [Fact]
    public async Task A_second_installation_beside_a_registered_one_is_refused_unless_it_is_not_registered_either()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        var elsewhere = setup.Root("elsewhere/YavShell");

        var refused = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync(new InstallOptions { Directory = elsewhere }));
        var copy = await setup.InstallAsync(new InstallOptions { Directory = elsewhere, AddToPath = false, Register = false });

        Assert.Contains($"YAV Shell {Fixtures.ProductVersion} is installed in {LongPath.Of(setup.Directory)}. Nothing was changed", refused.Message, StringComparison.Ordinal);
        Assert.Equal(LongPath.Of(elsewhere), copy.Directory);
        Assert.Equal(LongPath.Of(setup.Directory), setup.System.Registration!.InstallLocation);
    }

    [Fact]
    public async Task An_entry_under_installed_apps_whose_program_is_gone_is_replaced()
    {
        using var setup = new InstallSetup();
        setup.System.Registration = new InstallRegistration(setup.Root("gone/YavShell"), "0.1.0");

        Assert.Null(setup.Installer.Current());
        var outcome = await setup.InstallAsync();

        Assert.Equal(LongPath.Of(setup.Directory), setup.System.Registration!.InstallLocation);
        Assert.True(setup.Installer.IsInstalledProgram(outcome.Executable));
        Assert.False(setup.Installer.IsInstalledProgram(setup.Program));
    }

    [Fact]
    public async Task A_path_that_is_not_text_stops_the_installation_before_anything_is_written()
    {
        using var setup = new InstallSetup();
        setup.System.PathFailure = new InvalidDataException("The PATH of the user account is stored as MultiString, not as text, so YAV leaves it as it is.");

        var refused = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync());

        Assert.Contains("The PATH of your account could not be read: The PATH of the user account is stored as MultiString", refused.Message, StringComparison.Ordinal);
        Assert.EndsWith("Nothing was changed.", refused.Message, StringComparison.Ordinal);
        Assert.False(System.IO.Directory.Exists(setup.Directory));
        Assert.Null(setup.System.Registered);
    }

    [Fact]
    public async Task An_installation_that_failed_halfway_can_be_completed_and_removed()
    {
        using var setup = new InstallSetup();
        var good = setup.Files.ToList();
        setup.Files.Insert(1, new PackageFile("docs/adapters.md", () => throw new IOException("The disk is full.")));

        var failed = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync());

        Assert.Contains("could not be completed: The disk is full.", failed.Message, StringComparison.Ordinal);
        Assert.Contains($"'yav install --dir \"{LongPath.Of(setup.Directory)}\"' again completes the installation", failed.Message, StringComparison.Ordinal);
        Assert.Contains("docs/adapters.md", setup.Manifest().Files.Select(f => f.Path));
        Assert.Contains("examples/dotnet/yav.project.json", setup.Manifest().Files.Select(f => f.Path));
        Assert.Empty(setup.System.PathWrites);

        setup.Files.Clear();
        setup.Files.AddRange(good);
        var completed = await setup.InstallAsync();

        Assert.Equal(PathChange.Added, completed.Path);
        Assert.Equal(4, setup.Manifest().Files.Count);
        Assert.True(setup.Installer.Uninstall(setup.Directory, setup.Program).DirectoryRemoved);
    }

    [Fact]
    public async Task An_interrupted_first_installation_is_no_stranger_to_the_next_one()
    {
        using var setup = new InstallSetup();
        setup.Files.Add(new PackageFile("examples/broken.json", () => throw new UnauthorizedAccessException("Access is denied.")));
        await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync());
        setup.Files.RemoveAt(setup.Files.Count - 1);

        await setup.InstallAsync();

        Assert.Equal(4, setup.Manifest().Files.Count);
    }

    [Fact]
    public async Task Removing_takes_the_files_of_the_installation_its_path_entry_its_entry_under_installed_apps_and_the_directory()
    {
        using var setup = new InstallSetup();
        setup.System.CurrentPath = new UserPath(@"C:\a;%USERPROFILE%\bin;;C:\b", Expandable: false);
        await setup.InstallAsync();

        var outcome = setup.Installer.Uninstall(null, setup.Program);

        Assert.True(outcome.DirectoryRemoved);
        Assert.False(System.IO.Directory.Exists(setup.Directory));
        Assert.Equal(PathChange.Removed, outcome.Path);
        Assert.Equal(new UserPath(@"C:\a;%USERPROFILE%\bin;;C:\b", Expandable: false), setup.System.CurrentPath);
        Assert.Equal(2, setup.System.Announcements);
        Assert.True(outcome.RegistrationRemoved);
        Assert.Null(setup.System.Registration);
        Assert.Null(outcome.ProgramLeftAt);

        // The data of the user is not the installation's.
        Assert.True(System.IO.Directory.Exists(setup.Data));
        Assert.True(File.Exists(setup.Program));
    }

    [Fact]
    public async Task Removing_keeps_what_the_installation_did_not_put_there_and_with_it_the_directory()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        File.WriteAllText(setup.Installed("docs/my own [file].txt"), "mine");
        System.IO.Directory.CreateDirectory(setup.Installed("an empty folder of mine"));

        var outcome = setup.Installer.Uninstall(setup.Directory, setup.Program);

        Assert.False(outcome.DirectoryRemoved);
        Assert.Equal(
            ["an empty folder of mine", Path.Combine("docs", "my own [file].txt")],
            System.IO.Directory.EnumerateFileSystemEntries(setup.Directory, "*", SearchOption.AllDirectories)
                .Select(entry => Path.GetRelativePath(setup.Directory, entry))
                .Where(entry => entry != "docs")
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.False(System.IO.Directory.Exists(setup.Installed("examples")));
        Assert.True(outcome.RegistrationRemoved);
    }

    [Fact]
    public async Task Removing_leaves_an_entry_under_installed_apps_that_names_another_directory_alone()
    {
        using var setup = new InstallSetup();
        var copy = setup.Root("a copy/YavShell");
        await setup.InstallAsync();
        await setup.InstallAsync(new InstallOptions { Directory = copy, AddToPath = false, Register = false });

        var outcome = setup.Installer.Uninstall(copy, setup.Program);

        Assert.True(outcome.DirectoryRemoved);
        Assert.False(outcome.RegistrationRemoved);
        Assert.Equal(LongPath.Of(setup.Directory), setup.System.Registration!.InstallLocation);
        Assert.Equal(PathChange.WasNotThere, outcome.Path);
        Assert.True(File.Exists(setup.Installed("yav.exe")));
    }

    [Fact]
    public void Nothing_is_removed_from_a_directory_that_holds_no_installation()
    {
        using var setup = new InstallSetup();
        System.IO.Directory.CreateDirectory(setup.Directory);
        File.WriteAllText(setup.Installed("yav.exe"), "something of the user");

        var refused = Assert.Throws<InstallException>(() => setup.Installer.Uninstall(setup.Directory, setup.Program));

        Assert.Contains("does not hold an installation of YAV Shell", refused.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was removed.", refused.Message, StringComparison.Ordinal);
        Assert.Equal("something of the user", File.ReadAllText(setup.Installed("yav.exe")));
        Assert.Equal(0, setup.System.PathReads);
    }

    [Fact]
    public void Without_an_installation_removing_says_that_nothing_is_installed()
    {
        using var setup = new InstallSetup();

        var refused = Assert.Throws<InstallException>(() => setup.Installer.Uninstall(null, setup.Program));

        Assert.Equal("YAV Shell is not installed for this user account, so nothing was removed. Name an installation with --dir.", refused.Message);
    }

    [Fact]
    public async Task An_installation_without_an_entry_under_installed_apps_is_removed_by_its_own_program()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync(new InstallOptions { Directory = setup.Directory, AddToPath = false, Register = false });

        var outcome = setup.Installer.Uninstall(null, setup.Installed("yav.exe"));

        Assert.Equal(LongPath.Of(setup.Directory), outcome.Directory);
        Assert.Equal(LongPath.Of(setup.Installed("yav.exe")), outcome.ProgramLeftAt);
        AssertOnlyTheProgramIsLeft(setup, outcome);
    }

    [Fact]
    public async Task The_program_that_runs_stays_where_it_is_unchanged_and_is_named_for_the_caller_to_delete()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        var program = File.ReadAllBytes(setup.Installed("yav.exe"));

        // A program that is one file reads its parts from where it was started: while it runs it is neither deleted nor moved.
        var outcome = setup.Installer.Uninstall(null, setup.Installed("yav.exe"));

        Assert.Equal(LongPath.Of(setup.Installed("yav.exe")), outcome.ProgramLeftAt);
        Assert.Equal(program, File.ReadAllBytes(setup.Installed("yav.exe")));
        AssertOnlyTheProgramIsLeft(setup, outcome);
        Assert.Equal(PathChange.Removed, outcome.Path);
        Assert.True(outcome.RegistrationRemoved);
        Assert.Null(setup.System.Registration);

        // What the caller does once the process has ended: it deletes the program and the manifest beside it, and the
        // directory is empty then.
        File.Delete(outcome.ProgramLeftAt!);
        File.Delete(setup.Installed("yav-install.json"));
        System.IO.Directory.Delete(outcome.Directory);
    }

    [Fact]
    public async Task Beside_the_program_that_runs_the_files_of_the_user_stay_as_well()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        File.WriteAllText(setup.Installed("notes [1].txt"), "mine");

        var outcome = setup.Installer.Uninstall(null, setup.Installed("yav.exe"));

        Assert.False(outcome.DirectoryRemoved);
        Assert.Equal(["notes [1].txt", "yav-install.json", "yav.exe"], Entries(setup));
    }

    /// <summary>
    /// Only the program that runs is left, with a manifest that names only it: every other file of the installation is gone.
    /// </summary>
    private static void AssertOnlyTheProgramIsLeft(InstallSetup setup, UninstallOutcome outcome)
    {
        Assert.False(outcome.DirectoryRemoved);

        // Beside the program that runs stays a manifest that names only it, so that the directory is still an installation.
        Assert.Equal(["yav-install.json", "yav.exe"], Entries(setup));
        Assert.Equal(["yav.exe"], setup.Manifest().Files.Select(file => file.Path).ToArray());
    }

    [Fact]
    public async Task A_directory_that_still_holds_the_program_because_its_deletion_did_not_happen_is_installed_into_again()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        setup.Installer.Uninstall(null, setup.Installed("yav.exe"));

        // The deletion after the end of the process did not happen: the next installation takes the directory as its own.
        var again = await setup.InstallAsync();

        Assert.True(again.ProgramCopied);
        Assert.Equal(Fixtures.ProductVersion, again.EarlierVersion);
        Assert.Equal(File.ReadAllText(setup.Program), File.ReadAllText(setup.Installed("yav.exe")));
        Assert.Equal("guide", File.ReadAllText(setup.Installed("docs/user-guide.md")));
        Assert.Equal(
            ["LICENSE.txt", "docs/user-guide.md", "examples/dotnet/yav.project.json", "yav.exe"],
            setup.Manifest().Files.Select(file => file.Path).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_program_that_was_left_behind_is_removed_by_another_copy_together_with_its_directory()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync(new InstallOptions { Directory = setup.Directory, AddToPath = false, Register = false });
        setup.Installer.Uninstall(setup.Directory, setup.Installed("yav.exe"));

        var outcome = setup.Installer.Uninstall(setup.Directory, setup.Program);

        Assert.Null(outcome.ProgramLeftAt);
        Assert.True(outcome.DirectoryRemoved);
        Assert.False(System.IO.Directory.Exists(setup.Directory));
    }

    private static string[] Entries(InstallSetup setup) =>
        System.IO.Directory.EnumerateFileSystemEntries(setup.Directory).Select(entry => Path.GetFileName(entry)).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public async Task A_path_that_is_not_text_is_left_alone_by_a_removal_which_removes_everything_else()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        setup.System.PathFailure = new InvalidDataException("The PATH of the user account is stored as Binary, not as text, so YAV leaves it as it is.");
        var writes = setup.System.PathWrites.Count;

        var outcome = setup.Installer.Uninstall(null, setup.Program);

        Assert.True(outcome.DirectoryRemoved);
        Assert.Equal(PathChange.WasNotThere, outcome.Path);
        Assert.Equal(writes, setup.System.PathWrites.Count);
        Assert.True(outcome.RegistrationRemoved);
    }

    [Fact]
    public async Task A_manifest_that_names_files_outside_the_directory_removes_nothing_outside_of_it()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        var outside = setup.WriteProgram("Programs [2] 日本/outside.txt", "not the installation's");
        var absolute = setup.WriteProgram("absolute.txt", "not the installation's either");
        var manifest = setup.Manifest();
        var tampered = manifest with
        {
            Files = [.. manifest.Files, new InstalledFile("../outside.txt", 1, "x"), new InstalledFile(absolute, 1, "x"), new InstalledFile("LICENSE.txt:stream", 1, "x")],
        };
        File.WriteAllText(setup.Installed(Installer.ManifestName), JsonSerializer.Serialize(tampered, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

        var outcome = setup.Installer.Uninstall(setup.Directory, setup.Program);

        Assert.True(outcome.DirectoryRemoved);
        Assert.Equal("not the installation's", File.ReadAllText(outside));
        Assert.Equal("not the installation's either", File.ReadAllText(absolute));
    }

    [Fact]
    public async Task A_read_only_file_of_the_installation_is_removed_with_it()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        File.SetAttributes(setup.Installed("docs/user-guide.md"), FileAttributes.ReadOnly);

        var outcome = setup.Installer.Uninstall(setup.Directory, setup.Program);

        Assert.True(outcome.DirectoryRemoved);
    }

    [Fact]
    public async Task Without_a_directory_and_without_an_installation_the_default_directory_of_the_system_is_used()
    {
        using var setup = new InstallSetup();

        var outcome = await setup.InstallAsync(new InstallOptions());
        var removed = setup.Installer.Uninstall(null, setup.Program);

        Assert.Equal(LongPath.Of(setup.System.DefaultDirectory), outcome.Directory);
        Assert.Equal(outcome.Directory, removed.Directory);
        Assert.True(removed.DirectoryRemoved);
        Assert.Equal(LongPath.Of(setup.System.DefaultDirectory), setup.Installer.DirectoryFor(null));
        Assert.Equal(LongPath.Of(setup.Directory), setup.Installer.DirectoryFor(setup.Directory));
    }

    [Fact]
    public async Task The_stand_in_for_windows_stops_an_installation_outside_the_tests_directory_before_it_writes_anything()
    {
        using var setup = new InstallSetup();
        var outside = Path.Combine(Path.GetPathRoot(Path.GetTempPath())!, "yav tripwire " + Guid.NewGuid().ToString("N")[..8]);

        var stopped = await Assert.ThrowsAsync<InvalidOperationException>(() => setup.InstallAsync(new InstallOptions { Directory = outside }));

        Assert.StartsWith("Tripwire: ", stopped.Message, StringComparison.Ordinal);
        Assert.False(System.IO.Directory.Exists(outside));
        Assert.Single(setup.System.Violations);
        setup.System.Violations.Clear();
    }

    [Fact]
    public async Task The_root_of_a_drive_is_refused_before_anything_is_written()
    {
        using var setup = new InstallSetup();
        var root = Path.GetPathRoot(setup.Directory)!;

        var refused = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync(new InstallOptions { Directory = root }));

        Assert.Contains("is the root of a drive. Nothing was changed", refused.Message, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(root, Installer.ManifestName)));
        Assert.Equal(0, setup.System.PathReads);
    }

    [Fact]
    public async Task A_directory_whose_program_could_not_be_started_by_windows_is_refused_before_anything_is_written()
    {
        using var setup = new InstallSetup();
        var directory = setup.Root("deep/" + new string('d', Math.Max(1, 260 - setup.Root("deep/").Length - "/yav.exe".Length)));

        var refused = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync(new InstallOptions { Directory = directory }));

        Assert.Contains("Windows starts no program from a path of 260 characters or more. Nothing was changed", refused.Message, StringComparison.Ordinal);
        Assert.False(System.IO.Directory.Exists(directory));
    }

    [Fact]
    public async Task A_directory_with_a_semicolon_is_not_added_to_the_path_but_can_be_installed_without_it()
    {
        using var setup = new InstallSetup();
        var directory = setup.Root("Programs;YAV");

        var refused = await Assert.ThrowsAsync<InstallException>(() => setup.InstallAsync(new InstallOptions { Directory = directory }));
        var installed = await setup.InstallAsync(new InstallOptions { Directory = directory, AddToPath = false });

        Assert.Contains("holds a ';', which separates the folders of the PATH. Nothing was changed", refused.Message, StringComparison.Ordinal);
        Assert.Equal(PathChange.NotAsked, installed.Path);
        Assert.Empty(setup.System.PathWrites);
    }

    [Fact]
    public async Task The_path_is_read_again_before_it_is_written_so_that_a_change_made_meanwhile_stays()
    {
        using var setup = new InstallSetup();
        var installer = new Installer(setup.System, Fixtures.ProductVersion, setup.Data, (_, _) =>
        {
            // Another program changes the PATH while the installed program is checked.
            setup.System.CurrentPath = new UserPath(setup.System.CurrentPath.Value + @";C:\added meanwhile", Expandable: true);
            return Task.FromResult(new VersionAnswer("yav " + Fixtures.ProductVersion));
        });

        await installer.InstallAsync(setup.Program, setup.Files, new InstallOptions { Directory = setup.Directory }, CancellationToken.None);

        Assert.Equal(@"C:\Windows\System32;%USERPROFILE%\bin;C:\added meanwhile;" + LongPath.Of(setup.Directory), Assert.Single(setup.System.PathWrites).Value);
    }

    [Fact]
    public async Task Why_the_installed_program_gave_no_answer_is_said()
    {
        using var setup = new InstallSetup();
        var installer = new Installer(setup.System, Fixtures.ProductVersion, setup.Data, (_, _) =>
            Task.FromResult(new VersionAnswer(null, "ended with exit code 3: The application was unable to start correctly")));

        var failed = await Assert.ThrowsAsync<InstallException>(() =>
            installer.InstallAsync(setup.Program, setup.Files, new InstallOptions { Directory = setup.Directory }, CancellationToken.None));

        Assert.Contains("--version ended with exit code 3: The application was unable to start correctly. The files stay where they are", failed.Message, StringComparison.Ordinal);
        Assert.Empty(setup.System.PathWrites);
    }

    [Fact]
    public async Task An_installation_stopped_with_control_c_says_what_it_left_and_how_to_complete_or_remove_it()
    {
        using var setup = new InstallSetup();
        using var stop = new CancellationTokenSource();
        setup.Files.Insert(0, new PackageFile("docs/first.md", () =>
        {
            stop.Cancel();
            return new MemoryStream("first"u8.ToArray());
        }));

        var stopped = await Assert.ThrowsAsync<InstallException>(() =>
            setup.Installer.InstallAsync(setup.Program, setup.Files, new InstallOptions { Directory = setup.Directory }, stop.Token));

        Assert.Contains("was stopped before it was complete", stopped.Message, StringComparison.Ordinal);
        Assert.Contains($"'yav uninstall --dir \"{LongPath.Of(setup.Directory)}\"' removes it", stopped.Message, StringComparison.Ordinal);
        Assert.Contains("LICENSE.txt", setup.Manifest().Files.Select(f => f.Path));
        Assert.Empty(setup.System.PathWrites);
        Assert.True(setup.Installer.Uninstall(setup.Directory, setup.Program).DirectoryRemoved);
    }

    [Fact]
    public async Task What_an_interrupted_write_left_behind_keeps_neither_an_installation_nor_a_removal_from_its_directory()
    {
        using var setup = new InstallSetup();

        // Interrupted while the first manifest was written: only its temporary file is there.
        System.IO.Directory.CreateDirectory(setup.Directory);
        File.WriteAllText(setup.Installed(Installer.ManifestName + ".partial"), "{ \"prod");
        await setup.InstallAsync();

        // Interrupted while a file was written over: the temporary file beside it goes with the next installation or removal.
        File.WriteAllText(setup.Installed("docs/user-guide.md.partial-0123abcd"), "half a guide");
        File.WriteAllText(setup.Installed("yav.exe.partial-89abcdef"), "half a program");
        File.WriteAllText(setup.Installed("docs/notes.partial-0123abcd"), "the user's");
        var outcome = setup.Installer.Uninstall(setup.Directory, setup.Program);

        Assert.True(outcome.OtherFilesKept);
        Assert.Equal(["notes.partial-0123abcd"], System.IO.Directory.EnumerateFileSystemEntries(setup.Installed("docs")).Select(entry => Path.GetFileName(entry)).ToArray());
        Assert.Equal(["docs"], Entries(setup));
    }

    [Fact]
    public async Task An_installation_of_yav_shell_0_1_1_is_updated_and_removed()
    {
        using var setup = new InstallSetup();

        // What the PowerShell script of 0.1.1 left: its files, a package-manifest.json that names them, and the entry under "Installed apps".
        foreach (var name in new[] { "yav.exe", "yav.dll", "uninstall.ps1", "docs/user-guide.md" })
        {
            setup.WriteProgram(Path.GetRelativePath(setup.Root(string.Empty), setup.Installed(name)), "0.1.1");
        }

        File.WriteAllText(
            setup.Installed("package-manifest.json"),
            "\uFEFF{ \"product\": \"YAV Shell\", \"version\": \"0.1.1\", \"runtime\": \"win-x64\", \"files\": ["
            + "{ \"path\": \"yav.exe\", \"sha256\": \"x\" }, { \"path\": \"yav.dll\" }, { \"path\": \"uninstall.ps1\" }, { \"path\": \"docs/user-guide.md\" } ] }");
        setup.System.Registration = new InstallRegistration(LongPath.Of(setup.Directory), "0.1.1");

        var outcome = await setup.InstallAsync(new InstallOptions());

        Assert.Equal("0.1.1", outcome.EarlierVersion);
        Assert.Equal(LongPath.Of(setup.Directory), outcome.Directory);
        Assert.Equal(["LICENSE.txt", "docs", "examples", "yav-install.json", "yav.exe"], Entries(setup));
        Assert.Equal("guide", File.ReadAllText(setup.Installed("docs/user-guide.md")));

        // And a 0.1.1 installation is removed as it is, too.
        using var other = new InstallSetup();
        foreach (var name in new[] { "yav.exe", "uninstall.ps1" })
        {
            other.WriteProgram(Path.GetRelativePath(other.Root(string.Empty), other.Installed(name)), "0.1.1");
        }

        File.WriteAllText(other.Installed("package-manifest.json"), "{ \"product\": \"YAV Shell\", \"version\": \"0.1.1\", \"files\": [ { \"path\": \"yav.exe\" }, { \"path\": \"uninstall.ps1\" } ] }");
        Assert.True(other.Installer.Uninstall(other.Directory, other.Program).DirectoryRemoved);
    }

    [Fact]
    public async Task The_manifest_beside_the_program_that_was_left_holds_the_text_of_the_removal_and_a_new_installation_drops_it()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();

        var outcome = setup.Installer.Uninstall(null, setup.Installed("yav.exe"));
        var manifest = File.ReadAllText(setup.Installed(Installer.ManifestName));

        Assert.Matches("^[0-9a-f]{32}$", outcome.Removal);
        Assert.Contains($"\"removal\": \"{outcome.Removal}\"", manifest, StringComparison.Ordinal);
        Assert.False(outcome.OtherFilesKept);

        // The deletion after the end deletes only while the manifest holds that text: an installation in the meantime keeps its files.
        await setup.InstallAsync();
        Assert.DoesNotContain(outcome.Removal!, File.ReadAllText(setup.Installed(Installer.ManifestName)), StringComparison.Ordinal);
        Assert.DoesNotContain("removal", File.ReadAllText(setup.Installed(Installer.ManifestName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_empty_directory_that_another_program_uses_stays_and_the_rest_of_the_removal_goes_on()
    {
        using var setup = new InstallSetup();
        await setup.InstallAsync();
        // A console whose current directory it is, as a console left open in the installation directory.
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        using var console = Process.Start(new ProcessStartInfo(Path.Combine(system, "cmd.exe"), $"/d /c \"echo ready & \"{Path.Combine(system, "PING.EXE")}\" -n 30 127.0.0.1 >nul\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = setup.Directory,
        })!;

        // The process takes its current directory while it starts: once it writes, it holds it.
        Assert.Equal("ready", (await console.StandardOutput.ReadLineAsync())?.Trim());
        UninstallOutcome outcome;
        try
        {
            outcome = setup.Installer.Uninstall(setup.Directory, setup.Program);
        }
        finally
        {
            console.Kill(entireProcessTree: true);
            await console.WaitForExitAsync();
        }

        Assert.False(outcome.DirectoryRemoved);
        Assert.False(outcome.OtherFilesKept);
        Assert.Empty(System.IO.Directory.EnumerateFileSystemEntries(setup.Directory));
        Assert.Equal(PathChange.Removed, outcome.Path);
        Assert.True(outcome.RegistrationRemoved);
    }
}
