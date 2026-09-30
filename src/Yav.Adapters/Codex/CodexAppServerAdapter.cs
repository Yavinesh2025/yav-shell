using System.Text.Json;
using Yav.Adapters.Protocol;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;

namespace Yav.Adapters;

/// <summary>
/// Drives Codex through its local app server over standard input and output. One app-server process
/// serves every session of this adapter. OpenAI documents the app server as experimental, and YAV
/// reports it as such; it is never presented as production-supported.
/// </summary>
public sealed class CodexAppServerAdapter : IAgentAdapter, ISandboxReporting
{
    public const string AdapterId = "codex-app-server";
    public const string TestedVersions = "0.158.x";
    public const string ClientName = "yav_shell";

    /// <summary>How Codex names the user of the client as the one who decides requests for access.</summary>
    internal const string UserReviewer = "user";

    private const string MaturityNote =
        "OpenAI documents the Codex app server as experimental and not supported for production workloads; its protocol may change without notice";

    private readonly IProcessRunner _runner;
    private readonly TimeProvider _clock;
    private readonly AdapterOptions _options;
    private readonly SemaphoreSlim _connectGate = new(1, 1);
    private readonly CodexRateLimits _rateLimits = new();

    // The process that serves new requests. A process that ended, or was given up, keeps serving only the
    // conversations it had until it has told them so.
    private CodexServer? _server;
    private string? _serverVersion;
    private bool _disposed;

    public CodexAppServerAdapter(IProcessRunner runner, TimeProvider clock, AdapterOptions options)
    {
        _runner = runner;
        _clock = clock;
        _options = options;
    }

    public string Id => AdapterId;

    public string Provider => "openai";

    public string DisplayName => "Codex (app server)";

    public AdapterCapabilities Capabilities { get; } = new(
        AdapterFeatures.Streaming | AdapterFeatures.InteractiveApprovals | AdapterFeatures.Interrupt | AdapterFeatures.Steering
        | AdapterFeatures.ResumeSession | AdapterFeatures.StructuredOutput | AdapterFeatures.ModelListing | AdapterFeatures.EffortReadback
        | AdapterFeatures.UsageReporting | AdapterFeatures.RateLimitReporting | AdapterFeatures.ReadOnlyEnforcement
        | AdapterFeatures.ProviderSpeed | AdapterFeatures.MultiTurnProcess,
        [
            "The app server is experimental according to OpenAI; this adapter is tested with Codex " + TestedVersions + " only.",
            "Read-only and workspace-write boundaries are enforced by Codex's own sandbox. On Windows that needs the Windows sandbox to be set up.",
            "Token usage is reported; charges are not. YAV shows cost as Unavailable for this adapter.",
        ]);

    internal TimeProvider Clock => _clock;

    /// <summary>What is known about the rate limits of the account, whichever process said it.</summary>
    internal CodexRateLimits RateLimits => _rateLimits;

    private string? ResolveExecutable() =>
        string.IsNullOrEmpty(_options.ExecutablePath) ? _runner.Resolve("codex") : File.Exists(_options.ExecutablePath) ? _options.ExecutablePath : null;

    public async Task<AdapterDetection> DetectAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable();
        if (executable is null)
        {
            return new AdapterDetection(
                AdapterId, Provider, false, null, null, AdapterMaturity.Experimental, MaturityNote, false, TestedVersions,
                ["The Codex CLI was not found. Install it from https://developers.openai.com/codex and sign in with 'codex login'."]);
        }

        var problems = new List<string>();
        string? version = null;
        try
        {
            var result = await _runner.RunAsync(
                new ProcessSpec(executable, ["--version"], Path.GetDirectoryName(executable)!, _options.Environment, Label: "codex --version"),
                new CaptureOptions(Timeout: TimeSpan.FromSeconds(20)),
                cancellationToken).ConfigureAwait(false);
            version = ParseVersion(result.StandardOutput);
            if (result.ExitCode != 0 || version is null)
            {
                problems.Add("The Codex CLI did not report its version: " + (result.StandardError + result.StandardOutput).Trim());
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            problems.Add("The Codex CLI could not be started: " + ex.Message);
        }

        return new AdapterDetection(
            AdapterId, Provider, true, executable, version, AdapterMaturity.Experimental, MaturityNote,
            VersionTested: version is not null && version.StartsWith("0.158.", StringComparison.Ordinal),
            TestedVersions, problems);
    }

    internal static string? ParseVersion(string output)
    {
        foreach (var token in output.Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length > 0 && char.IsAsciiDigit(token[0]) && token.Contains('.'))
            {
                return token;
            }
        }

        return null;
    }

    public async Task<AuthStatus> GetAuthStatusAsync(CancellationToken cancellationToken)
    {
        var server = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var result = await server.Connection.RequestAsync(
            "account/read",
            writer => writer.WriteBoolean("refreshToken", false),
            cancellationToken,
            onAnswer: answer => server.NoteAccount(AccountFromRead(answer))).ConfigureAwait(false);

        return AuthFrom(result, _clock.GetUtcNow());
    }

    /// <summary>
    /// The account a conversation works with, as the answer to account/read says it: the kind of route, as it is
    /// shown before a run, and a description that names neither the person nor an organization.
    /// </summary>
    internal static AccountSaid AccountFromRead(JsonElement result)
    {
        var status = AuthFrom(result, DateTimeOffset.MinValue);
        return new AccountSaid(status.Route, status.RouteLabel, AccountReadSource);
    }

    /// <summary>
    /// The account Codex says it works with after it changed. The notification gives the kind of sign-in and,
    /// for ChatGPT, the plan; a sign-out gives none. A kind YAV does not know is not taken for a known route.
    /// </summary>
    internal static AccountSaid AccountFromUpdate(JsonElement parameters)
    {
        const string Source = "account/updated";
        var plan = parameters.Text("planType") ?? "unknown";
        return parameters.Text("authMode") switch
        {
            // Both are a sign-in with ChatGPT; with the second one the host application supplies the tokens.
            "chatgpt" or "chatgptAuthTokens" => new AccountSaid(AccountRouteKind.Subscription, $"ChatGPT plan ({plan})", Source),
            "apikey" => new AccountSaid(AccountRouteKind.ApiKey, "OpenAI API key", Source),
            "bedrockApiKey" or "bedrockAccessKeys" => new AccountSaid(AccountRouteKind.CloudProvider, "Amazon Bedrock", Source),
            null => new AccountSaid(AccountRouteKind.NotAuthenticated, "Not signed in", Source),
            var other => new AccountSaid(
                AccountRouteKind.Unknown, $"a sign-in of the kind '{other}', which this version of YAV does not know", Source),
        };
    }

    private const string AccountReadSource = "account/read";

    private static AuthStatus AuthFrom(JsonElement result, DateTimeOffset now)
    {
        const string Source = AccountReadSource;
        if (result.Child("account") is not { } account)
        {
            return new AuthStatus(
                false, AccountRouteKind.NotAuthenticated, "Not signed in", null, null, BillingKind.Unknown, RoutePolicy.Allowed,
                "Sign in with 'codex login' or /login codex.", Source, now);
        }

        switch (account.Text("type"))
        {
            case "chatgpt":
            {
                var plan = account.Text("planType") ?? "unknown";
                return new AuthStatus(
                    true, AccountRouteKind.Subscription, $"ChatGPT plan ({plan})", plan, "openai", BillingKind.IncludedInSubscription,
                    RoutePolicy.RequiresAcknowledgement,
                    "Usage counts against the limits of your ChatGPT plan. OpenAI documents ChatGPT sign-in for app-server clients and "
                    + "publishes no statement about third-party clients, so YAV asks you to acknowledge this route once.",
                    Source, now);
            }

            case "apiKey":
                return new AuthStatus(
                    true, AccountRouteKind.ApiKey, "OpenAI API key", null, "openai", BillingKind.PayPerToken,
                    RoutePolicy.RequiresAcknowledgement,
                    "Usage is billed per token to the API account, separately from any ChatGPT plan.",
                    Source, now);

            case "amazonBedrock":
                return new AuthStatus(
                    true, AccountRouteKind.CloudProvider, "Amazon Bedrock", null, "amazonBedrock", BillingKind.CloudProviderBilled,
                    RoutePolicy.RequiresAcknowledgement,
                    "Usage is billed by Amazon Web Services under your agreement with them.",
                    Source, now);

            default:
                return new AuthStatus(
                    true, AccountRouteKind.Unknown, "Unknown account type", null, account.Text("type"), BillingKind.Unknown,
                    RoutePolicy.RequiresAcknowledgement,
                    "This version of YAV does not know the account type the agent reported.",
                    Source, now);
        }
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var connection = (await ConnectAsync(cancellationToken).ConfigureAwait(false)).Connection;
        var models = new List<ModelInfo>();
        string? cursor = null;
        for (var page = 0; page < 20; page++)
        {
            var current = cursor;
            var result = await connection.RequestAsync(
                "model/list",
                writer =>
                {
                    writer.WriteBoolean("includeHidden", false);
                    writer.WriteNumber("limit", 100);
                    if (current is not null)
                    {
                        writer.WriteString("cursor", current);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            foreach (var item in result.Items("data"))
            {
                var id = item.Text("id") ?? item.Text("model");
                if (string.IsNullOrEmpty(id))
                {
                    continue;
                }

                var efforts = new List<string>();
                var descriptions = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var option in item.Items("supportedReasoningEfforts"))
                {
                    var effort = option.Text("reasoningEffort");
                    if (string.IsNullOrEmpty(effort))
                    {
                        continue;
                    }

                    efforts.Add(effort);
                    if (option.Text("description") is { Length: > 0 } description)
                    {
                        descriptions[effort] = description;
                    }
                }

                var tiers = item.Items("serviceTiers")
                    .Select(t => new ServiceTierInfo(
                        t.Text("id") ?? string.Empty,
                        t.Text("name") ?? t.Text("id") ?? string.Empty,
                        t.Text("description"),
                        Faster: IsFasterTier(t.Text("id"))))
                    .Where(t => t.Id.Length > 0)
                    .ToList();

                models.Add(new ModelInfo(
                    id,
                    item.Text("displayName") ?? id,
                    item.Text("description"),
                    efforts,
                    item.Text("defaultReasoningEffort"),
                    item.Flag("isDefault") ?? false,
                    item.Flag("hidden") ?? false,
                    tiers,
                    item.Text("model"),
                    "model/list")
                {
                    EffortDescriptions = descriptions,
                });
            }

            cursor = result.Text("nextCursor");
            if (string.IsNullOrEmpty(cursor))
            {
                break;
            }
        }

        return models;
    }

    public async Task<RateLimitSnapshot?> GetRateLimitsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = (await ConnectAsync(cancellationToken).ConfigureAwait(false)).Connection;

            // The reading is also what later updates are merged into. It is taken by the reader, before it reads
            // on: an update that follows the answer is newer, and must not be overwritten by the reading.
            RateLimitSnapshot? own = null;
            await connection.RequestAsync(
                "account/rateLimits/read",
                null,
                cancellationToken,
                onAnswer: result => own = _rateLimits.Replace(result, "account/rateLimits/read", _clock.GetUtcNow())).ConfigureAwait(false);
            return own;
        }
        catch (AgentProtocolException)
        {
            // Unavailable is reported as unavailable, never as zero usage.
            return null;
        }
    }

    internal static RateLimitSnapshot ParseRateLimits(JsonElement limits, string source, DateTimeOffset now)
    {
        static RateLimitWindow? Window(JsonElement? element)
        {
            if (element is not { } window)
            {
                return null;
            }

            var resets = window.Number("resetsAt");
            return new RateLimitWindow(
                (int?)window.Number("usedPercent"),
                (int?)window.Number("windowDurationMins"),
                resets is null ? null : DateTimeOffset.FromUnixTimeSeconds(resets.Value));
        }

        var credits = limits.Child("credits");
        return new RateLimitSnapshot(
            AdapterId,
            limits.Text("limitName") ?? limits.Text("limitId"),
            limits.Text("planType"),
            Window(limits.Child("primary")),
            Window(limits.Child("secondary")),
            credits?.Flag("hasCredits"),
            credits?.Flag("unlimited"),
            credits?.Text("balance"),
            limits.Child("rateLimitReachedType") is not null ? true : null,
            source,
            now);
    }

    public async Task<SandboxStatus> GetSandboxStatusAsync(CancellationToken cancellationToken)
    {
        try
        {
            var connection = (await ConnectAsync(cancellationToken).ConfigureAwait(false)).Connection;
            var result = await connection.RequestAsync("windowsSandbox/readiness", null, cancellationToken).ConfigureAwait(false);
            var state = result.Text("status") ?? "unknown";
            return state switch
            {
                "ready" => new SandboxStatus(true, state, "The Codex Windows sandbox is set up."),
                "notConfigured" => new SandboxStatus(
                    false, state,
                    "The Codex Windows sandbox is not set up. Codex then gives a read-only policy even when a writable workspace is requested, "
                    + "and read-only is not enforced by the operating system. Set it up by running 'codex' once and following its sandbox setup."),
                "updateRequired" => new SandboxStatus(false, state, "The Codex Windows sandbox needs to be set up again. Run 'codex' and follow its sandbox setup."),
                _ => new SandboxStatus(false, state, "Codex reported a sandbox state this version of YAV does not know."),
            };
        }
        catch (AgentProtocolException ex)
        {
            return new SandboxStatus(false, "unavailable", "The sandbox state could not be read: " + ex.Message);
        }
    }

    public Task<IAgentSession> StartSessionAsync(SessionRequest request, CancellationToken cancellationToken) =>
        OpenAsync("thread/start", null, request, cancellationToken);

    public Task<IAgentSession> ResumeSessionAsync(string sessionId, SessionRequest request, CancellationToken cancellationToken) =>
        OpenAsync("thread/resume", sessionId, request, cancellationToken);

    private async Task<IAgentSession> OpenAsync(string method, string? threadId, SessionRequest request, CancellationToken cancellationToken)
    {
        var server = await ConnectAsync(cancellationToken).ConfigureAwait(false);
        var (instructions, problem) = await ComposeInstructionsAsync(server, request, cancellationToken).ConfigureAwait(false);

        // The thread the answer names; what arrives for it is kept until its session is registered.
        string? opened = null;
        try
        {
            return await OpenAsync(server, method, threadId, request, instructions, problem, answer => opened = answer, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            if (opened is not null)
            {
                // Registered, or not used: either way nothing is kept for it any more.
                await server.NotOpenedAsync(opened).ConfigureAwait(false);
            }
        }
    }

    /// <param name="problem">What the conversation is told once it is open, about how it was opened. Null for nothing.</param>
    private async Task<IAgentSession> OpenAsync(
        CodexServer server,
        string method,
        string? threadId,
        SessionRequest request,
        string instructions,
        string? problem,
        Action<string> opening,
        CancellationToken cancellationToken)
    {
        var result = await server.Connection.RequestAsync(
            method,
            writer =>
            {
                if (threadId is not null)
                {
                    writer.WriteString("threadId", threadId);

                    // The history is not needed, and a long one in a single answer could exceed what one message may
                    // be, which would close the connection that every conversation of the process shares.
                    writer.WriteBoolean("excludeTurns", true);
                }
                else
                {
                    // Only a new thread takes the name of the client that started it.
                    writer.WriteString("serviceName", ClientName);
                }

                writer.WriteString("model", request.ModelId);
                writer.WriteString("cwd", request.WorkingDirectory);
                writer.WriteString("sandbox", request.Sandbox == SandboxLevel.ReadOnly ? "read-only" : "workspace-write");

                // "never" makes Codex refuse what would need approval instead of asking; permissions are never widened.
                writer.WriteString("approvalPolicy", request.Approvals == ApprovalMode.NeverAsk ? "never" : "on-request");

                // Requests for access go to the user, whatever Codex's own configuration names as their reviewer.
                writer.WriteString("approvalsReviewer", UserReviewer);

                // Added as developer instructions, which leaves Codex's own and the project's instructions in place.
                writer.WriteString("developerInstructions", instructions);
                if (request.ServiceTier is not null)
                {
                    writer.WriteString("serviceTier", request.ServiceTier);
                }

                if (request.Effort.Length > 0)
                {
                    writer.WritePropertyName("config");
                    writer.WriteStartObject();
                    writer.WriteString("model_reasoning_effort", request.Effort);
                    writer.WriteEndObject();
                }
            },
            cancellationToken,
            onAnswer: answer =>
            {
                // Codex can say something about the thread right after this answer, in the same breath; the reader
                // keeps it for the session from here on, before it reads any further.
                if ((answer.Child("thread")?.Text("id") ?? threadId) is { } named)
                {
                    opening(named);
                    server.Opening(named);
                }
            }).ConfigureAwait(false);

        var thread = result.Child("thread");
        var id = thread?.Text("id") ?? threadId
            ?? throw new AgentProtocolException($"The agent answered '{method}' without a thread identifier.");

        var effective = ParseEffective(result, method, _serverVersion);
        if ((RefusalFor(effective.ApprovalsReviewer) ?? SandboxRefusal(result.Child("sandbox"))) is { } refusal)
        {
            throw new AgentProtocolException(refusal, refused: true);
        }

        // The route shown before the run was read before, perhaps by another process; this is what this one says.
        var (account, seen, accountProblem) = await ReadAccountAsync(server, cancellationToken).ConfigureAwait(false);
        effective = effective with { Account = account };

        var session = new CodexAppServerSession(this, server, request, id, effective, seen);
        await session.PublishAsync(new SessionConfigured(_clock.GetUtcNow(), id, effective)).ConfigureAwait(false);
        foreach (var said in new[] { problem, accountProblem })
        {
            if (said is not null)
            {
                await session.PublishAsync(new AgentNotice(_clock.GetUtcNow(), said, IsWarning: true)).ConfigureAwait(false);
            }
        }

        await server.RegisterAsync(session).ConfigureAwait(false);
        return session;
    }

    /// <summary>
    /// Asks the process which account it works with now. The reader notes the answer in the order Codex said it,
    /// so that it does not overwrite a change Codex reported afterwards. Null, with the reason, when Codex does not say.
    /// </summary>
    /// <returns>The account, the number under which the process noted it, and what the user is told when it is not known.</returns>
    private static async Task<(AccountSaid? Account, long Seen, string? Problem)> ReadAccountAsync(CodexServer server, CancellationToken cancellationToken)
    {
        AccountSaid? said = null;
        long seen = 0;
        try
        {
            await server.Connection.RequestAsync(
                "account/read",
                writer => writer.WriteBoolean("refreshToken", false),
                cancellationToken,
                onAnswer: answer =>
                {
                    said = AccountFromRead(answer);
                    seen = server.NoteAccount(said);
                }).ConfigureAwait(false);
            return (said, seen, null);
        }
        catch (AgentProtocolException ex)
        {
            return (null, server.AccountVersion, $"Codex did not say which account this conversation works with ({ex.Message}). "
                + "That it is the route that was shown before the run is not confirmed.");
        }
    }

    /// <summary>
    /// True for the tier ids OpenAI documents as faster and metered at a higher rate: "priority" in the
    /// current catalog (shown as "Fast") and "fast" in earlier ones. Any other tier is never treated as the
    /// paid speed option.
    /// </summary>
    internal static bool IsFasterTier(string? tierId) => tierId is "priority" or "fast";

    /// <summary>Reads what the provider reports as in effect from the answer to thread/start or thread/resume.</summary>
    internal static EffectiveSettings ParseEffective(JsonElement result, string method, string? serverVersion)
    {
        var thread = result.Child("thread");
        var sandbox = result.Child("sandbox");
        return new EffectiveSettings(
            Model: result.Text("model"),
            Effort: result.Text("reasoningEffort") ?? thread?.Text("reasoningEffort"),
            Sandbox: NormalizeSandbox(sandbox),
            ApprovalPolicy: ApprovalPolicyName(result.Child("approvalPolicy")),
            ServiceTier: result.Text("serviceTier"),
            WorkingDirectory: result.Text("cwd"),
            CredentialSource: null,

            // The thread names the version that created it, which for a resumed thread can be an earlier one.
            AgentVersion: serverVersion ?? thread?.Text("cliVersion"),
            InstructionSources: result.Items("instructionSources").Select(s => s.GetString() ?? string.Empty).Where(s => s.Length > 0).ToList(),
            Tools: [],
            Source: method + " response",
            ApprovalsReviewer: result.Text("approvalsReviewer"),
            Widening: ParseWidening(sandbox));
    }

    /// <summary>
    /// Reads the settings Codex reports as in effect after they changed in an open conversation. The
    /// notification carries only settings; what it does not carry, such as the instruction files, is kept.
    /// </summary>
    internal static EffectiveSettings ParseSettingsUpdate(JsonElement settings, string method, EffectiveSettings? before)
    {
        var sandbox = settings.Child("sandboxPolicy");
        return new EffectiveSettings(
            Model: settings.Text("model"),
            Effort: settings.Text("effort"),
            Sandbox: NormalizeSandbox(sandbox),
            ApprovalPolicy: ApprovalPolicyName(settings.Child("approvalPolicy")),
            ServiceTier: settings.Text("serviceTier"),
            WorkingDirectory: settings.Text("cwd"),
            CredentialSource: before?.CredentialSource,
            AgentVersion: before?.AgentVersion,
            InstructionSources: before?.InstructionSources ?? [],
            Tools: before?.Tools ?? [],
            Source: method + " notification",
            ApprovalsReviewer: settings.Text("approvalsReviewer"),
            Widening: ParseWidening(sandbox),
            Account: before?.Account);
    }

    /// <summary>The policy is a word, or an object for the granular form.</summary>
    private static string? ApprovalPolicyName(JsonElement? policy) =>
        policy is { ValueKind: JsonValueKind.String } word ? word.GetString() : policy is null ? null : "granular";

    /// <summary>
    /// What widens the sandbox, read from the fields of the policy. Only the two policies YAV asks for carry
    /// them. For any other policy, and when a field is missing or is not what the protocol says, the result is
    /// null: the agent did not say, and what it did not say is never taken to be closed.
    /// </summary>
    internal static SandboxWidening? ParseWidening(JsonElement? policy) => policy is { } given ? ReadWidening(given, out _) : null;

    /// <summary>What widens a policy of the two kinds YAV asks for. Null, with the field that cannot be read, otherwise.</summary>
    private static SandboxWidening? ReadWidening(JsonElement policy, out string? unreadable)
    {
        unreadable = "networkAccess";
        if (policy.Flag("networkAccess") is not { } network)
        {
            return null;
        }

        switch (policy.Text("type"))
        {
            case "readOnly":
                // Nothing may be written, so no folder is added.
                unreadable = null;
                return new SandboxWidening(network, []);

            case "workspaceWrite":
            {
                // The folders Codex may write besides the working directory. The schema gives them a default, but
                // Codex writes them; a policy without them is not one whose reach is known.
                unreadable = "writableRoots";
                if (policy.Child("writableRoots") is not { ValueKind: JsonValueKind.Array } roots)
                {
                    return null;
                }

                var folders = new List<string>();
                foreach (var root in roots.EnumerateArray())
                {
                    if (root.ValueKind != JsonValueKind.String)
                    {
                        return null;
                    }

                    folders.Add(root.GetString()!);
                }

                unreadable = null;
                return new SandboxWidening(network, folders);
            }

            default:
                unreadable = "type";
                return null;
        }
    }

    /// <summary>
    /// Why a conversation is not used because of what Codex says about its sandbox: it names none, or it names one
    /// of the two kinds YAV asks for in a form that cannot be read, so that what the sandbox may reach is not
    /// known. Another kind is left to the verification of the settings, which does not accept it. Null otherwise.
    /// </summary>
    internal static string? SandboxRefusal(JsonElement? policy)
    {
        const string NotUsed = "YAV does not use a conversation whose limits it cannot see.";
        if (policy is not { ValueKind: JsonValueKind.Object } given || given.Text("type") is not { } type)
        {
            return "Codex does not say which sandbox this conversation has, so what the sandbox may reach is not known. " + NotUsed;
        }

        if (type is not ("readOnly" or "workspaceWrite") || ReadWidening(given, out var unreadable) is not null)
        {
            return null;
        }

        return $"Codex reports the sandbox '{type}' in a form this version of YAV cannot read (the field '{unreadable}'), "
            + "so what the sandbox may reach is not known. " + NotUsed;
    }

    /// <summary>
    /// Why a conversation is not used: Codex reports that something other than the user decides its requests
    /// for access, such as a sub-agent of Codex, and YAV would never see those requests; or it does not say who
    /// decides, and then nobody can say that it is the user. Null when Codex says that the user decides.
    /// </summary>
    internal static string? RefusalFor(string? approvalsReviewer) => approvalsReviewer switch
    {
        UserReviewer => null,
        null => "Codex does not say who decides the requests for access in this conversation, so nobody can say that it is you. "
            + "YAV lets only you decide about access, so the conversation is not used.",
        _ => $"Codex reports that '{approvalsReviewer}' decides the requests for access in this conversation, not you: they would be "
            + "approved or denied without you, and YAV would not see them. YAV lets only you decide about access, so the "
            + "conversation is not used. Check the setting approvals_reviewer of Codex.",
    };

    private static string? NormalizeSandbox(JsonElement? policy) => policy?.Text("type") switch
    {
        "readOnly" => "read-only",
        "workspaceWrite" => "workspace-write",
        "dangerFullAccess" => "danger-full-access",
        "externalSandbox" => "external-sandbox",
        null => null,
        var other => other,
    };

    /// <summary>
    /// The developer instructions of a conversation: the user's own, read from Codex's configuration as seen from
    /// the conversation's directory, in front of YAV's. When the user's own cannot be read, the conversation gets
    /// YAV's alone and is told why. A failure is not kept: the next conversation reads them again.
    /// </summary>
    private static async Task<(string Instructions, string? Problem)> ComposeInstructionsAsync(
        CodexServer server, SessionRequest request, CancellationToken cancellationToken)
    {
        var directory = request.WorkingDirectory;
        string? problem = null;
        if (!server.DeveloperInstructions.TryGetValue(directory, out var own))
        {
            try
            {
                // Read as seen from the conversation's directory, which includes the configuration of its project.
                var result = await server.Connection.RequestAsync(
                    "config/read",
                    writer =>
                    {
                        writer.WriteBoolean("includeLayers", false);
                        writer.WriteString("cwd", directory);
                    },
                    cancellationToken).ConfigureAwait(false);
                own = result.Child("config")?.Text("developer_instructions");
                server.DeveloperInstructions[directory] = own;
            }
            catch (AgentProtocolException ex)
            {
                own = null;
                problem = $"The developer instructions of your own Codex configuration could not be read ({ex.Message}), "
                    + "so they are not part of this conversation. Codex's other instructions and those of the project still apply.";
            }
        }

        // The parameter replaces the user's own developer instructions, so they are put in front of YAV's.
        var instructions = string.IsNullOrWhiteSpace(own)
            ? request.RoleInstructions
            : own.TrimEnd() + "\n\n" + request.RoleInstructions;
        return (instructions, problem);
    }

    /// <summary>
    /// What Codex says about a conversation, without sending a turn. Only what Codex answers as a request it
    /// cannot serve for the thread is "not found"; a failure, such as no answer or an agent that ended, is
    /// thrown as what it is, so that it is not taken for a conversation Codex no longer knows.
    /// </summary>
    public async Task<SessionProbe> ProbeSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var connection = (await ConnectAsync(cancellationToken).ConfigureAwait(false)).Connection;
        try
        {
            var result = await connection.RequestAsync(
                "thread/read",
                writer =>
                {
                    writer.WriteString("threadId", sessionId);
                    writer.WriteBoolean("includeTurns", true);
                },
                cancellationToken).ConfigureAwait(false);

            var thread = result.Child("thread") ?? throw new AgentProtocolException("Codex answered 'thread/read' without a thread.");

            if (thread.Child("status")?.Text("type") == "active")
            {
                return new SessionProbe(SessionProbeState.Active, "A turn is running.");
            }

            var last = thread.Items("turns").LastOrDefault();
            return last.ValueKind == JsonValueKind.Object
                ? last.Text("status") switch
                {
                    "completed" => new SessionProbe(SessionProbeState.LastTurnCompleted, null),
                    "interrupted" => new SessionProbe(SessionProbeState.LastTurnInterrupted, null),
                    "failed" => new SessionProbe(SessionProbeState.LastTurnFailed, last.Child("error")?.Text("message")),

                    // Only the status of the thread says whether something runs it; it did not say so above. A turn
                    // recorded as in progress then ended without finishing, as Codex itself reports it.
                    "inProgress" => new SessionProbe(
                        SessionProbeState.LastTurnInterrupted, "The last turn is recorded as in progress, but nothing runs the conversation."),
                    _ => new SessionProbe(SessionProbeState.Idle, null),
                }
                : new SessionProbe(SessionProbeState.Idle, "The session has no turns.");
        }
        catch (AgentProtocolException ex) when (ex.Code == InvalidRequest)
        {
            // Codex answers so for a thread it does not know, or an id it cannot read as one.
            return new SessionProbe(SessionProbeState.NotFound, ex.Message);
        }
    }

    /// <summary>The JSON-RPC error code Codex answers with for a request it cannot serve, such as one for an unknown thread.</summary>
    private const int InvalidRequest = -32600;

    public LoginFlow? GetLoginFlow()
    {
        var executable = ResolveExecutable();
        return executable is null
            ? null
            : new LoginFlow(
                executable,
                ["login"],
                "Codex's own sign-in",
                [
                    "The sign-in runs in Codex itself. YAV does not see or store the credentials.",
                    "Signing in with ChatGPT uses your plan's included usage. Signing in with an API key is billed per token.",
                ]);
    }

    private async Task<CodexServer> ConnectAsync(CancellationToken cancellationToken)
    {
        if (_server is { Connection.IsAlive: true } alive)
        {
            return alive;
        }

        await _connectGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_server is { Connection.IsAlive: true } existing)
            {
                return existing;
            }

            if (_server is not null)
            {
                await _server.Connection.DisposeAsync().ConfigureAwait(false);
                _server = null;
            }

            var executable = ResolveExecutable()
                ?? throw new AgentProtocolException("The Codex CLI was not found. Run /doctor for details.");

            var process = _runner.Start(new ProcessSpec(
                executable, ["app-server"], Path.GetDirectoryName(executable)!, _options.Environment, Label: "codex app-server"));

            // Everything that arrives on this connection is routed to what belongs to this process only.
            var server = new CodexServer(this);
            var connection = new JsonRpcConnection(process, _options.EffectiveRequestTimeout, new DiagnosticTail())
            {
                OnNotification = server.RouteNotificationAsync,
                OnServerRequest = server.RouteServerRequestAsync,
                OnProtocolProblem = server.ProblemAsync,
                OnClosed = server.ClosedAsync,
            };
            server.Connection = connection;

            try
            {
                JsonElement result;
                try
                {
                    result = await connection.RequestAsync(
                        "initialize",
                        writer =>
                        {
                            writer.WritePropertyName("clientInfo");
                            writer.WriteStartObject();
                            writer.WriteString("name", ClientName);
                            writer.WriteString("title", "YAV Shell");
                            writer.WriteString("version", _options.EffectiveClientVersion);
                            writer.WriteEndObject();
                        },
                        cancellationToken,
                        _options.EffectiveStartupTimeout).ConfigureAwait(false);
                }
                catch (AgentProtocolException ex) when (ex.Code is null)
                {
                    throw new AgentProtocolException(
                        $"The Codex app server did not answer within {_options.EffectiveStartupTimeout.TotalSeconds:0.#} seconds. {ex.Message}", inner: ex);
                }

                _serverVersion = ParseUserAgentVersion(result.Text("userAgent"));
                await connection.NotifyAsync("initialized", cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await connection.DisposeAsync().ConfigureAwait(false);
                throw;
            }

            // What was read of the configuration belongs to the process it was read from; a new one reads it anew.
            _server = server;
            return server;
        }
        finally
        {
            _connectGate.Release();
        }
    }

    /// <summary>
    /// Ends the agent process behind a connection, together with everything it started. The sessions that
    /// used it are told that it ended; the next session starts a new process.
    /// </summary>
    internal async Task AbandonAsync(JsonRpcConnection connection)
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (ReferenceEquals(_server?.Connection, connection))
            {
                _server = null;
            }
        }
        finally
        {
            _connectGate.Release();
        }

        await connection.DisposeAsync().ConfigureAwait(false);
    }

    private static string? ParseUserAgentVersion(string? userAgent)
    {
        if (userAgent is null)
        {
            return null;
        }

        var slash = userAgent.IndexOf('/');
        if (slash < 0)
        {
            return null;
        }

        var rest = userAgent[(slash + 1)..];
        var end = rest.IndexOf(' ');
        return end > 0 ? rest[..end] : rest;
    }

    public async ValueTask DisposeAsync()
    {
        await _connectGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_server is not null)
            {
                await _server.Connection.DisposeAsync().ConfigureAwait(false);
                _server = null;
            }
        }
        finally
        {
            _connectGate.Release();
        }
    }
}
