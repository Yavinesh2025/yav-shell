using Yav.Coordinator;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>
/// Paths of more than 260 characters, which is where programs on Windows used to end. The isolated
/// workspace makes every path of a project longer than it is in the project.
/// </summary>
public class LongPathTests
{
    private const string Task = "Make the file say fixed.";

    /// <summary>Longer than 260 characters before the directory of the project is counted.</summary>
    private static readonly string Deep = string.Join('/', Enumerable.Repeat("a directory with a long name", 9)) + "/the file ünï.txt";

    /// <summary>
    /// A repository as a user has it: nothing in it says that paths may be long. The repositories of the
    /// tests say so, because the tests themselves could not commit such a file otherwise.
    /// </summary>
    private static async Task<CoordinatorHarness> WithADeepFileAsync(string content)
    {
        var harness = new CoordinatorHarness((Deep, content), ("README.md", "# App\n"));
        var unset = await harness.Runner.RunAsync(
            new Yav.Core.Ports.ProcessSpec("git", ["config", "--unset", "core.longpaths"], harness.ProjectPath),
            new Yav.Core.Ports.CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
            CancellationToken.None);
        Assert.True(unset.ExitCode == 0, unset.StandardError);
        return harness;
    }

    [Fact]
    public async Task A_file_with_a_path_of_more_than_260_characters_is_worked_on_checked_applied_and_put_back()
    {
        Assert.True(Deep.Length > 260, $"{Deep.Length} characters");
        await using var harness = await WithADeepFileAsync("one\n");
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", Deep, "bug"));
        harness.Agents.ImplementerTurn(Step.Write(Deep, "fixed\n"), Step.Message("Done.")).ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(Deep, Assert.Single(outcome.Candidate!.Changes.Files).Path);
        Assert.Equal(GateStatus.Passed, harness.Database.GetGateResults(outcome.RunId).Single().Status);
        Assert.Equal("one\n", harness.ReadProject(Deep));

        var applied = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.True(applied.Succeeded, applied.Message);
        Assert.Equal("fixed\n", harness.ReadProject(Deep));

        var undone = await harness.Coordinator.UndoAsync(harness.ProjectPath, false, harness.Observer, CancellationToken.None);

        Assert.True(undone.Succeeded, undone.Message);
        Assert.Equal("one\n", harness.ReadProject(Deep));
    }

    [Fact]
    public async Task A_text_in_a_file_with_such_a_path_is_replaced_without_a_model()
    {
        await using var harness = await WithADeepFileAsync("timeout = 30\n");
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", Deep, "bug"));
        harness.Agents.ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.Coordinator.RunAsync(
            new RunRequest(harness.ProjectPath, "Replace the timeout.") { MechanicalEdit = new(Deep, "timeout = 30", "timeout = 60", 1, false) },
            harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);
        Assert.Equal("timeout = 60\n", harness.ReadProject(Deep));
    }
}
