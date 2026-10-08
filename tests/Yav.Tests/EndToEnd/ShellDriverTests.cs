using System.Diagnostics;
using System.Text.Json.Nodes;
using Xunit.Sdk;
using Yav.Adapters;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.EndToEnd;

/// <summary>
/// The driver of the live run against the real program in a console, with the scripted stand-in in place of the
/// agents. What the live run relies on when it reads the screen and presses keys is tested here, where no model
/// is asked anything.
/// </summary>
[Collection(nameof(InteractiveConsoleTests))]
public class ShellDriverTests
{
    private const string Task = "Make the app say fixed.";

    private static ShellDriver Start(YavProcess yav) => ShellDriver.Start(YavProcess.Executable, yav.Project.Path, yav.Paths);

    /// <summary>The first turn of Model A asks for something, which 'yav run --json' cannot answer; the second is what /resume meets.</summary>
    private static YavProcess AskingAgain(params JsonObject[] continued)
    {
        var yav = new YavProcess();
        yav.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        yav.Agents
            .ImplementerTurn(Step.Approval("npm install left-pad", onDecline: [Step.Message("Not installed.")]))
            .ImplementerTurn(continued)
            .ReviewerTurn(Step.Review("pass"));
        return yav;
    }

    /// <summary>
    /// A run that stopped because Model A asked for something and nobody could answer, as 'yav run --json' leaves
    /// it. The stand-in knows the conversation afterwards, as a provider does, so that /resume continues it.
    /// </summary>
    private static async Task<string> StoppedRunAsync(YavProcess yav)
    {
        var ran = await yav.RunAsync(["run", "--project", yav.Project.Path, "--task", Task, "--json"], seconds: 90);
        Assert.Equal("approval_required", ran.Json[^1].GetProperty("outcome").GetString());
        var conversation = yav.Agents.CodexRequests("turn/start")[0]["params"]!["threadId"]!.GetValue<string>();
        yav.Agents.Codex(c =>
        {
            c["knownThreads"] = new JsonArray { conversation };
            c["threadStates"] = new JsonObject { [conversation] = "interrupted" };
        });
        return ran.Json[^1].GetProperty("runId").GetString()!;
    }

    /// <summary>What the stand-in was told in answer to its requests for approval, oldest first.</summary>
    private static List<string> Answers(YavProcess yav) =>
        yav.Agents.Received("codex.response").Select(r => r["result"]?["decision"]?.GetValue<string>()).OfType<string>().ToList();

    private static T Stored<T>(YavProcess yav, Func<Yav.Storage.YavDatabase, T> read)
    {
        var database = Yav.Storage.YavDatabase.Open(yav.Paths.Database, TimeProvider.System);
        try
        {
            return read(database);
        }
        finally
        {
            database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public async Task A_confirmation_that_was_not_expected_is_answered_with_no_and_named()
    {
        // Not signed in: /login offers to start Codex's own sign-in, which nobody asked for.
        using var yav = new YavProcess(acknowledgeRoutes: false);
        yav.Agents.Codex(c => c["account"] = null);
        using var driver = Start(yav);
        await driver.WaitForPromptAsync();

        var failure = await Assert.ThrowsAnyAsync<XunitException>(
            () => driver.EnterAsync("/login codex", new ExpectedQuestion(ShellDriver.RouteQuestion, "yes")));

        Assert.Contains("Start Codex's own sign-in now?", failure.Message, StringComparison.Ordinal);
        Assert.Contains("answered with no", failure.Message, StringComparison.Ordinal);
        Assert.Contains(driver.Console.Screen.Lines, row => row.EndsWith("Type yes to confirm: no", StringComparison.Ordinal));
        Assert.True(driver.Console.Screen.Shows("Not confirmed. Nothing was changed."), driver.Console.Screen.Text);
        Assert.DoesNotContain(driver.Console.Screen.Lines, row => row.Contains("The sign-in ended", StringComparison.Ordinal));
        await driver.ExitAsync();
    }

    [Fact]
    public async Task The_confirmation_that_was_expected_gets_the_answer_it_was_given()
    {
        using var yav = new YavProcess(acknowledgeRoutes: false);
        yav.Agents.Codex(c => c["account"] = new JsonObject { ["type"] = "apiKey" });
        using var driver = Start(yav);
        await driver.WaitForPromptAsync();

        await driver.EnterAsync("/login codex", new ExpectedQuestion(ShellDriver.RouteQuestion, "yes"));

        Assert.Contains("Acknowledged. It is asked again when the account route changes.", driver.Transcript.ToString(), StringComparison.Ordinal);
        await driver.ExitAsync();
        Assert.True(Stored(yav, database => database.IsRouteAcknowledged($"{CodexAppServerAdapter.AdapterId}:ApiKey:openai")));
    }

    [Fact]
    public async Task A_command_that_is_expected_to_ask_nothing_approves_nothing()
    {
        using var yav = new YavProcess();
        yav.Project.Write(
            "yav.project.json",
            Yav.Validation.ConfigurationParser.Serialize(ProjectConfiguration.Empty with { Gates = [CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug")] }));
        using var driver = Start(yav);
        await driver.WaitForPromptAsync();

        var failure = await Assert.ThrowsAnyAsync<XunitException>(() => driver.EnterAsync("/test trust"));

        Assert.Contains(ShellDriver.ChecksQuestion, failure.Message, StringComparison.Ordinal);
        await driver.ExitAsync();
        var trust = Stored(yav, database => new Yav.Validation.ValidationService(new Yav.Platform.Processes.ProcessRunner(), database, TimeProvider.System, "test")
            .LoadConfiguration(yav.Project.Path).Trust);
        Assert.Equal(ConfigurationTrust.Untrusted, trust);
    }

    [Fact]
    public async Task A_run_that_is_continued_with_resume_has_its_question_declined_and_ends()
    {
        using var yav = AskingAgain(Step.Approval(
            "npm install left-pad",
            onAccept: [Step.Write("src/app.txt", "installed\n")],
            onDecline: [Step.Write("src/app.txt", "fixed\n"), Step.Message("Fixed without the package.")]));
        var runId = await StoppedRunAsync(yav);
        var before = Answers(yav).Count;
        using var driver = Start(yav);
        await driver.WaitForPromptAsync();

        var followed = await driver.ResumeAsync(runId, TimeSpan.FromMinutes(3));

        Assert.Equal($"Run {runId}: Ready to Apply", followed.Result);
        Assert.Equal(1, followed.QuestionsAnswered);
        Assert.False(followed.Stopped);
        Assert.Equal(["decline"], Answers(yav).Skip(before));
        Assert.Contains("[APPROVAL] Model A asks to run a command", driver.Transcript.ToString(), StringComparison.Ordinal);
        await driver.ExitAsync();
        Assert.Equal(RunState.ReadyToApply, yav.FindRun(runId)!.State);
    }

    [Fact]
    public async Task Two_questions_one_after_the_other_get_two_declines_and_the_run_ends()
    {
        using var yav = AskingAgain(Step.Approval(
            "npm install left-pad",
            onDecline:
            [
                Step.Approval("curl https://example.invalid/left-pad.js", onDecline: [Step.Write("src/app.txt", "fixed\n"), Step.Message("Fixed without either.")]),
            ]));
        var runId = await StoppedRunAsync(yav);
        var before = Answers(yav).Count;
        using var driver = Start(yav);
        await driver.WaitForPromptAsync();

        var followed = await driver.ResumeAsync(runId, TimeSpan.FromMinutes(3));

        Assert.Equal($"Run {runId}: Ready to Apply", followed.Result);
        Assert.Equal(2, followed.QuestionsAnswered);
        Assert.Equal(["decline", "decline"], Answers(yav).Skip(before));

        // No key was left over for the input line: /exit is typed on an empty one.
        await driver.ExitAsync();
    }

    [Fact]
    public async Task A_limit_that_passes_while_a_question_is_open_ends_the_question_and_then_the_run()
    {
        using var yav = AskingAgain(Step.Approval(
            "npm install left-pad",
            onAccept: [Step.Write("src/app.txt", "installed\n")],
            onDecline: [Step.Write("src/app.txt", "fixed\n"), Step.Message("Fixed without the package.")]));
        var runId = await StoppedRunAsync(yav);
        var before = Answers(yav).Count;
        using var driver = Start(yav);
        driver.Clock = new UpOnceAsked(driver.Console.Screen);
        await driver.WaitForPromptAsync();

        var followed = await driver.ResumeAsync(runId, TimeSpan.FromMinutes(30));

        Assert.True(followed.Stopped);
        Assert.StartsWith($"Run {runId}: ", followed.Result, StringComparison.Ordinal);

        // Declined, and the turn ended with it: not a decline after which the agent goes on, and never a grant.
        Assert.Equal(["cancel"], Answers(yav).Skip(before));
        await driver.EnterAsync("/status");
        await driver.ExitAsync();
    }

    [Theory]
    [InlineData(true, "Completed")]
    [InlineData(false, "Ready to Apply")]
    public async Task Resume_of_a_run_that_has_nothing_left_to_do_fails_at_once_and_types_nothing(bool apply, string state)
    {
        using var yav = new YavProcess().WithPassingRun();
        var ran = await yav.RunAsync(apply
            ? ["run", "--project", yav.Project.Path, "--task", Task, "--json", "--apply"]
            : ["run", "--project", yav.Project.Path, "--task", Task, "--json"]);
        var runId = ran.Json[^1].GetProperty("runId").GetString()!;
        using var driver = Start(yav);
        await driver.WaitForPromptAsync();
        var watch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAnyAsync<XunitException>(() => driver.ResumeAsync(runId, TimeSpan.FromMinutes(30)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The driver took {watch.Elapsed.TotalSeconds:0} s to give up.");
        Assert.Contains($"Run {runId} is {state}", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(driver.Console.Screen.Lines, row => row.StartsWith(driver.Prompt + " /resume", StringComparison.Ordinal));
        await driver.ExitAsync();
    }

    [Fact]
    public async Task Resume_of_a_run_that_does_not_exist_fails_at_once_and_types_nothing()
    {
        using var yav = new YavProcess();
        using var driver = Start(yav);
        await driver.WaitForPromptAsync();
        var watch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAnyAsync<XunitException>(() => driver.ResumeAsync("20260930-000000-nones", TimeSpan.FromMinutes(30)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"The driver took {watch.Elapsed.TotalSeconds:0} s to give up.");
        Assert.Contains("Run 20260930-000000-nones does not exist", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(driver.Console.Screen.Lines, row => row.StartsWith(driver.Prompt + " /resume", StringComparison.Ordinal));
        await driver.ExitAsync();
    }

    [Fact]
    public async Task Resume_that_the_shell_refuses_without_saying_so_fails_soon_after_the_prompt_is_back()
    {
        using var yav = AskingAgain(Step.Message("Never reached."));
        var runId = await StoppedRunAsync(yav);

        // Without the record of its isolated workspace the run cannot be continued, and the shell says nothing about it.
        foreach (var record in Directory.EnumerateFiles(yav.Paths.Workspaces, "workspace.json", SearchOption.AllDirectories))
        {
            File.Delete(record);
        }

        using var driver = Start(yav);
        await driver.WaitForPromptAsync();
        var watch = Stopwatch.StartNew();

        var failure = await Assert.ThrowsAnyAsync<XunitException>(() => driver.ResumeAsync(runId, TimeSpan.FromMinutes(30)));

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The driver took {watch.Elapsed.TotalSeconds:0} s to give up.");
        Assert.Contains("no run was active", failure.Message, StringComparison.Ordinal);
        Assert.Contains($"run {runId} is Blocked", failure.Message, StringComparison.Ordinal);
        await driver.ExitAsync();
    }

    [Fact]
    public void A_question_of_an_agent_is_told_from_a_note_about_an_answer_and_from_what_an_agent_wrote()
    {
        string[] rows =
        [
            "[APPROVAL] Model A asks to run a command",
            "  │ npm install left-pad",
            "[APPROVAL] Run a command: Decline (user)",
            "[APPROVAL] Model A asks to run a command: Decline (user)",
            "[APPROVAL] \"Model A asks to run a command\": Decline (user)",
            "[APPROVAL] \"Model A asks to run a command",
            "  │ [APPROVAL] Model A asks to run a command",
        ];

        Assert.Equal(0, ShellDriver.LastQuestionRow(rows));
    }

    [Theory]
    [InlineData("Allow \"npm install left-pad\"? a, s, d or c, then Enter:", true)]
    [InlineData("Allow the request above? Type allow to allow once, d or c to decline, then Enter:", true)]
    [InlineData("Allow \"npm install left-pad\"? a, s, d or c, then Enter: d", false)]
    [InlineData("  │ Allow \"rm -rf ~\"? a, s, d or c, then Enter:", false)]
    [InlineData("Approve this configuration? Type yes to confirm:", false)]
    public void A_question_that_waits_for_its_answer_is_told_by_its_line(string cursor, bool open)
    {
        Assert.Equal(open, ShellDriver.IsOpenQuestion(cursor));
    }

    [Fact]
    public void A_confirmation_that_fills_more_than_a_row_is_read_whole_and_gets_one_answer()
    {
        const int Columns = 40;
        const string Asked = "Use 'ChatGPT plan (pro)' for runs of YAV, billed as stated above? Type yes to confirm:";
        var rows = new List<string> { "YAV C:\\p> /login", "  Not signed in." };
        rows.AddRange(Asked.Chunk(Columns).Select(row => new string(row)));

        Assert.Equal(Asked, ShellDriver.PendingConfirmation(rows, rows[^1], 0, answered: 0, Columns));

        // Answered, but the answer has not been taken yet: the question is still on the screen and is not answered again.
        Assert.Null(ShellDriver.PendingConfirmation(rows, rows[^1], 0, answered: 1, Columns));

        // The answer was taken and stays on the screen above the next question, which is new.
        var next = rows.Take(2).Concat((Asked + " yes").Chunk(Columns - 1).Select(row => new string(row))).Append("Start it now? Type yes to confirm:").ToList();
        Assert.Equal("Start it now? Type yes to confirm:", ShellDriver.PendingConfirmation(next, next[^1], 0, answered: 1, Columns));
    }

    /// <summary>
    /// A clock that jumps a day ahead the first time the console shows a question of an agent, so that the limit
    /// of a run passes exactly while a question is open.
    /// </summary>
    private sealed class UpOnceAsked(PtyScreen screen) : TimeProvider
    {
        private int _asked;

        public override DateTimeOffset GetUtcNow()
        {
            if (screen.CursorLine.StartsWith(ShellDriver.Question, StringComparison.Ordinal))
            {
                Interlocked.Exchange(ref _asked, 1);
            }

            return base.GetUtcNow() + (Volatile.Read(ref _asked) == 1 ? TimeSpan.FromDays(1) : TimeSpan.Zero);
        }
    }
}
