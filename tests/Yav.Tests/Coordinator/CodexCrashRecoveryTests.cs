using Yav.Coordinator;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>
/// What a run that stopped with a crash of YAV is advised to do, from what Codex says about its conversation. The
/// Codex process of YAV ends with YAV, so a turn it recorded as in progress is run by nobody afterwards.
/// </summary>
public class CodexCrashRecoveryTests
{
    private const string Task = "Make the app say fixed.";
    private const int DeadProcess = 999_999;

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task After_a_crash_a_turn_recorded_as_in_progress_that_nothing_runs_is_not_waited_for(bool asRecorded)
    {
        // Codex 0.158 reports such a turn as interrupted itself; a version that reports it as recorded says only
        // through the status of the thread that nothing runs it.
        await using var harness = Harness(c =>
        {
            c["threadStates"] = new System.Text.Json.Nodes.JsonObject { ["thr-crashed"] = "inProgress" };
            c["turnsAsRecorded"] = asRecorded;
        });
        await CrashedRunAsync(harness, written: "half\n");

        var report = await harness.Coordinator.ReconcileAsync(CancellationToken.None);

        var finding = Assert.Single(report.Runs);
        Assert.Equal(ReconciliationAdvice.ContinueImplementation, finding.Advice);
        Assert.Contains(finding.Observations, o => o.Contains("interrupted", StringComparison.Ordinal));
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task After_a_crash_a_turn_codex_still_runs_is_not_started_a_second_time()
    {
        await using var harness = Harness(c =>
        {
            c["threadStates"] = new System.Text.Json.Nodes.JsonObject { ["thr-crashed"] = "inProgress" };
            c["threadStatus"] = new System.Text.Json.Nodes.JsonObject { ["thr-crashed"] = "active" };
        });
        var runId = await CrashedRunAsync(harness, written: "half\n");
        var report = await harness.Coordinator.ReconcileAsync(CancellationToken.None);

        var outcome = await harness.Coordinator.ResumeAsync(runId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        Assert.Equal(ReconciliationAdvice.WaitForProvider, Assert.Single(report.Runs).Advice);
        Assert.Equal(RunOutcomeKind.NeedsReconciliation, outcome.Kind);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    /// <summary>
    /// Leaves behind what a crash of YAV in the middle of the implementation would: a run recorded as Implementing
    /// by a process that no longer exists, its conversation with Codex, and the workspace as it was.
    /// </summary>
    private static async Task<string> CrashedRunAsync(CoordinatorHarness harness, string written)
    {
        var preflight = await harness.Coordinator.PreflightAsync(new RunRequest(harness.ProjectPath, Task), harness.Configuration, CancellationToken.None);
        Assert.True(preflight.CanRun, string.Join("; ", preflight.Blocking.Select(p => p.Message)));
        var profile = preflight.Resolution.Profile!;

        var workspace = await harness.Workspaces.PrepareAsync(
            new WorkspaceRequest("t-crashed", harness.ProjectPath, WorkspaceMode.GitWorktree, preflight.Configuration.Effective, null, true),
            preflight.Inspection!, null, CancellationToken.None);
        WorkspaceHarness.WriteIn(workspace, "src/app.txt", written);

        var now = harness.Clock.GetUtcNow();
        var database = harness.Database;
        database.SaveTask(new TaskContext("t-crashed", harness.ProjectPath, now, workspace.WorkspaceId, "thr-crashed", CoordinatorHarness.CodexId, null, null));
        database.SaveRequirement("t-crashed", new Requirement(1, Task, now, []));
        var runId = Ids.NewRunId(harness.Clock);
        database.CreateRun(
            new RunRecord(
                runId, "t-crashed", 1, RunKind.CodingTask, harness.ProjectPath, Task, 1, profile.ComputeHash(), RunState.Preparing,
                RunDisposition.Pending, null, 0, null, now, now, DeadProcess, "0.1.0-test"),
            profile);
        database.Transition(runId, RunState.Preparing, RunState.Implementing, null);
        database.SaveSession(new SessionRecord("t-crashed", AgentRole.Implementer, CoordinatorHarness.CodexId, "thr-crashed", "model-a", null, null, now));
        foreach (var (setting, value) in new[] { (ProfileSettings.Model, "model-a"), (ProfileSettings.Effort, "xhigh"), (ProfileSettings.Sandbox, "workspace-write") })
        {
            database.SaveConfirmation(runId, new ProfileConfirmation(AgentRole.Implementer, setting, value, value, VerificationStatus.Verified, "thread/start response", now));
        }

        return runId;
    }

    private static CoordinatorHarness Harness(Action<System.Text.Json.Nodes.JsonObject> codex)
    {
        var harness = new CoordinatorHarness(pid => pid != DeadProcess);
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.Codex(codex);
        return harness;
    }
}
