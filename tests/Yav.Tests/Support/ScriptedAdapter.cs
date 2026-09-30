using System.Collections.Concurrent;
using System.Threading.Channels;
using Yav.Core.Agents;

namespace Yav.Tests.Support;

/// <summary>
/// An adapter that lives in the test process and does what the test says. It stands for an agent that
/// misbehaves in ways the real adapters already prevent, so the coordinator's own safeguards can be seen working.
/// </summary>
public sealed class ScriptedAdapter : IAgentAdapter
{
    public const string AdapterId = "scripted";

    private readonly Func<ScriptedSession, TurnRequest, Task> _turn;

    public ScriptedAdapter(Func<ScriptedSession, TurnRequest, Task> turn)
    {
        _turn = turn;
    }

    public string Id => AdapterId;

    public string Provider => "test";

    public string DisplayName => "Scripted agent";

    public ConcurrentQueue<ScriptedSession> Sessions { get; } = new();

    /// <summary>The directory a conversation says it works in, given the one it was started in. Null: the same.</summary>
    public Func<SessionRequest, string?>? ReportsDirectory { get; init; }

    /// <summary>What the agent says about a conversation when it is asked after a crash.</summary>
    public SessionProbe Probe { get; init; } = new(SessionProbeState.Unsupported, null);

    public AdapterCapabilities Capabilities { get; } = new(
        AdapterFeatures.Streaming | AdapterFeatures.InteractiveApprovals | AdapterFeatures.Interrupt | AdapterFeatures.StructuredOutput
        | AdapterFeatures.ModelListing | AdapterFeatures.EffortReadback | AdapterFeatures.ReadOnlyEnforcement | AdapterFeatures.MultiTurnProcess,
        []);

    public Task<AdapterDetection> DetectAsync(CancellationToken cancellationToken) => Task.FromResult(new AdapterDetection(
        AdapterId, Provider, true, @"C:\tools\scripted.exe", "1.0.0", AdapterMaturity.Stable, null, true, "1.0.x", []));

    public Task<AuthStatus> GetAuthStatusAsync(CancellationToken cancellationToken) => Task.FromResult(new AuthStatus(
        true, AccountRouteKind.ApiKey, "API key", null, "test", BillingKind.PayPerToken, RoutePolicy.Allowed, null, "test", Builders.Now));

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<ModelInfo>>(
    [
        new ModelInfo("scripted-a", "Scripted A", null, ["low", "high"], "high", true, false, [], null, "test"),
        new ModelInfo("scripted-b", "Scripted B", null, ["low", "high"], "high", false, false, [], null, "test"),
    ]);

    public Task<RateLimitSnapshot?> GetRateLimitsAsync(CancellationToken cancellationToken) => Task.FromResult<RateLimitSnapshot?>(null);

    public Task<IAgentSession> StartSessionAsync(SessionRequest request, CancellationToken cancellationToken)
    {
        var session = new ScriptedSession(this, request, $"scripted-{Sessions.Count + 1}", _turn);
        Sessions.Enqueue(session);
        return Task.FromResult<IAgentSession>(session);
    }

    public Task<IAgentSession> ResumeSessionAsync(string sessionId, SessionRequest request, CancellationToken cancellationToken) =>
        StartSessionAsync(request, cancellationToken);

    public Task<SessionProbe> ProbeSessionAsync(string sessionId, CancellationToken cancellationToken) => Task.FromResult(Probe);

    public LoginFlow? GetLoginFlow() => null;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class ScriptedSession : IAgentSession
{
    private readonly Channel<AgentEvent> _events = Channel.CreateUnbounded<AgentEvent>();
    private readonly Func<ScriptedSession, TurnRequest, Task> _turn;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ApprovalDecision>> _approvals = new();
    private int _turns;

    internal ScriptedSession(ScriptedAdapter adapter, SessionRequest request, string id, Func<ScriptedSession, TurnRequest, Task> turn)
    {
        AdapterId = adapter.Id;
        Request = request;
        SessionId = id;
        _turn = turn;
        Effective = new EffectiveSettings(
            request.ModelId, request.Effort, request.Sandbox == SandboxLevel.ReadOnly ? "read-only" : "workspace-write", "on-request", null,
            adapter.ReportsDirectory is { } reports ? reports(request) : request.WorkingDirectory, null, "1.0.0", [], [], "scripted session");
        _events.Writer.TryWrite(new SessionConfigured(Builders.Now, id, Effective));
    }

    public SessionRequest Request { get; }

    public string AdapterId { get; }

    public AgentRole Role => Request.Role;

    public string? SessionId { get; }

    public EffectiveSettings? Effective { get; }

    public ChannelReader<AgentEvent> Events => _events.Reader;

    /// <summary>Every answer to an approval request that reached the agent.</summary>
    public ConcurrentQueue<(string ApprovalId, ApprovalDecision Decision)> Answers { get; } = new();

    public int Interrupts { get; private set; }

    public void Emit(AgentEvent agentEvent) => _events.Writer.TryWrite(agentEvent);

    public string File(string relative) => Path.Combine(Request.WorkingDirectory, relative.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Asks for approval and waits for the answer, as an agent would.</summary>
    public Task<ApprovalDecision> AskAsync(string approvalId, string command)
    {
        var waiter = new TaskCompletionSource<ApprovalDecision>(TaskCreationOptions.RunContinuationsAsynchronously);
        _approvals[approvalId] = waiter;
        Emit(new ApprovalRequested(Builders.Now, new ApprovalRequest(approvalId, ApprovalKind.CommandExecution, "Run a command", command, Request.WorkingDirectory, null, [], false)));
        return waiter.Task;
    }

    public void Complete(string? finalMessage = null, System.Text.Json.JsonElement? structured = null, TurnOutcome outcome = TurnOutcome.Completed) =>
        Emit(new TurnCompleted(Builders.Now, $"turn-{_turns}", outcome, finalMessage, structured, null, null, []));

    public Task StartTurnAsync(TurnRequest request, CancellationToken cancellationToken)
    {
        _turns++;
        Emit(new TurnStarted(Builders.Now, $"turn-{_turns}"));
        _ = Task.Run(() => _turn(this, request), CancellationToken.None);
        return Task.CompletedTask;
    }

    /// <summary>When set, an answer does not reach the agent: the request is not open any more, as when the agent took it back in the same moment.</summary>
    public string? RefusesAnswersWith { get; set; }

    /// <summary>Completed when somebody tried to answer, whether the answer arrived or not.</summary>
    public Task AnswerAttempted => _answerAttempted.Task;

    private readonly TaskCompletionSource _answerAttempted = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task RespondToApprovalAsync(string approvalId, ApprovalDecision decision, CancellationToken cancellationToken)
    {
        _answerAttempted.TrySetResult();
        if (RefusesAnswersWith is { } reason)
        {
            return Task.FromException(new AgentException(reason));
        }

        Answers.Enqueue((approvalId, decision));
        if (_approvals.TryRemove(approvalId, out var waiter))
        {
            waiter.TrySetResult(decision);
        }

        return Task.CompletedTask;
    }

    public Task InterruptAsync(CancellationToken cancellationToken)
    {
        Interrupts++;
        Complete(outcome: TurnOutcome.Interrupted);
        return Task.CompletedTask;
    }

    public Task<bool> SteerAsync(string text, CancellationToken cancellationToken) => Task.FromResult(false);

    public Task ShutdownAsync(TimeSpan grace, CancellationToken cancellationToken) => Task.CompletedTask;

    public ValueTask DisposeAsync()
    {
        _events.Writer.TryComplete();
        return ValueTask.CompletedTask;
    }
}
