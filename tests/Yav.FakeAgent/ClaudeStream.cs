using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;

namespace Yav.FakeAgent;

/// <summary>
/// Speaks the Claude Code headless format: "stream-json" messages on standard output, user messages and
/// control requests on standard input. The process serves several turns until its input closes.
///
/// It says what Claude Code 2.1.284 was seen to say, which is not always what the published types suggest:
/// the message at the start of a turn carries no effort (the answer to "get_settings" does), its tools are
/// in alphabetical order, and with a schema for the result there is one tool more, "StructuredOutput".
/// </summary>
internal sealed partial class ClaudeStream
{
    private const int StandardInput = -10;

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial nint GetStdHandle(int which);

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    private readonly Scenario _scenario = Scenario.Load();
    private readonly JsonLineWriter _out = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<JsonObject?>> _pending = new();
    private readonly string _sessionId;
    private readonly string _model;
    private readonly string? _requestedEffort;
    private readonly string _permissionMode;
    private readonly string _role;
    private readonly string _cwd = Environment.CurrentDirectory;
    private readonly bool _hostAnswersPrompts;
    private readonly bool _hasSchema;

    /// <summary>Started for a conversation, and not only to be asked something.</summary>
    private readonly bool _conversation;
    private readonly List<string> _tools;
    private CancellationTokenSource? _activeTurn;
    private Task _turn = Task.CompletedTask;
    private int _requestCounter;
    private int _settingsAsked;
    private int _turns;
    private decimal _cost;

    private ClaudeStream(string[] args)
    {
        string? sessionId = null;
        string? resume = null;
        var model = "opus";
        string? effort = null;
        var mode = "default";
        string? tools = null;
        var promptTool = (string?)null;
        var prompts = "host";
        var schema = false;

        for (var i = 0; i < args.Length; i++)
        {
            var argument = args[i];
            string Value(string name) => argument.StartsWith(name + "=", StringComparison.Ordinal) ? argument[(name.Length + 1)..] : args[++i];
            switch (argument.Split('=')[0])
            {
                case "--session-id": sessionId = Value("--session-id"); break;
                case "--resume" or "-r": resume = Value("--resume"); break;
                case "--model": model = Value("--model"); break;
                case "--effort": effort = Value("--effort"); break;
                case "--permission-mode": mode = Value("--permission-mode"); break;
                case "--tools": tools = Value("--tools"); break;
                case "--permission-prompt-tool": promptTool = Value("--permission-prompt-tool"); break;
                case "--permission-prompts": prompts = Value("--permission-prompts"); break;
                case "--json-schema": schema = true; _ = Value("--json-schema"); break;
                case "--output-format" or "--input-format" or "--append-system-prompt" or "--append-system-prompt-file"
                    or "--setting-sources" or "--settings" or "--add-dir" or "--disallowedTools" or "--allowedTools"
                    or "--max-budget-usd" or "--mcp-config":
                    _ = Value(argument.Split('=')[0]);
                    break;
            }
        }

        _conversation = resume is not null || sessionId is not null;
        _sessionId = resume ?? sessionId ?? Guid.NewGuid().ToString();
        _model =_scenario.Claude["effectiveModel"] is JsonValue forced ? forced.GetValue<string>() : model;
        _requestedEffort = effort;
        _permissionMode = mode;
        _tools = tools is null
            ? ["Bash", "Edit", "Write", "Read", "Glob", "Grep"]
            : tools.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Order(StringComparer.Ordinal).ToList();
        if (schema)
        {
            // The tool the model hands the structured result back with.
            _tools.Add("StructuredOutput");
        }

        _role = _tools.Contains("Edit") || _tools.Contains("Write") ? "implementer" : "reviewer";
        if (_scenario.Claude["additionalTools"] is JsonArray additional)
        {
            // A version that gives a conversation tools nobody asked for.
            _tools.AddRange(additional.Select(tool => tool!.GetValue<string>()));
        }

        _hostAnswersPrompts = promptTool == "stdio" && prompts != "none";
        _hasSchema = schema;

        _scenario.Log("claude.invocation", new JsonObject
        {
            ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["cwd"] = _cwd,
            ["role"] = _role,
            ["apiKeyPresent"] = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")),
            ["apiKeyValue"] = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"),
        });
    }

    public static async Task<int> RunAsync(string[] args)
    {
        var scenario = Scenario.Load();
        if (Scenario.Text(scenario.Claude, "startupFailure") is { Length: > 0 } failure)
        {
            Console.Error.WriteLine(failure);
            return 1;
        }

        // The same for a conversation only: what is asked before any conversation, such as the models, is answered.
        if (Scenario.Text(scenario.Claude, "conversationFailure") is { Length: > 0 } later
            && args.Any(argument => argument is "--session-id" or "--resume" or "-r"))
        {
            // Said, so that a test can count how often a conversation was started.
            scenario.Log("claude.invocation", new JsonObject
            {
                ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
                ["endedAtOnce"] = true,
            });
            Console.Error.WriteLine(later);
            return 1;
        }

        var stream = new ClaudeStream(args);
        return await stream.LoopAsync();
    }

    /// <summary>
    /// What is sent with the next request. Null for a model that takes no effort, whatever was asked for.
    /// "effectiveEfforts" gives one value for each time the question is asked; the last one is repeated.
    /// </summary>
    private string? EffectiveEffort
    {
        get
        {
            if (Scenario.Flag(_scenario.Claude, "modelTakesNoEffort"))
            {
                return null;
            }

            if (_scenario.Claude["effectiveEfforts"] is JsonArray { Count: > 0 } byQuestion)
            {
                return byQuestion[Math.Min(Math.Max(_settingsAsked, 1), byQuestion.Count) - 1]?.GetValue<string>();
            }

            return _scenario.Claude["effectiveEffort"] is JsonValue forced ? forced.GetValue<string>() : _requestedEffort ?? "medium";
        }
    }

    /// <summary>The answer to "get_settings", with what a scenario makes of it.</summary>
    private void AnswerSettings(string requestId)
    {
        _settingsAsked++;
        JsonObject Applied(string? effort)
        {
            var applied = new JsonObject
            {
                ["model"] = _model,
                ["advisor"] = null,
                ["ultracode"] = false,
                ["ultracodeRequested"] = false,
                ["ultracodeAvailable"] = true,
            };
            switch (Scenario.Text(_scenario.Claude, "appliedEffort"))
            {
                // A version that names the field differently, and one that answers with a budget in place of a level.
                case "missing": break;
                case "number": applied["effort"] = 3; break;
                default: applied["effort"] = effort; break;
            }

            return applied;
        }

        if (Scenario.Flag(_scenario.Claude, "strayAnswer"))
        {
            // An answer to something somebody else asked, which arrives first.
            Success("asked-by-somebody-else", new JsonObject { ["effective"] = new JsonObject(), ["sources"] = new JsonArray(), ["applied"] = Applied("low") });
        }

        var sources = new JsonArray();
        foreach (var source in _scenario.Claude["settingsSources"] as JsonArray ?? [])
        {
            sources.Add(new JsonObject { ["source"] = source?.GetValue<string>(), ["settings"] = new JsonObject() });
        }

        Success(requestId, new JsonObject { ["effective"] = new JsonObject(), ["sources"] = sources, ["applied"] = Applied(EffectiveEffort) });
    }

    private async Task<int> LoopAsync()
    {
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
                Console.Error.WriteLine("invalid input line");
                continue;
            }

            _scenario.Log("claude.input", message);
            switch (Scenario.Text(message, "type"))
            {
                case "control_request":
                    HandleControl(message);
                    break;

                case "control_response":
                {
                    var response = message["response"] as JsonObject;
                    var requestId = Scenario.Text(response, "request_id");
                    if (_pending.TryRemove(requestId, out var waiter))
                    {
                        waiter.TrySetResult(Scenario.Text(response, "subtype") == "success" ? response?["response"] as JsonObject : null);
                    }

                    break;
                }

                case "user":
                    await _turn;
                    _activeTurn = new CancellationTokenSource();
                    var token = _activeTurn.Token;
                    _turn = Task.Run(() => PlayAsync(token));
                    if (Scenario.Flag(_scenario.Claude, "deafAfterPrompt"))
                    {
                        // Nothing is read any more, so that the input can be closed while the turn is played.
                        await _turn;
                        return 0;
                    }

                    break;
            }
        }

        _activeTurn?.Cancel();
        try
        {
            await _turn;
        }
        catch (OperationCanceledException)
        {
        }

        return 0;
    }

    private void HandleControl(JsonObject message)
    {
        var requestId = Scenario.Text(message, "request_id");
        var request = message["request"] as JsonObject ?? [];
        switch (Scenario.Text(request, "subtype"))
        {
            // In a conversation only: a version that does not say which account the conversation works with.
            case "initialize" when _conversation && Scenario.Text(_scenario.Claude, "accountRequest") == "silent":
                break;

            case "initialize" when !_conversation || Scenario.Text(_scenario.Claude, "accountRequest") != "unsupported":
            {
                var answer = new JsonObject
                {
                    ["commands"] = new JsonArray(),
                    ["agents"] = new JsonArray(),
                    ["output_style"] = "default",
                    ["available_output_styles"] = new JsonArray { "default" },
                    ["models"] = Models(),
                    ["fast_mode_state"] = "off",
                };
                if (!_conversation || Scenario.Text(_scenario.Claude, "accountRequest") != "withoutAccount")
                {
                    answer["account"] = Account();
                    if (_conversation)
                    {
                        _scenario.Log("claude.output.account", Account());
                    }
                }

                Success(requestId, answer);
                if (_conversation && Scenario.Flag(_scenario.Claude, "endAfterQuestions"))
                {
                    // It said what it works with, and ends before it is given the prompt.
                    Console.Error.WriteLine("ended after it was asked");
                    Environment.Exit(3);
                }

                break;
            }

            case "interrupt":
                _activeTurn?.Cancel();
                Success(requestId, new JsonObject { ["still_queued"] = new JsonArray() });
                break;

            // "unsupported": a version that does not know the request. "silent": one that never answers it.
            case "get_settings" when Scenario.Text(_scenario.Claude, "settingsRequest") == "silent":
                break;

            case "get_settings" when Scenario.Text(_scenario.Claude, "settingsRequest") != "unsupported":
                AnswerSettings(requestId);
                break;

            default:
                _out.Write(new JsonObject
                {
                    ["type"] = "control_response",
                    ["response"] = new JsonObject
                    {
                        ["subtype"] = "error", ["request_id"] = requestId,
                        ["error"] = "Unsupported control request subtype: " + Scenario.Text(request, "subtype"),
                    },
                });
                break;
        }
    }

    private JsonArray Models()
    {
        if (_scenario.Claude["models"] is JsonArray configured)
        {
            var result = new JsonArray();
            foreach (var model in configured)
            {
                result.Add(new JsonObject
                {
                    ["value"] = Scenario.Text(model, "value"),
                    ["resolvedModel"] = (model as JsonObject)?["resolvedModel"]?.DeepClone(),
                    ["displayName"] = Scenario.Text(model, "displayName", Scenario.Text(model, "value")),
                    ["description"] = "A model.",
                    ["supportsEffort"] = ((model as JsonObject)?["efforts"] as JsonArray)?.Count > 0,
                    ["supportedEffortLevels"] = (model as JsonObject)?["efforts"]?.DeepClone() ?? new JsonArray(),
                    ["supportsFastMode"] = Scenario.Flag(model, "fast"),
                });
            }

            return result;
        }

        return
        [
            new JsonObject
            {
                ["value"] = "opus", ["resolvedModel"] = "claude-opus-5-5", ["displayName"] = "Opus", ["description"] = "Complex reasoning.",
                ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray { "low", "medium", "high", "xhigh", "max" },
                ["supportsFastMode"] = true,
            },
            new JsonObject
            {
                ["value"] = "sonnet", ["resolvedModel"] = "claude-sonnet-5-5", ["displayName"] = "Sonnet", ["description"] = "Daily coding.",
                ["supportsEffort"] = true, ["supportedEffortLevels"] = new JsonArray { "low", "medium", "high", "xhigh", "max" },
                ["supportsFastMode"] = false,
            },
        ];
    }

    private static string ApiKeySource() =>
        string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY")) ? "none" : "ANTHROPIC_API_KEY";

    /// <summary>
    /// The account, as Claude Code 2.1.284 was seen to say it: a cloud provider is named alone, a key is named
    /// only when there is one, and the plan, the address and the organization only when the subscription is
    /// what is used. "sessionAccount" is what a conversation says when that is not what was said before the run.
    /// </summary>
    private JsonObject Account()
    {
        if (_conversation && _scenario.Claude["sessionAccount"] is JsonObject said)
        {
            return (JsonObject)said.DeepClone();
        }

        var auth = _scenario.Claude["auth"];
        var provider = Scenario.Text(auth, "apiProvider", "firstParty");
        if (provider != "firstParty")
        {
            return new JsonObject { ["apiProvider"] = provider };
        }

        var key = ApiKeySource() != "none" ? ApiKeySource() : Scenario.Text(auth, "apiKeySource", "none");
        if (key is { Length: > 0 } and not "none")
        {
            return new JsonObject { ["apiKeySource"] = key, ["apiProvider"] = provider, ["tokenSource"] = "claude.ai" };
        }

        var plan = auth is null ? "max" : Scenario.Text(auth, "subscriptionType");
        if (plan.Length == 0)
        {
            return new JsonObject { ["apiProvider"] = provider, ["tokenSource"] = Scenario.Text(auth, "authMethod", "claude.ai") };
        }

        return new JsonObject
        {
            ["apiProvider"] = provider,
            ["email"] = "user@example.invalid",
            ["organization"] = "Example",

            // Where the sign-in says "max", this says "Claude Max".
            ["subscriptionType"] = "Claude " + char.ToUpperInvariant(plan[0]) + plan[1..],
        };
    }

    private async Task PlayAsync(CancellationToken cancellation)
    {
        _turns++;
        _out.Write(new JsonObject
        {
            ["type"] = "system",
            ["subtype"] = "init",
            ["apiKeySource"] = ApiKeySource(),
            ["claude_code_version"] = Scenario.Text(_scenario.Claude, "version", "2.1.284 (Claude Code)").Split(' ')[0],
            ["cwd"] = _cwd,
            ["tools"] = new JsonArray(_tools.Select(t => (JsonNode?)JsonValue.Create(t)).ToArray()),
            ["mcp_servers"] = new JsonArray(),
            ["model"] = _model,
            ["permissionMode"] = _permissionMode,
            ["slash_commands"] = new JsonArray(),
            ["output_style"] = "default",
            ["skills"] = new JsonArray(),
            ["plugins"] = new JsonArray(),
            ["fast_mode_state"] = Scenario.Text(_scenario.Claude, "fastModeState", "off"),
            ["per_turn_effort_active"] = true,
            ["capabilities"] = new JsonArray { "interrupt_receipt_v1" },
            ["uuid"] = Guid.NewGuid().ToString(),
            ["session_id"] = _sessionId,
        });

        var subtype = "success";
        var isError = false;
        string result = string.Empty;
        JsonNode? structured = null;
        var denials = new JsonArray();
        var interrupted = false;
        var errors = new JsonArray();

        try
        {
            foreach (var node in _scenario.NextTurn(_role))
            {
                cancellation.ThrowIfCancellationRequested();
                if (node is not JsonObject step)
                {
                    continue;
                }

                switch (Scenario.Text(step, "type"))
                {
                    case "message":
                        result = Scenario.Text(step, "text");
                        Assistant(new JsonObject { ["type"] = "text", ["text"] = result });
                        break;

                    case "review":
                    {
                        var output = Scenario.ReviewOutput(step);
                        result = output.ToJsonString();
                        structured = _hasSchema ? output : null;
                        Assistant(new JsonObject { ["type"] = "text", ["text"] = result });
                        if (_hasSchema)
                        {
                            // With a schema, the result is handed back through a tool of its own.
                            var toolId = "toolu_" + Guid.NewGuid().ToString("N")[..12];
                            Assistant(new JsonObject { ["type"] = "tool_use", ["id"] = toolId, ["name"] = "StructuredOutput", ["input"] = output.DeepClone() });
                            ToolResult(toolId, "Structured output provided successfully", false);
                        }

                        break;
                    }

                    case "tool":
                    {
                        var toolId = "toolu_" + Guid.NewGuid().ToString("N")[..12];
                        var input = new JsonObject();
                        foreach (var (name, value) in step["input"] as JsonObject ?? [])
                        {
                            input[name] = value is JsonValue text && text.TryGetValue<string>(out var written)
                                ? written.Replace("{cwd}", _cwd, StringComparison.Ordinal)
                                : value?.DeepClone();
                        }

                        Assistant(new JsonObject { ["type"] = "tool_use", ["id"] = toolId, ["name"] = Scenario.Text(step, "name"), ["input"] = input });
                        ToolResult(toolId, Scenario.Flag(step, "fails") ? "The tool could not do that." : "ok", Scenario.Flag(step, "fails"));
                        break;
                    }

                    case "rawFinal":
                        result = Scenario.Text(step, "text");
                        Assistant(new JsonObject { ["type"] = "text", ["text"] = result });
                        break;

                    case "reasoning":
                        Assistant(new JsonObject { ["type"] = "thinking", ["thinking"] = Scenario.Text(step, "text"), ["signature"] = "sig" });
                        break;

                    case "command":
                    {
                        var toolId = "toolu_" + Guid.NewGuid().ToString("N")[..12];
                        Assistant(new JsonObject
                        {
                            ["type"] = "tool_use", ["id"] = toolId, ["name"] = "Bash",
                            ["input"] = new JsonObject { ["command"] = Scenario.Text(step, "command") },
                        });
                        await Task.Delay(Scenario.Number(step, "runsMs", 0), cancellation);
                        ToolResult(toolId, Scenario.Text(step, "output"), Scenario.Number(step, "exitCode") != 0);
                        break;
                    }

                    case "write":
                    case "delete":
                    {
                        var toolId = "toolu_" + Guid.NewGuid().ToString("N")[..12];
                        var path = Path.GetFullPath(Path.Combine(_cwd, Scenario.Text(step, "path")));
                        Assistant(new JsonObject
                        {
                            ["type"] = "tool_use", ["id"] = toolId, ["name"] = Scenario.Text(step, "type") == "delete" ? "Bash" : "Write",
                            ["input"] = new JsonObject { ["file_path"] = path, ["content"] = Scenario.Text(step, "content") },
                        });
                        if (!_tools.Contains("Write") && !_tools.Contains("Edit"))
                        {
                            denials.Add(new JsonObject { ["tool_name"] = "Write", ["tool_use_id"] = toolId, ["tool_input"] = new JsonObject { ["file_path"] = path } });
                            ToolResult(toolId, "The Write tool is not available.", true);
                            break;
                        }

                        Scenario.ApplyFileStep(step, _cwd);
                        ToolResult(toolId, "ok", false);
                        break;
                    }

                    case "approval":
                    {
                        var toolId = "toolu_" + Guid.NewGuid().ToString("N")[..12];
                        var command = Scenario.Text(step, "command");

                        // A shell with its command, unless the step names another tool and what it is given.
                        var tool = Scenario.Text(step, "tool", "Bash");
                        var given = step["input"] is JsonObject input ? (JsonObject)input.DeepClone() : new JsonObject { ["command"] = command };
                        Assistant(new JsonObject { ["type"] = "tool_use", ["id"] = toolId, ["name"] = tool, ["input"] = given.DeepClone() });
                        var allowed = false;
                        if (_hostAnswersPrompts)
                        {
                            var answer = await AskAsync(toolId, tool, given, step, cancellation);
                            allowed = Scenario.Text(answer, "behavior") == "allow";
                            if (!allowed && Scenario.Flag(answer, "interrupt"))
                            {
                                throw new OperationCanceledException();
                            }
                        }

                        if (!allowed)
                        {
                            denials.Add(new JsonObject { ["tool_name"] = tool, ["tool_use_id"] = toolId, ["tool_input"] = given.DeepClone() });
                            _out.Write(new JsonObject
                            {
                                ["type"] = "system", ["subtype"] = "permission_denied", ["tool_name"] = tool, ["tool_use_id"] = toolId,
                                ["message"] = $"Permission to use {tool} has been denied.",
                                ["uuid"] = Guid.NewGuid().ToString(), ["session_id"] = _sessionId,
                            });
                        }

                        ToolResult(toolId, allowed ? "done" : "Permission denied.", !allowed);
                        if (step[allowed ? "onAccept" : "onDecline"] is JsonArray nested)
                        {
                            foreach (var inner in nested)
                            {
                                if (inner is JsonObject innerStep && Scenario.Text(innerStep, "type") is "write" or "delete")
                                {
                                    Scenario.ApplyFileStep(innerStep, _cwd);
                                }
                                else if (inner is JsonObject text && Scenario.Text(text, "type") == "message")
                                {
                                    result = Scenario.Text(text, "text");
                                    Assistant(new JsonObject { ["type"] = "text", ["text"] = result });
                                }
                            }
                        }

                        break;
                    }

                    case "askAndLeave":
                    {
                        // Asks for permission and does not read the answer: its input is closed before it asks.
                        var toolId = "toolu_" + Guid.NewGuid().ToString("N")[..12];
                        CloseHandle(GetStdHandle(StandardInput));
                        _out.Write(new JsonObject
                        {
                            ["type"] = "control_request",
                            ["request_id"] = $"req-{Interlocked.Increment(ref _requestCounter)}",
                            ["request"] = new JsonObject
                            {
                                ["subtype"] = "can_use_tool", ["tool_name"] = "Bash", ["tool_use_id"] = toolId, ["title"] = "Run command",
                                ["input"] = new JsonObject { ["command"] = Scenario.Text(step, "command", "whoami") },
                            },
                        });
                        await Task.Delay(Scenario.Number(step, "ms", 400), CancellationToken.None);
                        Console.Error.WriteLine("gone");
                        Environment.Exit(Scenario.Number(step, "exitCode", 3));
                        break;
                    }

                    case "rateLimit":
                        _out.Write(new JsonObject
                        {
                            ["type"] = "rate_limit_event",
                            ["rate_limit_info"] = new JsonObject
                            {
                                ["status"] = Scenario.Text(step, "status", "allowed"),
                                ["rateLimitType"] = Scenario.Text(step, "window", "five_hour"),
                                ["utilization"] = step["utilization"]?.DeepClone(),
                                ["resetsAt"] = 1_790_000_000,
                            },
                            ["uuid"] = Guid.NewGuid().ToString(), ["session_id"] = _sessionId,
                        });
                        break;

                    case "retry":
                        _out.Write(new JsonObject
                        {
                            ["type"] = "system", ["subtype"] = "api_retry", ["attempt"] = Scenario.Number(step, "attempt", 1),
                            ["max_retries"] = 10, ["retry_delay_ms"] = 500, ["error_status"] = 529, ["error"] = Scenario.Text(step, "error", "overloaded"),
                            ["uuid"] = Guid.NewGuid().ToString(), ["session_id"] = _sessionId,
                        });
                        break;

                    case "unknownMessage":
                        _out.Write(new JsonObject { ["type"] = "future_message_kind", ["anything"] = 42, ["session_id"] = _sessionId });
                        break;

                    case "sleep":
                        await Task.Delay(Scenario.Number(step, "ms", 100), cancellation);
                        break;

                    case "hang":
                        await Task.Delay(Timeout.Infinite, cancellation);
                        break;

                    case "fail":
                        subtype = "success";
                        isError = true;
                        result = Scenario.Text(step, "message", "API Error");
                        if (Scenario.Text(step, "subtype") is { Length: > 0 } explicitSubtype)
                        {
                            subtype = explicitSubtype;
                            errors.Add(result);
                        }

                        goto done;

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
                }
            }
        }
        catch (OperationCanceledException)
        {
            subtype = "error_during_execution";
            isError = true;
            errors.Add("interrupted");
            interrupted = true;
        }

    done:
        _cost += 0.25m;
        var usage = _scenario.Claude["usage"] as JsonObject ?? new JsonObject
        {
            ["input_tokens"] = 50, ["cache_creation_input_tokens"] = 100, ["cache_read_input_tokens"] = 900, ["output_tokens"] = 300,
        };
        var message = new JsonObject
        {
            ["type"] = "result",
            ["subtype"] = subtype,
            ["duration_ms"] = 1200,
            ["duration_api_ms"] = 1000,
            ["is_error"] = isError,
            ["num_turns"] = _turns,
            ["stop_reason"] = "end_turn",
            ["total_cost_usd"] = (double)_cost,
            ["usage"] = usage.DeepClone(),
            ["modelUsage"] = new JsonObject
            {
                [_model] = new JsonObject
                {
                    ["inputTokens"] = 50 * _turns, ["outputTokens"] = 300 * _turns, ["thinkingTokens"] = 120 * _turns,
                    ["cacheReadInputTokens"] = 900 * _turns, ["cacheCreationInputTokens"] = 100 * _turns,
                    ["webSearchRequests"] = 0, ["costUSD"] = (double)_cost, ["contextWindow"] = 1_000_000, ["maxOutputTokens"] = 64_000,
                    ["provider"] = "firstParty",
                },
            },
            ["permission_denials"] = denials,

            // How the turn ended, as the program says it. "aborted_streaming" is what an interrupt gives.
            ["terminal_reason"] = interrupted ? "aborted_streaming" : "completed",
            ["fast_mode_state"] = Scenario.Text(_scenario.Claude, "fastModeState", "off"),
            ["uuid"] = Guid.NewGuid().ToString(),
            ["session_id"] = _sessionId,
        };
        if (subtype == "success")
        {
            message["result"] = result;
            if (structured is not null)
            {
                message["structured_output"] = structured.DeepClone();
            }
        }
        else
        {
            message["errors"] = errors;
        }

        _out.Write(message);
    }

    private async Task<JsonObject?> AskAsync(string toolId, string tool, JsonObject given, JsonObject step, CancellationToken cancellation)
    {
        var requestId = $"req-{Interlocked.Increment(ref _requestCounter)}";
        var waiter = new TaskCompletionSource<JsonObject?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = waiter;
        _out.Write(new JsonObject
        {
            ["type"] = "control_request",
            ["request_id"] = requestId,
            ["request"] = new JsonObject
            {
                ["subtype"] = "can_use_tool",
                ["tool_name"] = tool,
                ["input"] = given.DeepClone(),
                ["tool_use_id"] = toolId,
                ["decision_reason"] = step["reason"]?.DeepClone(),
                ["title"] = tool == "Bash" ? "Run command" : null,
                ["default_to_no"] = Scenario.Text(step, "marked") == "defaultToNo" ? true : null,
                ["requires_user_interaction"] = Scenario.Text(step, "marked") == "needsTheUser" ? true : null,
            },
        });
        return await waiter.Task.WaitAsync(cancellation);
    }

    private void Assistant(JsonObject block) => _out.Write(new JsonObject
    {
        ["type"] = "assistant",
        ["message"] = new JsonObject
        {
            ["id"] = "msg_" + Guid.NewGuid().ToString("N")[..12],
            ["type"] = "message",
            ["role"] = "assistant",
            ["model"] = _model,
            ["content"] = new JsonArray { block },
            ["stop_reason"] = null,
            ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 1 },
        },
        ["parent_tool_use_id"] = null,
        ["uuid"] = Guid.NewGuid().ToString(),
        ["session_id"] = _sessionId,
    });

    private void ToolResult(string toolId, string content, bool isError) => _out.Write(new JsonObject
    {
        ["type"] = "user",
        ["message"] = new JsonObject
        {
            ["role"] = "user",
            ["content"] = new JsonArray
            {
                new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = toolId, ["content"] = content, ["is_error"] = isError },
            },
        },
        ["parent_tool_use_id"] = null,
        ["uuid"] = Guid.NewGuid().ToString(),
        ["session_id"] = _sessionId,
    });

    private void Success(string requestId, JsonObject payload) => _out.Write(new JsonObject
    {
        ["type"] = "control_response",
        ["response"] = new JsonObject { ["subtype"] = "success", ["request_id"] = requestId, ["response"] = payload },
    });
}
