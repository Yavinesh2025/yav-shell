using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace Yav.FakeAgent;

/// <summary>
/// Speaks the Codex app-server wire format over standard input and output: one JSON object per line,
/// requests with an id, notifications without, and no "jsonrpc" field.
/// </summary>
internal sealed partial class CodexAppServer
{
    private const int StandardInput = -10;

    private readonly Scenario _scenario = Scenario.Load();
    private readonly JsonLineWriter _out = new();
    private readonly ConcurrentDictionary<string, ThreadState> _threads = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonNode?>> _pending = new();
    private bool _initialized;
    private int _serverRequestCounter;
    private int _threadCounter;
    private int _turnCounter;
    private int _itemCounter;

    // The turn that was started last, for a scenario that stops reading once a turn runs.
    private Task? _playing;
    private int _configReads;

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int which);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    private sealed class ThreadState
    {
        public required string Id { get; init; }

        public required string Cwd { get; set; }

        public required string Role { get; init; }

        public required string Model { get; set; }

        public string? Effort { get; set; }

        public string? ActiveTurnId { get; set; }

        public CancellationTokenSource? ActiveTurn { get; set; }

        public string? LastTurnStatus { get; set; }

        public bool HasOutputSchema { get; set; }

        public string ApprovalPolicy { get; set; } = "on-request";

        public string ApprovalsReviewer { get; set; } = "user";

        public JsonObject SandboxPolicy { get; set; } = new() { ["type"] = "workspaceWrite" };

        public string? ServiceTier { get; set; }

        public bool CloseAfterTurn { get; set; }
    }

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length > 0 && args[0] is "generate-json-schema" or "generate-ts")
        {
            return 0;
        }

        var server = new CodexAppServer();
        return await server.LoopAsync();
    }

    private async Task<int> LoopAsync()
    {
        var delay = Scenario.Number(_scenario.Codex, "startupDelayMs");
        if (delay > 0)
        {
            await Task.Delay(delay);
        }

        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        string? line;
        while ((line = await input.ReadLineAsync()) is not null)
        {
            if (line.Length == 0)
            {
                continue;
            }

            var message = JsonHelpers.ParseObject(line);
            if (message is null)
            {
                Console.Error.WriteLine("Failed to deserialize JSONRPCMessage");
                continue;
            }

            var hasMethod = message["method"] is not null;
            var hasId = message["id"] is not null;
            if (hasMethod && hasId)
            {
                _scenario.Log("codex.request", message);
                Handle(message);
                if (Scenario.Text(message, "method") == "turn/start" && _playing is { } playing && Scenario.Flag(_scenario.Codex, "deafAfterTurnStart"))
                {
                    // Nothing is read any more, so that the input can be closed while the turn is played.
                    await playing;
                    return 0;
                }
            }
            else if (hasMethod)
            {
                _scenario.Log("codex.notification", message);
            }
            else if (hasId)
            {
                _scenario.Log("codex.response", message);
                var key = message["id"]!.ToJsonString();
                if (_pending.TryRemove(key, out var waiter))
                {
                    waiter.TrySetResult(message["error"] is null ? message["result"] : null);
                }
            }
        }

        // Standard input closed: the connection is over.
        foreach (var thread in _threads.Values)
        {
            thread.ActiveTurn?.Cancel();
        }

        return 0;
    }

    private void Handle(JsonObject request)
    {
        var id = request["id"]!.DeepClone();
        var method = request["method"]!.GetValue<string>();
        var parameters = request["params"] as JsonObject ?? [];

        if (method != "initialize" && !_initialized)
        {
            Error(id, -32600, "Not initialized");
            return;
        }

        switch (method)
        {
            case "initialize":
                if (_initialized)
                {
                    Error(id, -32600, "Already initialized");
                    return;
                }

                _initialized = true;
                Reply(id, new JsonObject
                {
                    ["userAgent"] = $"yav_shell/{Scenario.Text(_scenario.Codex, "version", "0.158.0")} (Windows 10.0.26200; x86_64) test",
                    ["codexHome"] = Path.Combine(Path.GetTempPath(), "fake-codex-home"),
                    ["platformFamily"] = "windows",
                    ["platformOs"] = "windows",
                });

                // Codex sends the warnings about its configuration to every connection right after this answer.
                foreach (var step in _scenario.Codex["afterInitialize"] as JsonArray ?? [])
                {
                    SendNotification(step as JsonObject ?? [], null, null);
                }

                break;

            case "account/read":
                Reply(id, new JsonObject
                {
                    ["account"] = _scenario.Codex.ContainsKey("account")
                        ? _scenario.Codex["account"]?.DeepClone()
                        : new JsonObject { ["type"] = "chatgpt", ["email"] = "user@example.invalid", ["planType"] = "pro" },
                    ["requiresOpenaiAuth"] = true,
                });
                break;

            case "model/list":
                Reply(id, new JsonObject { ["data"] = Models(), ["nextCursor"] = null });
                break;

            case "account/rateLimits/read":
                if (Scenario.Flag(_scenario.Codex, "rateLimitsUnavailable"))
                {
                    Error(id, -32603, "rate limits are unavailable");
                    return;
                }

            {
                var reading = new JsonObject
                {
                    ["rateLimits"] = _scenario.Codex["rateLimits"]?.DeepClone() ?? new JsonObject
                    {
                        ["planType"] = "pro",
                        ["primary"] = new JsonObject { ["usedPercent"] = 12, ["windowDurationMins"] = 300, ["resetsAt"] = 1_790_000_000 },
                        ["secondary"] = new JsonObject { ["usedPercent"] = 40, ["windowDurationMins"] = 10080, ["resetsAt"] = 1_790_500_000 },
                        ["credits"] = new JsonObject { ["hasCredits"] = false, ["unlimited"] = false, ["balance"] = null },
                    },
                    ["rateLimitsByLimitId"] = _scenario.Codex["rateLimitsByLimitId"]?.DeepClone(),
                };

                // An update that is newer than the reading, written in the same breath as the answer.
                if (_scenario.Codex["rateLimitsUpdateAfterRead"] is JsonObject newer)
                {
                    var answer = new JsonObject { ["id"] = id, ["result"] = reading };
                    var update = NotificationMessage("account/rateLimits/updated", new JsonObject { ["rateLimits"] = newer.DeepClone() });
                    _out.WriteRaw(answer.ToJsonString() + "\n" + update.ToJsonString() + "\n");
                    break;
                }

                Reply(id, reading);
                break;
            }

            case "windowsSandbox/readiness":
                Reply(id, new JsonObject { ["status"] = Scenario.Text(_scenario.Codex, "sandboxReadiness", "ready") });
                break;

            case "config/read":
            {
                // The first reads fail, as when the configuration file cannot be parsed while it is being edited.
                if (Interlocked.Increment(ref _configReads) <= Scenario.Number(_scenario.Codex, "configReadFailures"))
                {
                    Error(id, -32603, "failed to load configuration: expected a table at line 3 of config.toml");
                    return;
                }

                // The configuration in effect as seen from the directory, which includes the layers of its project.
                var directory = Scenario.Text(parameters, "cwd");
                var instructions = directory.Length > 0 && (_scenario.Codex["projectDeveloperInstructions"] as JsonObject)?[directory] is JsonValue project
                    ? project.DeepClone()
                    : _scenario.Codex["developerInstructions"]?.DeepClone();
                Reply(id, new JsonObject
                {
                    ["config"] = new JsonObject
                    {
                        ["developer_instructions"] = instructions,
                        ["model"] = "model-a",
                    },
                    ["origins"] = new JsonObject(),
                });
                break;
            }

            case "thread/start":
                StartThread(id, parameters, resumedId: null);
                break;

            case "thread/resume":
            {
                var threadId = Scenario.Text(parameters, "threadId");
                var known = _scenario.Codex["knownThreads"] as JsonArray ?? [];
                if (!known.Any(k => k?.GetValue<string>() == threadId) && !_threads.ContainsKey(threadId))
                {
                    Error(id, -32600, $"thread not found: {threadId}");
                    return;
                }

                StartThread(id, parameters, threadId);
                break;
            }

            case "thread/read":
            {
                // Codex could not read the thread, or does not answer at all.
                if (_scenario.Codex["threadReadError"] is JsonObject failure)
                {
                    Error(id, Scenario.Number(failure, "code", -32603), Scenario.Text(failure, "message"));
                    return;
                }

                if (Scenario.Flag(_scenario.Codex, "threadReadHangs"))
                {
                    return;
                }

                var threadId = Scenario.Text(parameters, "threadId");
                var probe = _scenario.Codex["threadStates"] as JsonObject;
                var lastStatus = probe?[threadId] is JsonValue value ? value.GetValue<string>() : null;
                if (lastStatus is null && !_threads.ContainsKey(threadId))
                {
                    Error(id, -32600, $"thread not found: {threadId}");
                    return;
                }

                _threads.TryGetValue(threadId, out var thread);
                lastStatus ??= thread?.LastTurnStatus;
                var turns = new JsonArray();
                if (lastStatus is not null)
                {
                    turns.Add(new JsonObject { ["id"] = "turn-last", ["status"] = lastStatus, ["items"] = new JsonArray() });
                }

                // A thread known from its records can still be run by Codex, for another client, say.
                var recorded = (_scenario.Codex["threadStatus"] as JsonObject)?[threadId] is JsonValue given ? given.GetValue<string>() : null;
                var active = thread?.ActiveTurnId is not null || recorded == "active";
                if (thread?.ActiveTurnId is { } running)
                {
                    turns.Add(new JsonObject { ["id"] = running, ["status"] = "inProgress", ["items"] = new JsonArray() });
                }

                // As Codex 0.158 does: a turn recorded as in progress in a thread that nothing runs is reported as
                // interrupted. A scenario can stand for a version that reports it as recorded.
                if (!active && !Scenario.Flag(_scenario.Codex, "turnsAsRecorded"))
                {
                    foreach (var turn in turns.OfType<JsonObject>().Where(t => Scenario.Text(t, "status") == "inProgress"))
                    {
                        turn["status"] = "interrupted";
                    }
                }

                // A thread this process did not load, only knows from its records, is not loaded.
                var status = active ? "active" : recorded ?? (thread is null ? "notLoaded" : "idle");
                Reply(id, new JsonObject
                {
                    ["thread"] = ThreadJson(threadId, thread?.Cwd ?? string.Empty, thread?.Model ?? "model-a", thread?.Effort, active, turns, status: status),
                });
                break;
            }

            case "turn/start":
                StartTurn(id, parameters);
                break;

            case "turn/interrupt":
            {
                var threadId = Scenario.Text(parameters, "threadId");
                if (!_threads.TryGetValue(threadId, out var thread) || thread.ActiveTurn is null)
                {
                    Error(id, -32600, "no active turn to interrupt");
                    return;
                }

                if (!Scenario.Flag(_scenario.Codex, "ignoreInterrupt"))
                {
                    thread.ActiveTurn.Cancel();
                }

                Reply(id, []);
                break;
            }

            case "turn/steer":
            {
                var threadId = Scenario.Text(parameters, "threadId");
                if (!_threads.TryGetValue(threadId, out var thread) || thread.ActiveTurnId is null)
                {
                    Error(id, -32600, "no active turn to steer");
                    return;
                }

                if (Scenario.Flag(_scenario.Codex, "refuseSteer"))
                {
                    Error(id, -32600, "the turn cannot take input right now");
                    return;
                }

                if (Scenario.Text(parameters, "expectedTurnId") != thread.ActiveTurnId)
                {
                    Error(id, -32600, $"expected active turn id `{Scenario.Text(parameters, "expectedTurnId")}` but found `{thread.ActiveTurnId}`");
                    return;
                }

                Reply(id, new JsonObject { ["turnId"] = thread.ActiveTurnId });
                break;
            }

            default:
                Error(id, -32601, $"Method not found: {method}");
                break;
        }
    }

    private JsonArray Models()
    {
        if (_scenario.Codex["models"] is JsonArray configured)
        {
            var result = new JsonArray();
            foreach (var model in configured)
            {
                var id = Scenario.Text(model, "id");
                var efforts = new JsonArray();
                foreach (var effort in (model as JsonObject)?["efforts"] as JsonArray ?? [])
                {
                    efforts.Add(effort is JsonObject described
                        ? new JsonObject { ["reasoningEffort"] = Scenario.Text(described, "value"), ["description"] = Scenario.Text(described, "description") }
                        : new JsonObject { ["reasoningEffort"] = effort?.GetValue<string>(), ["description"] = "" });
                }

                result.Add(new JsonObject
                {
                    ["id"] = id,
                    ["model"] = id,
                    ["displayName"] = Scenario.Text(model, "displayName", id),
                    ["description"] = Scenario.Text(model, "description", "A model."),
                    ["hidden"] = Scenario.Flag(model, "hidden"),
                    ["isDefault"] = Scenario.Flag(model, "isDefault"),
                    ["supportedReasoningEfforts"] = efforts,
                    ["defaultReasoningEffort"] = Scenario.Text(model, "defaultEffort", "medium"),
                    ["serviceTiers"] = (model as JsonObject)?["serviceTiers"]?.DeepClone() ?? new JsonArray(),
                });
            }

            return result;
        }

        return
        [
            Model("model-a", true, ["low", "medium", "high", "xhigh"]),
            Model("model-b", false, ["low", "medium", "high", "xhigh", "max"]),
        ];

        static JsonObject Model(string id, bool isDefault, string[] efforts)
        {
            var list = new JsonArray();
            foreach (var effort in efforts)
            {
                list.Add(new JsonObject { ["reasoningEffort"] = effort, ["description"] = effort });
            }

            return new JsonObject
            {
                ["id"] = id,
                ["model"] = id,
                ["displayName"] = id.ToUpperInvariant(),
                ["description"] = "A flagship model.",
                ["hidden"] = false,
                ["isDefault"] = isDefault,
                ["supportedReasoningEfforts"] = list,
                ["defaultReasoningEffort"] = "medium",
                ["serviceTiers"] = new JsonArray { new JsonObject { ["id"] = "fast", ["name"] = "Fast", ["description"] = "Faster responses at a higher credit rate." } },
            };
        }
    }

    private void StartThread(JsonNode id, JsonObject parameters, string? resumedId)
    {
        var sandbox = Scenario.Text(parameters, "sandbox", "workspace-write");
        var role = sandbox == "read-only" ? "reviewer" : "implementer";
        var config = parameters["config"] as JsonObject;
        var requestedEffort = Scenario.Text(config, "model_reasoning_effort", string.Empty);
        // A forced value applies to both roles unless it is given for one role only.
        var effort = (_scenario.Codex["effectiveEffort:" + role] ?? _scenario.Codex["effectiveEffort"]) is JsonValue forcedEffort
            ? forcedEffort.GetValue<string>()
            : requestedEffort.Length > 0 ? requestedEffort : "medium";
        var model = (_scenario.Codex["effectiveModel:" + role] ?? _scenario.Codex["effectiveModel"]) is JsonValue forcedModel
            ? forcedModel.GetValue<string>()
            : Scenario.Text(parameters, "model", "model-a");

        var threadId = resumedId ?? $"thr-{Interlocked.Increment(ref _threadCounter)}-{Environment.ProcessId}";
        var thread = new ThreadState
        {
            Id = threadId,
            Cwd = Scenario.Text(parameters, "cwd", Environment.CurrentDirectory),
            Role = role,
            Model = model,
            Effort = effort,
        };
        _threads[threadId] = thread;

        var effectiveSandbox = (_scenario.Codex["effectiveSandbox:" + role] ?? _scenario.Codex["effectiveSandbox"]) is JsonValue forced ? forced.GetValue<string>() : sandbox switch
        {
            "read-only" => "readOnly",
            "danger-full-access" => "dangerFullAccess",
            _ => "workspaceWrite",
        };
        var policy = new JsonObject { ["type"] = effectiveSandbox };

        // A policy given as it is, in a form this version of Codex may not even write.
        if ((_scenario.Codex["effectiveSandboxPolicy:" + role] ?? _scenario.Codex["effectiveSandboxPolicy"]) is JsonObject verbatim)
        {
            policy = (JsonObject)verbatim.DeepClone();
        }

        // The fields that widen a policy, as Codex writes them. Without them the policy stands for a version of
        // Codex that no longer reports them.
        else if (!Scenario.Flag(_scenario.Codex, "sandboxWithoutDetails"))
        {
            var network = (_scenario.Codex["effectiveNetworkAccess:" + role] ?? _scenario.Codex["effectiveNetworkAccess"]) is JsonValue open && open.GetValue<bool>();
            switch (effectiveSandbox)
            {
                case "workspaceWrite":
                    policy["writableRoots"] = _scenario.Codex["effectiveWritableRoots"]?.DeepClone() ?? new JsonArray();
                    policy["networkAccess"] = network;
                    policy["excludeTmpdirEnvVar"] = false;
                    policy["excludeSlashTmp"] = false;
                    break;

                case "readOnly":
                    policy["networkAccess"] = network;
                    break;

                case "externalSandbox":
                    policy["networkAccess"] = network ? "enabled" : "restricted";
                    break;
            }
        }

        // Who decides approvals: what the client asked for, else the user's own configuration. A forced value
        // stands for one the client cannot override, such as a requirement set by an administrator.
        var reviewer = (_scenario.Codex["effectiveApprovalsReviewer:" + role] ?? _scenario.Codex["effectiveApprovalsReviewer"]) is JsonValue forcedReviewer
            ? forcedReviewer.GetValue<string>()
            : Scenario.Text(parameters, "approvalsReviewer", Scenario.Text(_scenario.Codex, "configuredApprovalsReviewer", "user"));

        var sources = new JsonArray();
        foreach (var name in new[] { "AGENTS.md" })
        {
            var path = Path.Combine(thread.Cwd, name);
            if (File.Exists(path))
            {
                sources.Add(path);
            }
        }

        thread.ApprovalPolicy = Scenario.Text(parameters, "approvalPolicy", "on-request");
        thread.ApprovalsReviewer = reviewer;
        thread.SandboxPolicy = policy;
        thread.ServiceTier = Scenario.Text(parameters, "serviceTier") is { Length: > 0 } tier ? tier : null;

        // A thread that is resumed may have been created by an earlier version than the one that runs.
        var createdBy = resumedId is null ? null : Scenario.Text(_scenario.Codex, "threadCliVersion") is { Length: > 0 } earlier ? earlier : null;

        // A resumed thread comes with its whole history unless the client asks for it without.
        var history = new JsonArray();
        if (resumedId is not null && !Scenario.Flag(parameters, "excludeTurns"))
        {
            history.Add(TurnJson("turn-earlier", "completed", null));
        }

        var answer = new JsonObject
        {
            ["thread"] = ThreadJson(threadId, thread.Cwd, model, effort, false, history, createdBy),
            ["model"] = model,
            ["modelProvider"] = "openai",
            ["cwd"] = thread.Cwd,
            ["approvalPolicy"] = thread.ApprovalPolicy,
            ["approvalsReviewer"] = reviewer,
            ["sandbox"] = policy.DeepClone(),
            ["reasoningEffort"] = effort,
            ["serviceTier"] = parameters["serviceTier"]?.DeepClone(),
            ["instructionSources"] = sources,
        };

        // Codex 0.158 always names both; an answer without them stands for a version that does not.
        if (Scenario.Flag(_scenario.Codex, "omitApprovalsReviewer"))
        {
            answer.Remove("approvalsReviewer");
        }

        if (Scenario.Flag(_scenario.Codex, "omitSandbox"))
        {
            answer.Remove("sandbox");
        }

        // What Codex says about the thread right after the answer, before the client could know it by its id. It
        // is written together with the answer, as Codex's buffered output can deliver it in the same read.
        var written = new List<JsonObject>
        {
            new() { ["id"] = id, ["result"] = answer },
            NotificationMessage("thread/started", new JsonObject { ["thread"] = ThreadJson(threadId, thread.Cwd, model, effort, false, [], createdBy) }),
        };
        foreach (var node in _scenario.Codex["afterThreadStart"] as JsonArray ?? [])
        {
            var step = node as JsonObject ?? [];
            switch (Scenario.Text(step, "type"))
            {
                case "settingsUpdated":
                    written.Add(NotificationMessage("thread/settings/updated", SettingsUpdate(thread, step)));
                    break;

                case "serverRequest":
                {
                    // A request for the thread that nobody waits for here; its answer is recorded when it comes.
                    var requestId = NextRequestId();
                    _pending[requestId.ToJsonString()] = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
                    written.Add(new JsonObject { ["method"] = Scenario.Text(step, "method"), ["id"] = requestId, ["params"] = NotificationParameters(step, thread, null) });
                    break;
                }

                default:
                    written.Add(NotificationMessage(Scenario.Text(step, "method"), NotificationParameters(step, thread, null)));
                    break;
            }
        }

        _out.WriteRaw(string.Concat(written.Select(message => message.ToJsonString() + "\n")));
    }

    /// <param name="status">The kind of status to report instead of the one <paramref name="active"/> stands for.</param>
    private JsonObject ThreadJson(string id, string cwd, string model, string? effort, bool active, JsonArray turns, string? createdBy = null, string? status = null) => new()
    {
        ["id"] = id,
        ["sessionId"] = id,
        ["preview"] = "",
        ["ephemeral"] = false,
        ["modelProvider"] = "openai",
        ["model"] = model,
        ["reasoningEffort"] = effort,
        ["createdAt"] = 1_790_000_000,
        ["updatedAt"] = 1_790_000_000,
        ["status"] = (status ?? (active ? "active" : "idle")) == "active"
            ? new JsonObject { ["type"] = "active", ["activeFlags"] = new JsonArray() }
            : new JsonObject { ["type"] = status ?? "idle" },
        ["cwd"] = cwd,

        // The version that created the thread; a new thread is created by the version that runs.
        ["cliVersion"] = createdBy ?? Scenario.Text(_scenario.Codex, "version", "0.158.0"),
        ["source"] = "vscode",
        ["projectId"] = null,
        ["turns"] = turns,
    };

    private void StartTurn(JsonNode id, JsonObject parameters)
    {
        var threadId = Scenario.Text(parameters, "threadId");
        if (!_threads.TryGetValue(threadId, out var thread))
        {
            Error(id, -32600, $"thread not found: {threadId}");
            return;
        }

        if (thread.ActiveTurnId is not null)
        {
            Error(id, -32600, "a turn is already active");
            return;
        }

        if (parameters["effort"] is JsonValue effort && _scenario.Codex["effectiveEffort"] is null)
        {
            thread.Effort = effort.GetValue<string>();
        }

        thread.HasOutputSchema = parameters["outputSchema"] is JsonObject;
        var turnId = $"turn-{Interlocked.Increment(ref _turnCounter)}";
        thread.ActiveTurnId = turnId;
        thread.ActiveTurn = new CancellationTokenSource();
        var script = _scenario.NextTurn(thread.Role);

        Reply(id, new JsonObject { ["turn"] = TurnJson(turnId, "inProgress", null) });
        Notify("turn/started", new JsonObject { ["threadId"] = threadId, ["turn"] = TurnJson(turnId, "inProgress", null) });
        var token = thread.ActiveTurn.Token;
        _playing = Task.Run(() => PlayAsync(thread, turnId, script, token));
    }

    private static JsonObject TurnJson(string id, string status, JsonObject? error) => new()
    {
        ["id"] = id,
        ["status"] = status,
        ["items"] = new JsonArray(),
        ["itemsView"] = "notLoaded",
        ["error"] = error,
        ["startedAt"] = 1_790_000_000,
        ["completedAt"] = status == "inProgress" ? null : 1_790_000_060,
        ["durationMs"] = status == "inProgress" ? null : 60_000,
    };

    private async Task PlayAsync(ThreadState thread, string turnId, JsonArray script, CancellationToken cancellation)
    {
        var status = "completed";
        JsonObject? error = null;
        try
        {
            var outcome = await RunStepsAsync(thread, turnId, script, cancellation);
            if (outcome is not null)
            {
                status = outcome.Value.Status;
                error = outcome.Value.Error;
            }
        }
        catch (OperationCanceledException)
        {
            status = "interrupted";
        }

        thread.LastTurnStatus = status;
        thread.ActiveTurnId = null;
        thread.ActiveTurn = null;
        Notify("turn/completed", new JsonObject { ["threadId"] = thread.Id, ["turn"] = TurnJson(turnId, status, error) });
        if (thread.CloseAfterTurn)
        {
            _threads.TryRemove(thread.Id, out _);
            Notify("thread/closed", new JsonObject { ["threadId"] = thread.Id });
        }
    }

    private async Task<(string Status, JsonObject? Error)?> RunStepsAsync(ThreadState thread, string turnId, JsonArray script, CancellationToken cancellation)
    {
        foreach (var node in script)
        {
            cancellation.ThrowIfCancellationRequested();
            if (node is not JsonObject step)
            {
                continue;
            }

            var common = new Func<JsonObject>(() => new JsonObject { ["threadId"] = thread.Id, ["turnId"] = turnId });
            switch (Scenario.Text(step, "type"))
            {
                case "message":
                    Message(thread, turnId, Scenario.Text(step, "text"), Scenario.Text(step, "phase", "commentary"));
                    break;

                case "review":
                    Message(thread, turnId, Scenario.ReviewOutput(step).ToJsonString(), "final_answer");
                    break;

                case "rawFinal":
                    Message(thread, turnId, Scenario.Text(step, "text"), "final_answer");
                    break;

                case "bigMessage":
                    Message(thread, turnId, new string('x', Scenario.Number(step, "kilobytes", 64) * 1024), "commentary");
                    break;

                case "reasoning":
                {
                    var item = new JsonObject
                    {
                        ["type"] = "reasoning", ["id"] = NextItem(),
                        ["summary"] = new JsonArray { Scenario.Text(step, "text") }, ["content"] = new JsonArray(),
                    };
                    Item("item/completed", thread, turnId, item);
                    break;
                }

                case "command":
                {
                    var itemId = NextItem();
                    JsonObject Command(string state, int? exit, string? output) => new()
                    {
                        ["type"] = "commandExecution", ["id"] = itemId, ["command"] = Scenario.Text(step, "command"),
                        ["cwd"] = thread.Cwd, ["status"] = state, ["commandActions"] = new JsonArray(),
                        ["exitCode"] = exit, ["aggregatedOutput"] = output, ["durationMs"] = exit is null ? null : 120,
                    };
                    Item("item/started", thread, turnId, Command("inProgress", null, null));
                    var output = Scenario.Text(step, "output");
                    if (output.Length > 0)
                    {
                        var delta = common();
                        delta["itemId"] = itemId;
                        delta["delta"] = output;
                        Notify("item/commandExecution/outputDelta", delta);
                    }

                    await Task.Delay(Scenario.Number(step, "runsMs", 0), cancellation);
                    var exitCode = Scenario.Number(step, "exitCode");
                    Item("item/completed", thread, turnId, Command(exitCode == 0 ? "completed" : "failed", exitCode, output));
                    break;
                }

                case "write":
                case "delete":
                {
                    var itemId = NextItem();
                    var type = Scenario.Text(step, "type");
                    var full = Path.GetFullPath(Path.Combine(thread.Cwd, Scenario.Text(step, "path")));
                    var existed = File.Exists(full);
                    JsonObject Change(string state) => new()
                    {
                        ["type"] = "fileChange", ["id"] = itemId, ["status"] = state,
                        ["changes"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["path"] = full,
                                ["kind"] = new JsonObject { ["type"] = type == "delete" ? "delete" : existed ? "update" : "add" },
                                ["diff"] = "",
                            },
                        },
                    };
                    Item("item/started", thread, turnId, Change("inProgress"));
                    Scenario.ApplyFileStep(step, thread.Cwd);
                    Item("item/completed", thread, turnId, Change("completed"));
                    break;
                }

                case "approval":
                {
                    var decision = await AskApprovalAsync(thread, turnId, step, cancellation);
                    var text = decision is JsonValue value && value.TryGetValue<string>(out var simple) ? simple : decision is null ? "error" : "accept";
                    if (text == "cancel")
                    {
                        return ("interrupted", null);
                    }

                    var branch = text is "accept" or "acceptForSession" ? "onAccept" : "onDecline";
                    if (step[branch] is JsonArray steps)
                    {
                        var nested = await RunStepsAsync(thread, turnId, (JsonArray)steps.DeepClone(), cancellation);
                        if (nested is not null)
                        {
                            return nested;
                        }
                    }

                    break;
                }

                case "withdrawnApproval":
                {
                    // Asks, then takes the request back by itself before anyone answered, and carries on.
                    var requestId = NextRequestId();
                    _out.Write(new JsonObject
                    {
                        ["method"] = "item/commandExecution/requestApproval",
                        ["id"] = requestId,
                        ["params"] = new JsonObject
                        {
                            ["threadId"] = thread.Id, ["turnId"] = turnId, ["itemId"] = NextItem(), ["startedAtMs"] = 1_790_000_000_000,
                            ["reason"] = null, ["command"] = Scenario.Text(step, "command"), ["cwd"] = thread.Cwd,
                        },
                    });
                    await Task.Delay(Scenario.Number(step, "afterMs", 300), cancellation);
                    Notify("serverRequest/resolved", new JsonObject { ["threadId"] = thread.Id, ["requestId"] = requestId.DeepClone() });
                    break;
                }

                case "usage":
                {
                    var payload = common();
                    payload["tokenUsage"] = new JsonObject
                    {
                        ["total"] = step["total"]?.DeepClone(),
                        ["last"] = step["last"]?.DeepClone(),
                        ["modelContextWindow"] = 400_000,
                    };
                    Notify("thread/tokenUsage/updated", payload);
                    break;
                }

                case "rateLimits":
                    Notify("account/rateLimits/updated", new JsonObject { ["rateLimits"] = step["rateLimits"]?.DeepClone() });
                    break;

                case "error":
                {
                    var payload = common();
                    payload["willRetry"] = Scenario.Flag(step, "willRetry", true);
                    payload["error"] = new JsonObject { ["message"] = Scenario.Text(step, "message"), ["codexErrorInfo"] = step["codexErrorInfo"]?.DeepClone() };
                    Notify("error", payload);
                    break;
                }

                case "reroute":
                {
                    var payload = common();
                    payload["fromModel"] = Scenario.Text(step, "from");
                    payload["toModel"] = Scenario.Text(step, "to");
                    payload["reason"] = "highRiskCyberActivity";
                    Notify("model/rerouted", payload);
                    break;
                }

                case "warning":
                    Notify("warning", new JsonObject { ["threadId"] = thread.Id, ["message"] = Scenario.Text(step, "message") });
                    break;

                case "warningForAll":
                    Notify("warning", new JsonObject { ["threadId"] = null, ["message"] = Scenario.Text(step, "message") });
                    break;

                case "configWarning":
                    Notify("configWarning", new JsonObject
                    {
                        ["summary"] = Scenario.Text(step, "summary"), ["details"] = step["details"]?.DeepClone(), ["path"] = step["path"]?.DeepClone(), ["range"] = null,
                    });
                    break;

                case "deprecationNotice":
                    Notify("deprecationNotice", new JsonObject { ["summary"] = Scenario.Text(step, "summary"), ["details"] = step["details"]?.DeepClone() });
                    break;

                case "settingsUpdated":
                    UpdateSettings(thread, step);
                    break;

                case "subAgent":
                    await SubAgentAsync(thread, step, cancellation);
                    break;

                case "unknownNotification":
                    Notify("future/thing/happened", new JsonObject { ["threadId"] = thread.Id, ["anything"] = 42 });
                    break;

                case "closeInput":
                    // What YAV writes afterwards fails as it does when the agent is gone; the turn is played on.
                    CloseHandle(GetStdHandle(StandardInput));
                    break;

                case "threadClosed":
                    // Codex unloads the thread once the turn has ended, as it does with a thread nobody follows.
                    thread.CloseAfterTurn = true;
                    break;

                case "autoReview":
                    AutoReview(thread.Id, thread.Cwd, turnId, Scenario.Text(step, "method"), Scenario.Text(step, "command", "rm -rf build"));
                    break;

                case "notification":
                    SendNotification(step, thread, turnId);
                    break;

                case "waitForSignal":
                {
                    // Goes on when the test creates a file of that name next to the scenario.
                    var signal = Path.Combine(Path.GetDirectoryName(Environment.GetEnvironmentVariable("YAV_FAKE_SCENARIO")) ?? ".", Scenario.Text(step, "name"));
                    while (!File.Exists(signal))
                    {
                        await Task.Delay(20, cancellation);
                    }

                    break;
                }

                case "serverRequest":
                {
                    var parameters = step["params"] is JsonObject given ? (JsonObject)given.DeepClone() : [];
                    if (Scenario.Flag(step, "forThread", true))
                    {
                        parameters["threadId"] = thread.Id;
                        parameters["turnId"] = turnId;
                    }

                    var answer = SendRequest(Scenario.Text(step, "method"), parameters);
                    if (Scenario.Flag(step, "wait"))
                    {
                        await answer.WaitAsync(cancellation);
                    }

                    break;
                }

                case "sleep":
                    await Task.Delay(Scenario.Number(step, "ms", 100), cancellation);
                    break;

                case "hang":
                    await Task.Delay(Timeout.Infinite, cancellation);
                    break;

                case "fail":
                    return ("failed", new JsonObject
                    {
                        ["message"] = Scenario.Text(step, "message", "The turn failed."),
                        ["codexErrorInfo"] = step["codexErrorInfo"]?.DeepClone(),
                        ["additionalDetails"] = null,
                    });

                case "crash":
                    Console.Error.WriteLine("fatal: simulated crash");
                    Environment.Exit(Scenario.Number(step, "exitCode", 3));
                    break;

                case "garbage":
                    _out.WriteRaw(Scenario.Text(step, "text", "this is not json") + "\n");
                    break;

                case "stderr":
                    Console.Error.WriteLine(Scenario.Text(step, "text"));
                    break;

                case "pieces":
                {
                    var payload = common();
                    payload["itemId"] = NextItem();
                    payload["delta"] = Scenario.Text(step, "text");
                    _out.WriteInPieces(
                        new JsonObject { ["method"] = "item/agentMessage/delta", ["params"] = payload, ["emittedAtMs"] = 1 },
                        Scenario.Number(step, "size", 5));
                    break;
                }
            }
        }

        return null;
    }

    private async Task<JsonNode?> AskApprovalAsync(ThreadState thread, string turnId, JsonObject step, CancellationToken cancellation)
    {
        var requestId = NextRequestId();
        var kind = Scenario.Text(step, "kind", "command");
        var method = kind switch
        {
            "file" => "item/fileChange/requestApproval",
            "permissions" => "item/permissions/requestApproval",
            _ => "item/commandExecution/requestApproval",
        };
        var itemId = NextItem();
        var parameters = new JsonObject
        {
            ["threadId"] = thread.Id,
            ["turnId"] = turnId,
            ["itemId"] = itemId,
            ["startedAtMs"] = 1_790_000_000_000,
            ["reason"] = step["reason"]?.DeepClone(),
        };
        JsonObject? change = null;
        if (kind == "permissions")
        {
            // Codex asks for more than its sandbox gives: folders to read or write, or the network.
            parameters["cwd"] = thread.Cwd;
            parameters["permissions"] = step["permissions"]?.DeepClone() ?? new JsonObject();
        }
        else if (kind == "file")
        {
            // Codex announces the change as an item and then asks by the item's id; the request names no file.
            change = new JsonObject { ["type"] = "fileChange", ["id"] = itemId, ["status"] = "inProgress", ["changes"] = Changes(thread, step["announced"] ?? step["changes"]) };
            if (Scenario.Flag(step, "announce", true))
            {
                Item("item/started", thread, turnId, (JsonObject)change.DeepClone());
            }

            if (step["announced"] is not null)
            {
                change["changes"] = Changes(thread, step["changes"]);
                Notify("item/fileChange/patchUpdated", new JsonObject
                {
                    ["threadId"] = thread.Id, ["turnId"] = turnId, ["itemId"] = itemId, ["changes"] = change["changes"]!.DeepClone(),
                });
            }

            parameters["grantRoot"] = step["grantRoot"]?.DeepClone();
        }
        else
        {
            // A command that is not named is sent as null, as Codex does for some requests.
            parameters["command"] = step["command"]?.DeepClone();
            parameters["cwd"] = thread.Cwd;
            if (step["approvalKind"] is JsonValue approvalKind)
            {
                parameters["kind"] = approvalKind.DeepClone();
            }

            if (step["networkHost"] is JsonValue host)
            {
                parameters["networkApprovalContext"] = new JsonObject { ["host"] = host.DeepClone(), ["protocol"] = "https" };
            }
        }

        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId.ToJsonString()] = waiter;
        Notify("thread/status/changed", new JsonObject
        {
            ["threadId"] = thread.Id,
            ["status"] = new JsonObject { ["type"] = "active", ["activeFlags"] = new JsonArray { "waitingOnApproval" } },
        });
        _out.Write(new JsonObject { ["method"] = method, ["id"] = requestId.DeepClone(), ["params"] = parameters });

        var result = await waiter.Task.WaitAsync(cancellation);
        Notify("serverRequest/resolved", new JsonObject { ["threadId"] = thread.Id, ["requestId"] = requestId.DeepClone() });
        if (kind == "permissions")
        {
            // What is granted is what the answer names; nothing named is nothing granted.
            return JsonValue.Create((result as JsonObject)?["permissions"] is JsonObject { Count: > 0 } ? "accept" : "decline");
        }

        var decision = (result as JsonObject)?["decision"];
        if (change is not null)
        {
            var accepted = decision is JsonValue value && value.TryGetValue<string>(out var text) && text is "accept" or "acceptForSession";
            change["status"] = accepted ? "completed" : "declined";
            Item("item/completed", thread, turnId, change);
        }

        return decision;
    }

    /// <summary>
    /// Changes the settings of an open thread, as another client of the same Codex could, and says so with
    /// everything that is in effect afterwards.
    /// </summary>
    private void UpdateSettings(ThreadState thread, JsonObject step) => Notify("thread/settings/updated", SettingsUpdate(thread, step));

    /// <summary>Changes the settings of the thread and returns the notification that says so.</summary>
    private static JsonObject SettingsUpdate(ThreadState thread, JsonObject step)
    {
        thread.Model = Scenario.Text(step, "model", thread.Model);
        thread.Effort = Scenario.Text(step, "effort", thread.Effort ?? string.Empty) is { Length: > 0 } effort ? effort : null;
        thread.ApprovalsReviewer = Scenario.Text(step, "approvalsReviewer", thread.ApprovalsReviewer);
        if (step["sandboxPolicy"] is JsonObject policy)
        {
            thread.SandboxPolicy = (JsonObject)policy.DeepClone();
        }

        var settings = new JsonObject
        {
            ["activePermissionProfile"] = null,
            ["approvalPolicy"] = thread.ApprovalPolicy,
            ["approvalsReviewer"] = thread.ApprovalsReviewer,
            ["collaborationMode"] = new JsonObject
            {
                ["mode"] = "default",
                ["settings"] = new JsonObject { ["model"] = thread.Model, ["reasoning_effort"] = thread.Effort, ["developer_instructions"] = null },
            },
            ["cwd"] = thread.Cwd,
            ["disabledPluginIds"] = new JsonArray(),
            ["effort"] = thread.Effort,
            ["model"] = thread.Model,
            ["modelProvider"] = "openai",
            ["personality"] = null,
            ["sandboxPolicy"] = thread.SandboxPolicy.DeepClone(),
            ["serviceTier"] = thread.ServiceTier,
            ["summary"] = null,
        };

        // The schema requires the reviewer; a notification without it stands for a version that does not name it.
        if (Scenario.Flag(step, "withoutApprovalsReviewer"))
        {
            settings.Remove("approvalsReviewer");
        }

        return new JsonObject { ["threadId"] = thread.Id, ["threadSettings"] = settings };
    }

    /// <summary>
    /// Codex's own reviewer takes part in a decision about access, in the shape of the protocol's notifications:
    /// a review starts or ends, a strict review is required, or the reviewer warns.
    /// </summary>
    private void AutoReview(string threadId, string cwd, string turnId, string method, string command)
    {
        JsonObject Review(string status) => new() { ["status"] = status, ["riskLevel"] = status == "inProgress" ? null : "low", ["userAuthorization"] = null, ["rationale"] = null };
        var action = new JsonObject { ["type"] = "command", ["command"] = command, ["cwd"] = cwd, ["source"] = "shell" };
        var parameters = method switch
        {
            "item/autoApprovalReview/started" => new JsonObject
            {
                ["threadId"] = threadId, ["turnId"] = turnId, ["reviewId"] = "review-1", ["startedAtMs"] = 1_790_000_000_000,
                ["targetItemId"] = NextItem(), ["review"] = Review("inProgress"), ["action"] = action,
            },
            "item/autoApprovalReview/completed" => new JsonObject
            {
                ["threadId"] = threadId, ["turnId"] = turnId, ["reviewId"] = "review-1", ["startedAtMs"] = 1_790_000_000_000,
                ["completedAtMs"] = 1_790_000_001_000, ["decisionSource"] = "agent", ["targetItemId"] = NextItem(),
                ["review"] = Review("approved"), ["action"] = action,
            },
            "autoApprovalReview/strictReviewRequired" => new JsonObject { ["threadId"] = threadId, ["turnId"] = turnId, ["startedAtMs"] = 1_790_000_000_000 },
            _ => new JsonObject { ["threadId"] = threadId, ["message"] = "The reviewer found the command risky." },
        };

        Notify(method, parameters);
    }

    /// <summary>
    /// The model starts a sub-agent: Codex announces a thread whose parent is this one, and the sub-agent works
    /// in it. Once resumed, the same sub-agent is announced again. The sub-agent can ask for approval in its own
    /// thread, and waits for each answer; Codex's own reviewer can take part in its decisions.
    /// </summary>
    private async Task SubAgentAsync(ThreadState parent, JsonObject step, CancellationToken cancellation)
    {
        var childId = $"thr-sub-{Interlocked.Increment(ref _threadCounter)}-{Environment.ProcessId}";
        var child = ThreadJson(childId, parent.Cwd, parent.Model, parent.Effort, true, []);
        child["parentThreadId"] = parent.Id;
        child["agentNickname"] = "Scout";
        child["agentRole"] = "explorer";
        child["source"] = new JsonObject
        {
            ["subAgent"] = new JsonObject
            {
                ["thread_spawn"] = new JsonObject
                {
                    ["parent_thread_id"] = parent.Id, ["depth"] = 1, ["agent_nickname"] = "Scout", ["agent_role"] = "explorer", ["agent_path"] = null,
                },
            },
        };

        for (var announced = 0; announced < (Scenario.Flag(step, "resumed") ? 2 : 1); announced++)
        {
            Notify("thread/started", new JsonObject { ["thread"] = child.DeepClone() });
            var turnId = $"turn-{Interlocked.Increment(ref _turnCounter)}";
            var itemId = NextItem();
            var message = new JsonObject { ["type"] = "agentMessage", ["id"] = itemId, ["text"] = Scenario.Text(step, "text"), ["phase"] = "final_answer" };
            Notify("turn/started", new JsonObject { ["threadId"] = childId, ["turn"] = TurnJson(turnId, "inProgress", null) });
            for (var asked = 0; asked < Scenario.Number(step, "asks"); asked++)
            {
                await SendRequest("item/commandExecution/requestApproval", new JsonObject
                {
                    ["threadId"] = childId, ["turnId"] = turnId, ["itemId"] = NextItem(), ["startedAtMs"] = 1_790_000_000_000,
                    ["command"] = "curl https://example.invalid/data", ["cwd"] = parent.Cwd, ["reason"] = null,
                }).WaitAsync(cancellation);
            }

            if (Scenario.Text(step, "autoReview") is { Length: > 0 } review)
            {
                AutoReview(childId, parent.Cwd, turnId, review, "curl https://example.invalid/data");
            }

            Notify("item/started", new JsonObject { ["threadId"] = childId, ["turnId"] = turnId, ["item"] = message.DeepClone(), ["startedAtMs"] = 1_790_000_000_000 });
            Notify("item/completed", new JsonObject { ["threadId"] = childId, ["turnId"] = turnId, ["item"] = message, ["completedAtMs"] = 1_790_000_000_000 });

            // The schema requires both breakdowns; the last one is that of the sub-agent's last request.
            var usage = new JsonObject { ["totalTokens"] = 900, ["inputTokens"] = 800, ["cachedInputTokens"] = 0, ["outputTokens"] = 100, ["reasoningOutputTokens"] = 0 };
            Notify("thread/tokenUsage/updated", new JsonObject
            {
                ["threadId"] = childId,
                ["turnId"] = turnId,
                ["tokenUsage"] = new JsonObject { ["total"] = usage.DeepClone(), ["last"] = usage, ["modelContextWindow"] = 400_000 },
            });
            Notify("turn/completed", new JsonObject { ["threadId"] = childId, ["turn"] = TurnJson(turnId, "completed", null) });
        }
    }

    /// <summary>The changes of a file step in the form of the protocol, with paths made full against the working directory.</summary>
    private static JsonArray Changes(ThreadState thread, JsonNode? given)
    {
        var changes = new JsonArray();
        foreach (var node in given as JsonArray ?? [])
        {
            var moved = Scenario.Text(node, "movedTo");
            changes.Add(new JsonObject
            {
                ["path"] = Path.GetFullPath(Path.Combine(thread.Cwd, Scenario.Text(node, "path"))),
                ["kind"] = new JsonObject
                {
                    ["type"] = Scenario.Text(node, "kind", "update"),
                    ["move_path"] = moved.Length == 0 ? null : Path.GetFullPath(Path.Combine(thread.Cwd, moved)),
                },
                ["diff"] = "",
            });
        }

        return changes;
    }

    private void Message(ThreadState thread, string turnId, string text, string phase)
    {
        var itemId = NextItem();
        Item("item/started", thread, turnId, new JsonObject { ["type"] = "agentMessage", ["id"] = itemId, ["text"] = "", ["phase"] = phase });
        foreach (var chunk in Chunks(text, 40))
        {
            Notify("item/agentMessage/delta", new JsonObject
            {
                ["threadId"] = thread.Id, ["turnId"] = turnId, ["itemId"] = itemId, ["delta"] = chunk,
            });
        }

        Item("item/completed", thread, turnId, new JsonObject { ["type"] = "agentMessage", ["id"] = itemId, ["text"] = text, ["phase"] = phase });
    }

    private static IEnumerable<string> Chunks(string text, int size)
    {
        for (var offset = 0; offset < text.Length; offset += size)
        {
            var length = Math.Min(size, text.Length - offset);
            // Never split a surrogate pair.
            if (offset + length < text.Length && char.IsHighSurrogate(text[offset + length - 1]))
            {
                length++;
            }

            yield return text.Substring(offset, length);
            offset += length - size;
        }
    }

    private void Item(string method, ThreadState thread, string turnId, JsonObject item)
    {
        var payload = new JsonObject { ["threadId"] = thread.Id, ["turnId"] = turnId, ["item"] = item };
        payload[method == "item/started" ? "startedAtMs" : "completedAtMs"] = 1_790_000_000_000;
        Notify(method, payload);
    }

    private string NextItem() => $"item-{Interlocked.Increment(ref _itemCounter)}";

    private void Notify(string method, JsonObject parameters) => _out.Write(NotificationMessage(method, parameters));

    /// <summary>A notification step: its method and parameters as given, with the thread and turn filled in when it is for the thread.</summary>
    private void SendNotification(JsonObject step, ThreadState? thread, string? turnId) =>
        Notify(Scenario.Text(step, "method"), NotificationParameters(step, thread, turnId));

    private static JsonObject NotificationParameters(JsonObject step, ThreadState? thread, string? turnId)
    {
        var parameters = step["params"] is JsonObject given ? (JsonObject)given.DeepClone() : [];
        if (Scenario.Flag(step, "forThread") && thread is not null)
        {
            parameters["threadId"] = thread.Id;
            if (turnId is not null)
            {
                parameters["turnId"] = turnId;
            }
        }

        return parameters;
    }

    private static JsonObject NotificationMessage(string method, JsonObject parameters) =>
        new() { ["method"] = method, ["params"] = parameters, ["emittedAtMs"] = 1_790_000_000_000 };

    /// <summary>
    /// The id of the next request of the server. Codex 0.158 numbers its requests from 0; the protocol also allows
    /// a text, which a scenario can ask for.
    /// </summary>
    private JsonNode NextRequestId()
    {
        var number = Interlocked.Increment(ref _serverRequestCounter);
        return Scenario.Flag(_scenario.Codex, "stringRequestIds") ? JsonValue.Create($"srv-{number}") : JsonValue.Create(number - 1);
    }

    /// <summary>Sends a request to the client. The task ends with its result, or with null when it answered with an error.</summary>
    private Task<JsonNode?> SendRequest(string method, JsonObject parameters)
    {
        var id = NextRequestId();
        var waiter = new TaskCompletionSource<JsonNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id.ToJsonString()] = waiter;
        _out.Write(new JsonObject { ["method"] = method, ["id"] = id, ["params"] = parameters });
        return waiter.Task;
    }

    private void Reply(JsonNode id, JsonObject result) => _out.Write(new JsonObject { ["id"] = id, ["result"] = result });

    private void Error(JsonNode id, int code, string message) =>
        _out.Write(new JsonObject { ["id"] = id, ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });
}
