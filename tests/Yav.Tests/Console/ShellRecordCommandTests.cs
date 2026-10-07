using Yav.Console.Commands;
using Yav.Console.Composition;
using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

/// <summary>/history, /usage, /latency, /limits, /settings, /doctor, /help, /exec, /open, /cd and /exit as the user meets them.</summary>
public class ShellRecordCommandTests
{
    private const string Task = "Make the app say fixed.";

    private static async Task<ShellHarness> StartedAsync(ShellOptions? options = null, Action<ShellHarness>? arrange = null)
    {
        var shell = new ShellHarness(options ?? new ShellOptions());
        try
        {
            arrange?.Invoke(shell);
            shell.Start();
            await shell.WaitForPromptAsync();
            return shell;
        }
        catch
        {
            await shell.DisposeAsync();
            throw;
        }
    }

    private static async Task<ShellHarness> ReadyAsync(ShellOptions? options = null, Action<ShellHarness>? arrange = null)
    {
        var shell = await StartedAsync(options, s =>
        {
            if (arrange is null)
            {
                s.WithPassingRun();
            }
            else
            {
                arrange(s);
            }
        });
        try
        {
            shell.Enter(Task);
            await shell.WaitForRunToEndAsync();
            Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);
            return shell;
        }
        catch
        {
            await shell.DisposeAsync();
            throw;
        }
    }

    private static RunRecord LastRun(ShellHarness shell) => shell.Services.Database.FindRun(shell.Shell.Session.LastRunId!)!;

    private static AppSettings Stored(ShellHarness shell) => new SettingsStore(shell.Paths).Load().Settings;

    private static async Task ConfirmAsync(ShellHarness shell, string command, string answer = "yes")
    {
        shell.Enter(command);
        await shell.WaitForAsync("Type yes to confirm:");
        await shell.EnterAndWaitAsync(answer);
    }

    [Fact]
    public async Task Help_says_what_is_a_request_and_what_is_a_command_and_lists_every_command()
    {
        await using var shell = await StartedAsync(new ShellOptions { Width = 140 });

        await shell.EnterAndWaitAsync("/help");

        shell.AssertShows(
            "Text without a slash is a request for Model A, or a follow-up to the task. Text with a slash is a command of YAV.",
            "YAV never runs what you type as a command of the operating system, except what you give to /exec or enter in /shell.",
            "Shift+Enter, Ctrl+J: start a new line",
            "Paste: several lines are one input; a pasted line break never sends it",

            // What the consoles were seen to do, in InteractiveConsoleTests.
            "Up, Down: earlier input; with something typed, earlier input that begins with it",
            "Esc: close the list of completions; while a run is active, empty the line",
            "Ctrl+C: empty the line; on an empty line: stop the run, or leave",

            // What ApprovalAnswerTests and ShellTests show a question does.
            "At a question of an agent: a, s, d or c, or the word allow where it is asked for, then Enter; Esc declines; Ctrl+C declines and stops the turn");
        var rows = shell.Terminal.Lines;
        foreach (var command in new[]
        {
            "open", "cd", "models", "effort", "login", "quality", "speed", "adaptive", "optimization", "new", "resume", "attach", "status", "stop",
            "diff", "test", "review", "apply", "discard", "undo", "history", "usage", "latency", "limits", "queue", "exec", "shell", "settings",
            "doctor", "help", "exit",

            // Not in the list of the specification, which asks for the local edit but names no command for it.
            "replace",
        })
        {
            Assert.NotNull(CommandCatalog.Find(command));
            Assert.Contains(rows, row => row.StartsWith("  /" + command + " ", StringComparison.Ordinal) || row.StartsWith("  /" + command + "  ", StringComparison.Ordinal));
        }

        Assert.Equal(32, CommandCatalog.All.Count);
        Assert.Empty(shell.Terminal.RowsTheTerminalBegan);
    }

    [Theory]
    [InlineData("/help apply", "/apply", "Waits until an active run has ended.")]
    [InlineData("/help replace", "/replace <file> <text> <replacement> [--count <n> | --all]", "The text has to occur exactly once, unless --count <n> says how often it occurs or --all takes every occurrence.")]
    [InlineData("/help /stop", "/stop", "")]
    [InlineData("/? exit", "/exit", "")]
    public async Task Help_explains_one_command(string command, string usage, string note)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        Assert.Contains(shell.Terminal.Lines, row => row.StartsWith(usage, StringComparison.Ordinal));
        if (note.Length > 0)
        {
            shell.AssertShows(note);
        }
        else
        {
            shell.AssertDoesNotShow("Waits until an active run has ended.");
        }
    }

    [Fact]
    public async Task Help_for_something_that_is_no_command_says_so()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/help rm");

        shell.AssertShows("/rm is not a command of YAV.");
    }

    [Fact]
    public async Task History_lists_the_runs_of_the_project_and_shows_what_happened_in_one()
    {
        await using var shell = await ReadyAsync();
        var run = LastRun(shell);

        await shell.EnterAndWaitAsync("/history");

        shell.AssertShows("Run When State Models Request", run.RunId, "Ready to Apply", "model-a / model-b", Task);

        await shell.EnterAndWaitAsync($"/history {run.RunId}");

        shell.AssertShows("State: Ready to Apply", "[PREPARE]", "[CODE A]", "[READY]", "event(s).");

        // What an agent said is kept behind the gutter in the history as well.
        Assert.Contains(shell.Terminal.Lines, row => System.Text.RegularExpressions.Regex.IsMatch(row, "^[0-9:]{8}   │ .*Changed app[.]txt to say fixed[.]"));
        Assert.Contains(shell.Terminal.Lines, row => System.Text.RegularExpressions.Regex.IsMatch(row, "^[0-9:]{8} [[]READY[]]"));
    }

    [Fact]
    public async Task History_of_another_project_is_not_shown_here()
    {
        await using var shell = await ReadyAsync();
        using var other = new TempDirectory("other project");

        await shell.EnterAndWaitAsync($"/open {other.Path}");
        await shell.EnterAndWaitAsync("/history");

        shell.AssertShows("No run is stored for this project.");
    }

    [Fact]
    public async Task A_run_is_exported_to_a_file_the_user_names_without_control_sequences_and_never_over_a_file_that_exists()
    {
        await using var shell = await ReadyAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Message("\u001b[2Jcleared?", "commentary"), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("pass"));
        });
        var run = LastRun(shell);
        var target = Path.Combine(shell.Paths.Home, "exports", "run.txt");

        await shell.EnterAndWaitAsync($"/history export {run.RunId} {target}");

        shell.AssertShows($"Run {run.RunId} was written to {target}.", "Read it before you pass it on.");
        var text = File.ReadAllText(target);
        Assert.Contains($"run {run.RunId}", text, StringComparison.Ordinal);
        Assert.Contains(Task, text, StringComparison.Ordinal);
        Assert.Contains("A model: model-a / model-a / Verified", text, StringComparison.Ordinal);
        Assert.Contains("cleared?", text, StringComparison.Ordinal);
        Assert.DoesNotContain('\u001b', text);

        File.WriteAllText(target, "mine");
        await shell.EnterAndWaitAsync($"/history export {run.RunId} {target}");

        shell.AssertShows("exists already. It was not overwritten; name another file.");
        Assert.Equal("mine", File.ReadAllText(target));
    }

    [Fact]
    public async Task A_run_that_is_ready_is_not_deleted_from_the_history_and_another_one_only_after_a_yes()
    {
        await using var shell = await ReadyAsync();
        var run = LastRun(shell);

        await shell.EnterAndWaitAsync($"/history delete {run.RunId}");

        shell.AssertShows($"Run {run.RunId} is ready and was not applied. Apply it or /discard {run.RunId} first.");
        Assert.NotNull(shell.Services.Database.FindRun(run.RunId));

        await shell.EnterAndWaitAsync("/apply");
        await ConfirmAsync(shell, $"/history delete {run.RunId}", "no");
        Assert.NotNull(shell.Services.Database.FindRun(run.RunId));

        await ConfirmAsync(shell, $"/history delete {run.RunId}");

        shell.AssertShows($"Run {run.RunId} was deleted from the history.");
        Assert.Null(shell.Services.Database.FindRun(run.RunId));
        Assert.Equal("fixed\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task Prune_removes_what_is_older_than_the_user_keeps_and_nothing_that_is_ready()
    {
        await using var shell = await ReadyAsync();

        await shell.EnterAndWaitAsync("/history prune");

        shell.AssertShows("Removed 0 run(s) and 0 event(s) older than 30 days. Runs that are ready to apply or need attention are kept.");
        Assert.NotNull(shell.Services.Database.FindRun(LastRun(shell).RunId));
    }

    [Fact]
    public async Task Usage_shows_what_the_provider_reported_for_each_role_and_unavailable_where_it_reported_nothing()
    {
        await using var shell = await ReadyAsync(new ShellOptions { Width = 160 }, s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Usage(input: 1000, cached: 200, output: 300, reasoning: 100), Step.Message("Done."))
                .ReviewerTurn(Step.Review("pass"));
        });
        var run = LastRun(shell);

        await shell.EnterAndWaitAsync("/usage");

        shell.AssertShows(
            $"Usage of run {run.RunId}",
            "A model-a 800 200 Unavailable 300 100 1,300 1 0 Unavailable",
            "Billing routes: A: ChatGPT plan (pro)",
            "What a provider did not report is Unavailable, never 0.",
            "YAV does not turn it into an amount of money.");
        Assert.DoesNotContain(shell.Terminal.Lines, row => row.Contains("USD", StringComparison.Ordinal) || System.Text.RegularExpressions.Regex.IsMatch(row, "[$][0-9]"));
        var reviewer = shell.Terminal.Lines.Single(row => row.TrimStart().StartsWith("B ", StringComparison.Ordinal) && row.Contains("model-b", StringComparison.Ordinal));
        Assert.Contains("Unavailable", reviewer, StringComparison.Ordinal);
        Assert.DoesNotMatch(@"\s0\s+0\s+0\s", reviewer);
    }

    [Fact]
    public async Task Usage_of_everything_covers_the_last_thirty_days()
    {
        await using var shell = await ReadyAsync();

        await shell.EnterAndWaitAsync("/usage all");

        shell.AssertShows("Usage of the last 30 days, all projects", "model-a", "model-b");
    }

    [Fact]
    public async Task Latency_shows_measured_times_of_the_console_and_of_the_run()
    {
        await using var shell = await ReadyAsync(new ShellOptions { Width = 160 });
        var run = LastRun(shell);

        await shell.EnterAndWaitAsync("/latency");

        shell.AssertShows(
            "Start to prompt, this time:",
            "Development target: 1 s on the reference machine; a target, not a promise",
            $"Run {run.RunId}",
            "Elapsed:",
            "of it waiting for you:",
            "Until the first action of an agent:",
            "Until an accepted result:",
            "Stage Times Elapsed Work Note",
            "Implementation (Model A) 1",
            "Review (Model B) 1",
            "Required checks",
            "provider time and tools",
            "Provider time is what is left of a turn without its tools: waiting, reasoning and answering are not told apart.",
            "These are measurements of this run, not a forecast for another.");
        shell.AssertDoesNotShow("%");
    }

    [Fact]
    public async Task How_long_a_command_takes_to_answer_is_measured_and_shown_with_the_time_that_is_aimed_for()
    {
        await using var shell = await StartedAsync();
        await shell.EnterAndWaitAsync("/help");
        await shell.EnterAndWaitAsync("/limits");
        await shell.EnterAndWaitAsync("/nonsense");

        await shell.EnterAndWaitAsync("/latency");

        shell.AssertShows(
            "Commands",
            "Answered, median of the last 3:",
            "Slowest:",
            "Development target: 200 ms; a target, not a promise");
        var measured = shell.Services.Database.GetSpans(null).Where(s => s.Kind == Yav.Core.Timing.SpanKind.LocalCommand).ToList();
        Assert.Equal(["/help", "/limits", "/unknown", "/latency"], measured.Select(s => s.Label).ToArray());
        Assert.All(measured, span => Assert.InRange(span.Duration!.Value, TimeSpan.Zero, TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task What_a_request_says_is_not_kept_with_the_measurements()
    {
        await using var shell = await ReadyAsync();

        await shell.EnterAndWaitAsync("/latency");

        var measured = shell.Services.Database.GetSpans(null).Where(s => s.Kind == Yav.Core.Timing.SpanKind.LocalCommand).ToList();
        Assert.Contains(measured, s => s.Label == "request");
        Assert.DoesNotContain(measured, s => s.Label.Contains("fixed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Latency_accounts_for_the_whole_time_of_a_run_and_names_what_no_stage_covers()
    {
        await using var shell = await ReadyAsync();
        await shell.EnterAndWaitAsync("/apply");

        await shell.EnterAndWaitAsync("/latency");

        shell.AssertShows("Elapsed", "Not part of any stage", "Preparation", "Evaluating the evidence", "Apply", "Waiting for you");
        shell.AssertDoesNotShow("LocalPreparation", "AgentInitialization", "ApprovalWaiting");
        var run = LastRun(shell);
        var spans = shell.Services.Database.GetSpans(run.RunId);
        Assert.True(spans.Min(s => s.StartedAt) < run.CreatedAt, "the run was recorded before anything was measured");
    }

    [Fact]
    public async Task Latency_tells_the_time_in_tools_apart_from_the_rest_of_a_turn()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Command("dotnet test", "all passed", milliseconds: 1_500), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("pass"));
        });
        shell.Enter("Make the app say fixed.");
        await shell.WaitForRunToEndAsync();

        await shell.EnterAndWaitAsync("/latency");

        shell.AssertShows(
            "of it in tools the agents ran:",
            "Tools",
            "Provider time is what is left of a turn without its tools: waiting, reasoning and answering are not told apart.");
        var tools = shell.Services.Database.GetSpans(LastRun(shell).RunId).Single(s => s.Kind == Yav.Core.Timing.SpanKind.ToolActivity);
        // The command runs for 1500 ms. On a busy machine the report of its start is taken up late, which shortens what is measured.
        Assert.True(tools.Duration >= TimeSpan.FromMilliseconds(500), $"{tools.Duration}");
        Assert.DoesNotContain("dotnet", tools.Label, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Review_and_required_checks_run_side_by_side()
    {
        await using var shell = await ReadyAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.ToolGate("tests", "sleep", "2"));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Sleep(2_000), Step.Review("pass"));
        });

        await shell.EnterAndWaitAsync("/latency");

        shell.AssertShows("ran side by side:");
        var spans = shell.Services.Database.GetSpans(LastRun(shell).RunId);
        var review = spans.Single(s => s.Kind == Yav.Core.Timing.SpanKind.Review);
        var tests = spans.Single(s => s.Kind == Yav.Core.Timing.SpanKind.Tests);
        Assert.True(review.StartedAt < tests.EndedAt && tests.StartedAt < review.EndedAt, "review and checks did not overlap");
    }

    [Fact]
    public async Task Limits_are_shown_with_what_they_can_and_cannot_do()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/limits");

        shell.AssertShows(
            "Elapsed time: no limit",
            "Repair cycles: 2 after the first candidate",
            "Tokens: no limit",
            "Waiting requests: 20",
            "A turn that is running is not cut off, and usage a provider reports late can exceed the limit.",
            "It is not a cap on what a provider charges.");
    }

    [Theory]
    [InlineData("/limits repairs 1", "Limit 'repairs' is 1.")]
    [InlineData("/limits repairs 0", "Limit 'repairs' is 0.")]
    [InlineData("/limits minutes 90", "Limit 'minutes' is 90.")]
    [InlineData("/limits minutes 0", "Limit 'minutes' is off.")]
    [InlineData("/limits tokens 2000000", "Limit 'tokens' is 2,000,000.")]
    [InlineData("/limits ratelimit 80", "Limit 'ratelimit' is 80.")]
    [InlineData("/limits queue 3", "Limit 'queue' is 3.")]
    public async Task A_limit_is_set_by_the_user(string command, string expected)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows(expected);
        var limits = Stored(shell).Limits;
        var value = long.Parse(command.Split(' ')[2], System.Globalization.CultureInfo.InvariantCulture);
        Assert.Equal(value, command.Split(' ')[1] switch
        {
            "repairs" => limits.MaxRepairCycles,
            "minutes" => limits.MaxElapsedMinutes,
            "tokens" => limits.MaxRunTokens,
            "ratelimit" => limits.StopAtRateLimitPercent,
            _ => limits.MaxQueueLength,
        });
    }

    [Theory]
    [InlineData("/limits repairs 99", "That value is outside what is allowed")]
    [InlineData("/limits ratelimit 101", "That value is outside what is allowed")]
    [InlineData("/limits tokens -5", "Usage: /limits minutes|repairs|tokens|ratelimit|queue <n>")]
    [InlineData("/limits tokens many", "Usage: /limits minutes|repairs|tokens|ratelimit|queue <n>")]
    [InlineData("/limits money 5", "That value is outside what is allowed")]
    [InlineData("/limits repairs", "Usage: /limits minutes|repairs|tokens|ratelimit|queue <n>")]
    public async Task A_limit_that_is_not_understood_changes_nothing(string command, string expected)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows(expected);
        Assert.Equal(new LimitSettings(), Stored(shell).Limits);
    }

    [Fact]
    public async Task With_no_repair_cycle_allowed_findings_stop_the_run_and_model_a_is_not_asked_again()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("changes_required", file: "src/app.txt", title: "The empty case is not handled"));
        });
        await shell.EnterAndWaitAsync("/limits repairs 0");

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        Assert.Equal(RunState.Blocked, LastRun(shell).State);
        Assert.Equal(2, shell.Agents.CodexRequests("turn/start").Count);
        shell.AssertShows("The empty case is not handled", "[BLOCKED]");
        shell.AssertDoesNotShow("[READY]");
    }

    [Fact]
    public async Task The_queue_takes_no_more_requests_than_the_user_allows()
    {
        await using var shell = await StartedAsync(new ShellOptions { Configure = s => _ = s }, s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        });
        await shell.EnterAndWaitAsync("/limits queue 1");
        shell.Enter(Task);
        await shell.WaitForAsync("update ");

        await shell.EnterAndWaitAsync("First follow-up.");
        await shell.EnterAndWaitAsync("Second follow-up.");

        shell.AssertShows("[QUEUE] 1 request(s) are waiting, which is the limit. The request was not added; see /queue and /limits.");
        Assert.Equal(["First follow-up."], shell.Services.Database.GetQueue(shell.Project.Path).Select(q => q.Text).ToArray());
        shell.Enter("/stop");
        await shell.WaitForRunToEndAsync();
    }

    [Fact]
    public async Task Settings_show_where_everything_is_kept_and_that_nothing_is_sent_anywhere()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/settings");

        shell.AssertShows(
            $"File: {shell.Paths.SettingsFile}",
            $"Data: {shell.Paths.Home}",
            "Model A: model-a (codex-app-server), effort maximum",
            "telemetry: off - YAV sends nothing anywhere",
            "trust: on for this project",
            "Credentials are never kept in settings.");
    }

    [Theory]
    [InlineData("/settings shell cmd", "/shell and /exec use cmd.")]
    [InlineData("/settings plain on", "Plain output is on from the next start.")]
    [InlineData("/settings verbose on", "Output of commands and checks is shown as it arrives, from the next run.")]
    [InlineData("/settings telemetry on", "Telemetry is marked on. This version of YAV contains nothing that transmits data, so nothing is sent.")]
    [InlineData("/settings keep-runs 10", "/history prune removes runs older than 10 days.")]
    [InlineData("/settings keep-workspaces 3", "Isolated workspaces are kept for 3 days.")]
    [InlineData("/settings trust off", "This project is not trusted: configuration of agents that is kept in the repository is not loaded.")]
    [InlineData("/settings adapter claude-cli off", "Adapter claude-cli: off")]
    [InlineData("/settings workspace in-place", "Recorded: you accept that an agent may work directly in this project")]
    public async Task A_setting_is_changed_by_the_user_and_says_what_it_means(string command, string expected)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows(expected);
        var stored = Stored(shell);
        switch (command.Split(' ')[1])
        {
            case "shell":
                Assert.Equal("cmd", stored.Shell);
                break;
            case "plain":
                Assert.True(stored.PlainOutput);
                break;
            case "verbose":
                Assert.True(shell.Shell.Session.Verbose);
                break;
            case "telemetry":
                Assert.True(stored.Telemetry);
                break;
            case "keep-runs":
                Assert.Equal(10, stored.Retention.KeepRunsDays);
                break;
            case "keep-workspaces":
                Assert.Equal(3, stored.Retention.KeepWorkspacesDays);
                break;
            case "trust":
                Assert.False(shell.Services.Database.IsProjectTrusted(shell.Project.Path));
                break;
            case "adapter":
                Assert.False(stored.AdapterFor("claude-cli").Enabled);
                break;
        }
    }

    [Theory]
    [InlineData("/settings shell bash")]
    [InlineData("/settings keep-runs 0")]
    [InlineData("/settings keep-runs forever")]
    [InlineData("/settings color blue")]
    [InlineData("/settings modelA gpt")]
    [InlineData("/settings apikey sk-ant-123")]
    public async Task A_setting_that_does_not_exist_or_a_value_outside_what_is_allowed_changes_nothing(string command)
    {
        await using var shell = await StartedAsync();
        var before = File.ReadAllText(shell.Paths.SettingsFile);

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows("is not a setting YAV has, or the value is outside what is allowed. /settings lists them.");
        Assert.Equal(before, File.ReadAllText(shell.Paths.SettingsFile));
    }

    [Fact]
    public async Task With_verbose_output_what_a_command_of_the_agent_wrote_is_shown_behind_the_gutter()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Command("dotnet build", "Build succeeded.\n"), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("pass"));
        });
        await shell.EnterAndWaitAsync("/settings verbose on");

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        Assert.Contains("  │ Build succeeded.", shell.Terminal.Lines);
        Assert.Contains(shell.Terminal.Lines, row => row.StartsWith("  $ dotnet build -> exit 0", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Doctor_reports_what_it_found_without_asking_a_model_anything()
    {
        await using var shell = await StartedAsync(new ShellOptions { Width = 140 });

        await shell.EnterAndWaitAsync("/doctor");

        shell.AssertShows(
            "Asking the agents for their version, account and models. No inference is requested.",
            "System",
            "Storage",
            "Tools",
            "ChatGPT plan (pro)",
            "warning(s).");
        Assert.Empty(shell.Agents.CodexRequests("turn/start"));
        Assert.DoesNotContain(shell.Agents.Received("claude.input"), message => message["type"]?.GetValue<string>() == "user");
    }

    [Fact]
    public async Task Exec_runs_a_command_as_the_user_says_so_and_shows_it_to_no_model()
    {
        await using var shell = await StartedAsync(new ShellOptions { Configure = s => _ = s }, s => s.WithPassingRun());
        await shell.EnterAndWaitAsync("/settings shell cmd");

        await shell.EnterAndWaitAsync("/exec echo by the user> made-by-exec.txt");

        shell.AssertShows(
            $"[LOCAL] cmd in {shell.Project.Path}. This runs as you, with your rights, outside every agent sandbox and outside the isolated workspace.",
            "[LOCAL] exit 0 after");
        Assert.Equal("by the user", shell.Project.Read("made-by-exec.txt").Trim());
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
        Assert.Empty(shell.Services.Database.ListRuns(null, 10));
    }

    [Fact]
    public async Task Exec_reports_the_exit_code_of_a_command_that_failed()
    {
        await using var shell = await StartedAsync();
        await shell.EnterAndWaitAsync("/settings shell cmd");

        await shell.EnterAndWaitAsync("/exec exit 7");

        shell.AssertShows("[LOCAL] exit 7 after");
    }

    [Fact]
    public async Task Exec_without_a_command_runs_nothing()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/exec");

        shell.AssertShows("Usage: /exec <command>", "This runs as you, with your rights, outside every agent sandbox and outside the isolated workspace.");
        shell.AssertDoesNotShow("exit 0");
    }

    [Fact]
    public async Task Shell_starts_nothing_where_there_is_no_console_to_give_to_it()
    {
        // A terminal in memory can be asked a question, but it is no console: a shell given the console of
        // this process would wait for keys nobody can type, and would outlive the test.
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/shell", seconds: 20);

        shell.AssertShows("A shell needs a console. Input or output is not a terminal here, so none was started.");
        shell.AssertDoesNotShow("'exit' returns to YAV.");
    }

    [Fact]
    public async Task Open_selects_another_project_and_says_how_it_will_be_worked_on()
    {
        await using var shell = await StartedAsync();
        using var other = new TempDirectory("other project");
        other.Write("notes.txt", "hello\n");

        await shell.EnterAndWaitAsync($"/open {other.Path}");

        shell.AssertShows(
            $"Project: {other.Path}",
            "Kind: not a Git repository: worked on in a protected copy",
            "Trust: not trusted: configuration kept in the repository is not loaded (/settings trust on)",
            "Checks: none configured (/test detect proposes some)");
        Assert.Equal(other.Path, shell.Shell.Session.ProjectPath);
        Assert.Equal($"YAV {other.Path}>", shell.Terminal.CursorLine);
        Assert.Equal(other.Path, Stored(shell).LastProject);
    }

    [Theory]
    [InlineData("/open {project}\\does not exist")]
    [InlineData("/cd nowhere")]
    [InlineData("/open {project}\\src\\app.txt")]
    public async Task A_directory_that_does_not_exist_is_not_opened(string command)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command.Replace("{project}", shell.Project.Path, StringComparison.Ordinal));

        shell.AssertShows("does not exist. The project was not changed.");
        Assert.Equal(shell.Project.Path, shell.Shell.Session.ProjectPath);
    }

    [Fact]
    public async Task Cd_is_relative_to_the_project_and_open_without_a_path_says_where_the_shell_is()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/cd src");

        var inside = Path.Combine(shell.Project.Path, "src");
        Assert.Equal(inside, shell.Shell.Session.ProjectPath);
        shell.AssertShows("Kind: Git repository, branch");

        await shell.EnterAndWaitAsync("/cd ..");
        await shell.EnterAndWaitAsync("/open");

        Assert.Equal(shell.Project.Path, shell.Shell.Session.ProjectPath);
        Assert.Contains($"Project: {shell.Project.Path}", shell.Terminal.Lines);
    }

    [Fact]
    public async Task A_request_without_a_project_is_not_sent()
    {
        await using var shell = new ShellHarness().WithPassingRun();
        var missing = Path.Combine(shell.Paths.Home, "no such project");

        shell.StartIn(missing);
        await shell.WaitForPromptAsync();
        shell.Enter(Task);

        // The folder is asked for; without an answer nothing is selected and nothing is sent.
        await shell.AnswerWhenAskedAsync("Project folder - type or paste its path, then Enter:", string.Empty);
        await shell.WaitForPromptAsync();

        shell.AssertShows(
            $"The directory '{missing}' does not exist. Select a project with /open <path>.",
            "Project: none selected - use /open <path>",
            "No project is selected, so the request has nowhere to go yet.",
            "The request was not sent. /open <path> selects a project.");
        Assert.Equal("YAV>", shell.Terminal.CursorLine);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Leaving_during_a_run_asks_first_and_stops_the_run_in_a_way_that_can_be_continued()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        });
        shell.Enter(Task);
        await shell.WaitForAsync("update ");

        await ConfirmAsync(shell, "/exit", "no");

        shell.AssertShows("A run is active. Leaving stops it; its workspace and conversations are kept and /resume continues it after the next start.");
        Assert.False(shell.Shell.Session.ExitRequested);
        Assert.NotNull(shell.Shell.Session.Active);

        shell.Enter("/exit");
        await shell.WaitForAsync("Stop the run and leave? Type yes to confirm:");
        shell.Keys.Type("yes").Enter();
        Assert.Equal(0, await shell.WaitForExitAsync());

        shell.AssertShows("State saved. Nothing continues to run after YAV has ended.");
        var run = shell.Services.Database.ListRuns(shell.Project.Path, 1).Single();
        Assert.Equal(RunState.Interrupted, run.State);
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task The_next_start_names_the_run_that_was_left_and_how_to_continue_it()
    {
        await using var first = await ReadyAsync();
        var runId = LastRun(first).RunId;
        Assert.Equal(0, await first.ExitAsync());

        await using var second = await first.RestartAsync();
        second.Start();
        await second.WaitForPromptAsync();

        second.AssertShows($"Last run here: {runId}, Ready to Apply", "/diff shows it and /apply writes it into the project.");
        await second.EnterAndWaitAsync("/apply");
        Assert.Equal("fixed\n", second.Project.Read("src/app.txt"));
    }
}
