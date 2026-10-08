using System.Text;
using System.Text.Json;
using Yav.Adapters.Protocol;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;

namespace Yav.Adapters;

/// <summary>
/// Drives the installed, unmodified Claude Code CLI in its documented non-interactive mode, using the
/// message and control formats published with Anthropic's Agent SDK. YAV never reads, stores or relays
/// Claude.ai credentials: the CLI authenticates itself with whatever the user set up in it.
/// </summary>
public sealed class ClaudeCliAdapter : IAgentAdapter
{
    public const string AdapterId = "claude-cli";
    public const string TestedVersions = "2.1.284, 2.1.285";
    public const string PolicyUrl = "https://code.claude.com/docs/en/legal-and-compliance";

    // "--permission-prompts" and "--restricted" are required; the later of the two arrived in 2.1.259.
    private static readonly Version MinimumVersion = new(2, 1, 259);

    // The releases this was tried with. What Claude Code says changes from one release to the next, so another
    // one is used and is not called tested: what it does not say any more is shown as not confirmed.
    private static readonly Version[] TestedReleases = [.. TestedVersions.Split(", ").Select(Version.Parse)];

    private readonly IProcessRunner _runner;
    private readonly TimeProvider _clock;
    private readonly AdapterOptions _options;
    private readonly Func<string?> _apiKey;

    /// <param name="apiKey">Returns the API key YAV holds in the operating system's credential store, or null.</param>
    public ClaudeCliAdapter(IProcessRunner runner, TimeProvider clock, AdapterOptions options, Func<string?> apiKey)
    {
        _runner = runner;
        _clock = clock;
        _options = options;
        _apiKey = apiKey;
    }

    public string Id => AdapterId;

    public string Provider => "anthropic";

    public string DisplayName => "Claude Code (CLI)";

    public AdapterCapabilities Capabilities { get; } = new(
        AdapterFeatures.Streaming | AdapterFeatures.InteractiveApprovals | AdapterFeatures.Interrupt | AdapterFeatures.ResumeSession
        | AdapterFeatures.StructuredOutput | AdapterFeatures.ModelListing | AdapterFeatures.EffortReadback | AdapterFeatures.UsageReporting
        | AdapterFeatures.ReadOnlyEnforcement | AdapterFeatures.ProviderSpeed | AdapterFeatures.MultiTurnProcess,
        [
            "The reviewer's read-only boundary is enforced by removing every tool that can write or run code, not by an operating-system sandbox.",
            "As implementer, commands the agent runs have your user's permissions once you approve them.",
            "An API key given to the agent is inherited by the commands it runs, so an implementer is refused in an untrusted project.",
            "A running turn cannot be steered.",
            "Reported cost is Claude Code's own estimate, not a billing statement.",
        ]);

    internal TimeProvider Clock => _clock;

    internal IProcessRunner Runner => _runner;

    internal AdapterOptions Options => _options;

    internal string? ApiKey()
    {
        var key = _apiKey();
        return string.IsNullOrWhiteSpace(key) ? null : key;
    }

    internal string? ResolveExecutable() =>
        string.IsNullOrEmpty(_options.ExecutablePath) ? _runner.Resolve("claude") : File.Exists(_options.ExecutablePath) ? _options.ExecutablePath : null;

    /// <summary>The environment for the agent: the configured additions, plus the key YAV holds when there is one.</summary>
    internal IReadOnlyDictionary<string, string?> BuildEnvironment()
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        if (_options.Environment is not null)
        {
            foreach (var (key, value) in _options.Environment)
            {
                environment[key] = value;
            }
        }

        if (ApiKey() is { } apiKey)
        {
            environment["ANTHROPIC_API_KEY"] = apiKey;
        }

        return environment;
    }

    public async Task<AdapterDetection> DetectAsync(CancellationToken cancellationToken)
    {
        const string Note = "Anthropic documents running the CLI with -p as the way to drive Claude Code from other languages";
        var executable = ResolveExecutable();
        if (executable is null)
        {
            return new AdapterDetection(
                AdapterId, Provider, false, null, null, AdapterMaturity.Stable, Note, false, TestedVersions,
                ["Claude Code was not found. Install it from https://code.claude.com and sign in, or give YAV an API key with /login claude."]);
        }

        var problems = new List<string>();
        string? version = null;
        var tested = false;
        try
        {
            var result = await _runner.RunAsync(
                new ProcessSpec(executable, ["--version"], Path.GetDirectoryName(executable)!, _options.Environment, Label: "claude --version"),
                new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
                cancellationToken).ConfigureAwait(false);
            version = CodexAppServerAdapter.ParseVersion(result.StandardOutput);
            if (result.ExitCode != 0 || version is null)
            {
                problems.Add("Claude Code did not report its version: " + (result.StandardError + result.StandardOutput).Trim());
            }
            else if (Version.TryParse(version, out var parsed))
            {
                if (parsed < MinimumVersion)
                {
                    problems.Add($"Claude Code {version} is too old. YAV needs {MinimumVersion} or later for the options that keep the reviewer read-only. Run 'claude update'.");
                }
                else
                {
                    tested = TestedReleases.Contains(parsed);
                }
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            problems.Add("Claude Code could not be started: " + ex.Message);
        }

        return new AdapterDetection(AdapterId, Provider, true, executable, version, AdapterMaturity.Stable, Note, tested, TestedVersions, problems);
    }

    public async Task<AuthStatus> GetAuthStatusAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        if (ApiKey() is not null)
        {
            // In non-interactive mode the CLI always uses a key that is present in its environment.
            return new AuthStatus(
                true, AccountRouteKind.ApiKey, "Anthropic API key (held by YAV in the Windows Credential Manager)", null, "firstParty",
                BillingKind.PayPerToken, RoutePolicy.RequiresAcknowledgement,
                "Usage is billed per token to the Claude Console organization the key belongs to, separately from any Claude subscription.",
                "credential store", now);
        }

        var executable = ResolveExecutable();
        if (executable is null)
        {
            return new AuthStatus(false, AccountRouteKind.NotAuthenticated, "Claude Code is not installed", null, null, BillingKind.Unknown, RoutePolicy.Allowed, null, "claude auth status", now);
        }

        var result = await _runner.RunAsync(
            new ProcessSpec(executable, ["auth", "status", "--json"], Path.GetDirectoryName(executable)!, _options.Environment, Label: "claude auth status"),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
            cancellationToken).ConfigureAwait(false);

        const string Source = "claude auth status";
        var status = JsonReading.ParseObject(result.StandardOutput);
        if (status is not { } json || json.Flag("loggedIn") != true)
        {
            return new AuthStatus(
                false, AccountRouteKind.NotAuthenticated, "Not signed in", null, null, BillingKind.Unknown, RoutePolicy.Allowed,
                "Sign in to Claude Code with 'claude auth login', or give YAV an API key with /login claude.", Source, now);
        }

        var provider = json.Text("apiProvider") ?? "firstParty";
        if (provider is not "firstParty")
        {
            return new AuthStatus(
                true, provider == "gateway" ? AccountRouteKind.Gateway : AccountRouteKind.CloudProvider, $"Cloud provider ({provider})", null, provider,
                BillingKind.CloudProviderBilled, RoutePolicy.RequiresAcknowledgement,
                "Usage is billed by the cloud provider under your agreement with them.", Source, now);
        }

        var method = json.Text("authMethod");
        var plan = json.Text("subscriptionType");

        // What it works with decides, not what the sign-in is called: Claude Code says "claude.ai" also for a
        // sign-in to the Claude Console, where it is given a key and what is used is billed per token.
        var keySource = json.Text("apiKeySource");
        var key = (keySource is { Length: > 0 } && keySource != "none") || method is "api_key" or "api_key_helper";
        if (key)
        {
            var from = keySource switch
            {
                "/login managed key" => "created by signing in to the Claude Console",
                "apiKeyHelper" => "from the apiKeyHelper of Claude Code",
                _ => "configured in Claude Code",
            };
            return new AuthStatus(
                true, AccountRouteKind.ApiKey, $"Anthropic API key ({from})", null, provider, BillingKind.PayPerToken,
                RoutePolicy.RequiresAcknowledgement,
                "Usage is billed per token to the Claude Console organization the key belongs to.", Source, now);
        }

        if (plan is null && method != "claude.ai")
        {
            // A token from the environment without a plan, or a way of signing in this version of YAV does not know.
            return new AuthStatus(
                true, AccountRouteKind.Unknown, $"Signed in ({method ?? "in a way Claude Code does not name"}); which account is billed is not reported", null, provider,
                BillingKind.Unknown, RoutePolicy.RequiresAcknowledgement,
                "Claude Code reports neither a plan nor a key. With a token from its environment (CLAUDE_CODE_OAUTH_TOKEN, ANTHROPIC_AUTH_TOKEN) it "
                + "works on the account the token belongs to. YAV cannot tell you which one that is or how it is billed.",
                Source, now);
        }

        if (method == "claude.ai" || plan is not null)
        {
            return new AuthStatus(
                true, AccountRouteKind.Subscription, $"Claude subscription ({plan ?? "plan unknown"})", plan, provider,
                BillingKind.IncludedInSubscription, RoutePolicy.RequiresAcknowledgement,
                "Claude Code is signed in with your own Claude subscription. Anthropic's terms say that subscription sign-in is for ordinary use of "
                + "Claude Code and other Anthropic applications, that developers of products should use an API key or a cloud provider, and that "
                + "third parties may not offer Claude.ai login or relay subscription credentials. They also say this does not prevent a user from "
                + "signing in to the unmodified Claude Code with their own subscription. YAV runs the unmodified CLI and never touches its "
                + "credentials, but it cannot decide whether your use is covered: read " + PolicyUrl + ". YAV uses this subscription without asking; use an API key if your use is not covered.",
                Source, now);
        }

        return new AuthStatus(
            true, AccountRouteKind.ApiKey, "Anthropic API key (configured in Claude Code)", null, provider, BillingKind.PayPerToken,
            RoutePolicy.RequiresAcknowledgement,
            "Usage is billed per token to the Claude Console organization the key belongs to.", Source, now);
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        var executable = ResolveExecutable() ?? throw new AgentProtocolException("Claude Code was not found. Run /doctor for details.");

        // The control request "initialize" returns the models of the account. No prompt is sent, so nothing is inferred.
        var process = _runner.Start(new ProcessSpec(
            executable,
            ["-p", "--output-format", "stream-json", "--input-format", "stream-json", "--verbose", "--permission-prompts", "none", "--no-session-persistence"],
            Path.GetTempPath(),
            BuildEnvironment(),
            Label: "claude (model list)"));
        await using (process.ConfigureAwait(false))
        {
            var diagnostics = new DiagnosticTail();
            var errors = DrainAsync(process.StandardError, diagnostics);
            var request = "{\"type\":\"control_request\",\"request_id\":\"yav-init\",\"request\":{\"subtype\":\"initialize\"}}\n";
            try
            {
                await process.StandardInput.WriteAsync(Encoding.UTF8.GetBytes(request), cancellationToken).ConfigureAwait(false);
                await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                throw new AgentProtocolException("Claude Code ended before it could be asked for its models. " + diagnostics.Read(), inner: ex);
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(_options.EffectiveStartupTimeout);
            var reader = new JsonLineReader(process.StandardOutput);
            var read = Task.Run(async () =>
            {
                while (await reader.ReadFrameAsync(CancellationToken.None).ConfigureAwait(false) is { } frame)
                {
                    try
                    {
                        using var document = JsonDocument.Parse(frame);
                        var root = document.RootElement;
                        if (root.Text("type") == "control_response" && root.Child("response") is { } response && response.Text("request_id") == "yav-init")
                        {
                            return response.Text("subtype") == "success" ? response.Child("response")?.Clone() : null;
                        }
                    }
                    catch (JsonException)
                    {
                    }
                }

                return null;
            });

            JsonElement? payload;
            try
            {
                payload = await read.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AgentProtocolException(
                    $"Claude Code did not answer within {_options.EffectiveStartupTimeout.TotalSeconds:0.#} seconds. " + diagnostics.Read());
            }
            finally
            {
                process.CloseInput();
            }

            if (payload is not { } result)
            {
                throw new AgentProtocolException("Claude Code did not return its models. " + diagnostics.Read());
            }

            var models = new List<ModelInfo>();
            foreach (var item in result.Items("models"))
            {
                var value = item.Text("value");
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                var efforts = item.Items("supportedEffortLevels").Select(e => e.GetString()).Where(e => !string.IsNullOrEmpty(e)).Select(e => e!).ToList();
                var tiers = item.Flag("supportsFastMode") == true
                    ? new List<ServiceTierInfo> { new("fast", "Fast mode", "Faster responses at a higher price per token, paid from usage credits.", Faster: true) }
                    : [];
                models.Add(new ModelInfo(
                    value, item.Text("displayName") ?? value, item.Text("description"), efforts, null,
                    IsDefault: value == "default", Hidden: false, tiers, item.Text("resolvedModel"), "initialize control request"));
            }

            await errors.WaitAsync(TimeSpan.FromMilliseconds(200), CancellationToken.None).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            return models;
        }
    }

    internal static Task DrainAsync(Stream stream, DiagnosticTail diagnostics) => Task.Run(async () =>
    {
        var decoder = new Platform.Processes.OutputDecoder(diagnostics.Append);
        var buffer = new byte[8 * 1024];
        try
        {
            int read;
            while ((read = await stream.ReadAsync(buffer).ConfigureAwait(false)) > 0)
            {
                decoder.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        decoder.Complete();
    });

    public Task<RateLimitSnapshot?> GetRateLimitsAsync(CancellationToken cancellationToken) => Task.FromResult<RateLimitSnapshot?>(null);

    public Task<IAgentSession> StartSessionAsync(SessionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IAgentSession>(Open(request, null));

    public Task<IAgentSession> ResumeSessionAsync(string sessionId, SessionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IAgentSession>(Open(request, sessionId));

    private ClaudeCliSession Open(SessionRequest request, string? resumeId)
    {
        if (request.Role == AgentRole.Implementer && !request.ProjectTrusted && ApiKey() is not null)
        {
            // Commands the agent runs inherit its environment. In a project that is not trusted those commands are
            // controlled by the repository, so the key would be exposed to it.
            throw new CredentialIsolationException(
                "Claude cannot implement in this project while it is untrusted: commands the agent runs would inherit ANTHROPIC_API_KEY, "
                + "and Claude Code documents no way to withhold it from them. Trust the project, or use Claude as the reviewer, "
                + "which has no tool that can run anything.");
        }

        return new ClaudeCliSession(this, request, resumeId);
    }

    public Task<SessionProbe> ProbeSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(new SessionProbe(SessionProbeState.Unsupported, "Claude Code offers no way to examine a session without sending a turn."));

    public LoginFlow? GetLoginFlow()
    {
        var executable = ResolveExecutable();
        return executable is null
            ? null
            : new LoginFlow(
                executable,
                ["auth", "login"],
                "Claude Code's own sign-in",
                [
                    "The sign-in runs in Claude Code itself, through Anthropic's own flow. YAV does not see or store the credentials.",
                    "For usage billed to a Claude Console organization, sign in with 'claude auth login --console' or give YAV an API key.",
                    "Read " + PolicyUrl + " to see which sign-in is meant for which use.",
                ]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
