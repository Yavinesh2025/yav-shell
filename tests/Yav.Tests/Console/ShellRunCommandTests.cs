using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

/// <summary>The commands that inspect, check, deliver and continue a run, as the user meets them.</summary>
public class ShellRunCommandTests
{
    private const string Task = "Make the app say fixed.";

    private static readonly string NineLines = string.Concat(Enumerable.Range(1, 9).Select(i => $"line {i}\n"));

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

    /// <summary>A shell in which one request has run and its result waits to be applied.</summary>
    private static async Task<ShellHarness> ReadyAsync(ShellOptions? options = null, Action<ShellHarness>? arrange = null)
    {
        var shell = await StartedAsync(options, s =>
        {
            s.WithPassingRun();
            arrange?.Invoke(s);
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

    private static async Task<IsolatedWorkspace?> WorkspaceOfAsync(ShellHarness shell, RunRecord run)
    {
        var task = shell.Services.Database.FindTask(run.TaskId);
        return task?.WorkspaceId is null ? null : await shell.Services.Workspaces.FindAsync(task.WorkspaceId, CancellationToken.None);
    }

    /// <summary>A configuration file as a project brings it along, with one check that runs the fixture's tool.</summary>
    private static string FileWith(ShellHarness shell, params string[] toolArguments)
    {
        var gate = CoordinatorHarness.ToolGate("unit", toolArguments) with { Title = "Unit tests" };
        return shell.Services.Validation.Serialize(ProjectConfiguration.Empty with { Gates = [gate] });
    }

    private static async Task ConfirmAsync(ShellHarness shell, string command, string answer = "yes")
    {
        shell.Enter(command);
        await shell.WaitForAsync("Type yes to confirm:");
        await shell.EnterAndWaitAsync(answer);
    }

    [Fact]
    public async Task Status_says_what_is_requested_and_what_the_providers_confirmed()
    {
        await using var shell = await ReadyAsync();

        await shell.EnterAndWaitAsync("/status");

        shell.AssertShows(
            $"Project: {shell.Project.Path}",
            "Quality Lock: ON (Strict Max)",
            "A implements model-a codex-app-server maximum = xhigh ChatGPT plan (pro); included in the subscription, counted against its limits",
            "B reviews model-b codex-app-server maximum = max",
            "Last run",
            "State: Ready to Apply",
            "Candidate:",
            "1 file(s): +0 ~1 -0",
            "Review: Pass: 0 blocking finding(s), 0 suggestion(s)",
            "Checks: tests: Passed",
            "A effort xhigh xhigh Verified",
            "A model model-a model-a Verified",
            "A sandbox workspace-write workspace-write Verified",
            "B effort max max Verified",
            "B sandbox read-only read-only Verified");
    }

    [Fact]
    public async Task Status_during_a_run_shows_where_the_run_is()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        });
        shell.Enter(Task);
        await shell.WaitForAsync("update ");

        await shell.EnterAndWaitAsync("/status");

        shell.AssertShows("Active run", "State: Implementing", "Running for:", $"Request: {Task}");
        await shell.EnterAndWaitAsync("/stop");
        await shell.WaitForRunToEndAsync();
    }

    [Fact]
    public async Task A_setting_the_provider_put_into_effect_differently_stops_the_run_and_is_shown_as_a_mismatch()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Agents.Codex(c => c["effectiveEffort:implementer"] = "medium");
        });

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();
        await shell.EnterAndWaitAsync("/status");

        shell.AssertShows("[BLOCKED]", "A effort xhigh medium MISMATCH");
        Assert.Empty(shell.Agents.CodexRequests("turn/start"));
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task Diff_shows_what_the_candidate_changes_compared_with_where_the_task_started()
    {
        await using var shell = await ReadyAsync();

        await shell.EnterAndWaitAsync("/diff");

        var run = LastRun(shell);
        shell.AssertShows($"Run {run.RunId}, candidate", "1 file(s): +0 ~1 -0, compared with the state the task started from");
        Assert.Contains("-one", shell.Terminal.Lines);
        Assert.Contains("+fixed", shell.Terminal.Lines);
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task Diff_as_a_summary_names_the_files_and_their_sizes()
    {
        await using var shell = await ReadyAsync();

        await shell.EnterAndWaitAsync("/diff --stat");

        shell.AssertShows("modified src/app.txt 4 B 6 B");
        Assert.DoesNotContain("+fixed", shell.Terminal.Lines);
    }

    [Fact]
    public async Task Diff_is_written_to_a_file_on_request_and_never_over_a_file_that_exists()
    {
        await using var shell = await ReadyAsync();
        var target = Path.Combine(shell.Paths.Home, "exports", "change.diff");

        await shell.EnterAndWaitAsync($"/diff --export {target}");

        shell.AssertShows($"The diff was written to {target}");
        var written = File.ReadAllText(target);
        Assert.Contains("-one", written, StringComparison.Ordinal);
        Assert.Contains("+fixed", written, StringComparison.Ordinal);

        File.WriteAllText(target, "mine");
        await shell.EnterAndWaitAsync($"/diff --export {target}");

        shell.AssertShows($"'{target}' exists already. It was not overwritten; name another file.");
        Assert.Equal("mine", File.ReadAllText(target));
    }

    [Theory]
    [InlineData("/diff")]
    [InlineData("/apply")]
    [InlineData("/discard")]
    [InlineData("/review show")]
    [InlineData("/usage")]
    [InlineData("/resume")]
    public async Task A_command_about_a_run_says_so_when_there_is_none(string command)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows("There is no run to refer to yet. /history lists earlier runs.");
        Assert.Null(shell.Shell.Session.Active);
    }

    [Theory]
    [InlineData("/diff 20990101-000000-zzzzz")]
    [InlineData("/apply 20990101-000000-zzzzz")]
    [InlineData("/history 20990101-000000-zzzzz")]
    public async Task A_run_that_does_not_exist_is_said_not_to_exist(string command)
    {
        await using var shell = await ReadyAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows("Run '20990101-000000-zzzzz' does not exist. /history lists the runs.");
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task A_run_that_cannot_be_continued_says_why()
    {
        await using var shell = await ReadyAsync();
        var run = LastRun(shell);

        shell.Enter("/resume " + run.RunId);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows($"Run {run.RunId} is ready to apply; there is nothing to resume. Use /apply, or send a follow-up.");
        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);
    }

    [Fact]
    public async Task What_a_run_said_when_it_ended_is_not_said_a_second_time()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("changes_required", title: "The empty case is not handled"))
                .ImplementerTurn(Step.Message("I see no way to do that."))
                .ReviewerTurn(Step.Review("changes_required", title: "The empty case is not handled"))
                .ImplementerTurn(Step.Message("Still none."))
                .ReviewerTurn(Step.Review("changes_required", title: "The empty case is not handled"));
        });

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        Assert.Equal(RunState.Blocked, LastRun(shell).State);
        var reason = LastRun(shell).StateReason!;
        var beginning = reason[..Math.Min(40, reason.Length)];
        var row = Assert.Single(shell.Terminal.Lines, row => row.Contains(beginning, StringComparison.Ordinal));
        Assert.StartsWith("[BLOCKED]", row, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_show_gives_the_result_of_the_review_and_says_what_it_does_not_cover()
    {
        await using var shell = await ReadyAsync();

        await shell.EnterAndWaitAsync("/review show");

        shell.AssertShows(
            "by model-b (codex-app-server): Pass: 0 blocking finding(s), 0 suggestion(s)",
            "A passing review does not say that the required checks passed. /status shows both.");
    }

    [Fact]
    public async Task Review_show_gives_what_the_reviewer_wrote_in_full_and_behind_the_gutter()
    {
        // A reviewer that is a model writes more than a stand-in does: several hundred words are usual.
        var summary = "The candidate meets the request. "
            + string.Join(" ", Enumerable.Range(1, 60).Select(i => $"Sentence {i} of the summary."))
            + " THE SUMMARY ENDS HERE.\nA second paragraph of the summary.";
        var coverage = "Read every file. "
            + string.Join(" ", Enumerable.Range(1, 60).Select(i => $"Item {i} of what was read."))
            + " THE COVERAGE ENDS HERE.";
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("pass", summary: summary, coverage: coverage));
        });
        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        await shell.EnterAndWaitAsync("/review show");

        // Only what stands behind the gutter is looked at, as the text it was before the screen broke its lines.
        const string Gutter = "  │ ";
        var rows = shell.Terminal.Lines.ToList();
        var written = string.Join(' ', rows.Where(row => row.StartsWith(Gutter, StringComparison.Ordinal)).Select(row => row[Gutter.Length..].Trim()));
        Assert.Contains("Sentence 60 of the summary. THE SUMMARY ENDS HERE. A second paragraph of the summary.", written, StringComparison.Ordinal);
        Assert.Contains("Item 60 of what was read. THE COVERAGE ENDS HERE.", written, StringComparison.Ordinal);
        Assert.Contains("  Summary", rows);
        Assert.Contains("  What was inspected", rows);
    }

    [Fact]
    public async Task Findings_are_sent_to_model_a_and_the_repaired_candidate_is_reviewed_again()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("changes_required", file: "src/app.txt", title: "The empty case is not handled"))
                .ImplementerTurn(Step.Write("src/app.txt", "fixed, also when empty\n"), Step.Message("Repaired."))
                .ReviewerTurn(Step.Review("pass"));
        });

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        var rows = shell.Terminal.Lines.ToList();
        Assert.Contains(rows, row => row.StartsWith("  ! Major src/app.txt:1  The empty case is not handled", StringComparison.Ordinal));
        Assert.True(rows.FindIndex(r => r.StartsWith("[REPAIR]", StringComparison.Ordinal)) > rows.FindIndex(r => r.StartsWith("[REVIEW B]", StringComparison.Ordinal)), shell.Terminal.Text);
        shell.AssertShows("[REPAIR]", "1 of 2", "[READY]");
        Assert.Equal(1, LastRun(shell).RepairCyclesUsed);

        var repair = shell.Agents.CodexRequests("turn/start").Select(t => t["params"]!["input"]![0]!["text"]!.GetValue<string>()).First(p => p.Contains("The empty case is not handled", StringComparison.Ordinal));
        Assert.Contains("An empty input produces the wrong result.", repair, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_review_can_be_asked_for_again_and_runs_in_a_conversation_of_its_own()
    {
        await using var shell = await ReadyAsync();
        var before = shell.Agents.CodexRequests("turn/start").Count;

        shell.Enter("/review");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[REVIEW B]");
        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);
        Assert.Equal(before + 1, shell.Agents.CodexRequests("turn/start").Count);
        var threads = shell.Agents.CodexRequests("thread/start");
        Assert.All(threads.Where(t => t["params"]!["model"]!.GetValue<string>() == "model-b"), t => Assert.Equal("read-only", t["params"]!["sandbox"]!.GetValue<string>()));
    }

    [Fact]
    public async Task A_change_to_a_protected_path_waits_for_the_users_approval_of_exactly_that_path()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            var configuration = ProjectConfiguration.Empty with
            {
                Gates = [CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug")],
                ProtectedPaths = ["README.md"],
            };
            s.Services.Validation.TrustConfiguration(s.Project.Path, configuration, s.Services.Validation.Serialize(configuration));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Write("README.md", "# App\nfixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("pass"));
        });

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        var run = LastRun(shell);
        Assert.Equal(RunState.Blocked, run.State);
        shell.AssertShows("[BLOCKED]", "README.md");

        await shell.EnterAndWaitAsync("/apply");
        shell.AssertShows($"Run {run.RunId} is Blocked / Needs Attention. Only a candidate that passed review and the required checks can be applied.");
        Assert.Equal("# App\n", shell.Project.Read("README.md"));

        await shell.EnterAndWaitAsync("/review approve src/app.txt");
        shell.AssertShows($"'src/app.txt' is not a protected path that the current candidate of run {run.RunId} changes.");

        await shell.EnterAndWaitAsync("/review approve README.md");
        shell.AssertShows($"Recorded: the change to the protected path 'README.md' is approved for the current candidate of run {run.RunId}.");
        shell.Enter("/resume");
        await shell.WaitForRunToEndAsync();

        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);
        await shell.EnterAndWaitAsync("/apply");
        Assert.Equal("# App\nfixed\n", shell.Project.Read("README.md"));
    }

    [Fact]
    public async Task Test_lists_the_checks_that_are_approved()
    {
        await using var shell = await StartedAsync(arrange: s => s.WithPassingRun());

        await shell.EnterAndWaitAsync("/test list");

        shell.AssertShows("Check Kind Required Command Limit Needs", "tests Test yes", "expect-no-text", "60s", "Checks run in a disposable copy of the candidate.");
    }

    [Fact]
    public async Task Without_approved_checks_nothing_runs_and_a_request_is_blocked_before_anything_is_sent()
    {
        await using var shell = await StartedAsync(arrange: s => s.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")));

        await shell.EnterAndWaitAsync("/test");
        shell.AssertShows("No check is approved for this project. /test detect proposes some from what the project contains.");

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[BLOCKED]");
        Assert.Empty(shell.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task Checks_a_project_brings_along_run_only_after_the_user_saw_and_approved_them()
    {
        var marker = Path.Combine(Path.GetTempPath(), "yav-tests", $"ran-{Guid.NewGuid():N}.txt");
        await using var shell = await StartedAsync(arrange: s => s.Project.Write("yav.project.json", FileWith(s, "write-file", marker, "ran")));
        try
        {
            await shell.EnterAndWaitAsync("/test list");
            shell.AssertShows("No check is approved for this project.", "yav.project.json is not approved. Nothing of it is used. /test trust shows it and asks.");
            await shell.EnterAndWaitAsync("/test");
            Assert.False(File.Exists(marker), "a command of an unapproved configuration ran");

            await ConfirmAsync(shell, "/test trust", "no");
            shell.AssertShows(
                "unit Test yes",
                "write-file",
                "These commands run on your machine with your rights whenever a candidate is checked. Approve them only when you know what they do.",
                "Not confirmed. Nothing was changed.");
            await shell.EnterAndWaitAsync("/test");
            Assert.False(File.Exists(marker), "a command ran although the configuration was declined");

            await ConfirmAsync(shell, "/test trust");
            shell.AssertShows("Approved. It applies from the next run");
            await shell.EnterAndWaitAsync("/test unit");

            shell.AssertShows("[LOCAL] Running 1 check(s) in the project itself, as you. This is not evidence for a candidate.", "[TESTS] Unit tests: Passed (exit 0)");
            Assert.True(File.Exists(marker));
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task A_configuration_that_changed_after_it_was_approved_is_not_used_until_it_is_approved_again()
    {
        var marker = Path.Combine(Path.GetTempPath(), "yav-tests", $"ran-{Guid.NewGuid():N}.txt");
        await using var shell = await StartedAsync(arrange: s => s.Project.Write("yav.project.json", FileWith(s, "exit", "0")));
        try
        {
            await ConfirmAsync(shell, "/test trust");
            shell.Project.Write("yav.project.json", FileWith(shell, "write-file", marker, "ran"));

            await shell.EnterAndWaitAsync("/test list");
            shell.AssertShows("differs from what you approved. The approved version above applies.", "tool exit 0");
            shell.AssertDoesNotShow("write-file");
            await shell.EnterAndWaitAsync("/test unit");

            shell.AssertShows("[TESTS] Unit tests: Passed (exit 0)");
            Assert.False(File.Exists(marker), "the changed command ran without approval");
        }
        finally
        {
            File.Delete(marker);
        }
    }

    [Fact]
    public async Task Detect_proposes_checks_from_what_the_project_contains_and_runs_nothing_by_itself()
    {
        await using var shell = await StartedAsync(new ShellOptions
        {
            Files = [("src/app.txt", "one\n"), ("package.json", """{ "scripts": { "test": "node test.js", "lint": "eslint ." } }""")],
        });

        await ConfirmAsync(shell, "/test detect", "no");

        shell.AssertShows(
            "Proposed from what the project contains. Nothing runs until you approve it:",
            "lint npm run lint",
            "test npm run test",
            "Not confirmed. Nothing was changed.");
        Assert.Empty(shell.Services.Validation.LoadConfiguration(shell.Project.Path).Effective.Gates);
        Assert.False(shell.Project.Exists("yav.project.json"));

        await ConfirmAsync(shell, "/test detect");

        shell.AssertShows("Approved, and written to");
        Assert.Equal(["lint", "test"], shell.Services.Validation.LoadConfiguration(shell.Project.Path).Effective.Gates.Select(g => g.Id).Order().ToArray());
        Assert.True(shell.Project.Exists("yav.project.json"));
    }

    [Fact]
    public async Task A_failing_check_is_waived_only_by_the_user_with_a_reason_and_for_one_candidate()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(
                CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"),
                CoordinatorHarness.TextGate("docs", "README.md", "documented"));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("pass"));
        });
        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();
        var run = LastRun(shell);
        Assert.Equal(RunState.Blocked, run.State);
        shell.AssertShows("Gate 'docs' failed (exit 1). It also fails on the unmodified baseline, so the failure predates this task.");

        // The failure was there before the task, so it was not sent to Model A: one turn of each model and no repair.
        Assert.Equal(2, shell.Agents.CodexRequests("turn/start").Count);
        Assert.Equal(0, run.RepairCyclesUsed);

        await shell.EnterAndWaitAsync("/test waive docs");
        shell.AssertShows("Usage: /test waive <gate> <reason>. The reason is recorded with the run.");
        await shell.EnterAndWaitAsync("/test waive nonsense because I say so");
        shell.AssertShows("'nonsense' is not a check of this project. /test list shows them.");
        Assert.Empty(shell.Services.Database.GetWaivers(run.RunId));

        await shell.EnterAndWaitAsync("/test waive docs the documentation is written in another task");
        shell.AssertShows($"Recorded: check 'docs' is waived for the current candidate of run {run.RunId}, because: the documentation is written in another task");
        shell.Enter("/resume");
        await shell.WaitForRunToEndAsync();

        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);
        var waiver = Assert.Single(shell.Services.Database.GetWaivers(run.RunId));
        Assert.Equal("the documentation is written in another task", waiver.Reason);
    }

    [Fact]
    public async Task Apply_writes_the_candidate_into_the_project_and_a_second_apply_does_nothing()
    {
        await using var shell = await ReadyAsync();
        var run = LastRun(shell);

        await shell.EnterAndWaitAsync("/apply");

        shell.AssertShows("[APPLY] Applying candidate", "[DONE] Applied 1 file(s): +0 ~1 -0. Reverse it with /undo while the files stay unedited.");
        Assert.Equal("fixed\n", shell.Project.Read("src/app.txt"));
        Assert.Equal("# App\n", shell.Project.Read("README.md"));
        Assert.Equal((RunState.Completed, RunDisposition.Applied), (LastRun(shell).State, LastRun(shell).Disposition));

        await shell.EnterAndWaitAsync("/apply");

        shell.AssertShows($"Run {run.RunId} was already applied.");
    }

    [Fact]
    public async Task A_file_the_user_edited_meanwhile_is_never_overwritten()
    {
        await using var shell = await ReadyAsync();
        shell.Project.Write("src/app.txt", "my own edit\n");

        await shell.EnterAndWaitAsync("/apply");

        shell.AssertShows(
            "[APPLY] src/app.txt:",
            "1 file(s) in the project no longer match what the task started from. Nothing was written.",
            "/apply --merge");
        Assert.Equal("my own edit\n", shell.Project.Read("src/app.txt"));
        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);
    }

    [Fact]
    public async Task Edits_of_the_user_are_combined_with_the_candidate_in_isolation_and_checked_again_before_anything_is_written()
    {
        await using var shell = await StartedAsync(
            new ShellOptions { Files = [("src/app.txt", NineLines), ("README.md", "# App\n")] },
            s =>
            {
                s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
                s.Agents
                    .ImplementerTurn(Step.Write("src/app.txt", NineLines.Replace("line 1\n", "line 1 by the task\n", StringComparison.Ordinal)), Step.Message("Done."))
                    .ReviewerTurn(Step.Review("pass"))
                    .ReviewerTurn(Step.Review("pass"));
            });
        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();
        var mine = NineLines.Replace("line 9\n", "line 9 by me\n", StringComparison.Ordinal);
        shell.Project.Write("src/app.txt", mine);

        shell.Enter("/apply --merge");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Combined your edits with the candidate in 1 file(s): src/app.txt. The result is a new candidate and is checked again.", "[READY]");
        Assert.Equal(mine, shell.Project.Read("src/app.txt"));

        // Model A implemented once; Model B reviewed the first candidate and the combined one.
        Assert.Equal(3, shell.Agents.CodexRequests("turn/start").Count);
        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);

        await shell.EnterAndWaitAsync("/apply");

        Assert.Equal(
            NineLines.Replace("line 1\n", "line 1 by the task\n", StringComparison.Ordinal).Replace("line 9\n", "line 9 by me\n", StringComparison.Ordinal),
            shell.Project.Read("src/app.txt").ReplaceLineEndings("\n"));
    }

    [Fact]
    public async Task Edits_that_cannot_be_combined_change_nothing()
    {
        await using var shell = await ReadyAsync();
        shell.Project.Write("src/app.txt", "my own edit\n");

        shell.Enter("/apply --merge");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("These files were changed both by you and by the task in ways that cannot be combined automatically: src/app.txt. Nothing was changed.");
        Assert.Equal("my own edit\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task Discard_removes_the_isolated_changes_after_a_yes_and_leaves_the_project_alone()
    {
        await using var shell = await ReadyAsync();
        var run = LastRun(shell);
        var workspace = (await WorkspaceOfAsync(shell, run))!;
        Assert.True(Directory.Exists(workspace.RootPath));

        await ConfirmAsync(shell, "/discard", "no");

        shell.AssertShows("Remove the isolated workspace of this task and what it changed there? The project is not touched.", "Not confirmed. Nothing was changed.");
        Assert.True(Directory.Exists(workspace.RootPath));
        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);

        await ConfirmAsync(shell, "/discard");

        shell.AssertShows("[LOCAL] Discarded the isolated changes of this task. The project was not touched.");
        Assert.False(Directory.Exists(workspace.RootPath));
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
        Assert.Equal(RunDisposition.Discarded, shell.Services.Database.FindRun(run.RunId)!.Disposition);
        Assert.Equal(string.Empty, shell.Project.Git("status", "--porcelain"));

        await shell.EnterAndWaitAsync($"/apply {run.RunId}");
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task Undo_reverses_the_last_apply_and_only_that()
    {
        await using var shell = await ReadyAsync();
        var run = LastRun(shell);

        await shell.EnterAndWaitAsync("/undo");
        shell.AssertShows("Nothing was applied to this project by YAV, so there is nothing to undo.");

        await shell.EnterAndWaitAsync("/apply");
        shell.Project.Write("README.md", "# App\nmy note\n");
        await shell.EnterAndWaitAsync("/undo");

        shell.AssertShows($"[LOCAL] Reversed 1 file(s) of run {run.RunId}.");
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
        Assert.Equal("# App\nmy note\n", shell.Project.Read("README.md"));
        Assert.Equal(RunDisposition.Undone, shell.Services.Database.FindRun(run.RunId)!.Disposition);

        await shell.EnterAndWaitAsync("/undo");
        shell.AssertShows("The most recent apply was already undone, and no earlier apply is still in effect.");
    }

    [Fact]
    public async Task Undo_leaves_a_file_alone_that_was_edited_after_the_apply()
    {
        await using var shell = await ReadyAsync(arrange: s => s.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Write("README.md", "# App\nfixed\n"), Step.Message("Done.")));

        // The arrangement above adds a turn after the usual one; the usual one ran. A follow-up uses the added turn.
        shell.Enter("Also say it in the README.");
        await shell.WaitForRunToEndAsync();
        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);
        await shell.EnterAndWaitAsync("/apply");
        Assert.Equal("# App\nfixed\n", shell.Project.Read("README.md"));
        shell.Project.Write("README.md", "# App\nfixed\nand my own line\n");

        await shell.EnterAndWaitAsync("/undo");

        shell.AssertShows("[LOCAL] README.md:", "1 file(s) were edited after the apply, so nothing was reversed. Use /undo --skip-edited to reverse the other files and leave those as they are.");
        Assert.Equal("fixed\n", shell.Project.Read("src/app.txt"));
        Assert.Equal("# App\nfixed\nand my own line\n", shell.Project.Read("README.md"));

        await shell.EnterAndWaitAsync("/undo --skip-edited");

        shell.AssertShows("1 file(s) you edited since were left as they are.");
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
        Assert.Equal("# App\nfixed\nand my own line\n", shell.Project.Read("README.md"));
    }

    [Fact]
    public async Task Resume_continues_an_interrupted_run_in_the_conversation_it_had()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang())
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Finished what was begun."))
                .ReviewerTurn(Step.Review("pass"));
        });
        shell.Enter(Task);
        await shell.WaitForAsync("update ");
        shell.Enter("/stop");
        await shell.WaitForRunToEndAsync();
        var run = LastRun(shell);
        Assert.Equal(RunState.Interrupted, run.State);

        shell.Enter("/resume");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Finished what was begun.", "[READY]");
        Assert.Equal(run.RunId, LastRun(shell).RunId);
        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);
        Assert.Single(shell.Agents.CodexRequests("thread/start"), t => t["params"]!["model"]!.GetValue<string>() == "model-a");
        var turns = shell.Agents.CodexRequests("turn/start");
        Assert.Equal(3, turns.Count);
        Assert.Equal(turns[0]["params"]!["threadId"]!.GetValue<string>(), turns[1]["params"]!["threadId"]!.GetValue<string>());
        Assert.NotEqual(turns[0]["params"]!["threadId"]!.GetValue<string>(), turns[2]["params"]!["threadId"]!.GetValue<string>());
    }

    [Fact]
    public async Task The_queue_is_shown_changed_and_emptied_by_the_user()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        });
        await shell.EnterAndWaitAsync("/queue");
        shell.AssertShows("No request is waiting.");

        shell.Enter(Task);
        await shell.WaitForAsync("update ");
        await shell.EnterAndWaitAsync("First follow-up.");
        await shell.EnterAndWaitAsync("/queue add Second follow-up.");
        await shell.EnterAndWaitAsync("/queue");

        shell.AssertShows("1", "yes First follow-up.", "2", "yes Second follow-up.");
        Assert.Equal(2, shell.Services.Database.GetQueue(shell.Project.Path).Count);

        await shell.EnterAndWaitAsync("/queue remove 1");
        shell.AssertShows("Removed: \"First follow-up.\"");
        Assert.Equal(["Second follow-up."], shell.Services.Database.GetQueue(shell.Project.Path).Select(q => q.Text).ToArray());

        await shell.EnterAndWaitAsync("/queue remove 7");
        shell.AssertShows("Usage: /queue remove <n>, where <n> is a number from /queue.");

        await shell.EnterAndWaitAsync("/queue clear");
        shell.AssertShows("1 waiting request(s) removed.");
        Assert.Empty(shell.Services.Database.GetQueue(shell.Project.Path));

        shell.Enter("/stop");
        await shell.WaitForRunToEndAsync();
        Assert.Single(shell.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_request_that_waits_is_added_to_the_running_turn_only_when_the_user_chooses_to()
    {
        const string Addition = "Also end the file with a line break.";
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Sleep(4_000), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("pass"));
        });
        shell.Enter(Task);

        // The line of the stage is written before the turn is sent, and only a turn that runs can be added to.
        await shell.WaitUntilAsync(() => shell.Agents.CodexRequests("turn/start").Count == 1, "the turn of the first request at the agent");
        await shell.EnterAndWaitAsync(Addition);
        Assert.Empty(shell.Agents.CodexRequests("turn/steer"));

        await shell.EnterAndWaitAsync("/queue steer 1");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[CODE A] Requirement 2 was added to the running turn. The candidate is reviewed and tested against all 2 requirements", "[READY]");
        Assert.Empty(shell.Services.Database.GetQueue(shell.Project.Path));
        Assert.Single(shell.Agents.CodexRequests("turn/steer"));
        Assert.Equal(2, shell.Agents.CodexRequests("turn/start").Count);
        Assert.Equal(2, LastRun(shell).AcceptanceVersion);
        var review = shell.Agents.CodexRequests("turn/start")[1]["params"]!["input"]![0]!["text"]!.GetValue<string>();
        Assert.Contains(Addition, review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_that_cannot_be_added_to_the_turn_keeps_waiting()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Sleep(4_000), Step.Review("pass"))
                .ImplementerTurn(Step.Write("README.md", "# App\nmore\n"), Step.Message("More."))
                .ReviewerTurn(Step.Review("pass"));
        });
        shell.Enter(Task);
        await shell.WaitForAsync("[REVIEW B]");
        await shell.EnterAndWaitAsync("Say more in the README.");

        await shell.EnterAndWaitAsync("/queue steer 1");

        shell.AssertShows("The request was not added: the agent that implements cannot take input during a turn, or it is not implementing right now. The request keeps waiting.");
        Assert.Empty(shell.Agents.CodexRequests("turn/steer"));
        Assert.Single(shell.Services.Database.GetQueue(shell.Project.Path));

        await shell.WaitForAsync("More.", 120);
        await shell.WaitForRunToEndAsync(120);
        Assert.Empty(shell.Services.Database.GetQueue(shell.Project.Path));
    }

    [Fact]
    public async Task Requests_that_wait_are_not_started_when_the_run_before_them_needs_a_decision()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        });
        shell.Enter(Task);
        await shell.WaitForAsync("update ");
        await shell.EnterAndWaitAsync("A follow-up that waits.");

        shell.Enter("/stop");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[QUEUE] 1 request(s) are waiting. They are not started, because the run needs a decision first. See /queue.");
        Assert.Single(shell.Agents.CodexRequests("turn/start"));
        Assert.Single(shell.Services.Database.GetQueue(shell.Project.Path));
    }

    [Fact]
    public async Task New_starts_an_unrelated_task_with_a_workspace_and_conversations_of_its_own()
    {
        await using var shell = await ReadyAsync(arrange: s => s.Agents
            .ImplementerTurn(Step.Write("README.md", "# App\nsecond\n"), Step.Message("Second task done."))
            .ReviewerTurn(Step.Review("pass")));
        var first = LastRun(shell);

        await shell.EnterAndWaitAsync("/new");

        shell.AssertShows(
            "The next request starts a new task with its own workspace and its own conversations.",
            $"Run {first.RunId} is ready but was not applied.");
        shell.Enter("Describe the app in the README.");
        await shell.WaitForRunToEndAsync();

        var second = LastRun(shell);
        Assert.NotEqual(first.TaskId, second.TaskId);
        Assert.Equal(RunState.ReadyToApply, shell.Services.Database.FindRun(first.RunId)!.State);
        Assert.Equal(2, shell.Agents.CodexRequests("thread/start").Count(t => t["params"]!["model"]!.GetValue<string>() == "model-a"));
        Assert.NotEqual((await WorkspaceOfAsync(shell, first))!.RootPath, (await WorkspaceOfAsync(shell, second))!.RootPath);
        var prompt = shell.Agents.CodexRequests("turn/start").Select(t => t["params"]!["input"]![0]!["text"]!.GetValue<string>()).Last(p => p.Contains("Describe the app in the README.", StringComparison.Ordinal));
        Assert.DoesNotContain(Task, prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_follow_up_continues_the_task_in_the_same_workspace_and_conversation()
    {
        await using var shell = await ReadyAsync(arrange: s => s.Agents
            .ImplementerTurn(Step.Write("README.md", "# App\nsecond\n"), Step.Message("Follow-up done."))
            .ReviewerTurn(Step.Review("pass")));
        var first = LastRun(shell);

        shell.Enter("Also describe it in the README.");
        await shell.WaitForRunToEndAsync();

        var second = LastRun(shell);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(RunState.ReadyToApply, second.State);
        Assert.Single(shell.Agents.CodexRequests("thread/start"), t => t["params"]!["model"]!.GetValue<string>() == "model-a");
        Assert.Equal((await WorkspaceOfAsync(shell, first))!.RootPath, (await WorkspaceOfAsync(shell, second))!.RootPath);

        await shell.EnterAndWaitAsync("/apply");
        Assert.Equal("fixed\n", shell.Project.Read("src/app.txt"));
        Assert.Equal("# App\nsecond\n", shell.Project.Read("README.md"));
    }

    [Fact]
    public async Task An_attached_file_is_named_to_model_a_with_the_next_request_and_only_with_that()
    {
        await using var shell = await StartedAsync(
            new ShellOptions { Files = [("src/app.txt", "one\n"), ("docs/spec notes.md", "The app says fixed.\n"), (".env", "TOKEN=1\n")] },
            s => s.WithPassingRun());

        await shell.EnterAndWaitAsync("/attach docs/spec notes.md");
        var attached = shell.Project.File("docs/spec notes.md");
        shell.AssertShows($"Attached to the next request: {attached}");

        await shell.EnterAndWaitAsync("/attach .env");
        shell.AssertShows("'.env' looks like it holds credentials. It was not attached: what is attached is named to a provider.");
        await shell.EnterAndWaitAsync("/attach missing.md");
        shell.AssertShows("missing.md' does not exist.");
        Assert.Equal([attached], shell.Shell.Session.Attachments);

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        var prompt = shell.Agents.CodexRequests("turn/start").Select(t => t["params"]!["input"]![0]!["text"]!.GetValue<string>()).First(p => p.Contains(Task, StringComparison.Ordinal));
        Assert.Contains("spec notes.md", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain(".env", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("TOKEN=1", prompt, StringComparison.Ordinal);
        Assert.Empty(shell.Shell.Session.Attachments);
    }

    /// <summary>A shell in a project with settings to replace, a check, and a reviewer that passes. There is no turn for Model A.</summary>
    private static Task<ShellHarness> WithSettingsAsync() => StartedAsync(
        new ShellOptions
        {
            Files =
            [
                ("src/app.txt", "timeout = 30\nretries = 3\nretry timeout = 5\n"),
                ("src/other.txt", "timeout = 30\n"),
                ("docs/my notes.txt", "say \"hi\" to everybody\n"),
            ],
        },
        s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents.ReviewerTurn(Step.Review("pass"));
        });

    private static string[] ModelsAsked(ShellHarness shell) =>
        shell.Agents.CodexRequests("thread/start").Select(t => t["params"]!["model"]!.GetValue<string>()).ToArray();

    [Fact]
    public async Task Replace_changes_a_text_without_model_a_and_the_result_is_reviewed_and_checked_like_any_change()
    {
        await using var shell = await WithSettingsAsync();

        shell.Enter("/replace src/app.txt \"timeout = 30\" \"timeout = 60\"");
        await shell.WaitForRunToEndAsync();

        var run = LastRun(shell);
        Assert.Equal(RunState.ReadyToApply, run.State);
        Assert.Equal(RunKind.MechanicalEdit, run.Kind);
        shell.AssertShows(
            "[LOCAL] Replaced 1 occurrence(s) in src/app.txt (line 1) locally. Model A was not called; review and checks follow as for any change.",
            "[REVIEW B] No blocking findings reported",
            "[TESTS] Required check passed",
            "[READY] Changes available for inspection (/diff) and application (/apply)");
        Assert.Equal(["model-b"], ModelsAsked(shell));
        var reviewed = Assert.Single(shell.Agents.CodexRequests("turn/start"))["params"]!["input"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Replace \"timeout = 30\" with \"timeout = 60\" in src/app.txt, where it occurs exactly once.", reviewed, StringComparison.Ordinal);
        Assert.Equal("timeout = 30\nretries = 3\nretry timeout = 5\n", shell.Project.Read("src/app.txt"));

        await shell.EnterAndWaitAsync("/apply");

        Assert.Equal("timeout = 60\nretries = 3\nretry timeout = 5\n", shell.Project.Read("src/app.txt"));
        Assert.Equal("timeout = 30\n", shell.Project.Read("src/other.txt"));
    }

    [Fact]
    public async Task Replace_does_nothing_when_the_text_is_there_more_often_than_was_said()
    {
        await using var shell = await WithSettingsAsync();

        shell.Enter("/replace src/app.txt timeout deadline");
        await shell.WaitForRunToEndAsync();

        Assert.Equal(RunState.Blocked, LastRun(shell).State);
        shell.AssertShows("The edit was not performed: Expected 1 match(es) but found 2. An ambiguous change is a task for Model A; send it as a request instead.");
        Assert.Empty(ModelsAsked(shell));

        await shell.EnterAndWaitAsync("/apply");

        Assert.Equal("timeout = 30\nretries = 3\nretry timeout = 5\n", shell.Project.Read("src/app.txt"));
    }

    [Theory]
    [InlineData("--count 2", "in exactly 2 places.")]
    [InlineData("--all", "wherever it occurs.")]
    public async Task Replace_changes_several_places_when_that_is_what_was_said(string option, string said)
    {
        await using var shell = await WithSettingsAsync();

        shell.Enter($"/replace src/app.txt timeout deadline {option}");
        await shell.WaitForRunToEndAsync();

        Assert.Equal(RunState.ReadyToApply, LastRun(shell).State);
        shell.AssertShows("[LOCAL] Replaced 2 occurrence(s) in src/app.txt (lines 1, 3) locally.");
        Assert.Contains($"Replace \"timeout\" with \"deadline\" in src/app.txt, {said}", LastRun(shell).RequestText, StringComparison.Ordinal);
        await shell.EnterAndWaitAsync("/apply");
        Assert.Equal("deadline = 30\nretries = 3\nretry deadline = 5\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task Replace_takes_a_path_with_a_blank_and_text_with_quotes()
    {
        await using var shell = await WithSettingsAsync();

        shell.Enter("/replace \"docs/my notes.txt\" \"say \"\"hi\"\"\" \"say \"\"good morning\"\"\"");
        await shell.WaitForRunToEndAsync();
        await shell.EnterAndWaitAsync("/apply");

        Assert.Equal("say \"good morning\" to everybody\n", shell.Project.Read("docs/my notes.txt"));
    }

    [Theory]
    [InlineData("/replace", "/replace <file> <text> <replacement> [--count <n> | --all]")]
    [InlineData("/replace src/app.txt timeout", "/replace <file> <text> <replacement> [--count <n> | --all]")]
    [InlineData("/replace src/app.txt timeout deadline extra", "/replace <file> <text> <replacement> [--count <n> | --all]")]
    [InlineData("/replace src/app.txt \"\" deadline", "The text to replace is empty.")]
    [InlineData("/replace src/app.txt timeout timeout", "The replacement is the text that is there already.")]
    [InlineData("/replace src/app.txt timeout deadline --count 0", "--count needs a number of 1 or more.")]
    [InlineData("/replace src/app.txt timeout deadline --count many", "--count needs a number of 1 or more.")]
    [InlineData("/replace src/app.txt timeout deadline --count 2 --all", "--count and --all contradict each other.")]
    [InlineData(@"/replace ..\outside.txt timeout deadline", "is not a file inside the project")]
    [InlineData(@"/replace C:\Windows\win.ini timeout deadline", "is not a file inside the project")]
    public async Task Replace_that_is_not_written_as_it_has_to_be_starts_nothing(string command, string expected)
    {
        await using var shell = await WithSettingsAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows(expected);
        Assert.Empty(shell.Services.Database.ListRuns(null, 10));
        Assert.Empty(ModelsAsked(shell));
    }

    [Theory]
    [InlineData("/replace src/missing.txt timeout deadline", "The edit was not performed: 'src/missing.txt' does not exist.")]
    [InlineData("/replace src/app.txt Timeout deadline", "The edit was not performed: The text to replace was not found.")]
    public async Task Replace_that_cannot_be_made_in_the_project_ends_without_a_candidate_and_without_a_model(string command, string expected)
    {
        await using var shell = await WithSettingsAsync();

        shell.Enter(command);
        await shell.WaitForRunToEndAsync();

        Assert.Equal(RunState.Blocked, LastRun(shell).State);
        shell.AssertShows(expected);
        Assert.Empty(ModelsAsked(shell));
    }

    [Fact]
    public async Task Replace_waits_while_a_run_is_active()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            s.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        });
        shell.Enter(Task);
        await shell.WaitForAsync("update ");

        await shell.EnterAndWaitAsync("/replace src/app.txt one two");

        shell.AssertShows("/replace waits until the active run has ended");
        Assert.Single(shell.Services.Database.ListRuns(null, 10));
        await shell.EnterAndWaitAsync("/stop");
        await shell.WaitForRunToEndAsync();
    }
}
