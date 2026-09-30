using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Yav.Adapters;
using Yav.Core.Agents;
using Yav.Platform.Processes;

namespace Yav.Tests.Support;

/// <summary>
/// Describes what the scripted agent fixture should do and gives access to everything it received.
/// </summary>
public sealed class AgentFixture : IDisposable
{
    private readonly TempDirectory _directory = new("agent");
    private readonly JsonObject _scenario = [];
    private readonly string _scenarioPath;
    private readonly string _logPath;

    public AgentFixture()
    {
        _scenarioPath = _directory.File("scenario.json");
        _logPath = _directory.File("received.jsonl");
        _scenario["log"] = _logPath;
        _scenario["codex"] = new JsonObject();
        _scenario["claude"] = new JsonObject();
        _scenario["implementer"] = new JsonArray();
        _scenario["reviewer"] = new JsonArray();
        Workspace = _directory.CreateDirectory("workspace ünï");
        Save();
    }

    /// <summary>A directory that stands for the isolated workspace the agent works in.</summary>
    public string Workspace { get; }

    public ManualClock Clock { get; } = new();

    public AgentFixture Codex(Action<JsonObject> configure)
    {
        configure((JsonObject)_scenario["codex"]!);
        Save();
        return this;
    }

    public AgentFixture Claude(Action<JsonObject> configure)
    {
        configure((JsonObject)_scenario["claude"]!);
        Save();
        return this;
    }

    /// <summary>Adds the script for the implementer's next turn.</summary>
    public AgentFixture ImplementerTurn(params JsonObject[] steps) => Turn("implementer", steps);

    /// <summary>Adds the script for the reviewer's next turn.</summary>
    public AgentFixture ReviewerTurn(params JsonObject[] steps) => Turn("reviewer", steps);

    private AgentFixture Turn(string role, JsonObject[] steps)
    {
        var script = new JsonArray();
        foreach (var step in steps)
        {
            script.Add(step);
        }

        ((JsonArray)_scenario[role]!).Add(script);
        Save();
        return this;
    }

    public AdapterOptions Options(string flavor = "codex", TimeSpan? requestTimeout = null, TimeSpan? startupTimeout = null) => new(
        ExecutablePath: Fixtures.FakeAgent,
        Environment: new Dictionary<string, string?>
        {
            ["YAV_FAKE_SCENARIO"] = _scenarioPath,
            ["YAV_FAKE_FLAVOR"] = flavor,
        },
        RequestTimeout: requestTimeout ?? TimeSpan.FromSeconds(30),
        StartupTimeout: startupTimeout ?? TimeSpan.FromSeconds(30),
        ClientVersion: "0.1.0-test",
        DiagnosticsDirectory: _directory.CreateDirectory("diagnostics"));

    public CodexAppServerAdapter CodexAppServer(TimeSpan? requestTimeout = null, TimeSpan? startupTimeout = null) =>
        new(new ProcessRunner(), Clock, Options("codex", requestTimeout, startupTimeout));

    public CodexExecAdapter CodexExec() => new(new ProcessRunner(), Clock, Options());

    public ClaudeCliAdapter ClaudeCli(Func<string?>? apiKey = null, TimeSpan? startupTimeout = null) =>
        new(new ProcessRunner(), Clock, Options("claude", startupTimeout: startupTimeout), apiKey ?? (() => null));

    public SessionRequest Request(
        AgentRole role,
        string model = "model-a",
        string effort = "xhigh",
        bool trusted = true,
        string? serviceTier = null,
        string instructions = "ROLE INSTRUCTIONS") => new(
        Role: role,
        ModelId: model,
        Effort: effort,
        WorkingDirectory: Workspace,
        Sandbox: role == AgentRole.Reviewer ? SandboxLevel.ReadOnly : SandboxLevel.WorkspaceWrite,
        Approvals: role == AgentRole.Reviewer ? ApprovalMode.NeverAsk : ApprovalMode.AskUser,
        RoleInstructions: instructions,
        ProjectTrusted: trusted,
        ServiceTier: serviceTier,
        AdditionalReadableDirectories: []);

    /// <summary>Everything the fixture received on the given channel, oldest first.</summary>
    public List<JsonObject> Received(string channel)
    {
        var result = new List<JsonObject>();
        if (!File.Exists(_logPath))
        {
            return result;
        }

        foreach (var line in ReadLog())
        {
            if (JsonNode.Parse(line) is JsonObject entry && entry["channel"]?.GetValue<string>() == channel && entry["payload"] is JsonObject payload)
            {
                payload["_pid"] = entry["pid"]?.GetValue<int>();
                result.Add((JsonObject)payload.DeepClone());
            }
        }

        return result;
    }

    /// <summary>The requests of one method that the Codex fixture received.</summary>
    public List<JsonObject> CodexRequests(string method) =>
        Received("codex.request").Where(r => r["method"]?.GetValue<string>() == method).ToList();

    public string InWorkspace(string relative) => Path.Combine(Workspace, relative.Replace('/', Path.DirectorySeparatorChar));

    private string[] ReadLog()
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
            catch (IOException) when (attempt < 20)
            {
                Thread.Sleep(25);
            }
        }
    }

    private void Save() => File.WriteAllText(_scenarioPath, _scenario.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), new UTF8Encoding(false));

    public void Dispose() => _directory.Dispose();
}

/// <summary>Steps of a scripted turn.</summary>
public static partial class Step
{
    public static JsonObject Message(string text, string phase = "final_answer") => new() { ["type"] = "message", ["text"] = text, ["phase"] = phase };

    public static JsonObject Reasoning(string text) => new() { ["type"] = "reasoning", ["text"] = text };

    /// <param name="milliseconds">How long the command runs: the time between its start and its end.</param>
    public static JsonObject Command(string command, string output = "", int exitCode = 0, int milliseconds = 0) =>
        new() { ["type"] = "command", ["command"] = command, ["output"] = output, ["exitCode"] = exitCode, ["runsMs"] = milliseconds };

    public static JsonObject Write(string path, string content) => new() { ["type"] = "write", ["path"] = path, ["content"] = content };

    /// <summary>
    /// A tool that neither runs a command nor changes a file, with what it is given. "{cwd}" in a text of
    /// that stands for the directory the agent works in. Played by the stand-in for Claude Code.
    /// </summary>
    public static JsonObject Tool(string name, JsonObject input, bool fails = false) =>
        new() { ["type"] = "tool", ["name"] = name, ["input"] = input, ["fails"] = fails };

    /// <summary>
    /// The agent asks for permission and is gone before it can be answered: its input is closed when it asks.
    /// Played by the stand-in for Claude Code, which has to be told "deafAfterPrompt" for it: an input that
    /// is being read cannot be closed.
    /// </summary>
    public static JsonObject AskAndLeave(string command = "whoami") => new() { ["type"] = "askAndLeave", ["command"] = command };

    /// <summary>What Claude Code says about a limit of the account: the part of the window that is used, as a fraction.</summary>
    public static JsonObject ClaudeRateLimit(double utilization, string window = "five_hour", string status = "allowed") =>
        new() { ["type"] = "rateLimit", ["utilization"] = utilization, ["window"] = window, ["status"] = status };

    public static JsonObject Delete(string path) => new() { ["type"] = "delete", ["path"] = path };

    /// <param name="marked">
    /// How the stand-in for Claude Code marks the request: "defaultToNo" for one that must not be granted by a
    /// single key, "needsTheUser" for one the user has to answer in a way only Claude Code's own window offers.
    /// </param>
    public static JsonObject Approval(
        string command, JsonObject[]? onAccept = null, JsonObject[]? onDecline = null, string? reason = null, string kind = "command", string? marked = null)
    {
        var step = new JsonObject { ["type"] = "approval", ["command"] = command, ["kind"] = kind, ["reason"] = reason, ["marked"] = marked };
        step["onAccept"] = new JsonArray((onAccept ?? []).Select(s => (JsonNode?)s).ToArray());
        step["onDecline"] = new JsonArray((onDecline ?? []).Select(s => (JsonNode?)s).ToArray());
        return step;
    }

    /// <summary>
    /// The agent asks whether it may use a tool that is not a shell, with what the tool is given. Played by the
    /// stand-in for Claude Code.
    /// </summary>
    public static JsonObject ToolApproval(string tool, JsonObject input, string? marked = null) =>
        new() { ["type"] = "approval", ["tool"] = tool, ["input"] = input, ["command"] = string.Empty, ["marked"] = marked };

    /// <summary>The agent asks for approval and takes the request back by itself before it is answered.</summary>
    public static JsonObject WithdrawnApproval(string command, int afterMs = 300) =>
        new() { ["type"] = "withdrawnApproval", ["command"] = command, ["afterMs"] = afterMs };

    public static JsonObject Usage(long input, long cached, long output, long reasoning, long lastInput = 0, long lastCached = 0, long lastOutput = 0, long lastReasoning = 0) => new()
    {
        ["type"] = "usage",
        ["total"] = Tokens(input, cached, output, reasoning),
        ["last"] = Tokens(lastInput, lastCached, lastOutput, lastReasoning),
    };

    private static JsonObject Tokens(long input, long cached, long output, long reasoning) => new()
    {
        ["totalTokens"] = input + output,
        ["inputTokens"] = input,
        ["cachedInputTokens"] = cached,
        ["outputTokens"] = output,
        ["reasoningOutputTokens"] = reasoning,
    };

    public static JsonObject Review(
        string status = "pass", string? file = null, string? title = null, string? limitation = null, string? summary = null, string? coverage = null)
    {
        var step = new JsonObject { ["type"] = "review", ["status"] = status };
        if (summary is not null)
        {
            step["summary"] = summary;
        }

        if (coverage is not null)
        {
            step["coverage"] = coverage;
        }

        if (file is not null)
        {
            step["file"] = file;
        }

        if (title is not null)
        {
            step["title"] = title;
        }

        if (limitation is not null)
        {
            step["limitation"] = limitation;
        }

        return step;
    }

    public static JsonObject RawFinal(string text) => new() { ["type"] = "rawFinal", ["text"] = text };

    public static JsonObject Error(string message, bool willRetry = true, string? info = null) =>
        new() { ["type"] = "error", ["message"] = message, ["willRetry"] = willRetry, ["codexErrorInfo"] = info };

    public static JsonObject Fail(string message, string? info = null, string? subtype = null) =>
        new() { ["type"] = "fail", ["message"] = message, ["codexErrorInfo"] = info, ["subtype"] = subtype };

    public static JsonObject Sleep(int milliseconds) => new() { ["type"] = "sleep", ["ms"] = milliseconds };

    public static JsonObject Hang() => new() { ["type"] = "hang" };

    public static JsonObject Crash(int exitCode = 3) => new() { ["type"] = "crash", ["exitCode"] = exitCode };

    public static JsonObject Garbage(string text = "this is not json {") => new() { ["type"] = "garbage", ["text"] = text };

    public static JsonObject Pieces(string text, int size = 3) => new() { ["type"] = "pieces", ["text"] = text, ["size"] = size };

    public static JsonObject BigMessage(int kilobytes) => new() { ["type"] = "bigMessage", ["kilobytes"] = kilobytes };

    public static JsonObject Reroute(string from, string to) => new() { ["type"] = "reroute", ["from"] = from, ["to"] = to };

    public static JsonObject Warning(string message) => new() { ["type"] = "warning", ["message"] = message };

    public static JsonObject Unknown() => new() { ["type"] = "unknownNotification" };

    public static JsonObject UnknownMessage() => new() { ["type"] = "unknownMessage" };

    public static JsonObject Retry(int attempt, string error = "overloaded") => new() { ["type"] = "retry", ["attempt"] = attempt, ["error"] = error };

    public static JsonObject Stderr(string text) => new() { ["type"] = "stderr", ["text"] = text };

    public static JsonObject RateLimits(int usedPercent) => new()
    {
        ["type"] = "rateLimits",
        ["rateLimits"] = new JsonObject
        {
            ["planType"] = "pro",
            ["primary"] = new JsonObject { ["usedPercent"] = usedPercent, ["windowDurationMins"] = 300, ["resetsAt"] = 1_790_000_000 },
        },
    };
}

public static class SessionExtensions
{
    /// <summary>Reads events until the turn ends. Fails the test when that takes too long.</summary>
    public static async Task<List<AgentEvent>> ReadTurnAsync(this IAgentSession session, TimeSpan? timeout = null, Func<AgentEvent, Task>? onEvent = null)
    {
        var events = new List<AgentEvent>();
        using var cancellation = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var item in session.Events.ReadAllAsync(cancellation.Token))
            {
                events.Add(item);
                if (onEvent is not null)
                {
                    await onEvent(item);
                }

                if (item is TurnCompleted or SessionEnded)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw new TimeoutException(
                "The turn did not end in time. Events so far: " + string.Join(", ", events.Select(e => e.GetType().Name)));
        }

        return events;
    }

    public static async Task<List<AgentEvent>> RunTurnAsync(this IAgentSession session, string prompt, JsonElement? schema = null, Func<AgentEvent, Task>? onEvent = null)
    {
        await session.StartTurnAsync(new TurnRequest(prompt, schema, []), CancellationToken.None);
        return await session.ReadTurnAsync(onEvent: onEvent);
    }

    public static TurnCompleted Completion(this IEnumerable<AgentEvent> events) => events.OfType<TurnCompleted>().Single();
}
