using System.Security.Cryptography;
using Xunit.Abstractions;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Settings;
using Yav.Storage;
using Yav.Tests.Live;
using Yav.Tests.Support;

namespace Yav.Tests.Packaging;

/// <summary>
/// What the parts of the live run (<see cref="LiveRunTests"/>) decide without a console: when they run at all, which
/// task they take, what they type, what they judge, and what they write into their records.
/// </summary>
public class LiveRunRulesTests(ITestOutputHelper output)
{
    [Fact]
    public async Task The_part_that_continues_a_run_does_nothing_unless_the_script_says_that_the_usage_was_authorized()
    {
        using var run = new StandInRun();
        var part = run.Part(new() { [LiveRunFactAttribute.Variable] = "resume", ["YAV_LIVE_RECORD"] = "resume-1", ["YAV_LIVE_RUN_ID"] = "r-1" });

        var refused = await Assert.ThrowsAnyAsync<Exception>(() => new LiveRunTests(output).ResumeAsync(part));

        Assert.Contains("-IAuthorizeUsage", refused.Message, StringComparison.Ordinal);
        Assert.False(run.Exists("resume-1-screens.txt"));
    }

    [Theory]
    [InlineData("setup", false, "setup", null, true)]
    [InlineData("setup", false, null, null, false)]
    [InlineData("inspect", false, "Inspect", null, true)]
    [InlineData("resume", true, "resume", "1", true)]
    [InlineData("resume", true, "resume", null, false)]
    [InlineData("resume", true, "resume", "yes", false)]
    [InlineData("resume", true, "setup", "1", false)]
    public void A_part_runs_only_when_the_script_asked_for_it_and_one_that_asks_the_models_only_when_the_script_checked_the_authorization(
        string part, bool asksModels, string? asked, string? authorized, bool runs)
    {
        var variables = new Dictionary<string, string?> { [LiveRunFactAttribute.Variable] = asked, [LiveRunFactAttribute.Authorized] = authorized };

        Assert.Equal(runs, LiveRunFactAttribute.SkipReason(part, asksModels, name => variables.GetValueOrDefault(name)) is null);
    }

    [Fact]
    public void The_part_that_continues_a_run_is_marked_as_one_that_asks_the_models()
    {
        var parts = typeof(LiveRunTests).GetMethods()
            .Select(method => (method.Name, Fact: method.GetCustomAttributes(typeof(LiveRunFactAttribute), false).Cast<LiveRunFactAttribute>().SingleOrDefault()))
            .Where(part => part.Fact is not null)
            .ToDictionary(part => part.Name, part => part.Fact!.AsksModels);

        Assert.Equal(3, parts.Count);
        Assert.True(parts[nameof(LiveRunTests.A_run_that_waits_for_an_answer_is_continued_and_nothing_is_granted)]);
        Assert.False(parts[nameof(LiveRunTests.The_shell_is_set_up_the_way_a_user_sets_it_up)]);
        Assert.False(parts[nameof(LiveRunTests.What_the_run_left_is_looked_at_and_applied_the_way_a_user_does_it)]);
    }

    private static void Copy(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    [Fact]
    public void The_task_is_taken_only_as_it_was_approved_at_setup()
    {
        using var task = new TempDirectory("task");
        Copy(Path.Combine(StandInRun.Repository, "bench", "tasks", "01-small-edit"), task.Path);
        var approved = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(task.File("task.json"))));

        Assert.Equal("01-small-edit", LiveRunTests.ApprovedTask(task.Path, approved).Id);

        // A check that runs another command than the one that was approved.
        task.Write("task.json", task.Read("task.json").Replace("\"python\"", "\"powershell\"", StringComparison.Ordinal));
        var refused = Assert.ThrowsAny<Exception>(() => LiveRunTests.ApprovedTask(task.Path, approved));

        Assert.Contains("task.json", refused.Message, StringComparison.Ordinal);
        Assert.Contains("approved", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Apply_is_typed_with_the_number_of_the_run_it_applies()
    {
        var applying = LiveRunTests.InspectCommands(apply: true, runId: "20260930-101010-abcde");
        var looking = LiveRunTests.InspectCommands(apply: false, runId: null);

        Assert.Contains("/apply 20260930-101010-abcde", applying);
        Assert.DoesNotContain(applying, command => command.Trim() == "/apply");
        Assert.DoesNotContain(looking, command => command.StartsWith("/apply", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(true, 0, 0, true)]
    [InlineData(true, 0, 1, false)]
    [InlineData(true, 1, 1, false)]
    [InlineData(false, 0, 1, true)]
    public void After_apply_every_check_has_to_end_with_0(bool apply, int tests, int lint, bool passed)
    {
        var (ok, verdict) = LiveRunTests.Judge([("tests", tests), ("lint", lint)], apply);

        Assert.Equal(passed, ok);
        if (!passed)
        {
            Assert.Contains("lint: exit code 1", verdict, StringComparison.Ordinal);
            Assert.Equal(tests != 0, verdict.Contains("tests: exit code 1", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(0, false)]
    public async Task With_apply_the_part_fails_when_a_check_does_not_pass_and_its_record_begins_with_the_verdict(int exitCode, bool checkFails)
    {
        using var run = new StandInRun();
        Directory.CreateDirectory(run.Project);
        var part = run.Part(new() { ["YAV_LIVE_RECORD"] = "inspect-1" });
        Yav.Core.Runs.GateDefinition[] checks = [CoordinatorHarness.ToolGate("tests", "exit", exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture))];

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => new LiveRunTests(output).JudgeAsync(part, checks, apply: true, runId: "r-1"));

        // No run r-1 is recorded, so /apply did not apply it; whether the checks failed is said as well.
        Assert.Contains("/apply did not apply run r-1", failure.Message, StringComparison.Ordinal);
        Assert.Equal(checkFails, failure.Message.Contains("tests: exit code 1", StringComparison.Ordinal));
        Assert.StartsWith(checkFails ? "After /apply these checks do not pass" : "After /apply every check passes", run.Read("inspect-1-judgement.txt"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_apply_the_checks_are_recorded_and_decide_nothing()
    {
        using var run = new StandInRun();
        Directory.CreateDirectory(run.Project);
        var part = run.Part(new() { ["YAV_LIVE_RECORD"] = "inspect-1" });

        await new LiveRunTests(output).JudgeAsync(part, [CoordinatorHarness.ToolGate("tests", "exit", "1")], apply: false, runId: null);

        Assert.StartsWith("Checks that do not pass in the project, which was not changed", run.Read("inspect-1-judgement.txt"), StringComparison.Ordinal);
    }

    private static ApprovalRecord Decision(string runId, string id, string decision, string command) => new(
        runId, id, AgentRole.Implementer, ApprovalKind.CommandExecution, "Run a command", command, decision, "user",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch);

    private static void Save(YavPaths home, params ApprovalRecord[] decisions)
    {
        var database = YavDatabase.Open(home.Database, TimeProvider.System);
        try
        {
            foreach (var decision in decisions)
            {
                database.SaveApproval(decision);
            }
        }
        finally
        {
            database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    [Fact]
    public void The_record_says_that_nothing_was_granted_only_when_the_decisions_recorded_for_the_run_say_so()
    {
        using var directory = new TempDirectory("home");
        var home = new YavPaths(directory.Path);
        home.EnsureCreated();
        Save(home, Decision("r-1", "a-0", "Cancel", "npm test"), Decision("r-2", "a-1", "Accept", "npm install elsewhere"));
        var before = LiveRunRecords.LastDecision(home, "r-1");

        Save(home, Decision("r-1", "a-2", "Decline", "npm install left-pad"));
        var declined = LiveRunRecords.Note(1, LiveRunRecords.Decisions(home, "r-1", before));

        Save(home, Decision("r-1", "a-3", "AcceptForSession", "curl https://example.invalid/x"));
        var granted = LiveRunRecords.Note(2, LiveRunRecords.Decisions(home, "r-1", before));

        Assert.EndsWith("Decisions recorded for the run while this part ran: 1 (Decline 1). None was granted.", declined, StringComparison.Ordinal);
        Assert.DoesNotContain("None was granted", granted, StringComparison.Ordinal);
        Assert.Contains("curl https://example.invalid/x", granted, StringComparison.Ordinal);
        Assert.DoesNotContain("npm install elsewhere", granted, StringComparison.Ordinal);
    }

    [Fact]
    public void A_record_whose_decisions_cannot_be_read_does_not_say_that_nothing_was_granted()
    {
        using var directory = new TempDirectory("home");

        var note = LiveRunRecords.Note(1, LiveRunRecords.Decisions(new YavPaths(directory.Path), "r-1", 0));

        Assert.DoesNotContain("None was granted", note, StringComparison.Ordinal);
    }
}
