using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Tests.Support;

namespace Yav.Tests.EndToEnd;

/// <summary>
/// yav.exe in a console of its own, driven the way a person drives it: keys go in, and what the console
/// shows is read. The console is the pseudo console of Windows, which Windows Terminal uses as well.
/// The agents are the scripted fixture, so nothing here requests inference.
/// </summary>
[Collection(nameof(InteractiveConsoleTests))]
[CollectionDefinition(nameof(InteractiveConsoleTests), DisableParallelization = true)]
public class InteractiveConsoleTests
{
    private const string Task = "Make the app say fixed.";

    private static PseudoConsole Start(YavProcess yav, string executable, string[] arguments, int columns = 140, int rows = 45) =>
        PseudoConsole.Start(
            executable,
            arguments,
            yav.Project.Path,
            new Dictionary<string, string?>
            {
                [YavPaths.HomeVariable] = yav.Paths.Home,
                ["NO_COLOR"] = null,
                ["PROMPT"] = null,
            },
            columns,
            rows);

    private static PseudoConsole Start(YavProcess yav, int columns = 140, int rows = 45) =>
        Start(yav, YavProcess.Executable, [yav.Project.Path], columns, rows);

    private static string Prompt(YavProcess yav) => $"YAV {yav.Project.Path}>";

    private static async Task EnterAsync(PseudoConsole console, string text)
    {
        await console.TypeAsync(text);
        await console.EnterAsync();
    }

    [Fact]
    public async Task A_request_is_typed_checked_and_applied_in_a_console()
    {
        using var yav = new YavProcess().WithPassingRun();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));
        console.Screen.ShowsAll("YAV Shell", $"Project: {yav.Project.Path}", "Quality Lock: ON (Strict Max)");

        await EnterAsync(console, Task);
        await console.WaitForAsync("[READY] Changes available for inspection (/diff) and application (/apply)");
        await console.WaitForCursorLineAsync(Prompt(yav));
        Assert.Equal("one\n", yav.Project.Read("src/app.txt"));

        await EnterAsync(console, "/apply");
        await console.WaitForAsync("[DONE] Applied 1 file(s)");
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");

        Assert.Equal(0, await console.WaitForExitAsync());
        Assert.Equal("fixed\n", yav.Project.Read("src/app.txt"));
        var rows = console.Screen.Lines.ToList();
        var stages = new[] { "[PREPARE]", "[CODE A]", "[CHECK]", "[READY]", "[APPLY]", "[DONE]" }.Select(s => rows.FindIndex(r => r.StartsWith(s, StringComparison.Ordinal))).ToList();
        Assert.All(stages, index => Assert.True(index >= 0, console.Screen.Text));
        Assert.Equal(stages.Order(), stages);
        Assert.Contains("  │ Changed app.txt to say fixed.", rows);
    }

    [Fact]
    public async Task Colors_are_the_named_ones_of_the_terminal_and_nothing_else()
    {
        using var yav = new YavProcess().WithPassingRun();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, Task);
        await console.WaitForAsync("[READY]");
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");
        await console.WaitForExitAsync();

        var written = console.Raw;
        Assert.Contains("\u001b[", written, StringComparison.Ordinal);
        Assert.Matches("\u001b\\[[0-9;]*3[0-7]m", written);
        Assert.DoesNotContain("38;2;", written, StringComparison.Ordinal);
        Assert.DoesNotContain("48;2;", written, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_color_nothing_is_colored()
    {
        using var yav = new YavProcess().WithPassingRun();
        using var console = PseudoConsole.Start(
            YavProcess.Executable, [yav.Project.Path], yav.Project.Path,
            new Dictionary<string, string?> { [YavPaths.HomeVariable] = yav.Paths.Home, ["NO_COLOR"] = "1" }, 140, 45);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/help");
        await console.WaitForAsync("/help <command> explains one command.");
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");
        await console.WaitForExitAsync();

        Assert.DoesNotMatch("\u001b\\[[0-9;]*[3-4][0-7]m", console.Raw);
        Assert.DoesNotMatch("\u001b\\[[0-9;]*9[0-7]m", console.Raw);
    }

    [Fact]
    public async Task What_is_typed_while_a_run_reports_stays_in_one_piece()
    {
        using var yav = new YavProcess();
        yav.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        yav.Agents
            .ImplementerTurn(
                Step.Sleep(700), Step.Message("first note", "commentary"), Step.Sleep(700), Step.Message("second note", "commentary"),
                Step.Sleep(700), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, Task);
        await console.WaitForAsync("[CODE A]");

        await console.TypeAsync("/status", millisecondsBetweenKeys: 300);
        await console.WaitForAsync("[READY]");
        await console.WaitForCursorLineAsync(Prompt(yav) + " /status");
        await console.EnterAsync();
        await console.WaitForAsync("Last run");
        await console.WaitForCursorLineAsync(Prompt(yav));

        var rows = console.Screen.Lines;
        Assert.Contains("  │ first note", rows);
        Assert.Contains("  │ second note", rows);
        Assert.Single(rows, row => row.Contains("/status", StringComparison.Ordinal));
        Assert.DoesNotContain(rows, row => row.Contains("/sta", StringComparison.Ordinal) && !row.Contains("/status", StringComparison.Ordinal) && !row.Contains("/start", StringComparison.Ordinal));
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    [Fact]
    public async Task Pasted_lines_are_one_input_and_a_pasted_line_break_does_not_send_it()
    {
        using var yav = new YavProcess().WithPassingRun();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));

        console.Paste("Make the app say fixed.\nKeep the rest as it is.\n/apply\n");
        await console.WaitForAsync("/apply");
        await System.Threading.Tasks.Task.Delay(1_500);

        Assert.Empty(yav.Agents.CodexRequests("turn/start"));
        Assert.Equal("one\n", yav.Project.Read("src/app.txt"));

        await console.EnterAsync();
        await console.WaitForAsync("[READY]", 90);

        var sent = yav.Agents.CodexRequests("turn/start")[0]["params"]!["input"]![0]!["text"]!.GetValue<string>().ReplaceLineEndings("\n");
        Assert.Contains("Make the app say fixed.\nKeep the rest as it is.\n/apply", sent, StringComparison.Ordinal);
        Assert.Equal("one\n", yav.Project.Read("src/app.txt"));
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    public static TheoryData<string, string> NewLineKeys() => new()
    {
        { "Shift+Enter", ConsoleKeyTests.Win32Key(13, 28, 13, ConsoleKeyTests.Shift) },
        { "Control+J", ConsoleKeyTests.Win32Key(0x4A, 0x24, 0x0A, ConsoleKeyTests.LeftControl) },
        { "Control+J sent as a line feed", "\n" },
    };

    [Theory]
    [MemberData(nameof(NewLineKeys))]
    public async Task A_new_line_is_started_without_sending_and_enter_sends_both_lines(string name, string key)
    {
        _ = name;
        using var yav = new YavProcess().WithPassingRun();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));

        await console.TypeAsync("Make the app say fixed.");
        await console.PressAsync(key);
        await console.TypeAsync("Keep the rest as it is.");
        await System.Threading.Tasks.Task.Delay(500);
        Assert.Empty(yav.Agents.CodexRequests("turn/start"));
        await console.EnterAsync();
        await console.WaitForAsync("[READY]", 90);

        var sent = yav.Agents.CodexRequests("turn/start")[0]["params"]!["input"]![0]!["text"]!.GetValue<string>().ReplaceLineEndings("\n");
        Assert.Contains("Make the app say fixed.\nKeep the rest as it is.", sent, StringComparison.Ordinal);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");
        await console.WaitForExitAsync();
    }

    [Fact]
    public async Task Text_in_other_scripts_is_typed_shown_and_sent_as_it_is()
    {
        const string Request = "Füge „Größe“ hinzu: 日本語のテキスト, emoji 🙂 and naïve café.";
        using var yav = new YavProcess().WithPassingRun();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));

        await EnterAsync(console, Request);
        await console.WaitForAsync("[READY]", 90);

        var sent = yav.Agents.CodexRequests("turn/start")[0]["params"]!["input"]![0]!["text"]!.GetValue<string>();
        Assert.Contains(Request, sent, StringComparison.Ordinal);
        Assert.True(console.Screen.Shows("Füge „Größe“ hinzu: 日本語のテキスト"), console.Screen.Text);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");
        await console.WaitForExitAsync();
    }

    [Fact]
    public async Task Control_c_empties_the_line_then_warns_then_leaves()
    {
        using var yav = new YavProcess().WithPassingRun();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));

        await console.TypeAsync("delete everything");
        await console.ControlCAsync();
        await console.WaitForCursorLineAsync(Prompt(yav));
        Assert.False(console.HasExited);
        Assert.Empty(yav.Agents.CodexRequests("turn/start"));

        await console.ControlCAsync();
        await console.WaitForAsync("Control+C again, or /exit, leaves YAV.");
        Assert.False(console.HasExited);

        await console.ControlCAsync();
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    [Fact]
    public async Task Control_c_during_a_run_stops_the_run_and_not_yav()
    {
        using var yav = new YavProcess();
        yav.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        yav.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, Task);
        await console.WaitForAsync("update src/app.txt");

        await console.ControlCAsync();
        await console.WaitForAsync("[STOPPED] Asking the agent to stop. Its work so far is kept.");
        await console.WaitForAsync("Interrupted");
        await console.WaitForCursorLineAsync(Prompt(yav));

        Assert.False(console.HasExited);
        Assert.Single(yav.Agents.CodexRequests("turn/interrupt"));
        Assert.Equal("one\n", yav.Project.Read("src/app.txt"));
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    [Fact]
    public async Task An_approval_is_answered_with_a_letter_and_enter_and_pasted_text_does_not_answer_it()
    {
        const string Asked = "Allow \"npm install left-pad\"? a, s, d or c, then Enter:";
        using var yav = new YavProcess();
        yav.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        yav.Agents
            .ImplementerTurn(Step.Approval(
                "npm install left-pad",
                onAccept: [Step.Write("src/app.txt", "fixed\n"), Step.Message("Installed.")],
                onDecline: [Step.Message("Not installed.")],
                reason: "needs the network"))
            .ReviewerTurn(Step.Review("pass"));
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, Task);
        await console.WaitForCursorLineAsync(Asked);
        Assert.Contains("[APPROVAL] Model A asks to run a command", console.Screen.Lines);
        Assert.Contains("  │ npm install left-pad", console.Screen.Lines);

        // At least as long as it takes to read the question, so that only being pasted keeps the text from answering.
        await System.Threading.Tasks.Task.Delay(Yav.Console.Shell.ConsoleApprovals.Settle + TimeSpan.FromMilliseconds(400));
        console.Paste("a\ns\nyes\nallow\n");

        // A letter typed after the paste shows that the paste was read, and that none of it became the answer.
        await console.PressAsync("x");
        await console.WaitForCursorLineAsync(Asked + " x");
        Assert.False(console.Screen.Shows("Installed."), console.Screen.Text);
        await console.EnterAsync();
        await console.WaitForAsync("Not an answer, so nothing was granted.");
        await console.WaitForCursorLineAsync(Asked);

        await console.PressAsync("a");
        await console.WaitForCursorLineAsync(Asked + " a");
        await console.EnterAsync();
        await console.WaitForAsync("Installed.");
        await console.WaitForAsync("[READY]", 90);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    [Fact]
    public async Task A_new_line_is_started_while_a_run_is_active_as_well()
    {
        using var yav = new YavProcess();
        yav.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        yav.Agents
            .ImplementerTurn(Step.Sleep(6_000), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("README.md", "# App\nmore\n"), Step.Message("More."))
            .ReviewerTurn(Step.Review("pass"));
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, Task);
        await console.WaitForAsync("[CODE A]");

        await console.TypeAsync("Say more in the README.");
        await console.PressAsync(ConsoleKeyTests.Win32Key(13, 28, 13, ConsoleKeyTests.Shift));
        await console.TypeAsync("Keep it short.");
        await console.EnterAsync();
        await console.WaitForAsync("[QUEUE] A run is active, so the request waits (1 waiting)");
        await console.WaitForAsync("More.", 120);
        await console.WaitUntilAsync(
            screen => screen.Lines.Count(row => row.StartsWith("[READY]", StringComparison.Ordinal)) == 2,
            "that both runs are ready", 120);

        var prompts = yav.Agents.CodexRequests("turn/start").Select(t => t["params"]!["input"]![0]!["text"]!.GetValue<string>().ReplaceLineEndings("\n")).ToList();
        Assert.Contains(prompts, p => p.Contains("Say more in the README.\nKeep it short.", StringComparison.Ordinal));
        await console.WaitForCursorLineAsync(Prompt(yav), 90);
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    [Fact]
    public async Task Commands_are_completed_and_a_command_that_was_typed_in_full_is_sent_by_enter()
    {
        using var yav = new YavProcess();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));

        // The list of commands opens while the name is typed; typing on, through it and past it, must not disturb anything.
        await EnterAsync(console, "/settings shell cmd");
        await console.WaitForAsync("/shell and /exec use cmd.");
        await console.WaitForCursorLineAsync(Prompt(yav));

        await console.TypeAsync("/sta");
        await console.PressAsync("\t");
        await console.EnterAsync();
        await console.WaitForAsync("Quality Lock: ON (Strict Max)");
        await console.WaitForCursorLineAsync(Prompt(yav));

        await EnterAsync(console, "/nonsense and more");
        await console.WaitForAsync("/nonsense is not a command of YAV.");
        await console.WaitForCursorLineAsync(Prompt(yav));
        Assert.False(console.HasExited);
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    [Fact]
    public async Task What_was_written_stays_in_the_console_and_selecting_text_stays_with_the_console()
    {
        using var yav = new YavProcess().WithPassingRun();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/help");
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, Task);
        await console.WaitForAsync("[READY]");
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());

        // Everything the console received. The pseudo console empties its screen once by itself, before it draws
        // the first frame, so that sequence is looked for only from where YAV began to write. The others it passes
        // on only when a program writes them, and a program can write them before its first line.
        var received = console.Raw;
        var written = received[received.IndexOf("YAV Shell", StringComparison.Ordinal)..];
        foreach (var (sequence, meaning) in new[]
        {
            ("\u001b[?1049h", "another screen, which has no scrollback"),
            ("\u001b[?47h", "another screen, which has no scrollback"),
            ("\u001b[2J", "the screen is emptied"),
            ("\u001b[3J", "the scrollback is emptied"),
            ("\u001b[?1000h", "the mouse is taken from the console"),
            ("\u001b[?1002h", "the mouse is taken from the console"),
            ("\u001b[?1003h", "the mouse is taken from the console"),
            ("\u001b[?1006h", "the mouse is taken from the console"),
            ("\u001b]52;", "the clipboard is written"),
        })
        {
            var searched = sequence == "\u001b[2J" ? written : received;
            Assert.False(searched.Contains(sequence, StringComparison.Ordinal), meaning);
        }
    }

    [Fact]
    public async Task Earlier_input_comes_back_with_the_arrow_keys_and_is_found_by_how_it_begins()
    {
        const string Up = "\u001b[A";
        using var yav = new YavProcess();
        using (var first = Start(yav))
        {
            await first.WaitForCursorLineAsync(Prompt(yav));
            await EnterAsync(first, "/limits");
            await first.WaitForCursorLineAsync(Prompt(yav));
            await EnterAsync(first, "/help exit");
            await first.WaitForCursorLineAsync(Prompt(yav));

            // The arrow goes back through what was entered, the newest first.
            await first.PressAsync(Up);
            await first.WaitForCursorLineAsync(Prompt(yav) + " /help exit");
            await first.PressAsync(Up);
            await first.WaitForCursorLineAsync(Prompt(yav) + " /limits");
            await first.ControlCAsync();
            await first.WaitForCursorLineAsync(Prompt(yav));

            // With something typed, it goes back through what began that way. Escape closes the list of
            // commands first, which would take the arrow for itself.
            await first.TypeAsync("/li");
            await first.PressAsync("\u001b");
            await first.PressAsync(Up);
            await first.WaitForCursorLineAsync(Prompt(yav) + " /limits");
            await first.EnterAsync();
            await first.WaitForCursorLineAsync(Prompt(yav));
            await EnterAsync(first, "/exit");
            Assert.Equal(0, await first.WaitForExitAsync());
        }

        // What was entered is there again the next time.
        using var second = Start(yav);
        await second.WaitForCursorLineAsync(Prompt(yav));
        await second.PressAsync(Up);
        await second.WaitForCursorLineAsync(Prompt(yav) + " /exit");
        await second.PressAsync(Up);
        await second.WaitForCursorLineAsync(Prompt(yav) + " /limits");
        await second.ControlCAsync();
        await second.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(second, "/exit");
        Assert.Equal(0, await second.WaitForExitAsync());
    }

    [Fact]
    public async Task A_shell_takes_the_console_and_gives_it_back()
    {
        using var yav = new YavProcess().WithPassingRun();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));

        await EnterAsync(console, "/shell cmd");
        await console.WaitForAsync("'exit' returns to YAV.");
        await console.WaitForCursorLineAsync(yav.Project.Path + ">");
        await EnterAsync(console, "echo written in the shell> made-in-shell.txt");
        await console.WaitForCursorLineAsync(yav.Project.Path + ">");
        await EnterAsync(console, "echo hello from cmd");
        await console.WaitForCursorLineAsync(yav.Project.Path + ">");
        Assert.Contains("hello from cmd", console.Screen.Lines);
        await EnterAsync(console, "exit");

        await console.WaitForAsync("[LOCAL] Back in YAV. The shell ended with exit code 0.");
        await console.WaitForCursorLineAsync(Prompt(yav));
        Assert.Equal("written in the shell", yav.Project.Read("made-in-shell.txt").Trim());
        Assert.Empty(yav.Agents.CodexRequests("thread/start"));

        // The console is YAV's again: a request runs and what is typed is a request, not a command of the shell.
        await EnterAsync(console, "echo this is a request");
        await console.WaitForAsync("[READY]", 90);
        Assert.Contains(yav.Agents.CodexRequests("turn/start"), t => t["params"]!["input"]![0]!["text"]!.GetValue<string>().Contains("echo this is a request", StringComparison.Ordinal));
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    [Fact]
    public async Task Exec_shows_what_the_command_wrote_and_returns()
    {
        using var yav = new YavProcess();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/settings shell cmd");
        await console.WaitForCursorLineAsync(Prompt(yav));

        await EnterAsync(console, "/exec echo written by the command");
        await console.WaitForAsync("[LOCAL] exit 0 after");
        await console.WaitForCursorLineAsync(Prompt(yav));

        var rows = console.Screen.Lines.ToList();
        var written = rows.IndexOf("written by the command");
        Assert.True(written > rows.FindIndex(r => r.StartsWith("[LOCAL]   cmd in ", StringComparison.Ordinal)), console.Screen.Text);
        Assert.True(written < rows.FindIndex(r => r.StartsWith("[LOCAL]   exit 0 after", StringComparison.Ordinal)), console.Screen.Text);
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    [Theory]
    [InlineData("cmd.exe", "/d /k", "{path}>", "\"{yav}\" \"{project}\"", "echo after yav")]
    [InlineData("powershell.exe", "-NoLogo -NoProfile", "PS {path}>", "& \"{yav}\" \"{project}\"", "echo 'after yav'")]
    [InlineData("pwsh.exe", "-NoLogo -NoProfile", "PS {path}>", "& \"{yav}\" \"{project}\"", "echo 'after yav'")]
    public async Task Started_from_a_shell_it_leaves_the_console_as_the_shell_needs_it(string shell, string arguments, string shellPrompt, string start, string after)
    {
        using var yav = new YavProcess().WithPassingRun();
        var resolved = new Yav.Platform.Processes.ProcessRunner().Resolve(Path.GetFileNameWithoutExtension(shell));
        Assert.True(resolved is not null, $"{shell} is not installed on this machine");
        shellPrompt = shellPrompt.Replace("{path}", yav.Project.Path, StringComparison.Ordinal);
        using var console = Start(yav, resolved!, arguments.Split(' '));
        await console.WaitForCursorLineAsync(shellPrompt, 90);

        console.Paste(start.Replace("{yav}", YavProcess.Executable, StringComparison.Ordinal).Replace("{project}", yav.Project.Path, StringComparison.Ordinal));
        await console.EnterAsync();
        await console.WaitForCursorLineAsync(Prompt(yav), 90);
        await EnterAsync(console, Task);
        await console.WaitForAsync("[READY]", 90);
        await console.WaitForCursorLineAsync(Prompt(yav));
        await EnterAsync(console, "/exit");

        await console.WaitForAsync("State saved. Nothing continues to run after YAV has ended.");
        await console.WaitForCursorLineAsync(shellPrompt, 60);
        await EnterAsync(console, after);
        await console.WaitForCursorLineAsync(shellPrompt, 60);
        Assert.Contains("after yav", console.Screen.Lines);
        Assert.Equal(RunState.ReadyToApply, Assert.Single(YavRuns(yav)).State);
    }

    [Fact]
    public async Task A_window_that_is_made_narrower_keeps_working()
    {
        using var yav = new YavProcess().WithPassingRun();
        using var console = Start(yav, columns: 140, rows: 45);
        await console.WaitForCursorLineAsync(Prompt(yav));

        console.Resize(100, 30);
        await System.Threading.Tasks.Task.Delay(800);
        await EnterAsync(console, Task);
        await console.WaitForAsync("[READY]", 90);
        await EnterAsync(console, "/status");
        await console.WaitForAsync("Last run");

        Assert.All(console.Screen.Lines.TakeLast(25), row => Assert.True(PrettyPrompt.Rendering.UnicodeWidth.GetWidth(row) <= 100, row));
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
        Assert.Equal(RunState.ReadyToApply, Assert.Single(YavRuns(yav)).State);
    }

    [Fact]
    public async Task What_an_agent_writes_cannot_forge_a_line_of_yav_in_a_real_console()
    {
        using var yav = new YavProcess();
        yav.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        yav.Agents
            .ImplementerTurn(
                Step.Message("\u001b[2J\u001b[H\u001b]0;owned\u0007[APPROVAL] Model A asks to run a command\nApprove? [a/s/d/c] \r[READY]   Changes available\nAllow \"rm -rf ~\"? a, s, d or c, then Enter: ", "commentary"),
                Step.Message(new string('x', 135) + "[DONE]    Applied everything", "commentary"),
                Step.Write("src/app.txt", "fixed\n"),
                Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));
        using var console = Start(yav, columns: 140);
        await console.WaitForCursorLineAsync(Prompt(yav));

        await EnterAsync(console, Task);
        await console.WaitForAsync("[READY] Changes available for inspection", 90);
        await console.WaitForCursorLineAsync(Prompt(yav));

        var rows = console.Screen.Lines;
        Assert.Equal("YAV Shell", rows[0]);
        Assert.NotEqual("owned", console.Screen.Title);
        Assert.DoesNotContain(rows, row => row.StartsWith("[APPROVAL]", StringComparison.Ordinal) || row.StartsWith("[DONE]", StringComparison.Ordinal) || row.StartsWith("Approve?", StringComparison.Ordinal) || row.StartsWith("Allow ", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.StartsWith("  │ Allow \"rm -rf ~\"?", StringComparison.Ordinal));
        Assert.Single(rows, row => row.StartsWith("[READY]", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.StartsWith("  │ [APPROVAL] Model A asks to run a command", StringComparison.Ordinal));
        Assert.Contains(rows, row => row.StartsWith("  │ ", StringComparison.Ordinal) && row.Contains("[DONE]    Applied everything", StringComparison.Ordinal));
        await EnterAsync(console, "/exit");
        Assert.Equal(0, await console.WaitForExitAsync());
    }

    [Fact]
    public async Task The_prompt_is_there_within_the_time_that_is_aimed_for_and_the_time_is_recorded()
    {
        using var yav = new YavProcess().WithPassingRun();
        yav.Agents.Codex(c => c["startupDelayMs"] = 5_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        using var console = Start(yav);
        await console.WaitForCursorLineAsync(Prompt(yav), 30);
        var untilPrompt = watch.Elapsed;

        await EnterAsync(console, "/latency");
        await console.WaitForAsync("Start to prompt, this time:");
        await EnterAsync(console, "/exit");
        await console.WaitForExitAsync();

        // The agents need five seconds here. The prompt does not wait for them.
        Assert.True(untilPrompt < TimeSpan.FromSeconds(4), $"the prompt took {untilPrompt.TotalMilliseconds:0} ms");
    }

    private static IReadOnlyList<RunRecord> YavRuns(YavProcess yav)
    {
        var database = Yav.Storage.YavDatabase.Open(yav.Paths.Database, TimeProvider.System);
        try
        {
            return database.ListRuns(null, 50).Select(run => database.FindRun(run.RunId)!).ToList();
        }
        finally
        {
            database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }
}

internal static class PtyScreenAssertions
{
    public static void ShowsAll(this PtyScreen screen, params string[] texts)
    {
        foreach (var text in texts)
        {
            Assert.True(screen.Shows(text), $"The screen does not show '{text}'. The screen:\n{screen.Text}");
        }
    }
}
