using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Core.Text;

namespace Yav.Coordinator;

/// <summary>
/// The one place a run's events leave the coordinator. The observer and the stored history receive the
/// same events, so what the user saw and what /history shows cannot disagree.
/// </summary>
internal sealed class RunPublisher
{
    private const int SummaryLimit = 240;
    private const int DetailLimit = 16_000;

    private readonly IRunObserver _observer;
    private readonly IRunStore _store;
    private readonly TimeProvider _clock;
    private readonly Lock _gate = new();
    private bool _persist;

    public RunPublisher(string runId, IRunObserver observer, IRunStore store, TimeProvider clock)
    {
        RunId = runId;
        _observer = observer;
        _store = store;
        _clock = clock;
    }

    public string RunId { get; }

    /// <summary>Called once the run exists in the store. Earlier events are only shown.</summary>
    public void EnablePersistence() => _persist = true;

    public void Note(string stage, string message, NoteLevel level = NoteLevel.Info) =>
        Publish(new StageNote(RunId, _clock.GetUtcNow(), stage, message, level));

    public void Publish(RunEvent runEvent)
    {
        // Events come from the event loop, from approval prompts and from the checks that run side by side.
        lock (_gate)
        {
            if (_persist && ToRecord(runEvent) is { } record)
            {
                try
                {
                    _store.AppendEvent(record);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // History is a record of the run, not a precondition for it.
                    _persist = false;
                    Deliver(new StageNote(RunId, _clock.GetUtcNow(), Stages.Local, "The run history could not be written: " + ex.Message, NoteLevel.Warning));
                }
            }

            Deliver(runEvent);
        }
    }

    /// <summary>
    /// A title an agent gave its request, as YAV names it in a line of its own: in quotes and written out as it
    /// is, so that what an agent wrote never reads as a line of YAV.
    /// </summary>
    internal static string Quoted(string title) => "\"" + TerminalSanitizer.VisibleSingleLine(title) + "\"";

    private void Deliver(RunEvent runEvent)
    {
        try
        {
            _observer.OnEvent(runEvent);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A display problem must never change what the run does.
            _ = ex;
        }
    }

    private RunEventRecord? ToRecord(RunEvent runEvent)
    {
        // Text of YAV that names what an agent asked for or did is recorded as it is. Cleaning would drop that part together with what hides in it.
        (string Type, string Stage, string Summary, string? Detail)? entry = runEvent switch
        {
            RunStarted e => ("run.started", Stages.Prepare, e.RequestText, $"{e.Profile.Policy.Label}; A={e.Profile.Implementer.ModelId}; B={e.Profile.Reviewer.ModelId}"),
            StateChanged e => ("state", StageFor(e.To), $"{RunStateMachine.Display(e.From)} -> {RunStateMachine.Display(e.To)}", Visible(e.Reason)),
            StageNote e => (e.Level == NoteLevel.Info ? "note" : "note." + e.Level.ToString().ToLowerInvariant(), e.Stage, TerminalSanitizer.Visible(e.Message), null),
            SettingConfirmed e => ("setting", Stages.For(e.Confirmation.Role), Describe(e.Confirmation), e.Confirmation.Source),
            CandidateFrozen e => ("candidate", Stages.Check, $"Candidate {e.Candidate.ShortFingerprint}: {e.Candidate.Changes.Summary}",
                string.Join("\n", e.Candidate.Changes.Files.Select(f => $"{f.Kind}: {f.Path}"))),
            ReviewCompleted e => ("review", Stages.ReviewB, Describe(e.Review),
                string.Join("\n", e.Review.Findings.Select(f => $"[{f.Severity}{(f.Optional ? ", optional" : string.Empty)}] {f.Location}: {f.Title}"))),
            GateStarted e => ("gate.started", Stages.Tests, $"{e.Gate.Title}: {e.Gate.DisplayCommand}", e.WorkingDirectory),
            GateCompleted e => ("gate.result", Stages.Tests, Describe(e.Result), e.Result.OutputPath),
            AcceptanceEvaluated e => ("acceptance", Stages.Check,
                e.Decision.Accepted ? $"Candidate {e.Candidate.ShortFingerprint} accepted" : $"Candidate {e.Candidate.ShortFingerprint} not accepted: {e.Decision.Issues.Count} issue(s)",
                string.Join("\n", e.Decision.Issues.Select(i => $"{i.Kind} ({i.Resolution}): {i.Message}"))),
            RepairStarted e => ("repair", Stages.Repair, $"Repair {e.Cycle} of {e.MaxCycles}: {e.Findings} finding(s), {e.FailedGates} failed check(s)", null),
            RunFinished e => ("run.finished", StageFor(e.State), $"{RunStateMachine.Display(e.State)} ({e.Disposition})", Visible(e.Reason)),
            AgentActivity e => FromAgent(e),
            _ => null,
        };

        if (entry is not { } value)
        {
            return null;
        }

        return new RunEventRecord(
            0,
            RunId,
            runEvent.At,
            value.Type,
            value.Stage,
            Shorten(TerminalSanitizer.CleanSingleLine(value.Summary), SummaryLimit),
            value.Detail is null ? null : Shorten(TerminalSanitizer.Clean(value.Detail), DetailLimit));
    }

    private static (string Type, string Stage, string Summary, string? Detail)? FromAgent(AgentActivity activity)
    {
        var stage = Stages.For(activity.Role);
        return activity.Event switch
        {
            SessionConfigured e => ("agent.configured", stage,
                $"model={e.Effective.Model ?? "?"} effort={e.Effective.Effort ?? "?"} sandbox={e.Effective.Sandbox ?? "?"}", $"session {e.SessionId}; {e.Effective.Source}"),
            AssistantMessage e => ("agent.message", stage, e.Text, e.Text),
            ReasoningSummary e => ("agent.reasoning", stage, e.Text, e.Text),
            // What an agent ran, changed or used is recorded as it is; what a command wrote is text and is cleaned.
            CommandCompleted e => ("agent.command", stage, $"{TerminalSanitizer.VisibleSingleLine(e.Command)} ({e.Status}{(e.ExitCode is null ? string.Empty : ", exit " + e.ExitCode)})", e.Output),
            FilesChanged e => ("agent.files", stage, string.Join(", ", e.Changes.Select(c => $"{c.Kind.ToString().ToLowerInvariant()} {TerminalSanitizer.VisibleSingleLine(c.Path)}")), e.Status),
            // A tool is recorded once, when it has ended; an agent that reports the beginning as well reports the same again then.
            ToolActivity e when e.Status is not ("inProgress" or "started") => ("agent.tool", stage, TerminalSanitizer.VisibleSingleLine(e.Given.Length == 0 ? e.Tool : $"{e.Tool}: {e.Given}"), e.Status),
            ApprovalRequested e => ("approval.requested", Stages.Approval, Quoted(e.Request.Title), Visible(e.Request.Command)),
            ApprovalWithdrawn e => ("approval.withdrawn", Stages.Approval, $"The agent withdrew request {e.ApprovalId}", null),
            ModelRerouted e => ("agent.rerouted", stage, $"The provider rerouted {e.FromModel} to {e.ToModel}", e.Reason),
            ProviderRetry e => ("agent.retry", stage, $"Provider retry {e.Attempt}{(e.MaxAttempts is null ? string.Empty : " of " + e.MaxAttempts)}", e.Reason),
            AgentNotice e => (e.IsWarning ? "agent.warning" : "agent.notice", stage, e.Message, null),
            AgentError e => ("agent.error", stage, e.Message, e.Code),
            TurnCompleted e => ("agent.turn", stage, $"Turn {e.Outcome}", e.ErrorMessage),
            // A conversation can end while its process goes on, for one that was refused. An exit code says that the process ended.
            SessionEnded e => ("agent.ended", stage, e.ExitCode is null ? "The agent's conversation ended" : $"The agent process ended with exit code {e.ExitCode}", e.Reason),
            _ => null,
        };
    }

    internal static string Describe(Yav.Core.Profiles.ProfileConfirmation confirmation)
    {
        var role = confirmation.Role == AgentRole.Implementer ? "Model A" : "Model B";
        var value = new Yav.Core.Confirmed<string>(confirmation.Requested, confirmation.Effective, confirmation.Status, confirmation.Source).Describe();
        return $"{role} {confirmation.Setting}: {value}";
    }

    internal static string Describe(ReviewResult review)
    {
        if (!review.OutputValid)
        {
            return "The reviewer's output was invalid, which is not a pass";
        }

        var blocking = review.BlockingFindings.Count();
        var optional = review.Suggestions.Count();
        var verdict = review.Verdict switch
        {
            ReviewVerdict.Pass => "Pass",
            ReviewVerdict.ChangesRequired => "Changes required",
            _ => "Unable to verify",
        };
        return $"{verdict}: {blocking} blocking finding(s), {optional} suggestion(s)";
    }

    internal static string Describe(GateResult result)
    {
        var status = result.Status switch
        {
            GateStatus.Passed => "passed",
            GateStatus.Failed => $"failed (exit {result.ExitCode})",
            GateStatus.TimedOut => "timed out",
            GateStatus.Unverified => "Unverified: " + result.Limitation,
            GateStatus.Cancelled => "cancelled",
            _ => "could not be run: " + result.Limitation,
        };
        var baseline = result.IsBaselineRun ? " on the unmodified baseline" : string.Empty;
        return $"{result.GateTitle} {status}{baseline}";
    }

    internal static string StageFor(RunState state) => state switch
    {
        RunState.Preparing => Stages.Prepare,
        RunState.Implementing => Stages.CodeA,
        RunState.AwaitingApproval => Stages.Approval,
        RunState.Checking => Stages.Check,
        RunState.Repairing => Stages.Repair,
        RunState.ReadyToApply => Stages.Ready,
        RunState.Completed => Stages.Done,
        RunState.Blocked => Stages.Blocked,
        RunState.Interrupted => Stages.Stopped,
        RunState.RateLimited => Stages.Limit,
        RunState.NeedsReconciliation => Stages.Blocked,
        _ => Stages.Failed,
    };

    private static string Shorten(string text, int limit) =>
        text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit - 1), "…");

    private static string? Visible(string? text) => text is null ? null : TerminalSanitizer.Visible(text);
}
