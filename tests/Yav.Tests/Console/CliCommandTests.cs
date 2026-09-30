using System.Text;
using System.Text.Json;
using Yav.Console.Cli;
using Yav.Console.Doctor;
using Yav.Console.Output;
using Yav.Console.Rendering;
using Yav.Core.Runs;
using Yav.Platform.Consoles;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

/// <summary>'yav run' and 'yav doctor': no prompt, a result, and an exit code that says how it ended.</summary>
public class CliCommandTests
{
    private const string Task = "Make the app say fixed.";

    private sealed record Ran(int ExitCode, string Output, string Error)
    {
        public List<JsonElement> Json => Output
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => JsonDocument.Parse(line).RootElement.Clone())
            .ToList();

        public JsonElement Result => Json[^1];

        public string[] Lines => Output.ReplaceLineEndings("\n").Split('\n');
    }

    private static async Task<Ran> RunAsync(ShellHarness shell, CliOptions options, CancellationToken stop = default)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new Screen(new StreamTerminal(output), new ScreenOptions(Rich: false, Color: false, Unicode: false));
        var exitCode = await RunCommand.ExecuteAsync(options, shell.Services, screen, error, stop);
        return new Ran(exitCode, output.ToString(), error.ToString());
    }

    private static CliOptions Run(ShellHarness shell, bool json = true, bool apply = false, string? task = Task, string? promptFile = null) =>
        new(CliMode.Run, shell.Project.Path, promptFile is null ? task : null, promptFile, json, Plain: true, apply);

    [Fact]
    public async Task With_json_every_line_of_the_output_is_a_json_object_and_the_last_one_is_the_result()
    {
        await using var shell = new ShellHarness().WithPassingRun();

        var ran = await RunAsync(shell, Run(shell));

        Assert.Equal(0, ran.ExitCode);
        Assert.Equal(string.Empty, ran.Error);
        Assert.All(ran.Json, line => Assert.Equal(JsonValueKind.Object, line.ValueKind));
        Assert.All(ran.Json.SkipLast(1), line => Assert.Equal("event", line.GetProperty("type").GetString()));
        Assert.Equal("result", ran.Result.GetProperty("type").GetString());
        Assert.Equal("ready_to_apply", ran.Result.GetProperty("outcome").GetString());
        Assert.Equal(0, ran.Result.GetProperty("exitCode").GetInt32());
        Assert.Equal(JsonValueKind.Null, ran.Result.GetProperty("apply").ValueKind);
        Assert.Equal("src/app.txt", ran.Result.GetProperty("candidate").GetProperty("files")[0].GetProperty("path").GetString());
        Assert.True(ran.Result.GetProperty("acceptance").GetProperty("accepted").GetBoolean());
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));

        var events = ran.Json.Select(line => line.TryGetProperty("event", out var name) ? name.GetString() : null).ToList();
        Assert.Contains("run.started", events);
        Assert.Contains("candidate", events);
        Assert.Contains("review", events);
        Assert.Contains("gate.result", events);
        Assert.Contains("acceptance", events);
        Assert.Equal("run.finished", events[^2]);
    }

    [Fact]
    public async Task States_are_written_the_same_way_everywhere_in_the_json()
    {
        await using var shell = new ShellHarness().WithPassingRun();

        var ran = await RunAsync(shell, Run(shell));

        var states = ran.Json.Where(l => l.TryGetProperty("event", out var e) && e.GetString() == "state").Select(l => l.GetProperty("to").GetString()).ToList();
        Assert.Equal(["implementing", "checking", "ready_to_apply"], states);
        Assert.Equal("ready_to_apply", ran.Result.GetProperty("state").GetString());
        Assert.Equal("ready_to_apply", ran.Json.Single(l => l.TryGetProperty("event", out var e) && e.GetString() == "run.finished").GetProperty("state").GetString());
    }

    [Fact]
    public async Task With_apply_an_accepted_candidate_is_written_into_the_project()
    {
        await using var shell = new ShellHarness().WithPassingRun();

        var ran = await RunAsync(shell, Run(shell, apply: true));

        Assert.Equal(0, ran.ExitCode);
        Assert.True(ran.Result.GetProperty("apply").GetProperty("applied").GetBoolean());
        Assert.Equal("fixed\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task With_apply_a_candidate_that_was_not_accepted_is_not_written()
    {
        await using var shell = new ShellHarness();
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "fixed"));
        shell.Services.Update(s => s with { Limits = s.Limits with { MaxRepairCycles = 0 } });
        shell.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var ran = await RunAsync(shell, Run(shell, apply: true));

        Assert.Equal(ExitCodes.Blocked, ran.ExitCode);
        Assert.Equal("blocked", ran.Result.GetProperty("outcome").GetString());
        Assert.Equal(JsonValueKind.Null, ran.Result.GetProperty("apply").ValueKind);
        Assert.False(ran.Result.GetProperty("acceptance").GetProperty("accepted").GetBoolean());
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task A_request_for_approval_that_nobody_can_answer_ends_as_approval_required_and_grants_nothing()
    {
        await using var shell = new ShellHarness();
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents.ImplementerTurn(Step.Approval(
            "npm install left-pad",
            onAccept: [Step.Write("src/app.txt", "installed\n"), Step.Message("Installed.")],
            onDecline: [Step.Message("Not installed.")],
            reason: "needs the network"));

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var ran = await RunAsync(shell, Run(shell));

        Assert.Equal(ExitCodes.ApprovalRequired, ran.ExitCode);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(60), "the run waited for an answer nobody could give");
        Assert.Equal("approval_required", ran.Result.GetProperty("outcome").GetString());
        var pending = Assert.Single(ran.Result.GetProperty("pendingApprovals").EnumerateArray());
        Assert.Equal("npm install left-pad", pending.GetProperty("command").GetString());
        Assert.Equal("needs the network", pending.GetProperty("reason").GetString());
        Assert.DoesNotContain("Installed.", ran.Output, StringComparison.Ordinal);
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
        var answer = Assert.Single(shell.Agents.Received("codex.response"));
        Assert.NotEqual("accept", answer["result"]?["decision"]?.GetValue<string>());
        Assert.NotEqual("acceptForSession", answer["result"]?["decision"]?.GetValue<string>());
    }

    [Fact]
    public async Task What_was_asked_for_is_named_as_it_is_and_in_quotes_in_the_summary()
    {
        await using var shell = new ShellHarness();
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents.ImplementerTurn(Step.Approval("rm -rf \u001b[ ~", onDecline: [Step.Message("Not removed.")]));

        var ran = await RunAsync(shell, Run(shell, json: false));

        Assert.Equal(ExitCodes.ApprovalRequired, ran.ExitCode);
        Assert.DoesNotContain('\u001b', ran.Output);
        Assert.Contains("Asked:    \"rm -rf \\x1B[ ~\"", ran.Lines);
        Assert.Contains(ran.Lines, line => line.StartsWith("Reason:", StringComparison.Ordinal) && line.Contains("rm -rf \\x1B[ ~", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_without_approved_checks_is_blocked_and_nothing_is_sent()
    {
        await using var shell = new ShellHarness();
        shell.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n"));

        var ran = await RunAsync(shell, Run(shell));

        Assert.Equal(ExitCodes.Blocked, ran.ExitCode);
        Assert.Equal("blocked", ran.Result.GetProperty("outcome").GetString());
        Assert.NotEmpty(ran.Result.GetProperty("problems").EnumerateArray());
        Assert.Empty(shell.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_usage_limit_of_the_provider_ends_as_rate_limited()
    {
        await using var shell = new ShellHarness();
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Fail("You have hit your limit.", info: "usageLimitExceeded"));

        var ran = await RunAsync(shell, Run(shell));

        Assert.Equal(ExitCodes.RateLimited, ran.ExitCode);
        Assert.Equal("rate_limited", ran.Result.GetProperty("outcome").GetString());
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task A_failure_of_the_agent_ends_as_failed()
    {
        await using var shell = new ShellHarness();
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents.ImplementerTurn(Step.Fail("The model is unavailable."));

        var ran = await RunAsync(shell, Run(shell));

        Assert.Equal(ExitCodes.Failed, ran.ExitCode);
        Assert.Equal("failed", ran.Result.GetProperty("outcome").GetString());
    }

    [Fact]
    public async Task Stopping_ends_as_interrupted_and_keeps_what_can_be_continued()
    {
        await using var shell = new ShellHarness();
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        using var stop = new CancellationTokenSource();

        var running = RunAsync(shell, Run(shell), stop.Token);
        while (shell.Agents.CodexRequests("turn/start").Count == 0)
        {
            await System.Threading.Tasks.Task.Delay(50);
        }

        await System.Threading.Tasks.Task.Delay(300);
        await stop.CancelAsync();
        var ran = await running.WaitAsync(TimeSpan.FromSeconds(60));

        Assert.Equal(ExitCodes.Interrupted, ran.ExitCode);
        Assert.Equal("interrupted", ran.Result.GetProperty("outcome").GetString());
        var run = shell.Services.Database.FindRun(ran.Result.GetProperty("runId").GetString()!)!;
        Assert.Equal(RunState.Interrupted, run.State);
    }

    [Fact]
    public async Task What_an_agent_wrote_reaches_the_json_without_control_sequences()
    {
        await using var shell = new ShellHarness();
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents
            .ImplementerTurn(Step.Message("\u001b[2J\u001b]0;title\u0007 cleared \u001b[31mred", "commentary"), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var ran = await RunAsync(shell, Run(shell));

        Assert.Equal(0, ran.ExitCode);
        Assert.DoesNotContain('\u001b', ran.Output);
        Assert.DoesNotContain('\u0007', ran.Output);
        Assert.Contains(ran.Json, l => l.TryGetProperty("text", out var text) && text.GetString()!.Contains("cleared", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_json_the_stages_and_a_summary_are_written_as_plain_lines()
    {
        await using var shell = new ShellHarness().WithPassingRun();

        var ran = await RunAsync(shell, Run(shell, json: false));

        Assert.Equal(0, ran.ExitCode);
        Assert.DoesNotContain('\u001b', ran.Output);
        var lines = ran.Lines.ToList();
        var stages = new[] { "[PREPARE]", "[CODE A]", "[CHECK]", "[READY]" }.Select(s => lines.FindIndex(l => l.StartsWith(s, StringComparison.Ordinal))).ToList();
        Assert.All(stages, index => Assert.True(index >= 0, ran.Output));
        Assert.Equal(stages.Order(), stages);
        Assert.Contains("  | Changed app.txt to say fixed.", lines);
        Assert.Contains("Outcome:  Ready to Apply", lines);
        Assert.Contains(lines, l => l.StartsWith("Changes:  1 file(s): +0 ~1 -0", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("Next:     the project was not changed.", StringComparison.Ordinal));
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task In_a_window_what_an_agent_wrote_stays_behind_its_mark_on_every_row()
    {
        await using var shell = new ShellHarness();
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents
            .ImplementerTurn(Step.Message(new string('x', 75) + "[READY]   Changes available", "commentary"), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        // Eighty columns of which the last stays free: four for the mark and 75 for the text, so the forged line begins a row.
        var terminal = new VirtualTerminal(80, 60);
        var screen = new Screen(terminal, new ScreenOptions(Rich: false, Color: false, Unicode: false));
        using var error = new StringWriter();

        var exitCode = await RunCommand.ExecuteAsync(Run(shell, json: false), shell.Services, screen, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Empty(terminal.RowsTheTerminalBegan);
        Assert.Single(terminal.Lines, row => row.StartsWith("[READY]", StringComparison.Ordinal));
        Assert.Contains(terminal.Lines, row => row.StartsWith("  | [READY]   Changes available", StringComparison.Ordinal));
    }

    [Fact]
    public async Task The_request_is_read_from_a_file_exactly_as_it_is()
    {
        await using var shell = new ShellHarness().WithPassingRun();
        var request = "Make the app say fixed.\n\n  - keep the indentation\n  - and \"quotes\", $variables and %PATH%\n";
        var file = Path.Combine(shell.Paths.Home, "request ünï.md");
        File.WriteAllBytes(file, [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes(request)]);

        var ran = await RunAsync(shell, Run(shell, promptFile: file));

        Assert.Equal(0, ran.ExitCode);
        var sent = shell.Agents.CodexRequests("turn/start").Select(t => t["params"]!["input"]![0]!["text"]!.GetValue<string>()).First();
        Assert.Contains(request.TrimEnd(), sent, StringComparison.Ordinal);
        Assert.DoesNotContain('\uFEFF', sent);
    }

    [Theory]
    [InlineData("Make the app say fixed.\n")]
    [InlineData("Make the app say fixed.\r\n\r\n")]
    [InlineData("Make the app say fixed.")]
    public async Task The_line_break_at_the_end_of_a_prompt_file_is_not_part_of_the_request(string content)
    {
        await using var shell = new ShellHarness().WithPassingRun();
        var file = Path.Combine(shell.Paths.Home, "request.md");
        File.WriteAllText(file, "  indented, which stays\n" + content);

        var ran = await RunAsync(shell, Run(shell, promptFile: file));

        var started = ran.Json.Single(l => l.TryGetProperty("event", out var e) && e.GetString() == "run.started");
        Assert.Equal("  indented, which stays\nMake the app say fixed.", started.GetProperty("request").GetString());
    }

    [Theory]
    [InlineData("missing", "does not exist")]
    [InlineData("empty", "is empty")]
    [InlineData("binary", "is not UTF-8 text")]
    [InlineData("large", "is not sent")]
    public async Task A_prompt_file_that_cannot_be_used_sends_nothing(string kind, string expected)
    {
        await using var shell = new ShellHarness().WithPassingRun();
        var file = Path.Combine(shell.Paths.Home, kind + ".md");
        switch (kind)
        {
            case "empty":
                File.WriteAllText(file, "  \r\n ");
                break;
            case "binary":
                File.WriteAllBytes(file, [0x50, 0x4B, 0x03, 0x04, 0xFF, 0xFE, 0xC0, 0x80]);
                break;
            case "large":
                File.WriteAllText(file, new string('a', (1024 * 1024) + 1));
                break;
        }

        var ran = await RunAsync(shell, Run(shell, promptFile: file));

        Assert.Equal(ExitCodes.Invalid, ran.ExitCode);
        Assert.Equal("invalid", ran.Result.GetProperty("outcome").GetString());
        Assert.Contains(expected, ran.Result.GetProperty("reason").GetString(), StringComparison.Ordinal);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
        Assert.Empty(shell.Services.Database.ListRuns(null, 10));
    }

    [Fact]
    public async Task Without_json_a_problem_with_the_command_is_written_to_the_error_output()
    {
        await using var shell = new ShellHarness().WithPassingRun();

        var ran = await RunAsync(shell, Run(shell, json: false, promptFile: Path.Combine(shell.Paths.Home, "missing.md")));

        Assert.Equal(ExitCodes.Invalid, ran.ExitCode);
        Assert.Equal(string.Empty, ran.Output);
        Assert.StartsWith("yav: The prompt file", ran.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_is_continued_by_its_id()
    {
        await using var shell = new ShellHarness().WithPassingRun();
        shell.Agents
            .ImplementerTurn(Step.Write("README.md", "# App\nmore\n"), Step.Message("More."))
            .ReviewerTurn(Step.Review("pass"));
        var first = await RunAsync(shell, Run(shell));
        var taskId = first.Result.GetProperty("taskId").GetString()!;

        var second = await RunAsync(shell, Run(shell, task: "Also say more in the README.") with { TaskId = taskId });

        Assert.Equal(0, second.ExitCode);
        Assert.Equal(taskId, second.Result.GetProperty("taskId").GetString());
        Assert.NotEqual(first.Result.GetProperty("runId").GetString(), second.Result.GetProperty("runId").GetString());
        Assert.Equal(2, second.Result.GetProperty("candidate").GetProperty("files").GetArrayLength());
        Assert.Single(shell.Agents.CodexRequests("thread/start"), t => t["params"]!["model"]!.GetValue<string>() == "model-a");
    }

    private static readonly ConsoleCapabilities Redirected = new(
        InputIsTerminal: false, OutputIsTerminal: false, ErrorIsTerminal: false, VirtualTerminal: false, Unicode: true, Host: "test");

    private static async Task<Ran> DoctorAsync(ShellHarness shell, bool json)
    {
        using var output = new StringWriter();
        var exitCode = await DoctorCommand.ExecuteAsync(
            new CliOptions(CliMode.Doctor, shell.Project.Path, Json: json, Plain: true), shell.Services, Redirected, output, CancellationToken.None);
        return new Ran(exitCode, output.ToString(), string.Empty);
    }

    [Fact]
    public async Task Doctor_writes_one_json_object_with_every_check()
    {
        await using var shell = new ShellHarness().WithPassingRun();

        var ran = await DoctorAsync(shell, json: true);

        Assert.Equal(0, ran.ExitCode);
        var report = Assert.Single(ran.Json);
        Assert.Equal("doctor", report.GetProperty("type").GetString());
        Assert.False(report.GetProperty("problems").GetBoolean());
        var checks = report.GetProperty("checks").EnumerateArray().ToList();
        Assert.All(checks, c => Assert.Contains(c.GetProperty("status").GetString(), new[] { "ok", "info", "warning", "problem" }));
        Assert.Contains(checks, c => c.GetProperty("area").GetString() == "System" && c.GetProperty("name").GetString() == "Runtime");
        Assert.Contains(checks, c => c.GetProperty("name").GetString() == "Run" && c.GetProperty("detail").GetString()!.Contains("can start: A model-a at effort xhigh, B model-b at effort max, checks: tests", StringComparison.Ordinal));
        Assert.Empty(shell.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task Doctor_reports_the_tools_yav_uses_and_claims_nothing_about_others()
    {
        await using var shell = new ShellHarness();

        var ran = await DoctorAsync(shell, json: true);

        var tools = Assert.Single(ran.Json).GetProperty("checks").EnumerateArray()
            .Where(c => c.GetProperty("area").GetString() == "Tools")
            .Select(c => c.GetProperty("name").GetString() ?? string.Empty)
            .ToArray();

        // YAV starts Git. It searches in no file: the agents do that with tools of their own.
        Assert.Equal(["Git"], tools);
        Assert.DoesNotContain("searches", ran.Output, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Doctor_names_the_programs_of_the_store_that_agents_and_checks_are_likely_to_start()
    {
        var check = DoctorChecks.StorePrograms(name => name switch
        {
            "pwsh" => @"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\pwsh.exe",
            "python" => @"C:\Python314\python.exe",
            "python3" => @"C:\Program Files\WindowsApps\PythonSoftwareFoundation.Python.3.14_qbz5n2kfra8p0\python3.exe",
            _ => null,
        });

        Assert.NotNull(check);
        Assert.Equal("System", check.Area);
        Assert.Equal(CheckStatus.Warning, check.Status);
        Assert.Contains(@"pwsh (C:\Users\someone\AppData\Local\Microsoft\WindowsApps\pwsh.exe)", check.Detail, StringComparison.Ordinal);
        Assert.Contains(@"python3 (C:\Program Files\WindowsApps\", check.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain(@"C:\Python314", check.Detail, StringComparison.Ordinal);

        // What it means for a run, and that what YAV starts itself is not meant.
        Assert.Contains("keeps running when the run is stopped", check.Detail, StringComparison.Ordinal);
        Assert.Contains("Started by YAV itself", check.Detail, StringComparison.Ordinal);
        Assert.NotNull(check.Remedy);
    }

    [Fact]
    public void Doctor_does_not_take_the_stand_in_windows_puts_where_python_is_missing_for_a_program_of_the_store()
    {
        // Windows names python and python3 aliases of App Installer, which only point to the Store. They start no Python.
        const string Redirector = @"C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_1.29.380.0_x64__8wekyb3d8bbwe\AppInstallerPythonRedirector.exe";
        var check = DoctorChecks.StorePrograms(
            name => name switch
            {
                "pwsh" => @"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\pwsh.exe",
                "python3" => @"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\python3.exe",
                _ => null,
            },
            alias => alias.EndsWith(@"\python3.exe", StringComparison.Ordinal)
                ? new Yav.Platform.Processes.AppExecutionAliasTarget("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", Redirector)
                : new Yav.Platform.Processes.AppExecutionAliasTarget(
                    "Microsoft.PowerShell_8wekyb3d8bbwe", @"C:\Program Files\WindowsApps\Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe\pwsh.exe"));

        Assert.NotNull(check);
        Assert.Contains("pwsh (", check.Detail, StringComparison.Ordinal);
        Assert.DoesNotContain("python3", check.Detail, StringComparison.Ordinal);
        Assert.Null(DoctorChecks.StorePrograms(
            name => name == "python" ? @"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\python.exe" : null,
            _ => new Yav.Platform.Processes.AppExecutionAliasTarget("Microsoft.DesktopAppInstaller_8wekyb3d8bbwe", Redirector)));
        Assert.Null(DoctorChecks.StorePrograms(name => name == "python" ? Redirector : null, _ => null));
    }

    [Fact]
    public void Doctor_says_nothing_about_the_store_where_no_program_comes_from_it()
    {
        Assert.Null(DoctorChecks.StorePrograms(name => name == "pwsh" ? @"C:\Program Files\PowerShell\7\pwsh.exe" : null));
        Assert.Null(DoctorChecks.StorePrograms(_ => null));
    }

    [Yav.Tests.Platform.StorePowerShellFact(foundOnPath: true)]
    public async Task Doctor_says_that_the_powershell_of_this_machine_comes_from_the_store()
    {
        await using var shell = new ShellHarness();

        var ran = await DoctorAsync(shell, json: true);

        var check = Assert.Single(
            Assert.Single(ran.Json).GetProperty("checks").EnumerateArray(),
            c => c.GetProperty("name").GetString() == "Programs of the Store");
        Assert.Equal("warning", check.GetProperty("status").GetString());
        Assert.Contains("pwsh (", check.GetProperty("detail").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Doctor_says_where_the_runtime_is_that_runs_yav()
    {
        await using var shell = new ShellHarness();

        var ran = await DoctorAsync(shell, json: false);

        var runtime = System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory().TrimEnd('\\');
        Assert.Contains(ran.Lines, line => line.StartsWith("  [ok]     Runtime: ", StringComparison.Ordinal) && line.Contains(runtime, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Doctor_ends_with_a_problem_when_a_run_could_not_start()
    {
        await using var shell = new ShellHarness(new ShellOptions { AcknowledgeRoutes = false }).WithPassingRun();

        var ran = await DoctorAsync(shell, json: false);

        Assert.Equal(ExitCodes.DoctorFoundProblems, ran.ExitCode);
        Assert.Contains(ran.Lines, line => line.StartsWith("  [FAIL]   route-unacknowledged:", StringComparison.Ordinal));
        Assert.Contains(ran.Lines, line => line.Contains("problem(s)", StringComparison.Ordinal));
        Assert.DoesNotContain('\u001b', ran.Output);
    }
}
