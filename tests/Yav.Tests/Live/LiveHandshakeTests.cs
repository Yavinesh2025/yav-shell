using System.Text.Json;
using Xunit.Abstractions;
using Yav.Adapters;
using Yav.Adapters.Protocol;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Live;

/// <summary>Reported as skipped, not passed, unless live tests were asked for.</summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public LiveFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("YAV_LIVE_TESTS") != "1")
        {
            Skip = @"Talks to the agent CLIs installed on this machine. Run scripts\test.ps1 -Live.";
        }
    }
}

/// <summary>
/// Live tests against the agent CLIs installed on this machine. They perform only handshakes that the
/// providers serve without inference: version, account, model list, limits, sandbox state and the settings
/// in effect. No prompt reaches a model and no session is left behind. They run only with
/// "scripts\test.ps1 -Live".
/// </summary>
[Trait("Category", "Live")]
public class LiveHandshakeTests(ITestOutputHelper output)
{
    private static readonly AdapterOptions Options = new(ClientVersion: "0.1.0-live-test");

    [LiveFact]
    public async Task The_installed_codex_answers_the_handshake_and_lists_its_models()
    {
        await using var adapter = new CodexAppServerAdapter(new ProcessRunner(), TimeProvider.System, Options);
        var detection = await adapter.DetectAsync(CancellationToken.None);
        output.WriteLine($"codex: found={detection.Found} version={detection.Version} tested={detection.VersionTested} path={detection.ExecutablePath}");
        Assert.True(detection.Found, "The Codex CLI is not installed on this machine.");
        Assert.Matches(@"^\d+\.\d+\.\d+", detection.Version!);

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);
        output.WriteLine($"codex auth: authenticated={auth.Authenticated} route={auth.Route} label={auth.RouteLabel} billing={auth.Billing}");

        var models = await adapter.ListModelsAsync(CancellationToken.None);
        foreach (var model in models)
        {
            output.WriteLine(
                $"codex model: {model.Id} | efforts=[{string.Join(",", model.SupportedEfforts)}] default={model.DefaultEffort} "
                + $"isDefault={model.IsDefault} tiers=[{string.Join(",", model.ServiceTiers.Select(t => t.Id + (t.Faster ? "*" : "")))}]");
        }

        Assert.NotEmpty(models);
        Assert.All(models, m => Assert.False(string.IsNullOrWhiteSpace(m.Id)));

        var limits = await adapter.GetRateLimitsAsync(CancellationToken.None);
        output.WriteLine(limits is null
            ? "codex limits: Unavailable"
            : $"codex limits: plan={limits.PlanType} primary={limits.Primary?.UsedPercent}%/{limits.Primary?.WindowMinutes}min "
              + $"secondary={limits.Secondary?.UsedPercent}%/{limits.Secondary?.WindowMinutes}min credits={limits.HasCredits}");

        var sandbox = await adapter.GetSandboxStatusAsync(CancellationToken.None);
        output.WriteLine($"codex sandbox: state={sandbox.State} enforced={sandbox.OperatingSystemEnforced} | {sandbox.Detail}");
        Assert.Contains(sandbox.State, new[] { "ready", "notConfigured", "updateRequired", "unavailable" });
    }

    [LiveFact]
    public async Task The_installed_codex_reports_the_settings_in_effect_for_a_thread_that_is_not_kept()
    {
        var runner = new ProcessRunner();
        var executable = runner.Resolve("codex");
        Assert.NotNull(executable);
        using var directory = new TempDirectory("live");

        var process = runner.Start(new ProcessSpec(executable, ["app-server"], directory.Path));
        await using var connection = new JsonRpcConnection(process, TimeSpan.FromSeconds(60), new DiagnosticTail());
        await connection.RequestAsync(
            "initialize",
            writer =>
            {
                writer.WritePropertyName("clientInfo");
                writer.WriteStartObject();
                writer.WriteString("name", CodexAppServerAdapter.ClientName);
                writer.WriteString("title", "YAV Shell");
                writer.WriteString("version", "0.1.0-live-test");
                writer.WriteEndObject();
            },
            CancellationToken.None);
        await connection.NotifyAsync("initialized", CancellationToken.None);

        foreach (var (sandbox, approval) in new[] { ("read-only", "never"), ("workspace-write", "on-request") })
        {
            var result = await connection.RequestAsync(
                "thread/start",
                writer =>
                {
                    writer.WriteString("cwd", directory.Path);
                    writer.WriteString("sandbox", sandbox);
                    writer.WriteString("approvalPolicy", approval);
                    writer.WriteString("developerInstructions", "Live handshake test. No turn is sent.");

                    // Not written to disk, so nothing is left in the user's session list.
                    writer.WriteBoolean("ephemeral", true);
                },
                CancellationToken.None);

            var effective = CodexAppServerAdapter.ParseEffective(result, "thread/start", null);
            output.WriteLine(
                $"requested sandbox={sandbox} approval={approval} -> effective model={effective.Model} effort={effective.Effort} "
                + $"sandbox={effective.Sandbox} approval={effective.ApprovalPolicy} tier={effective.ServiceTier} cwd={effective.WorkingDirectory} "
                + $"version={effective.AgentVersion} instructions=[{string.Join(";", effective.InstructionSources)}]");
            output.WriteLine("  raw sandbox: " + (result.TryGetProperty("sandbox", out var raw) ? raw.GetRawText() : "(none)"));

            Assert.False(string.IsNullOrEmpty(effective.Model));
            Assert.NotNull(effective.Sandbox);
            Assert.NotNull(effective.ApprovalPolicy);
            Assert.True(result.TryGetProperty("thread", out var thread) && thread.TryGetProperty("id", out _));
        }
    }

    /// <summary>
    /// The names YAV uses when it talks to Codex, read from the adapter itself, are looked up in the schema
    /// the installed Codex generates. The interface is experimental and changes; this is what notices it.
    /// Nothing is sent anywhere: Codex writes the schema from what it was built with.
    /// </summary>
    [LiveFact]
    public async Task What_yav_says_to_codex_is_in_the_schema_the_installed_codex_generates()
    {
        var runner = new ProcessRunner();
        var executable = runner.Resolve("codex");
        Assert.NotNull(executable);
        using var directory = new TempDirectory("schema");
        var generated = await runner.RunAsync(
            new ProcessSpec(executable, ["app-server", "generate-json-schema", "--out", directory.Path], directory.Path),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(120)),
            CancellationToken.None);
        Assert.True(generated.ExitCode == 0, generated.StandardError + generated.StandardOutput);

        var known = new[] { "ClientRequest.json", "ClientNotification.json", "ServerNotification.json", "ServerRequest.json" }
            .SelectMany(file => MethodsIn(JsonDocument.Parse(File.ReadAllText(directory.File(file))).RootElement))
            .ToHashSet(StringComparer.Ordinal);
        output.WriteLine($"the schema names {known.Count} methods");

        var used = NamesInTheAdapter();
        output.WriteLine("YAV uses: " + string.Join(", ", used));
        Assert.True(used.Count >= 20, $"only {used.Count} names were found in the adapter");
        var unknown = used.Where(name => !known.Contains(name)).ToList();
        Assert.True(unknown.Count == 0, "The schema of the installed Codex does not know: " + string.Join(", ", unknown));

        foreach (var (file, properties) in new (string, string[])[]
        {
            ("v2/ThreadStartParams.json", ["model", "cwd", "sandbox", "approvalPolicy", "approvalsReviewer", "developerInstructions", "serviceName", "serviceTier", "config", "ephemeral"]),
            ("v2/ThreadResumeParams.json", ["threadId", "model", "cwd", "sandbox", "approvalPolicy", "approvalsReviewer", "developerInstructions", "config", "excludeTurns"]),
            ("v2/TurnStartParams.json", ["threadId", "input", "effort", "outputSchema"]),
            ("v2/TurnSteerParams.json", ["threadId", "expectedTurnId", "input"]),
            ("v2/TurnInterruptParams.json", ["threadId", "turnId"]),
            ("v2/ModelListParams.json", ["includeHidden", "cursor"]),
            ("v2/ThreadReadParams.json", ["threadId", "includeTurns"]),
        })
        {
            var schema = JsonDocument.Parse(File.ReadAllText(directory.File(file))).RootElement;
            var has = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            Assert.True(properties.All(has.Contains), $"{file} has no {string.Join(", ", properties.Where(p => !has.Contains(p)))}");
        }
    }

    private static IEnumerable<string> MethodsIn(JsonElement node)
    {
        switch (node.ValueKind)
        {
            case JsonValueKind.Object:
                if (node.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
                    && properties.TryGetProperty("method", out var method) && method.TryGetProperty("enum", out var names))
                {
                    foreach (var name in names.EnumerateArray())
                    {
                        yield return name.GetString()!;
                    }
                }

                foreach (var child in node.EnumerateObject().SelectMany(p => MethodsIn(p.Value)))
                {
                    yield return child;
                }

                break;

            case JsonValueKind.Array:
                foreach (var child in node.EnumerateArray().SelectMany(MethodsIn))
                {
                    yield return child;
                }

                break;
        }
    }

    /// <summary>Every name of a request or a notification that is written in the adapter for the Codex app server.</summary>
    private static List<string> NamesInTheAdapter()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
        {
            directory = directory.Parent;
        }

        var names = new SortedSet<string>(StringComparer.Ordinal) { "initialize", "initialized" };
        // CodexServer.cs holds what one app-server process says to every conversation.
        var codex = Path.Combine(directory!.FullName, "src", "Yav.Adapters", "Codex");
        foreach (var file in Directory.EnumerateFiles(codex, "CodexAppServer*.cs").Append(Path.Combine(codex, "CodexServer.cs")))
        {
            foreach (System.Text.RegularExpressions.Match match in System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(file), "\"([a-z][A-Za-z]*(/[A-Za-z]+)+)\""))
            {
                names.Add(match.Groups[1].Value);
            }
        }

        return [.. names];
    }

    [LiveFact]
    public async Task The_installed_claude_reports_its_version_account_route_and_models()
    {
        await using var adapter = new ClaudeCliAdapter(new ProcessRunner(), TimeProvider.System, Options, () => null);
        var detection = await adapter.DetectAsync(CancellationToken.None);
        output.WriteLine($"claude: found={detection.Found} version={detection.Version} tested={detection.VersionTested} problems=[{string.Join("; ", detection.Problems)}]");
        Assert.True(detection.Found, "Claude Code is not installed on this machine.");

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);
        output.WriteLine($"claude auth: authenticated={auth.Authenticated} route={auth.Route} label={auth.RouteLabel} billing={auth.Billing} policy={auth.Policy}");

        var models = await adapter.ListModelsAsync(CancellationToken.None);
        foreach (var model in models)
        {
            output.WriteLine(
                $"claude model: {model.Id} -> {model.ResolvedModelId} | efforts=[{string.Join(",", model.SupportedEfforts)}] "
                + $"fast={model.ServiceTiers.Any(t => t.Faster)} | {model.DisplayName}");
        }

        Assert.NotEmpty(models);
    }

    /// <summary>
    /// Claude Code says which tools a conversation has only when the conversation gets its first prompt. So a
    /// prompt is written to the program, but the address of the API is one nobody listens on: the program
    /// says what it would work with and then cannot reach a model. It is ended as soon as it has said it.
    ///
    /// This is the check that a stand-in cannot make: whether the installed program confirms the effort and
    /// the boundary of a review in the way the adapter reads them.
    /// </summary>
    [LiveFact]
    public async Task The_installed_claude_confirms_the_effort_and_the_boundary_of_a_review_without_a_model_being_asked()
    {
        var unreachable = Options with { Environment = new Dictionary<string, string?> { ["ANTHROPIC_BASE_URL"] = "http://127.0.0.1:9" } };
        await using var adapter = new ClaudeCliAdapter(new ProcessRunner(), TimeProvider.System, unreachable, () => null);
        var model = (await adapter.ListModelsAsync(CancellationToken.None)).First(m => m.SupportedEfforts.Count > 0 && m.ResolvedModelId is not null);
        var effort = model.SupportedEfforts[^1];
        using var directory = new TempDirectory("live");
        var leftBehind = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");
        var unique = directory.Path[^8..];

        EffectiveSettings? reported = null;
        try
        {
            var session = await adapter.StartSessionAsync(
                new SessionRequest(
                    AgentRole.Reviewer, model.Id, effort, directory.Path, SandboxLevel.ReadOnly, ApprovalMode.NeverAsk,
                    "Live handshake test.", ProjectTrusted: false, ServiceTier: null, AdditionalReadableDirectories: []),
                CancellationToken.None);
            await using (session.ConfigureAwait(false))
            {
                await session.StartTurnAsync(new TurnRequest("Answer with the single word: nothing.", ReviewSchema.AsElement(), []), CancellationToken.None);
                using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await foreach (var said in session.Events.ReadAllAsync(patience.Token))
                {
                    output.WriteLine("event: " + said.GetType().Name);
                    if (said is SessionConfigured configured)
                    {
                        reported = configured.Effective;
                    }

                    if (said is SessionConfigured or TurnCompleted or SessionEnded)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            // The conversation that was started is not kept: it is nobody's.
            if (Directory.Exists(leftBehind))
            {
                foreach (var left in Directory.EnumerateDirectories(leftBehind, "*" + unique))
                {
                    Directory.Delete(left, recursive: true);
                }
            }
        }

        var effective = Assert.IsType<EffectiveSettings>(reported);
        output.WriteLine(
            $"requested model={model.Id} effort={effort} -> model={effective.Model} effort={effective.Effort} ({effective.EffortSource}) "
            + $"sandbox={effective.Sandbox} mode={effective.ApprovalPolicy} credential={effective.CredentialSource} version={effective.AgentVersion}");
        output.WriteLine("tools: " + string.Join(", ", effective.Tools));

        Assert.Equal(model.ResolvedModelId, effective.Model);
        Assert.Equal(effort, effective.Effort);
        Assert.Equal("get_settings answer", effective.EffortSource);
        Assert.Equal("read-only", effective.Sandbox);
        Assert.Equal("dontAsk", effective.ApprovalPolicy);
        Assert.Contains("Read", effective.Tools);
    }
}
