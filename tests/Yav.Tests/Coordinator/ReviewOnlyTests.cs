using System.Text.Json;
using Yav.Console.Cli;
using Yav.Console.Output;
using Yav.Console.Rendering;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>
/// A project that has no approved check that is required runs only when the user accepted, for that project, that a
/// candidate is accepted on the review alone. The acceptance applies only while no approved check is required: an
/// approved check that is optional leaves it in force.
/// </summary>
public class ReviewOnlyTests
{
    private const string Task = "Make the app say fixed.";
    private const string Accepted = "Accepted on the review of Model B alone: the project has no approved check.";

    /// <summary>The implementer changes the file and the reviewer passes it. No check is approved.</summary>
    private static CoordinatorHarness WithoutChecks()
    {
        var harness = new CoordinatorHarness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Changed app.txt to say fixed."))
            .ReviewerTurn(Step.Review("pass"));
        return harness;
    }

    private static IEnumerable<StageNote> ReviewOnlyNotes(CoordinatorHarness harness) =>
        harness.Observer.Notes(Stages.Prepare).Where(n => n.Message.Contains("accepted on the review of Model B alone", StringComparison.Ordinal));

    private static IEnumerable<StageNote> OptionalNotes(CoordinatorHarness harness) =>
        harness.Observer.Notes(Stages.Prepare).Where(n => n.Message.Contains("accepted on Model B's review alone", StringComparison.Ordinal));

    private static IEnumerable<StageNote> DetectNotes(CoordinatorHarness harness) =>
        harness.Observer.Notes(Stages.Prepare).Where(n => n.Message.Contains("approve them with /test detect", StringComparison.Ordinal));

    [Fact]
    public async Task A_project_without_approved_checks_does_not_run_until_the_review_alone_is_accepted_for_it()
    {
        await using var harness = WithoutChecks();
        using var other = new TempDirectory("another project");
        harness.Database.AcceptReviewOnly(other.Path, Accepted);

        var blocked = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, blocked);
        Assert.Contains(blocked.Problems, p => p.Code == "gates-missing" && p.Severity == ProblemSeverity.Blocking);
        Assert.DoesNotContain(blocked.Problems, p => p.Code == "review-only");
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));

        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
    }

    [Fact]
    public async Task With_the_review_alone_accepted_a_candidate_is_ready_without_a_check_and_the_run_says_so()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.True(outcome.Decision!.Accepted, string.Join("; ", outcome.Decision.Issues.Select(i => i.Message)));
        Assert.True(harness.Database.GetLatestReview(outcome.RunId, outcome.Candidate!.CandidateId)!.IsCleanPass);
        Assert.Empty(harness.Database.GetGateResults(outcome.RunId));

        // The run itself records that checks were not required for it, so that what it was accepted under can be read later.
        var profile = harness.Database.GetProfile(outcome.RunId)!;
        Assert.False(profile.Policy.RequireGates);
        Assert.True(profile.Policy.QualityLock);
        Assert.True(profile.Policy.Strict);
        Assert.True(profile.Policy.RequireReview);

        var note = Assert.Single(ReviewOnlyNotes(harness));
        Assert.Equal(NoteLevel.Warning, note.Level);
        Assert.Contains("/test detect", note.Message, StringComparison.Ordinal);
        Assert.Contains("/quality gates required", note.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_approved_check_is_required_although_the_review_alone_was_accepted()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(GateStatus.Passed, Assert.Single(harness.Database.GetGateResults(outcome.RunId)).Status);
        Assert.True(harness.Database.GetProfile(outcome.RunId)!.Policy.RequireGates);
        Assert.Empty(ReviewOnlyNotes(harness));
    }

    [Fact]
    public async Task A_check_that_is_approved_but_not_required_does_not_end_the_acceptance()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        harness.TrustGates(CoordinatorHarness.TextGate("lint", "src/app.txt", "fixed", required: false));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.False(harness.Database.GetProfile(outcome.RunId)!.Policy.RequireGates);
        Assert.Single(ReviewOnlyNotes(harness));
    }

    [Fact]
    public async Task Withdrawing_the_acceptance_makes_the_project_wait_for_approved_checks_again()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        Assert.True(harness.Database.WithdrawReviewOnly(harness.ProjectPath));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(outcome.Problems, p => p.Code == "gates-missing");
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_candidate_accepted_on_the_review_alone_is_applied()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        var outcome = await harness.RunAsync(Task);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);

        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task A_candidate_accepted_on_the_review_alone_is_not_applied_once_a_check_was_approved_for_the_project()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        var outcome = await harness.RunAsync(Task);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));

        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.False(delivery.Succeeded);
        Assert.Contains(delivery.Decision!.Issues, i => i.Kind == AcceptanceIssueKind.GateMissing);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task An_approval_that_cannot_be_read_does_not_count_as_no_approved_check()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        harness.Database.SaveTrustedConfiguration(harness.ProjectPath, "0123", "{ this is not json");

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        var blocking = Assert.Single(outcome.Problems, p => p.Code == "review-only-unknown");
        Assert.Equal(ProblemSeverity.Blocking, blocking.Severity);
        Assert.Contains("The approved configuration stored by YAV can no longer be read", blocking.Message, StringComparison.Ordinal);
        Assert.Contains("/test trust", blocking.Remedy!, StringComparison.Ordinal);
        Assert.Contains(outcome.Problems, p => p.Code == "gates-invalid" && p.Message.StartsWith("The approved configuration stored by YAV", StringComparison.Ordinal));
        Assert.DoesNotContain(outcome.Problems, p => p.Code == "review-only");
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task An_approval_that_cannot_be_read_is_reported_beside_a_file_that_was_not_approved()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        harness.Database.SaveTrustedConfiguration(harness.ProjectPath, "0123", "{ this is not json");
        harness.WriteProject(
            harness.Validation.ConfigurationFileName,
            harness.Validation.Serialize(ProjectConfiguration.Empty with { Gates = [CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed")] }));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        var untrusted = Assert.Single(outcome.Problems, p => p.Code == "gates-untrusted");
        Assert.Contains("cannot be read", untrusted.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("has not been approved by you", untrusted.Message, StringComparison.Ordinal);
        Assert.Contains(outcome.Problems, p => p.Code == "gates-invalid" && p.Message.StartsWith("The approved configuration stored by YAV", StringComparison.Ordinal));
        Assert.Contains(outcome.Problems, p => p.Code == "review-only-unknown" && p.Severity == ProblemSeverity.Blocking);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_configuration_file_that_cannot_be_used_keeps_the_review_alone_from_accepting()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        harness.WriteProject(harness.Validation.ConfigurationFileName, "{ \"gates\": [ ");

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(outcome.Problems, p => p.Code == "gates-invalid");
        Assert.Contains(outcome.Problems, p => p.Code == "review-only-unknown" && p.Severity == ProblemSeverity.Blocking);
        Assert.DoesNotContain(outcome.Problems, p => p.Code == "review-only");
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_candidate_accepted_on_the_review_alone_is_not_applied_once_the_acceptance_was_withdrawn()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        var outcome = await harness.RunAsync(Task);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.True(harness.Database.WithdrawReviewOnly(harness.ProjectPath));

        var refused = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.False(refused.Succeeded);
        Assert.Contains("withdrew that acceptance", refused.Message, StringComparison.Ordinal);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
        Assert.Equal(RunState.ReadyToApply, harness.Database.FindRun(outcome.RunId)!.State);

        // Given once more, the acceptance covers the candidate again.
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task A_withdrawal_before_the_run_does_not_keep_a_run_with_checks_optional_from_being_applied()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        Assert.True(harness.Database.WithdrawReviewOnly(harness.ProjectPath));
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(RequireGates: false) };
        var outcome = await harness.RunAsync(Task);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);

        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task With_checks_optional_for_every_project_the_acceptance_of_one_is_not_mentioned()
    {
        await using var harness = WithoutChecks();
        harness.Database.AcceptReviewOnly(harness.ProjectPath, Accepted);
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(RequireGates: false) };

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Empty(ReviewOnlyNotes(harness));
        Assert.DoesNotContain(outcome.Problems, p => p.Code == "review-only");
        Assert.Single(OptionalNotes(harness));
    }

    [Fact]
    public async Task With_checks_optional_a_project_without_checks_runs_on_the_review_alone_and_says_so_in_one_line()
    {
        await using var harness = WithoutChecks();
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(RequireGates: false) };

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.True(outcome.Decision!.Accepted);
        Assert.Empty(harness.Database.GetGateResults(outcome.RunId));
        var note = Assert.Single(harness.Observer.Notes(Stages.Prepare), n => n.Message.Contains("Model B's review alone", StringComparison.Ordinal));
        Assert.Equal(NoteLevel.Warning, note.Level);
        Assert.DoesNotContain('\n', note.Message);

        // Nothing in the project suggests a check, so nothing is suggested.
        Assert.Empty(DetectNotes(harness));
    }

    [Fact]
    public async Task With_checks_optional_checks_the_project_suggests_are_named_but_not_run()
    {
        await using var harness = WithoutChecks();
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(RequireGates: false) };
        harness.WriteProject("Cargo.toml", "[package]\nname = \"app\"\n");

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Empty(harness.Database.GetGateResults(outcome.RunId));
        Assert.Single(OptionalNotes(harness));
        var suggestion = Assert.Single(DetectNotes(harness));
        Assert.Equal(NoteLevel.Info, suggestion.Level);
    }

    [Fact]
    public async Task With_checks_optional_an_approved_required_check_still_runs_and_must_pass()
    {
        await using var harness = WithoutChecks();
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(RequireGates: false) };
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(GateStatus.Passed, Assert.Single(harness.Database.GetGateResults(outcome.RunId)).Status);
        Assert.Empty(OptionalNotes(harness));
    }

    [Fact]
    public async Task With_checks_optional_a_configuration_file_that_cannot_be_used_keeps_the_review_alone_from_accepting()
    {
        await using var harness = WithoutChecks();
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(RequireGates: false) };
        harness.WriteProject(harness.Validation.ConfigurationFileName, "{ \"gates\": [ ");

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(outcome.Problems, p => p.Code == "review-only-unknown" && p.Severity == ProblemSeverity.Blocking);
        Assert.Empty(OptionalNotes(harness));
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task By_default_yav_run_runs_a_project_without_checks_on_the_review_alone()
    {
        await using var shell = new ShellHarness();
        shell.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var (exitCode, result) = await YavRunAsync(shell);

        Assert.Equal(0, exitCode);
        Assert.Equal("ready_to_apply", result.GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("acceptance").GetProperty("accepted").GetBoolean());
    }

    [Fact]
    public async Task Yav_run_honors_the_acceptance_of_the_review_alone_as_the_shell_does()
    {
        await using var shell = new ShellHarness(new ShellOptions { RequireChecks = true });
        shell.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var (blockedCode, blocked) = await YavRunAsync(shell);
        shell.Services.Database.AcceptReviewOnly(shell.Project.Path, Accepted);
        var (exitCode, result) = await YavRunAsync(shell);

        Assert.Equal(ExitCodes.Blocked, blockedCode);
        Assert.Equal("blocked", blocked.GetProperty("outcome").GetString());
        Assert.Contains(blocked.GetProperty("problems").EnumerateArray(), p => p.GetProperty("code").GetString() == "gates-missing");
        Assert.Equal(0, exitCode);
        Assert.Equal("ready_to_apply", result.GetProperty("outcome").GetString());
        Assert.True(result.GetProperty("acceptance").GetProperty("accepted").GetBoolean());
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    private static async Task<(int ExitCode, JsonElement Result)> YavRunAsync(ShellHarness shell)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var screen = new Screen(new StreamTerminal(output), new ScreenOptions(Rich: false, Color: false, Unicode: false));
        var options = new CliOptions(CliMode.Run, shell.Project.Path, Task, null, Json: true, Plain: true);

        var exitCode = await RunCommand.ExecuteAsync(options, shell.Services, screen, error, CancellationToken.None);

        var last = output.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries)[^1];
        return (exitCode, JsonDocument.Parse(last).RootElement.Clone());
    }
}
