using System.Collections.Concurrent;
using System.Text.Json;
using Yav.Adapters.Protocol;
using Yav.Core.Agents;

namespace Yav.Adapters;

/// <summary>
/// One app-server process of Codex and what YAV keeps about it: the conversations it serves, what arrived for a
/// conversation that is being opened, the warnings it gave for all conversations, what it said about its account,
/// and its sub-agents. What arrives on its connection is routed here and answered on this connection, never on
/// another one.
/// </summary>
internal sealed class CodexServer
{
    /// <summary>How many warnings that name no conversation are kept for the conversations opened later.</summary>
    internal const int KeptWarnings = 20;

    /// <summary>How many folders a warning about folders everybody may write to names before it only counts the rest.</summary>
    private const int NamedFolders = 5;

    private readonly CodexAppServerAdapter _adapter;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, CodexAppServerSession> _sessions = new(StringComparer.Ordinal);

    // What arrived for a thread whose opening was answered but whose session is not registered yet.
    private readonly Dictionary<string, List<Arrived>> _opening = new(StringComparer.Ordinal);

    // The last warnings that named no conversation, numbered, for the conversations opened later.
    private readonly Queue<(long Number, string Text)> _warnings = new();

    // The sub-agents the model started, by their own thread: the conversation each belongs to.
    private readonly Dictionary<string, string> _subAgentOwners = new(StringComparer.Ordinal);

    private long _warningCount;
    private (int? ExitCode, string Description)? _ended;

    // What the process said last about its account, and how often it said something about it.
    private AccountSaid? _account;
    private long _accountVersion;

    /// <param name="RawId">The id of a request, or null for a notification.</param>
    private sealed record Arrived(string Method, JsonElement Parameters, string? RawId);

    public CodexServer(CodexAppServerAdapter adapter) => _adapter = adapter;

    /// <summary>The connection to the process. Set once, right after it was made, before anything is sent on it.</summary>
    public JsonRpcConnection Connection { get; set; } = null!;

    /// <summary>
    /// The user's own developer instructions, by the directory the configuration was read for. A path spelled
    /// differently is read again: an answer is never used for another directory. Only what was read is kept.
    /// </summary>
    public ConcurrentDictionary<string, string?> DeveloperInstructions { get; } = new(StringComparer.Ordinal);

    private DateTimeOffset Now => _adapter.Clock.GetUtcNow();

    /// <summary>How often the process said something about its account until now.</summary>
    public long AccountVersion
    {
        get
        {
            lock (_gate)
            {
                return _accountVersion;
            }
        }
    }

    /// <summary>
    /// Notes what the process said about its account. Called by the reader, in the order the process said it.
    /// Returns the number under which it was noted.
    /// </summary>
    public long NoteAccount(AccountSaid account)
    {
        lock (_gate)
        {
            _account = account;
            return ++_accountVersion;
        }
    }

    /// <summary>
    /// What arrives for the thread from now on is kept until its session is registered. Called by the reader with
    /// the answer that names the thread, before it reads what follows the answer.
    /// </summary>
    public void Opening(string threadId)
    {
        lock (_gate)
        {
            if (!_sessions.ContainsKey(threadId))
            {
                _opening.TryAdd(threadId, []);
            }
        }
    }

    /// <summary>
    /// What was kept for a thread whose session is not registered after all is dropped. What Codex asked in it is
    /// refused, since nobody will answer it; left unanswered, it would be waited for.
    /// </summary>
    public async Task NotOpenedAsync(string threadId)
    {
        List<Arrived>? kept;
        lock (_gate)
        {
            _opening.Remove(threadId, out kept);
        }

        foreach (var request in kept?.Where(message => message.RawId is not null) ?? [])
        {
            try
            {
                await Connection.RespondErrorAsync(request.RawId!, -32601, $"YAV Shell does not use this conversation, so it does not handle '{request.Method}'.", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (AgentProtocolException)
            {
                // The process is gone, and with it what it asked; why the conversation was not opened is what counts.
            }
        }
    }

    /// <summary>
    /// Lets the session receive what arrives for its thread: first what arrived while it was being opened, in
    /// order, then the warnings kept for all conversations and what the process said about its account since the
    /// session learned it, then everything else as it arrives. A session whose process ended in the meantime is
    /// told so instead.
    /// </summary>
    public async Task RegisterAsync(CodexAppServerSession session)
    {
        var threadId = session.SessionId!;
        while (true)
        {
            List<Arrived>? arrived = null;
            (long Number, string Text)[] warnings = [];
            (int? ExitCode, string Description)? ended = null;
            (AccountSaid Said, long Version)? account = null;
            lock (_gate)
            {
                if (_opening.TryGetValue(threadId, out var kept) && kept.Count > 0)
                {
                    arrived = [.. kept];
                    kept.Clear();
                }
                else
                {
                    // Registered only when nothing waits any more, so that nothing arrives out of order.
                    _opening.Remove(threadId);
                    ended = _ended;
                    if (ended is null)
                    {
                        _sessions[threadId] = session;
                        warnings = [.. _warnings];
                        if (_account is { } said && _accountVersion > session.AccountVersion)
                        {
                            account = (said, _accountVersion);
                        }
                    }
                }
            }

            if (arrived is not null)
            {
                foreach (var message in arrived)
                {
                    await DeliverAsync(session, message).ConfigureAwait(false);
                }

                continue;
            }

            if (ended is { } end)
            {
                await session.ConnectionClosedAsync(end.ExitCode, end.Description).ConfigureAwait(false);
                return;
            }

            foreach (var (number, text) in warnings)
            {
                await session.WarnAsync(number, text).ConfigureAwait(false);
            }

            if (account is { } changed)
            {
                await session.AccountChangedAsync(changed.Said, changed.Version).ConfigureAwait(false);
            }

            return;
        }
    }

    /// <summary>The session no longer receives anything. Only this session: another one may use the same thread by now.</summary>
    public void Forget(CodexAppServerSession session)
    {
        lock (_gate)
        {
            if (session.SessionId is { } threadId && _sessions.TryGetValue(threadId, out var registered) && ReferenceEquals(registered, session))
            {
                _sessions.Remove(threadId);
            }
        }
    }

    public async ValueTask RouteNotificationAsync(string method, JsonElement parameters)
    {
        var threadId = parameters.ValueKind == JsonValueKind.Object ? parameters.Text("threadId") : null;
        if (threadId is not null)
        {
            CodexAppServerSession? session;
            CodexAppServerSession? owner;
            lock (_gate)
            {
                if (Keep(threadId, method, parameters, null))
                {
                    return;
                }

                _sessions.TryGetValue(threadId, out session);
                owner = session is null ? OwnerOf(threadId) : null;
            }

            if (session is not null)
            {
                await session.HandleNotificationAsync(method, parameters).ConfigureAwait(false);
            }
            else if (owner is not null && method is "item/autoApprovalReview/started" or "item/autoApprovalReview/completed"
                or "autoApprovalReview/strictReviewRequired" or "guardianWarning")
            {
                // What else a sub-agent does is not followed; that Codex's own reviewer decides for it is.
                await owner.SubAgentReviewedAsync(threadId, method, parameters).ConfigureAwait(false);
            }

            return;
        }

        switch (method)
        {
            case "account/rateLimits/updated" when parameters.Child("rateLimits") is { } limits:
            {
                // A sparse update: what it does not carry is still what was known before.
                var snapshot = _adapter.RateLimits.Merge(limits, method, Now);
                foreach (var session in Sessions())
                {
                    await session.PublishAsync(new RateLimitUpdated(Now, snapshot)).ConfigureAwait(false);
                }

                break;
            }

            // Warnings about Codex itself or its configuration name no conversation, so every conversation is told,
            // also one that is opened later: Codex gives them right after it starts, before there is any.
            case "warning":
                await WarnAllAsync(parameters.Text("message") ?? "Codex reported a warning without a text.").ConfigureAwait(false);
                break;

            case "configWarning":
                await WarnAllAsync(Notice("Codex configuration warning: ", parameters)).ConfigureAwait(false);
                break;

            case "deprecationNotice":
                await WarnAllAsync(Notice("Codex deprecation notice: ", parameters)).ConfigureAwait(false);
                break;

            case "windows/worldWritableWarning":
                await WarnAllAsync(WorldWritable(parameters)).ConfigureAwait(false);
                break;

            // The account changed, for every conversation of the process: each reports it, so that it is compared
            // again with the route that was shown before the run.
            case "account/updated":
            {
                var said = CodexAppServerAdapter.AccountFromUpdate(parameters);
                long version;
                List<CodexAppServerSession> sessions;
                lock (_gate)
                {
                    _account = said;
                    version = ++_accountVersion;
                    sessions = [.. _sessions.Values];
                }

                foreach (var session in sessions)
                {
                    await session.AccountChangedAsync(said, version).ConfigureAwait(false);
                }

                break;
            }

            // A thread with a parent is a sub-agent the model started. Its own events carry its own thread id,
            // which no conversation of YAV has, so the conversation it belongs to is told once that it exists.
            case "thread/started" when parameters.Child("thread") is { } thread
                && thread.Text("parentThreadId") is { } parentId
                && thread.Text("id") is { } childId:
                await SubAgentStartedAsync(childId, parentId, thread.Text("agentNickname")).ConfigureAwait(false);
                break;
        }
    }

    public async ValueTask RouteServerRequestAsync(string rawId, string method, JsonElement parameters)
    {
        var threadId = parameters.ValueKind == JsonValueKind.Object ? parameters.Text("threadId") : null;
        CodexAppServerSession? session = null;
        CodexAppServerSession? owner = null;
        if (threadId is not null)
        {
            lock (_gate)
            {
                if (Keep(threadId, method, parameters, rawId))
                {
                    return;
                }

                _sessions.TryGetValue(threadId, out session);
                owner = session is null ? OwnerOf(threadId) : null;
            }
        }

        if (session is not null)
        {
            await session.HandleServerRequestAsync(rawId, method, parameters).ConfigureAwait(false);
            return;
        }

        if (owner is not null)
        {
            // What a sub-agent asks cannot be shown to the user, so it is refused; the conversation says so.
            await owner.SubAgentAskedAsync(threadId!, method).ConfigureAwait(false);
        }

        // A request YAV cannot answer is refused on the connection it came from, so the agent does not wait for it.
        await Connection.RespondErrorAsync(rawId, -32601, $"YAV Shell does not handle '{method}'.", CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>A problem with what the process sent is told to the conversations it serves now.</summary>
    public async ValueTask ProblemAsync(string message)
    {
        foreach (var session in Sessions())
        {
            await session.PublishAsync(new AgentNotice(Now, message, IsWarning: true)).ConfigureAwait(false);
        }
    }

    /// <summary>The process ended: the conversations it served are told, and nothing is registered with it any more.</summary>
    public async ValueTask ClosedAsync(int? exitCode, string description)
    {
        List<CodexAppServerSession> sessions;
        lock (_gate)
        {
            _ended = (exitCode, description);
            sessions = [.. _sessions.Values];
            _sessions.Clear();
        }

        foreach (var session in sessions)
        {
            await session.ConnectionClosedAsync(exitCode, description).ConfigureAwait(false);
        }
    }

    /// <summary>The conversation a sub-agent's thread belongs to, while it is registered. Called under the lock.</summary>
    private CodexAppServerSession? OwnerOf(string threadId) =>
        _subAgentOwners.TryGetValue(threadId, out var ownerId) && _sessions.TryGetValue(ownerId, out var owner) ? owner : null;

    /// <summary>Keeps what arrived for a thread that is being opened. Called under the lock.</summary>
    private bool Keep(string threadId, string method, JsonElement parameters, string? rawId)
    {
        if (!_opening.TryGetValue(threadId, out var kept))
        {
            return false;
        }

        // The element is only valid while the reader handles it.
        kept.Add(new Arrived(method, parameters.Clone(), rawId));
        return true;
    }

    private static ValueTask DeliverAsync(CodexAppServerSession session, Arrived message) => message.RawId is { } rawId
        ? session.HandleServerRequestAsync(rawId, message.Method, message.Parameters)
        : session.HandleNotificationAsync(message.Method, message.Parameters);

    private List<CodexAppServerSession> Sessions()
    {
        lock (_gate)
        {
            return [.. _sessions.Values];
        }
    }

    private async ValueTask WarnAllAsync(string text)
    {
        long number;
        List<CodexAppServerSession> sessions;
        lock (_gate)
        {
            number = ++_warningCount;
            _warnings.Enqueue((number, text));
            while (_warnings.Count > KeptWarnings)
            {
                _warnings.Dequeue();
            }

            sessions = [.. _sessions.Values];
        }

        foreach (var session in sessions)
        {
            await session.WarnAsync(number, text).ConfigureAwait(false);
        }
    }

    private async ValueTask SubAgentStartedAsync(string childId, string parentId, string? nickname)
    {
        CodexAppServerSession? owner;
        lock (_gate)
        {
            // A sub-agent of a sub-agent belongs to the same conversation.
            var ownerId = _subAgentOwners.GetValueOrDefault(parentId, parentId);
            if (!_sessions.TryGetValue(ownerId, out owner))
            {
                return;
            }

            _subAgentOwners[childId] = ownerId;
        }

        await owner.SubAgentStartedAsync(childId, nickname).ConfigureAwait(false);
    }

    /// <summary>A notice of Codex with its summary, the file it is about and its details, as far as it gives them.</summary>
    private static string Notice(string prefix, JsonElement parameters)
    {
        var text = prefix + (parameters.Text("summary") ?? string.Empty);
        if (parameters.Text("path") is { Length: > 0 } path)
        {
            text += $" ({path})";
        }

        if (parameters.Text("details") is { Length: > 0 } details)
        {
            text += " " + details;
        }

        return text;
    }

    /// <summary>
    /// Codex says that its Windows sandbox cannot protect folders every user of the computer may write to. The
    /// first ones are named, the rest counted.
    /// </summary>
    private static string WorldWritable(JsonElement parameters)
    {
        var samples = parameters.Items("samplePaths").Select(p => p.ValueKind == JsonValueKind.String ? p.GetString() : null)
            .Where(p => !string.IsNullOrEmpty(p)).ToList();
        var unnamed = Math.Max(0, samples.Count - NamedFolders) + Math.Max(0, parameters.Number("extraCount") ?? 0);
        var text = "Codex reports folders that every user of this computer may write to, which its Windows sandbox cannot protect: "
            + (samples.Count == 0 ? "it named none" : string.Join("; ", samples.Take(NamedFolders)))
            + (unnamed > 0 ? $" and {unnamed} more" : string.Empty) + ".";
        return parameters.Flag("failedScan") == true
            ? text + " Codex could not check every folder, so there may be more."
            : text;
    }
}
