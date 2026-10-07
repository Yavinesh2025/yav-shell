using Yav.Console.Cli;
using Yav.Console.Composition;
using Yav.Console.Install;
using Yav.Core.Settings;
using Yav.Platform.Install;
using Yav.Tests.Support;

namespace Yav.Tests.Packaging;

/// <summary>
/// 'yav install' and 'yav uninstall' as the user meets them: what they say, which exit code they end with, and
/// what they leave behind. The registry, the PATH and the Credential Manager are stand-ins; every directory is
/// a new one under the directory for temporary files.
/// </summary>
public class InstallCommandTests
{
    /// <summary>
    /// Stands in for the registry of the user and the processes that run. Its default directory is one of the test's
    /// own, and it stops anything outside the tests' own directories before it is written (see <see cref="TestPlaces"/>).
    /// </summary>
    public sealed class StandInSystem : IInstallSystem
    {
        public StandInSystem(string defaultDirectory)
        {
            TestPlaces.Require(defaultDirectory, "The default directory", Violations);
            DefaultDirectory = defaultDirectory;
        }

        public string DefaultDirectory { get; }

        /// <summary>What the tripwires caught. Empty at the end of every test.</summary>
        public List<string> Violations { get; } = [];

        public UserPath UserPath { get; set; } = new(@"%USERPROFILE%\bin;C:\Windows\system32", Expandable: true);

        public InstallRegistration? Registration { get; set; }

        public List<int> Running { get; } = [];

        public int Announcements { get; private set; }

        public int PathWrites { get; private set; }

        public UserPath ReadUserPath() => UserPath;

        public void WriteUserPath(UserPath path)
        {
            TestPlaces.RequireAdded(UserPath.Value, path.Value, Violations);
            PathWrites++;
            UserPath = path;
        }

        public void AnnounceEnvironmentChange() => Announcements++;

        /// <summary>When set, reading the registration throws it, as a registry the user may not read does.</summary>
        public Exception? ReadFails { get; set; }

        public InstallRegistration? ReadRegistration() => ReadFails is { } failure ? throw failure : Registration;

        public void WriteRegistration(InstallRegistration registration, string executable, long installedBytes)
        {
            TestPlaces.Require(registration.InstallLocation, "The registered directory", Violations);
            TestPlaces.Require(executable, "The registered program", Violations);
            Registration = registration;
        }

        public void DeleteRegistration() => Registration = null;

        public IReadOnlyList<int> ProcessesRunning(string executable)
        {
            // Asked before an installation or a removal changes anything: the place it is about to change is checked here.
            TestPlaces.Require(executable, "The program", Violations);
            return Running;
        }
    }

    /// <summary>The console of a test: what was written, and the answers the user gives, one line each.</summary>
    public sealed class ScriptedConsole
    {
        private readonly Queue<string?> _answers = new();

        public StringWriter Output { get; } = new();

        public StringWriter Error { get; } = new();

        public int LinesRead { get; private set; }

        public ScriptedConsole Answer(params string?[] lines)
        {
            foreach (var line in lines)
            {
                _answers.Enqueue(line);
            }

            return this;
        }

        public Task<string?> ReadLine(CancellationToken cancellationToken)
        {
            LinesRead++;
            return Task.FromResult(_answers.Count > 0 ? _answers.Dequeue() : null);
        }

        public string[] OutputLines => Output.ToString().ReplaceLineEndings("\n").Split('\n');
    }

    /// <summary>A program to install, a place to install it, a data directory, and the stand-ins around them.</summary>
    public sealed class Setup : IDisposable
    {
        private readonly TempDirectory _root = new("install [cmd]");

        public Setup()
        {
            Program = _root.Write("download/yav.exe", "the program");
            Directory = _root.File("Programs/YAV Shell");
            Data = new YavPaths(_root.File("data"));
            Data.EnsureCreated();
            global::System.IO.File.WriteAllText(Data.SettingsFile, "{}");
            global::System.IO.File.WriteAllText(Data.Database, "a database");
            System = new StandInSystem(_root.File("Default Programs/YavShell"));
        }

        public string Program { get; set; }

        public string Directory { get; }

        public YavPaths Data { get; }

        public StandInSystem System { get; }

        public ScriptedConsole Console { get; } = new();

        public MemoryCredentialStore Credentials { get; } = new();

        public List<string> DeletedAfterExit { get; } = [];

        /// <summary>The text of the removal each program was handed over with.</summary>
        public List<string> Removals { get; } = [];

        /// <summary>How many lines the console had read when the program was handed to the deletion after the end.</summary>
        public int? LinesReadWhenDeleted { get; private set; }

        public string File(string relative) => _root.File(relative);

        public string Write(string relative, string content) => _root.Write(relative, content);

        public Installer Installer(string? version = null) => new(
            System, version ?? Fixtures.ProductVersion, Data.Home, (_, _) => Task.FromResult(new VersionAnswer("yav " + (version ?? Fixtures.ProductVersion))));

        /// <param name="named">False: the world names no installation directory, as for the real process; the stand-in's default directory is used then.</param>
        public InstallSurroundings World(
            bool singleFile = true, bool canAsk = true, bool ownWindow = false, string? inheritedPath = null, string? version = null, bool named = true) => new()
        {
            Installer = Installer(version),
            Program = Program,
            IsSingleFile = singleFile,
            Files = [new PackageFile("docs/user-guide.md", () => new MemoryStream("guide"u8.ToArray())), new PackageFile("LICENSE.txt", () => new MemoryStream("license"u8.ToArray()))],
            InstallDirectory = named ? Directory : null,
            Data = Data,
            Credentials = Credentials,
            Output = Console.Output,
            Error = Console.Error,
            CanAsk = canAsk,
            OwnWindow = ownWindow,
            ReadLine = Console.ReadLine,
            InheritedPath = inheritedPath ?? @"C:\Windows\system32",
            DeleteAfterExit = (program, removal) =>
            {
                DeletedAfterExit.Add(program);
                Removals.Add(removal);
                LinesReadWhenDeleted = Console.LinesRead;
            },
        };

        public void Dispose()
        {
            foreach (var parked in DeletedAfterExit.Where(global::System.IO.File.Exists))
            {
                global::System.IO.File.Delete(parked);
            }

            _root.Dispose();
            Assert.Empty(System.Violations);
        }
    }

    private static CliOptions Install(params string[] arguments) => CommandLine.Parse(["install", .. arguments]);

    private static CliOptions Uninstall(params string[] arguments) => CommandLine.Parse(["uninstall", .. arguments]);

    [Fact]
    public async Task A_build_of_the_source_tree_does_not_install_itself_and_says_which_program_does()
    {
        using var setup = new Setup();

        var code = await InstallCommand.InstallAsync(Install(), setup.World(singleFile: false), CancellationToken.None);

        Assert.Equal(5, code);
        Assert.Equal("yav: " + InstallReport.SourceTreeBuild, setup.Console.Error.ToString().Trim());
        Assert.Contains(@"dist\yav.exe", InstallReport.SourceTreeBuild, StringComparison.Ordinal);
        Assert.Equal(string.Empty, setup.Console.Output.ToString());
        Assert.False(Directory.Exists(setup.Directory));
        Assert.Equal(0, setup.System.PathWrites);
        Assert.Null(setup.System.Registration);
    }

    [Fact]
    public async Task Install_copies_the_program_adds_the_directory_to_the_path_registers_it_and_says_how_to_start_it()
    {
        using var setup = new Setup();

        var code = await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(string.Empty, setup.Console.Error.ToString());
        Assert.Equal("the program", File.ReadAllText(Path.Combine(setup.Directory, "yav.exe")));
        Assert.Equal("guide", File.ReadAllText(Path.Combine(setup.Directory, "docs", "user-guide.md")));
        Assert.Equal(@"%USERPROFILE%\bin;C:\Windows\system32;" + setup.Directory, setup.System.UserPath.Value);
        Assert.True(setup.System.UserPath.Expandable);
        Assert.Equal(1, setup.System.Announcements);
        Assert.Equal(new InstallRegistration(setup.Directory, Fixtures.ProductVersion), setup.System.Registration);
        var lines = setup.Console.OutputLines;
        Assert.Equal($"Installed YAV Shell {Fixtures.ProductVersion} in {setup.Directory}.", lines[0]);
        Assert.Equal($"Added {setup.Directory} to the PATH of your user account: consoles opened from now on find 'yav'.", lines[1]);
        Assert.Contains("\"Installed apps\" lists YAV Shell; it is removed there, or with 'yav uninstall'.", lines);
        Assert.Contains("Start it in the folder of a project:  yav", lines);
        Assert.Contains("Then type what you want done. The first request asks which models to use.", lines);
    }

    [Fact]
    public async Task A_console_that_was_open_before_is_told_how_it_finds_yav_too()
    {
        using var setup = new Setup();

        await InstallCommand.InstallAsync(Install(), setup.World(inheritedPath: @"C:\Windows\system32;C:\tools"), CancellationToken.None);

        var lines = setup.Console.OutputLines;
        Assert.Contains($"This console was opened before, so it does not know the new PATH. In it, run first:  $env:Path += ';{setup.Directory}'", lines);
        Assert.Contains($"(In CMD:  set \"PATH=%PATH%;{setup.Directory}\")", lines);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task Nobody_is_told_to_change_the_path_of_a_console_that_finds_yav_or_that_closes(bool consoleHasIt, bool ownWindow)
    {
        using var setup = new Setup();
        var inherited = consoleHasIt ? @"C:\Windows\system32;" + setup.Directory.ToUpperInvariant() + @"\" : @"C:\Windows\system32";

        await InstallCommand.InstallAsync(Install(), setup.World(inheritedPath: inherited, ownWindow: ownWindow), CancellationToken.None);

        Assert.DoesNotContain("$env:Path", setup.Console.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_path_that_names_the_directory_already_is_left_alone()
    {
        using var setup = new Setup();
        setup.System.UserPath = new UserPath(@"C:\a;" + setup.Directory + @"\;C:\b", Expandable: false);

        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);

        Assert.Equal(0, setup.System.PathWrites);
        Assert.Equal(0, setup.System.Announcements);
        Assert.Contains($"The PATH of your user account names {setup.Directory} already.", setup.Console.OutputLines);
    }

    [Fact]
    public async Task With_no_path_the_path_is_not_touched_and_the_program_is_named_by_its_full_path()
    {
        using var setup = new Setup();

        var code = await InstallCommand.InstallAsync(Install("--no-path"), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(0, setup.System.PathWrites);
        var executable = Path.Combine(setup.Directory, "yav.exe");
        Assert.Contains($"The PATH was left as it is (--no-path). YAV Shell starts with:  & '{executable}'", setup.Console.OutputLines);
        Assert.Contains($"Start it in the folder of a project:  & '{executable}'", setup.Console.OutputLines);
        Assert.DoesNotContain("$env:Path", setup.Console.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData('\'')]
    [InlineData('\u2018')]
    [InlineData('\u2019')]
    [InlineData('\u201A')]
    [InlineData('\u201B')]
    public void A_path_with_a_single_quote_of_any_kind_is_written_so_that_powershell_reads_it_as_it_is(char quote)
    {
        // PowerShell ends a single-quoted text at each of these characters unless it is written twice, as in C:\Users\O’Brien.
        var path = $@"C:\Users\O{quote}Brien\AppData\Local\Programs\YavShell\yav.exe";

        Assert.Equal($@"'C:\Users\O{quote}{quote}Brien\AppData\Local\Programs\YavShell\yav.exe'", InstallReport.PowerShellQuoted(path));
    }

    [Fact]
    public async Task With_no_register_installed_apps_is_not_changed_and_the_removal_is_named()
    {
        using var setup = new Setup();

        await InstallCommand.InstallAsync(Install("--no-register"), setup.World(), CancellationToken.None);

        Assert.Null(setup.System.Registration);
        Assert.Contains($"YAV Shell was not added to \"Installed apps\" (--no-register). 'yav uninstall --dir \"{setup.Directory}\"' removes it.", setup.Console.OutputLines);
    }

    [Fact]
    public async Task A_directory_named_on_the_command_line_is_the_one_that_is_used()
    {
        using var setup = new Setup();
        var other = setup.File("elsewhere/YAV");

        await InstallCommand.InstallAsync(Install("--dir", other), setup.World(), CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(other, "yav.exe")));
        Assert.False(Directory.Exists(setup.Directory));
        Assert.Equal(other, setup.System.Registration!.InstallLocation);
    }

    [Fact]
    public async Task In_a_window_opened_from_explorer_the_result_stays_until_enter_is_pressed()
    {
        using var setup = new Setup();
        setup.Console.Answer(string.Empty);

        var code = await InstallCommand.InstallAsync(Install(), setup.World(ownWindow: true), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(1, setup.Console.LinesRead);
        Assert.EndsWith(InstallReport.CloseWindow, setup.Console.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Open in Terminal) and type:  yav", setup.Console.OutputLines);
    }

    [Fact]
    public async Task Without_a_console_to_answer_nothing_waits_for_enter()
    {
        using var setup = new Setup();

        await InstallCommand.InstallAsync(Install(), setup.World(ownWindow: true, canAsk: false), CancellationToken.None);

        Assert.Equal(0, setup.Console.LinesRead);
        Assert.DoesNotContain(InstallReport.CloseWindow, setup.Console.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_installation_the_engine_refuses_ends_with_exit_code_5_and_says_why_on_the_error_output()
    {
        using var setup = new Setup();
        setup.Write("Programs/YAV Shell/my own file.txt", "mine");

        var code = await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);

        Assert.Equal(5, code);
        Assert.StartsWith("yav: ", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("was not created by the installation of YAV Shell", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal(string.Empty, setup.Console.Output.ToString());
        Assert.Equal(0, setup.System.PathWrites);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(setup.Directory, "my own file.txt")));
    }

    [Fact]
    public async Task Installing_from_the_installed_program_repairs_the_installation()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        File.WriteAllText(Path.Combine(setup.Directory, "docs", "user-guide.md"), "damaged");
        setup.System.UserPath = new UserPath(@"C:\Windows\system32", Expandable: true);
        setup.Console.Output.GetStringBuilder().Clear();
        setup.Program = Path.Combine(setup.Directory, "yav.exe");

        var code = await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal($"Repaired YAV Shell {Fixtures.ProductVersion} in {setup.Directory}: its files were written again.", setup.Console.OutputLines[0]);
        Assert.Equal("guide", File.ReadAllText(Path.Combine(setup.Directory, "docs", "user-guide.md")));
        Assert.Equal(@"C:\Windows\system32;" + setup.Directory, setup.System.UserPath.Value);
    }

    [Fact]
    public async Task Installing_over_an_older_version_says_from_which_version_it_was_updated()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(version: "0.1.1"), CancellationToken.None);
        setup.Console.Output.GetStringBuilder().Clear();

        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);

        Assert.Equal($"Updated YAV Shell from 0.1.1 to {Fixtures.ProductVersion} in {setup.Directory}.", setup.Console.OutputLines[0]);
    }

    [Fact]
    public async Task Installing_the_same_version_again_says_so()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Console.Output.GetStringBuilder().Clear();

        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);

        Assert.Equal($"Installed YAV Shell {Fixtures.ProductVersion} in {setup.Directory} again, over the same version.", setup.Console.OutputLines[0]);
    }

    [Fact]
    public async Task Uninstall_removes_the_installation_its_path_entry_and_its_registration_and_keeps_the_data()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Console.Output.GetStringBuilder().Clear();

        var code = await InstallCommand.UninstallAsync(Uninstall(), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(setup.Directory));
        Assert.Equal(@"%USERPROFILE%\bin;C:\Windows\system32", setup.System.UserPath.Value);
        Assert.Null(setup.System.Registration);
        Assert.True(File.Exists(setup.Data.SettingsFile));
        Assert.Equal(
            [
                $"Removed YAV Shell from {setup.Directory}.",
                $"Removed {setup.Directory} from the PATH of your user account.",
                "Removed YAV Shell from \"Installed apps\".",
                $"Your data was kept: {setup.Data.Home}. To remove it as well, delete that folder.",
                string.Empty,
            ],
            setup.Console.OutputLines);
        Assert.Empty(setup.DeletedAfterExit);
    }

    [Fact]
    public async Task A_directory_that_holds_other_files_is_kept_and_that_is_said()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Write("Programs/YAV Shell/notes.txt", "mine");
        setup.Console.Output.GetStringBuilder().Clear();

        await InstallCommand.UninstallAsync(Uninstall(), setup.World(), CancellationToken.None);

        Assert.Equal($"Removed the files of YAV Shell. {setup.Directory} holds other files and was kept.", setup.Console.OutputLines[0]);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(setup.Directory, "notes.txt")));
    }

    [Fact]
    public async Task The_installed_program_that_removes_itself_is_deleted_after_it_has_ended()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Program = Path.Combine(setup.Directory, "yav.exe");

        var code = await InstallCommand.UninstallAsync(Uninstall(), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);

        // While it runs, the program is neither deleted nor moved: it is named for the deletion after the end.
        var left = Assert.Single(setup.DeletedAfterExit);
        Assert.Equal(Path.GetFullPath(Path.Combine(setup.Directory, "yav.exe")), Path.GetFullPath(left));
        Assert.True(File.Exists(left));
        Assert.Contains($"Removed YAV Shell from {setup.Directory}. The program and its folder are deleted as soon as it has ended.", setup.Console.OutputLines);

        // Handed over with the text the manifest beside it holds: the deletion is tied to this removal.
        Assert.Contains(Assert.Single(setup.Removals), File.ReadAllText(Path.Combine(setup.Directory, Installer.ManifestName)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task In_a_window_of_its_own_the_program_is_handed_to_the_deletion_only_after_enter_was_pressed()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Program = Path.Combine(setup.Directory, "yav.exe");
        setup.Console.Answer(string.Empty);

        var code = await InstallCommand.UninstallAsync(Uninstall(), setup.World(ownWindow: true), CancellationToken.None);

        // Handed over earlier, the deletion would have waited its few seconds while the window still waited for Enter.
        Assert.Equal(0, code);
        Assert.Single(setup.DeletedAfterExit);
        Assert.Equal(1, setup.LinesReadWhenDeleted);
    }

    [Theory]
    [InlineData("Programs/100% YAV")]
    [InlineData("Programs/Yav! Shell")]
    public async Task A_program_whose_path_cmd_would_expand_is_not_handed_to_cmd_and_the_user_is_told_to_delete_the_folder(string relative)
    {
        using var setup = new Setup();
        var directory = setup.File(relative);
        await InstallCommand.InstallAsync(Install("--dir", directory), setup.World(), CancellationToken.None);
        setup.Program = Path.Combine(directory, "yav.exe");

        var code = await InstallCommand.UninstallAsync(Uninstall("--dir", directory), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Empty(setup.DeletedAfterExit);
        Assert.Contains(
            setup.Console.OutputLines,
            line => line.Contains("cannot delete itself in", StringComparison.Ordinal) && line.EndsWith("Delete the folder once YAV Shell has ended.", StringComparison.Ordinal));
        Assert.True(File.Exists(Path.Combine(directory, "yav.exe")));
    }

    [Fact]
    public async Task Uninstall_without_an_installation_ends_with_exit_code_5_and_changes_nothing()
    {
        using var setup = new Setup();
        Directory.CreateDirectory(setup.Directory);
        setup.Write("Programs/YAV Shell/yav.exe", "something of the user");

        var code = await InstallCommand.UninstallAsync(Uninstall(), setup.World(), CancellationToken.None);

        Assert.Equal(5, code);
        Assert.Contains("does not hold an installation of YAV Shell", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("something of the user", File.ReadAllText(Path.Combine(setup.Directory, "yav.exe")));
        Assert.Equal(0, setup.System.PathWrites);
    }

    [Fact]
    public async Task Removing_the_data_without_a_console_to_confirm_it_is_refused_before_anything_is_removed()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);

        var code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), setup.World(canAsk: false), CancellationToken.None);

        Assert.Equal(5, code);
        Assert.Contains("needs your confirmation, but nobody can be asked here. Nothing was removed.", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(setup.Directory, "yav.exe")));
        Assert.True(File.Exists(setup.Data.SettingsFile));
        Assert.NotNull(setup.System.Registration);
    }

    [Fact]
    public async Task Removing_the_data_takes_the_data_directory_and_the_stored_key_after_yes()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        var readOnly = setup.Write("data/workspaces/w1/.git/objects/ab/cdef", "object");
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);
        setup.Credentials.Write(AppServices.AnthropicKeyName, "sk-test", "test");
        setup.Console.Answer("yes");

        var code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.False(Directory.Exists(setup.Data.Home));
        Assert.False(setup.Credentials.Exists(AppServices.AnthropicKeyName));
        Assert.False(Directory.Exists(setup.Directory));
        Assert.Contains($"Removed {setup.Data.Home}.", setup.Console.OutputLines);
        Assert.Contains("Removed the API key YAV stored.", setup.Console.OutputLines);
    }

    [Theory]
    [InlineData("no")]
    [InlineData("y")]
    [InlineData("")]
    [InlineData(null)]
    public async Task Anything_but_yes_keeps_the_data_and_the_program_is_removed_all_the_same(string? answer)
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Credentials.Write(AppServices.AnthropicKeyName, "sk-test", "test");
        setup.Console.Answer(answer);

        var code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.True(File.Exists(setup.Data.SettingsFile));
        Assert.True(setup.Credentials.Exists(AppServices.AnthropicKeyName));
        Assert.False(Directory.Exists(setup.Directory));
        Assert.Contains("Type yes to remove your data as well: ", setup.Console.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Not confirmed: your data is kept.", setup.Console.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains(
            $"Your data was kept: {setup.Data.Home}. To remove it as well, delete that folder, and the API key YAV stored in the Windows Credential Manager (its name begins with YavShell/).",
            setup.Console.OutputLines);
    }

    [Fact]
    public async Task A_data_directory_that_holds_no_data_of_yav_is_not_removed()
    {
        using var setup = new Setup();
        var foreign = setup.Write("someone else/report.docx", "important");
        var world = setup.World() with { Data = new YavPaths(Path.GetDirectoryName(foreign)!) };
        setup.Console.Answer("yes");

        var code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), world, CancellationToken.None);

        Assert.Equal(5, code);
        Assert.Contains("holds no yav.db, so it is not taken for a data directory of YAV", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("important", File.ReadAllText(foreign));
        Assert.Equal(0, setup.Console.LinesRead);
    }

    [Theory]
    [InlineData(Environment.SpecialFolder.UserProfile)]
    [InlineData(Environment.SpecialFolder.LocalApplicationData)]
    [InlineData(Environment.SpecialFolder.Windows)]
    public void A_folder_of_windows_or_of_the_user_is_never_taken_for_a_data_directory(Environment.SpecialFolder folder)
    {
        Assert.NotNull(DataDirectory.Problem(Environment.GetFolderPath(folder)));
    }

    [Fact]
    public void The_root_of_a_drive_is_never_taken_for_a_data_directory()
    {
        Assert.Contains("root of a drive", DataDirectory.Problem(@"C:\"), StringComparison.Ordinal);
    }

    [Fact]
    public void A_data_directory_of_yav_and_one_that_does_not_exist_yet_may_be_removed()
    {
        using var setup = new Setup();

        Assert.Null(DataDirectory.Problem(setup.Data.Home));
        Assert.Null(DataDirectory.Problem(setup.File("not there")));
    }

    [Fact]
    public void Removing_the_data_does_not_enter_a_link_to_another_place()
    {
        using var outside = new TempDirectory("outside [data]");
        var kept = outside.Write("kept.txt", "kept");
        File.SetAttributes(kept, FileAttributes.ReadOnly);
        var data = new TempDirectory("data [link]");
        data.Write("yav.db", "a database");
        data.Write("settings.json", "{}");
        data.CreateDirectory("workspaces");
        data.Junction("workspaces/linked", outside.Path);

        DataDirectory.Remove(data.Path);

        Assert.False(Directory.Exists(data.Path));
        Assert.Equal("kept", File.ReadAllText(kept));
        Assert.True(File.GetAttributes(kept).HasFlag(FileAttributes.ReadOnly));
        File.SetAttributes(kept, FileAttributes.Normal);
    }

    [Fact]
    public async Task The_program_that_was_built_with_the_tests_refuses_to_install_itself_before_it_changes_anything()
    {
        using var setup = new Setup();
        var target = setup.File("target [x]");

        // --dir names a directory of the test first, and --no-path and --no-register follow: even a build that did install
        // itself would change nothing of the user's, neither the registered installation nor the PATH nor "Installed apps".
        var result = await new Yav.Platform.Processes.ProcessRunner().RunAsync(
            new Yav.Core.Ports.ProcessSpec(YavProcess.Executable, ["install", "--dir", target, "--no-path", "--no-register"], setup.File(string.Empty),
                new Dictionary<string, string?> { [YavPaths.HomeVariable] = setup.Data.Home }),
            new Yav.Core.Ports.CaptureOptions(Timeout: TimeSpan.FromSeconds(60)),
            CancellationToken.None);

        Assert.Equal(5, result.ExitCode);
        Assert.Equal("yav: " + InstallReport.SourceTreeBuild, result.StandardError.Trim());
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData("install", "--bogus")]
    [InlineData("install", "C:\\somewhere")]
    [InlineData("uninstall", "--no-path")]
    public async Task A_command_line_of_the_installation_that_is_not_understood_ends_with_exit_code_64(string verb, string argument)
    {
        using var setup = new Setup();
        var empty = setup.File("empty [dir]");
        Directory.CreateDirectory(empty);

        // An empty directory of the test is named with --dir before the argument under test: should a later version ever
        // understand that argument, the command reaches nothing of the user's, and certainly not the registered installation.
        var result = await new Yav.Platform.Processes.ProcessRunner().RunAsync(
            new Yav.Core.Ports.ProcessSpec(YavProcess.Executable, [verb, "--dir", empty, argument], setup.File(string.Empty),
                new Dictionary<string, string?> { [YavPaths.HomeVariable] = setup.Data.Home }),
            new Yav.Core.Ports.CaptureOptions(Timeout: TimeSpan.FromSeconds(60)),
            CancellationToken.None);

        Assert.Equal(64, result.ExitCode);
        Assert.StartsWith("yav: ", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task A_build_of_the_source_tree_removes_only_an_installation_named_with_dir()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Console.Output.GetStringBuilder().Clear();

        var refused = await InstallCommand.UninstallAsync(Uninstall(), setup.World(singleFile: false), CancellationToken.None);

        Assert.Equal(5, refused);
        Assert.Equal("yav: " + InstallReport.SourceTreeUninstall, setup.Console.Error.ToString().Trim());
        Assert.True(File.Exists(Path.Combine(setup.Directory, "yav.exe")));
        Assert.NotNull(setup.System.Registration);

        var removed = await InstallCommand.UninstallAsync(Uninstall("--dir", setup.Directory), setup.World(singleFile: false), CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.False(Directory.Exists(setup.Directory));
    }

    [Fact]
    public async Task Without_a_directory_named_anywhere_the_default_directory_of_the_system_is_installed_into_and_removed()
    {
        using var setup = new Setup();

        var installed = await InstallCommand.InstallAsync(Install(), setup.World(named: false), CancellationToken.None);

        Assert.Equal(0, installed);
        Assert.True(File.Exists(Path.Combine(setup.System.DefaultDirectory, "yav.exe")));
        Assert.Equal(setup.System.DefaultDirectory, setup.System.Registration!.InstallLocation);

        var removed = await InstallCommand.UninstallAsync(Uninstall(), setup.World(named: false), CancellationToken.None);

        Assert.Equal(0, removed);
        Assert.False(Directory.Exists(setup.System.DefaultDirectory));
        Assert.Null(setup.System.Registration);
    }

    [Fact]
    public async Task A_program_left_beside_files_of_the_user_is_deleted_after_the_end_and_the_folder_is_kept()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Write("Programs/YAV Shell/notes.txt", "mine");
        setup.Program = Path.Combine(setup.Directory, "yav.exe");
        setup.Console.Output.GetStringBuilder().Clear();

        await InstallCommand.UninstallAsync(Uninstall(), setup.World(), CancellationToken.None);

        Assert.Equal($"Removed the files of YAV Shell. {setup.Directory} holds other files and was kept.", setup.Console.OutputLines[0]);
        Assert.Contains("This program is deleted as soon as it has ended.", setup.Console.OutputLines);
        Assert.Single(setup.DeletedAfterExit);
    }

    [Fact]
    public async Task A_failure_nobody_foresaw_is_said_before_a_window_of_its_own_waits_for_enter()
    {
        using var setup = new Setup();
        setup.Console.Answer(string.Empty);
        var world = setup.World(ownWindow: true) with
        {
            Files = [new PackageFile("docs/broken.md", () => throw new InvalidOperationException("Something nobody foresaw."))],
        };

        var code = await InstallCommand.InstallAsync(Install(), world, CancellationToken.None);

        Assert.Equal(5, code);
        Assert.Equal("yav: the installation failed: InvalidOperationException: Something nobody foresaw.", setup.Console.Error.ToString().Trim());
        Assert.EndsWith(InstallReport.CloseWindow, setup.Console.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal(1, setup.Console.LinesRead);
    }

    [Fact]
    public async Task A_data_directory_that_holds_anything_yav_did_not_put_there_is_not_removed_and_nothing_is_asked()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Write("data/report.docx", "important");
        setup.Console.Answer("yes");

        var code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), setup.World(), CancellationToken.None);

        Assert.Equal(5, code);
        Assert.Contains("also holds report.docx, which YAV did not put there", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("Nothing was removed.", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("important", File.ReadAllText(setup.File("data/report.docx")));
        Assert.True(File.Exists(setup.Data.Database));
        Assert.True(File.Exists(Path.Combine(setup.Directory, "yav.exe")));
        Assert.Equal(0, setup.Console.LinesRead);
    }

    [Fact]
    public void A_folder_of_another_program_with_a_settings_json_is_not_taken_for_a_data_directory()
    {
        // Visual Studio Code keeps a settings.json in a folder of its own, as many programs do.
        using var folder = new TempDirectory("Code [User]");
        folder.Write("settings.json", "{ \"editor.fontSize\": 14 }");
        folder.CreateDirectory("logs");

        Assert.Contains("holds no yav.db", DataDirectory.Problem(folder.Path), StringComparison.Ordinal);
        var refused = Assert.Throws<InstallException>(() => DataDirectory.Remove(folder.Path));
        Assert.EndsWith("Nothing was removed.", refused.Message, StringComparison.Ordinal);
        Assert.True(File.Exists(folder.File("settings.json")));
    }

    [Fact]
    public void A_data_directory_yav_made_itself_is_removed_with_everything_it_holds()
    {
        using var setup = new Setup();
        setup.Write("data/yav.db-wal", "wal");
        setup.Write("data/history.txt", "/help");
        setup.Write("data/settings.json.tmp-0123abcd", "{}");
        setup.Write("data/settings.unreadable-20261007-120000.json", "{");
        setup.Write("data/logs/yav.log", "log");
        setup.Write("data/exports/run.json", "{}");
        File.SetAttributes(setup.Write("data/workspaces/w1/.git/objects/ab/cd", "object"), FileAttributes.ReadOnly | FileAttributes.Hidden);

        Assert.Null(DataDirectory.Problem(setup.Data.Home));
        DataDirectory.Remove(setup.Data.Home);

        Assert.False(Directory.Exists(setup.Data.Home));
    }

    [Fact]
    public void Removing_the_data_looks_again_and_removes_nothing_when_something_else_came_in_meanwhile()
    {
        using var setup = new Setup();
        Assert.Null(DataDirectory.Problem(setup.Data.Home));

        // Between the question and the removal, a file of the user was put there.
        var report = setup.Write("data/report.docx", "important");
        var refused = Assert.Throws<InstallException>(() => DataDirectory.Remove(setup.Data.Home));

        Assert.EndsWith("Nothing was removed.", refused.Message, StringComparison.Ordinal);
        Assert.Equal("important", File.ReadAllText(report));
        Assert.True(File.Exists(setup.Data.Database));
        Assert.True(Directory.Exists(setup.Data.Workspaces));
    }

    [Fact]
    public void A_removal_of_the_data_that_stops_part_way_leaves_a_directory_that_is_still_one_of_yav()
    {
        using var setup = new Setup();
        var busy = setup.Write("data/workspaces/w1/busy.txt", "in use");

        // A file another program holds cannot be deleted: the removal stops there, and the database and the settings go last.
        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => DataDirectory.Remove(setup.Data.Home));
        }

        Assert.True(File.Exists(setup.Data.Database));
        Assert.True(File.Exists(setup.Data.SettingsFile));
        Assert.Null(DataDirectory.Problem(setup.Data.Home));
    }

    [Fact]
    public async Task A_database_another_yav_has_open_keeps_the_data_directory_and_nothing_is_removed()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Console.Answer("yes");
        int code;

        // Opened as SQLite opens it: for reading and writing, shared with others.
        using (new FileStream(setup.Data.Database, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
        {
            Assert.Throws<InstallException>(() => DataDirectory.Remove(setup.Data.Home));
            code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), setup.World(), CancellationToken.None);
        }

        Assert.Equal(5, code);
        Assert.Contains("is in use or cannot be opened", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(setup.Data.SettingsFile));
        Assert.True(Directory.Exists(setup.Data.Workspaces));
        Assert.True(File.Exists(Path.Combine(setup.Directory, "yav.exe")));
        Assert.Equal(0, setup.Console.LinesRead);
    }

    [Fact]
    public async Task A_removal_of_the_program_that_is_refused_after_yes_keeps_the_data_and_the_key()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.System.Running.Add(4242);
        setup.Credentials.Write(AppServices.AnthropicKeyName, "sk-test", "test");
        setup.Console.Answer("yes");
        setup.Console.Output.GetStringBuilder().Clear();

        var code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), setup.World(), CancellationToken.None);

        // The data goes only after the program went: unapplied workspaces and the key survive a removal that was refused.
        Assert.Equal(5, code);
        Assert.Contains("process 4242", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(setup.Data.SettingsFile));
        Assert.True(setup.Credentials.Exists(AppServices.AnthropicKeyName));
        Assert.True(File.Exists(Path.Combine(setup.Directory, "yav.exe")));
        Assert.DoesNotContain(setup.Console.OutputLines, line => line.StartsWith("Removed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_data_directory_that_cannot_be_removed_completely_is_reported_and_the_key_is_removed_on_its_own()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        var busy = setup.Write("data/workspaces/w1/busy.txt", "in use");
        setup.Credentials.Write(AppServices.AnthropicKeyName, "sk-test", "test");
        setup.Console.Answer("yes");
        int code;

        using (new FileStream(busy, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), setup.World(), CancellationToken.None);
        }

        Assert.Equal(5, code);
        Assert.Contains($"yav: {setup.Data.Home} could not be removed completely:", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains($"Removed YAV Shell from {setup.Directory}.", setup.Console.Output.ToString(), StringComparison.Ordinal);
        Assert.Contains("Removed the API key YAV stored.", setup.Console.OutputLines);
        Assert.DoesNotContain($"Removed {setup.Data.Home}.", setup.Console.OutputLines);
        Assert.True(File.Exists(setup.Data.Database));
    }

    [Fact]
    public async Task Without_a_data_directory_the_stored_key_is_still_offered_for_removal()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        Directory.Delete(setup.Data.Home, recursive: true);
        setup.Credentials.Write(AppServices.AnthropicKeyName, "sk-test", "test");
        setup.Console.Answer("yes");

        var code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Contains($"This also removes the API key YAV stored for {setup.Data.Home}, which does not exist any more.", setup.Console.OutputLines);
        Assert.Contains("Removed the API key YAV stored.", setup.Console.OutputLines);
        Assert.False(setup.Credentials.Exists(AppServices.AnthropicKeyName));
    }

    [Fact]
    public async Task Without_a_data_directory_and_without_a_key_nothing_is_asked_and_that_is_said()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        Directory.Delete(setup.Data.Home, recursive: true);

        var code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), setup.World(), CancellationToken.None);

        Assert.Equal(0, code);
        Assert.Equal(0, setup.Console.LinesRead);
        Assert.Contains($"There is no data of YAV to remove: {setup.Data.Home} does not exist, and no API key is stored for it.", setup.Console.OutputLines);
        Assert.False(Directory.Exists(setup.Directory));
    }

    [Fact]
    public async Task A_key_the_credential_manager_does_not_give_up_is_reported_and_the_data_directory_goes_all_the_same()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Install(), setup.World(), CancellationToken.None);
        setup.Console.Answer("yes");
        var world = setup.World() with { Credentials = new StubbornCredentialStore() };

        var code = await InstallCommand.UninstallAsync(Uninstall("--remove-data"), world, CancellationToken.None);

        Assert.Equal(5, code);
        Assert.False(Directory.Exists(setup.Data.Home));
        Assert.Contains($"Removed {setup.Data.Home}.", setup.Console.OutputLines);
        Assert.Contains("the API key YAV stored could not be removed: Element not found.", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.Contains("It stays in the Windows Credential Manager", setup.Console.Error.ToString(), StringComparison.Ordinal);
    }

    /// <summary>A Credential Manager that holds a key and refuses to delete it, as CredDelete can.</summary>
    private sealed class StubbornCredentialStore : Yav.Core.Ports.ICredentialStore
    {
        public bool IsAvailable => true;

        public bool Exists(string name) => true;

        public string? Read(string name) => "sk-test";

        public void Write(string name, string secret, string comment)
        {
        }

        public bool Delete(string name) => throw new global::System.ComponentModel.Win32Exception(1168, "Element not found.");
    }

    /// <summary>A program, its manifest and its directory in a directory of the test, as a removal left them for the deletion after the end.</summary>
    private static (string Program, string Manifest, string Directory) LeftForDeletion(TempDirectory root, string relative, string removal)
    {
        var directory = root.CreateDirectory(relative);
        var program = root.Write(relative + "/yav.exe", "the program");
        var manifest = root.Write(relative + "/" + Installer.ManifestName, $"{{ \"product\": \"YAV Shell\", \"removal\": \"{removal}\" }}");
        return (program, manifest, directory);
    }

    private static void RunDeletion(string program, string removal)
    {
        using var deleting = global::System.Diagnostics.Process.Start(InstallSurroundings.DeletionStart(program, removal, waitSeconds: 0, retrySeconds: 0))!;
        Assert.True(deleting.WaitForExit(30_000));
    }

    [Theory]
    [InlineData("Programs/YAV Shell")]
    [InlineData("Programs (x86) & Co^1/O'Brien's [YAV] 日本")]
    [InlineData("Users/李明/Programs/YavShell")]
    public void The_deletion_after_the_end_removes_the_program_its_manifest_and_its_empty_directory(string relative)
    {
        using var root = new TempDirectory("deletion");
        var (program, manifest, directory) = LeftForDeletion(root, relative, "0123456789abcdef0123456789abcdef");

        RunDeletion(program, "0123456789abcdef0123456789abcdef");

        Assert.False(File.Exists(program));
        Assert.False(File.Exists(manifest));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void The_deletion_after_the_end_keeps_the_manifest_and_the_directory_while_the_program_is_still_there()
    {
        using var root = new TempDirectory("deletion in use");
        var (program, manifest, directory) = LeftForDeletion(root, "Programs/YAV Shell", "0123456789abcdef0123456789abcdef");

        // The program still runs: it cannot be deleted, so the directory stays an installation that can be removed later.
        using (new FileStream(program, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            RunDeletion(program, "0123456789abcdef0123456789abcdef");
        }

        Assert.True(File.Exists(program));
        Assert.True(File.Exists(manifest));
        Assert.True(Directory.Exists(directory));
    }

    [Fact]
    public void The_deletion_after_the_end_leaves_a_new_installation_in_the_same_directory_alone()
    {
        using var root = new TempDirectory("deletion reinstalled");
        var (program, manifest, directory) = LeftForDeletion(root, "Programs/YAV Shell", "fedcba9876543210fedcba9876543210");

        RunDeletion(program, "0123456789abcdef0123456789abcdef");

        Assert.True(File.Exists(program));
        Assert.True(File.Exists(manifest));
    }

    [Fact]
    public void The_deletion_after_the_end_keeps_a_directory_that_holds_anything_else()
    {
        using var root = new TempDirectory("deletion with notes");
        var (program, manifest, directory) = LeftForDeletion(root, "Programs/YAV Shell", "0123456789abcdef0123456789abcdef");
        var notes = root.Write("Programs/YAV Shell/notes.txt", "mine");

        RunDeletion(program, "0123456789abcdef0123456789abcdef");

        Assert.False(File.Exists(program));
        Assert.False(File.Exists(manifest));
        Assert.Equal("mine", File.ReadAllText(notes));
    }

    [Fact]
    public void The_deletion_after_the_end_starts_only_programs_of_windows_by_their_full_path_and_never_removes_a_tree()
    {
        var start = InstallSurroundings.DeletionStart(@"C:\Programs\YAV Shell\yav.exe", "0123456789abcdef0123456789abcdef");
        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);

        Assert.Equal(Path.Combine(system, "cmd.exe"), start.FileName);
        Assert.StartsWith("/d /v:off /s /c \"", start.Arguments, StringComparison.Ordinal);
        Assert.Contains($"\"{Path.Combine(system, "PING.EXE")}\" -n 4 127.0.0.1", start.Arguments, StringComparison.Ordinal);
        Assert.Contains($"\"{Path.Combine(system, "findstr.exe")}\" /l /c:0123456789abcdef0123456789abcdef <\"C:\\Programs\\YAV Shell\\yav-install.json\"", start.Arguments, StringComparison.Ordinal);
        Assert.Contains("(if not exist \"C:\\Programs\\YAV Shell\\yav.exe\" (del /f /q \"C:\\Programs\\YAV Shell\\yav-install.json\"", start.Arguments, StringComparison.Ordinal);
        Assert.Contains("rmdir \"C:\\Programs\\YAV Shell\"", start.Arguments, StringComparison.Ordinal);
        Assert.DoesNotContain("/s \"", start.Arguments, StringComparison.Ordinal);
        Assert.Equal(system, start.WorkingDirectory);
        Assert.True(start.CreateNoWindow);
        Assert.Throws<ArgumentException>(() => InstallSurroundings.DeletionStart(@"C:\Programs\YAV Shell\yav.exe", "\" & del C:\\x"));
    }
}
