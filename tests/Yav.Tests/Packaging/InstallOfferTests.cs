using Yav.Console.Install;
using Yav.Platform.Install;
using Yav.Tests.Support;
using Setup = Yav.Tests.Packaging.InstallCommandTests.Setup;

namespace Yav.Tests.Packaging;

/// <summary>
/// What 'yav' does before the shell starts when the program that runs is not installed: when it asks, what each
/// answer does, and what it leaves behind. Registry and console are stand-ins; nothing of the user is touched.
/// </summary>
public class InstallOfferTests
{
    private const string Program = @"C:\Users\me\Downloads\yav.exe";
    private const string InstalledHere = @"C:\Users\me\AppData\Local\Programs\YavShell";

    private static OfferSituation Situation(
        bool singleFile = true, bool canAsk = true, bool ownWindow = false, bool declinedBefore = false,
        string program = Program, string version = "0.2.0", InstallRegistration? installed = null, bool fromInstallation = false) =>
        new(singleFile, canAsk, ownWindow, declinedBefore, program, version, installed, fromInstallation);

    [Fact]
    public void A_copy_that_is_not_installed_offers_to_install_itself()
    {
        Assert.Equal(OfferKind.Install, InstallOffer.Decide(Situation()));
        Assert.Equal(OfferKind.Install, InstallOffer.Decide(Situation(ownWindow: true)));
    }

    [Fact]
    public void A_build_of_the_source_tree_never_offers_anything()
    {
        Assert.Equal(OfferKind.None, InstallOffer.Decide(Situation(singleFile: false, ownWindow: true)));
    }

    [Fact]
    public void Nothing_is_offered_where_nobody_can_answer()
    {
        Assert.Equal(OfferKind.None, InstallOffer.Decide(Situation(canAsk: false, ownWindow: true)));
    }

    [Fact]
    public void A_no_in_a_console_that_was_open_already_is_not_asked_again_there_but_a_double_click_asks()
    {
        Assert.Equal(OfferKind.None, InstallOffer.Decide(Situation(declinedBefore: true)));
        Assert.Equal(OfferKind.Install, InstallOffer.Decide(Situation(declinedBefore: true, ownWindow: true)));
    }

    [Fact]
    public void The_installed_program_itself_offers_nothing()
    {
        var installed = new InstallRegistration(InstalledHere, "0.1.0");

        Assert.Equal(OfferKind.None, InstallOffer.Decide(Situation(program: InstalledHere + @"\YAV.EXE", ownWindow: true, installed: installed)));
    }

    [Fact]
    public void A_program_in_an_installation_directory_offers_nothing_whether_installed_apps_lists_it_or_not()
    {
        // Installed with --no-register, or in another directory than the installation "Installed apps" knows of.
        Assert.Equal(OfferKind.None, InstallOffer.Decide(Situation(fromInstallation: true)));
        Assert.Equal(OfferKind.None, InstallOffer.Decide(Situation(fromInstallation: true, ownWindow: true)));
        Assert.Equal(OfferKind.None, InstallOffer.Decide(Situation(fromInstallation: true, ownWindow: true, installed: new InstallRegistration(InstalledHere, "0.1.1"))));
    }

    [Fact]
    public void Opened_from_explorer_a_newer_copy_offers_to_replace_an_older_installation()
    {
        var installed = new InstallRegistration(InstalledHere, "0.1.1");

        Assert.Equal(OfferKind.Replace, InstallOffer.Decide(Situation(ownWindow: true, installed: installed)));
    }

    [Theory]
    [InlineData("0.1.1", false)]
    [InlineData("0.2.0", true)]
    [InlineData("0.3.0", true)]
    [InlineData("not a version", true)]
    [InlineData("", true)]
    public void Another_copy_offers_nothing_unless_it_was_opened_from_explorer_and_is_newer(string installedVersion, bool ownWindow)
    {
        var installed = new InstallRegistration(InstalledHere, installedVersion);

        Assert.Equal(OfferKind.None, InstallOffer.Decide(Situation(ownWindow: ownWindow, installed: installed)));
    }

    [Theory]
    [InlineData("0.1.1", "0.2.0", true)]
    [InlineData("0.2.0", "0.2.0", false)]
    [InlineData("0.10.0", "0.9.0", false)]
    [InlineData("0.9.0", "0.10.0", true)]
    [InlineData("0.2.0-preview.1", "0.2.1", true)]
    [InlineData("garbage", "0.2.0", false)]
    public void Versions_are_compared_as_numbers(string installed, string version, bool older)
    {
        Assert.Equal(older, InstallOffer.IsOlder(installed, version));
    }

    [Theory]
    [InlineData(1, true, true)]
    [InlineData(1, false, false)]
    [InlineData(2, true, false)]
    [InlineData(4, true, false)]
    [InlineData(0, false, false)]
    public void Only_a_console_of_its_own_with_a_window_is_a_window_that_closes_with_yav(int sharing, bool hasWindow, bool own)
    {
        // One process and no window is a program started hidden (CREATE_NO_WINDOW): nobody would see a question there.
        Assert.Equal(own, ConsoleWindow.IsOwn(sharing, hasWindow));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void A_question_is_asked_only_in_a_console_somebody_sees(bool interactive, bool hasWindow, bool canAsk)
    {
        Assert.Equal(canAsk, ConsoleWindow.CanAsk(interactive, hasWindow));
    }

    [Theory]
    [InlineData(0x1u, (ushort)0, true)]
    [InlineData(0x101u, (ushort)0, true)]
    [InlineData(0x1u, (ushort)1, false)]
    [InlineData(0x0u, (ushort)0, false)]
    [InlineData(0x100u, (ushort)0, false)]
    public void A_window_that_was_asked_to_be_hidden_counts_as_none(uint flags, ushort showWindow, bool hidden)
    {
        // Start-Process -WindowStyle Hidden gives the console a window nobody sees: STARTF_USESHOWWINDOW with SW_HIDE.
        Assert.Equal(hidden, ConsoleWindow.IsHidden(flags, showWindow));
    }

    [Fact]
    public async Task Enter_in_a_console_that_was_open_installs_and_then_the_shell_starts()
    {
        using var setup = new Setup();
        setup.Console.Answer(string.Empty);
        var declined = 0;

        var ended = await InstallOffer.OfferAsync(setup.World(), declinedBefore: false, () => { declined++; return true; }, CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(0, declined);
        Assert.True(File.Exists(Path.Combine(setup.Directory, "yav.exe")));
        var lines = setup.Console.OutputLines;
        Assert.Equal($"YAV Shell {Fixtures.ProductVersion} is not installed for your user account.", lines[0]);
        Assert.Contains($"Installing it copies yav.exe to {setup.Directory} and adds that folder to the PATH of your user account,", lines);
        Assert.StartsWith(InstallReport.InstallQuestion + $"Installed YAV Shell {Fixtures.ProductVersion} in {setup.Directory}.", lines[4], StringComparison.Ordinal);
        Assert.Contains($"This console was opened before, so it does not know the new PATH. In it, run first:  $env:Path += ';{setup.Directory}'", lines);
        Assert.DoesNotContain(InstallReport.CloseWindow, setup.Console.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("y")]
    [InlineData("YES")]
    [InlineData(" Y ")]
    public async Task Y_or_yes_installs_as_enter_does(string answer)
    {
        using var setup = new Setup();
        setup.Console.Answer(answer);

        await InstallOffer.OfferAsync(setup.World(), false, () => true, CancellationToken.None);

        Assert.True(File.Exists(Path.Combine(setup.Directory, "yav.exe")));
    }

    [Fact]
    public async Task Opened_from_explorer_the_window_installs_shows_how_to_start_yav_and_closes_after_enter()
    {
        using var setup = new Setup();
        setup.Console.Answer(string.Empty, string.Empty);

        var ended = await InstallOffer.OfferAsync(setup.World(ownWindow: true), false, () => true, CancellationToken.None);

        Assert.Equal(0, ended);
        Assert.Equal(2, setup.Console.LinesRead);
        var output = setup.Console.Output.ToString();
        Assert.Contains("Open in Terminal) and type:  yav", output, StringComparison.Ordinal);
        Assert.DoesNotContain("$env:Path", output, StringComparison.Ordinal);
        Assert.EndsWith(InstallReport.CloseWindow, output, StringComparison.Ordinal);
        Assert.Equal(new InstallRegistration(setup.Directory, Fixtures.ProductVersion), setup.System.Registration);
    }

    [Fact]
    public async Task No_in_a_console_that_was_open_installs_nothing_starts_the_shell_and_is_remembered()
    {
        using var setup = new Setup();
        setup.Console.Answer("n");
        var declined = 0;

        var ended = await InstallOffer.OfferAsync(setup.World(), false, () => { declined++; return true; }, CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(1, declined);
        Assert.False(Directory.Exists(setup.Directory));
        Assert.Equal(0, setup.System.PathWrites);
        Assert.Contains("Not installed. 'yav install' installs it; in a console it is not offered again.", setup.Console.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task No_in_a_window_opened_from_explorer_is_not_remembered()
    {
        using var setup = new Setup();
        setup.Console.Answer("no");
        var declined = 0;

        var ended = await InstallOffer.OfferAsync(setup.World(ownWindow: true), false, () => { declined++; return true; }, CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(0, declined);
        Assert.False(Directory.Exists(setup.Directory));
    }

    [Fact]
    public async Task Answers_that_are_neither_yes_nor_no_are_asked_again_and_three_of_them_install_nothing()
    {
        using var setup = new Setup();
        setup.Console.Answer("maybe", "later", "what");
        var declined = 0;

        var ended = await InstallOffer.OfferAsync(setup.World(), false, () => { declined++; return true; }, CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(0, declined);
        Assert.False(Directory.Exists(setup.Directory));

        // Every prompt is read: the third answer is followed by a line that says nothing was installed, not by a fourth prompt.
        var output = setup.Console.Output.ToString();
        Assert.Equal(3, setup.Console.LinesRead);
        Assert.Equal(2, output.Split(InstallReport.AnswerAgain).Length - 1);
        Assert.EndsWith(InstallReport.AnswerAgain + InstallReport.NotUnderstood + Environment.NewLine + Environment.NewLine, output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_end_of_the_input_installs_nothing_and_remembers_nothing()
    {
        using var setup = new Setup();
        var declined = 0;

        var ended = await InstallOffer.OfferAsync(setup.World(), false, () => { declined++; return true; }, CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(0, declined);
        Assert.False(Directory.Exists(setup.Directory));
    }

    [Fact]
    public async Task Nothing_is_asked_when_it_is_installed_and_this_is_the_installed_program()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Yav.Console.Cli.CommandLine.Parse(["install"]), setup.World(), CancellationToken.None);
        setup.Console.Output.GetStringBuilder().Clear();
        setup.Program = Path.Combine(setup.Directory, "yav.exe");

        var ended = await InstallOffer.OfferAsync(setup.World(ownWindow: true), false, () => true, CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(string.Empty, setup.Console.Output.ToString());
        Assert.Equal(0, setup.Console.LinesRead);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task An_installation_made_with_no_register_is_not_offered_to_be_installed_again_by_its_own_program(bool ownWindow)
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Yav.Console.Cli.CommandLine.Parse(["install", "--no-register"]), setup.World(), CancellationToken.None);
        setup.Console.Output.GetStringBuilder().Clear();
        var pathWrites = setup.System.PathWrites;
        setup.Program = Path.Combine(setup.Directory, "yav.exe");
        setup.Console.Answer(string.Empty, string.Empty);

        var ended = await InstallOffer.OfferAsync(setup.World(ownWindow: ownWindow), false, () => true, CancellationToken.None);

        // Nothing says it is not installed, and Enter would not register it after all.
        Assert.Null(ended);
        Assert.Equal(string.Empty, setup.Console.Output.ToString());
        Assert.Equal(0, setup.Console.LinesRead);
        Assert.Null(setup.System.Registration);
        Assert.Equal(pathWrites, setup.System.PathWrites);
    }

    [Fact]
    public async Task The_program_of_an_installation_does_not_read_the_registry_to_find_out_that_it_is_installed()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Yav.Console.Cli.CommandLine.Parse(["install", "--no-register"]), setup.World(), CancellationToken.None);
        setup.Console.Output.GetStringBuilder().Clear();
        setup.Program = Path.Combine(setup.Directory, "yav.exe");

        // Read, the registry would fail the offer at every start, and say so.
        setup.System.ReadFails = new UnauthorizedAccessException("Access to the registry key is denied.");

        var ended = await InstallOffer.OfferAsync(setup.World(), false, () => true, CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(string.Empty, setup.Console.Output.ToString());
        Assert.Equal(string.Empty, setup.Console.Error.ToString());
    }

    [Fact]
    public async Task A_yav_install_json_that_cannot_be_read_beside_the_program_does_not_make_it_an_installed_copy()
    {
        using var setup = new Setup();
        setup.Write("download/" + Installer.ManifestName, "{ not json");
        setup.Console.Answer("n");

        await InstallOffer.OfferAsync(setup.World(), false, () => true, CancellationToken.None);

        Assert.StartsWith($"YAV Shell {Fixtures.ProductVersion} is not installed", setup.Console.Output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registration_whose_program_is_gone_counts_as_not_installed()
    {
        using var setup = new Setup();
        setup.System.Registration = new InstallRegistration(setup.Directory, "0.1.1");
        setup.Console.Answer(string.Empty);

        await InstallOffer.OfferAsync(setup.World(), false, () => true, CancellationToken.None);

        Assert.StartsWith($"YAV Shell {Fixtures.ProductVersion} is not installed", setup.Console.Output.ToString(), StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(setup.Directory, "yav.exe")));
    }

    [Fact]
    public async Task Opened_from_explorer_an_older_installation_is_replaced_after_yes()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Yav.Console.Cli.CommandLine.Parse(["install"]), setup.World(version: "0.1.1"), CancellationToken.None);
        setup.Console.Output.GetStringBuilder().Clear();
        setup.Console.Answer("y", string.Empty);

        var ended = await InstallOffer.OfferAsync(setup.World(ownWindow: true), false, () => true, CancellationToken.None);

        Assert.Equal(0, ended);
        var lines = setup.Console.OutputLines;
        Assert.Equal($"YAV Shell 0.1.1 is installed in {setup.Directory}. This is version {Fixtures.ProductVersion}.", lines[0]);
        Assert.StartsWith(InstallReport.ReplaceQuestion + $"Updated YAV Shell from 0.1.1 to {Fixtures.ProductVersion} in {setup.Directory}.", lines[1], StringComparison.Ordinal);
        Assert.Equal(Fixtures.ProductVersion, setup.System.Registration!.DisplayVersion);
    }

    [Fact]
    public async Task An_installation_that_fails_in_a_window_opened_from_explorer_says_why_and_ends_with_exit_code_5()
    {
        using var setup = new Setup();
        setup.Write("Programs/YAV Shell/my own file.txt", "mine");
        setup.Console.Answer(string.Empty, string.Empty);

        var ended = await InstallOffer.OfferAsync(setup.World(ownWindow: true), false, () => true, CancellationToken.None);

        Assert.Equal(5, ended);
        Assert.Contains("was not created by the installation of YAV Shell", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.EndsWith(InstallReport.CloseWindow, setup.Console.Output.ToString(), StringComparison.Ordinal);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(setup.Directory, "my own file.txt")));
    }

    [Fact]
    public async Task An_installation_that_fails_in_a_console_that_was_open_lets_the_shell_start_after_it_said_why()
    {
        using var setup = new Setup();
        setup.Write("Programs/YAV Shell/my own file.txt", "mine");
        setup.Console.Answer(string.Empty);

        var ended = await InstallOffer.OfferAsync(setup.World(), false, () => true, CancellationToken.None);

        Assert.Null(ended);
        Assert.Contains("was not created by the installation of YAV Shell", setup.Console.Error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_registry_that_cannot_be_read_does_not_keep_the_shell_from_starting()
    {
        using var setup = new Setup();
        setup.System.ReadFails = new UnauthorizedAccessException("Access to the registry key is denied.");
        setup.Console.Answer(string.Empty);

        var ended = await InstallOffer.OfferAsync(setup.World(ownWindow: true), false, () => true, CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(
            "yav: the offer to install YAV Shell failed while finding out what is installed: UnauthorizedAccessException: Access to the registry key is denied. The shell starts all the same.",
            setup.Console.Error.ToString().Trim());
        Assert.False(Directory.Exists(setup.Directory));
        Assert.Equal(0, setup.Console.LinesRead);
    }

    [Fact]
    public async Task A_running_installation_is_not_replaced()
    {
        using var setup = new Setup();
        await InstallCommand.InstallAsync(Yav.Console.Cli.CommandLine.Parse(["install"]), setup.World(version: "0.1.1"), CancellationToken.None);
        setup.System.Running.Add(4242);
        setup.Console.Answer("y", string.Empty);

        var ended = await InstallOffer.OfferAsync(setup.World(ownWindow: true), false, () => true, CancellationToken.None);

        Assert.Equal(5, ended);
        Assert.Contains("process 4242", setup.Console.Error.ToString(), StringComparison.Ordinal);
        Assert.Equal("0.1.1", setup.System.Registration!.DisplayVersion);
    }

    [Fact]
    public async Task The_directory_the_offer_shows_is_the_one_it_installs_into()
    {
        using var setup = new Setup();
        setup.Console.Answer(string.Empty);

        // As for the real process, no directory is named: the default directory of the system is shown and used.
        await InstallOffer.OfferAsync(setup.World(named: false), false, () => true, CancellationToken.None);

        Assert.Contains($"Installing it copies yav.exe to {setup.System.DefaultDirectory} and adds that folder to the PATH of your user account,", setup.Console.OutputLines);
        Assert.True(File.Exists(Path.Combine(setup.System.DefaultDirectory, "yav.exe")));
        Assert.Equal(setup.System.DefaultDirectory, setup.System.Registration!.InstallLocation);
    }

    [Fact]
    public async Task A_no_that_could_not_be_saved_says_that_it_is_asked_again()
    {
        using var setup = new Setup();
        setup.Console.Answer("n");

        var ended = await InstallOffer.OfferAsync(setup.World(), false, () => false, CancellationToken.None);

        Assert.Null(ended);
        Assert.Contains("Not installed. 'yav install' installs it. Your answer could not be saved, so it is offered again next time.", setup.Console.Output.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("finding out what is installed")]
    [InlineData("remembering your answer")]
    public async Task Whatever_goes_wrong_in_the_offer_the_shell_starts_and_what_failed_is_said(string doing)
    {
        using var setup = new Setup();
        var defect = new InvalidOperationException("A defect nobody foresaw.");
        if (doing == "finding out what is installed")
        {
            setup.System.ReadFails = defect;
        }

        setup.Console.Answer("n");

        var ended = await InstallOffer.OfferAsync(setup.World(), false, () => throw defect, CancellationToken.None);

        Assert.Null(ended);
        Assert.Equal(
            $"yav: the offer to install YAV Shell failed while {doing}: InvalidOperationException: A defect nobody foresaw. The shell starts all the same.",
            setup.Console.Error.ToString().Trim());
        Assert.False(Directory.Exists(setup.Directory));
    }

    [Fact]
    public async Task A_stop_the_caller_asked_for_is_passed_on()
    {
        using var setup = new Setup();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var world = setup.World() with { ReadLine = token => Task.FromCanceled<string?>(token) };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstallOffer.OfferAsync(world, false, () => true, stop.Token));
    }

    [Fact]
    public async Task Control_c_while_the_offer_asks_installs_nothing_whatever_line_the_console_hands_over()
    {
        using var setup = new Setup();
        using var stop = new CancellationTokenSource();

        // The handler of the process cancels the token while the question waits; the console still returns a yes.
        var world = setup.World() with { ReadLine = _ => { stop.Cancel(); return Task.FromResult<string?>("y"); } };

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => InstallOffer.OfferAsync(world, false, () => true, stop.Token));

        Assert.False(Directory.Exists(setup.Directory));
        Assert.Equal(string.Empty, setup.Console.Error.ToString());
    }

    [Theory]
    [InlineData(true, 5)]
    [InlineData(false, null)]
    public async Task Control_c_stops_an_installation_the_offer_started_between_two_files_and_what_it_wrote_is_said(bool ownWindow, int? exitCode)
    {
        using var setup = new Setup();
        using var stop = new CancellationTokenSource();
        setup.Console.Answer(string.Empty, string.Empty);

        // Control+C arrives while the first file is written: the handler of the process cancels the token the offer got.
        var world = setup.World(ownWindow: ownWindow) with
        {
            Files =
            [
                new PackageFile("docs/user-guide.md", () => { stop.Cancel(); return new MemoryStream("guide"u8.ToArray()); }),
                new PackageFile("LICENSE.txt", () => new MemoryStream("license"u8.ToArray())),
            ],
        };

        var ended = await InstallOffer.OfferAsync(world, false, () => true, stop.Token);

        Assert.Equal(exitCode, ended);
        Assert.Equal(
            $"yav: The installation in {setup.Directory} was stopped before it was complete. {Path.Combine(setup.Directory, Installer.ManifestName)} names the files it may have written: "
            + $"'yav install --dir \"{setup.Directory}\"' again completes the installation, 'yav uninstall --dir \"{setup.Directory}\"' removes it. The PATH and \"Installed apps\" were not changed.",
            setup.Console.Error.ToString().Trim());
        Assert.True(File.Exists(Path.Combine(setup.Directory, "docs", "user-guide.md")));
        Assert.False(File.Exists(Path.Combine(setup.Directory, "LICENSE.txt")));
        Assert.Null(setup.System.Registration);
        Assert.Equal(0, setup.System.PathWrites);
        Assert.Equal(ownWindow, setup.Console.Output.ToString().EndsWith(InstallReport.CloseWindow, StringComparison.Ordinal));
    }
}
