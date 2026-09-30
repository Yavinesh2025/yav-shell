using System.Text;
using System.Text.Json.Nodes;

namespace Yav.FakeAgent;

/// <summary>
/// Speaks the "codex exec --json" format: the prompt arrives on standard input, events leave as JSON lines,
/// and the process exits when the single turn is over. It cannot ask for approval.
/// </summary>
internal static class CodexExec
{
    public static async Task<int> RunAsync(string[] args)
    {
        var scenario = Scenario.Load();
        var writer = new JsonLineWriter();

        string? resumeId = null;
        var sandbox = "read-only";
        var cwd = Environment.CurrentDirectory;
        string? outputFile = null;
        string? schemaFile = null;
        var json = false;
        var promptFromArgument = (string?)null;
        var positional = new List<string>();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "resume":
                    resumeId = i + 1 < args.Length ? args[++i] : null;
                    break;
                case "-s" or "--sandbox":
                    sandbox = args[++i];
                    break;
                case "-C" or "--cd":
                    cwd = args[++i];
                    break;
                case "-c" or "--config":
                {
                    var setting = args[++i];
                    if (setting.StartsWith("sandbox_mode=", StringComparison.Ordinal))
                    {
                        sandbox = setting["sandbox_mode=".Length..].Trim('"');
                    }

                    break;
                }

                case "-m" or "--model" or "-p" or "--profile":
                    i++;
                    break;
                case "-o" or "--output-last-message":
                    outputFile = args[++i];
                    break;
                case "--output-schema":
                    schemaFile = args[++i];
                    break;
                case "--json":
                    json = true;
                    break;
                case "--skip-git-repo-check" or "--ephemeral" or "--ignore-user-config" or "--ignore-rules":
                    break;
                case "-":
                    break;
                default:
                    positional.Add(args[i]);
                    break;
            }
        }

        if (positional.Count > 0)
        {
            promptFromArgument = positional[^1];
        }

        using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
        var prompt = promptFromArgument ?? await input.ReadToEndAsync();
        scenario.Log("exec.invocation", new JsonObject
        {
            ["args"] = new JsonArray(args.Select(a => (JsonNode?)JsonValue.Create(a)).ToArray()),
            ["prompt"] = prompt,
            ["cwd"] = cwd,
            ["sandbox"] = sandbox,
            ["hasSchema"] = schemaFile is not null,
        });

        if (schemaFile is not null && !File.Exists(schemaFile))
        {
            Console.Error.WriteLine("output schema file could not be read");
            return 1;
        }

        if (!json)
        {
            Console.Error.WriteLine("the fixture only supports --json");
            return 64;
        }

        var role = sandbox == "read-only" ? "reviewer" : "implementer";
        var threadId = resumeId ?? $"thr-exec-{Environment.ProcessId}";
        writer.Write(new JsonObject { ["type"] = "thread.started", ["thread_id"] = threadId });
        writer.Write(new JsonObject { ["type"] = "turn.started" });

        var item = 0;
        string? finalMessage = null;
        foreach (var node in scenario.NextTurn(role))
        {
            if (node is not JsonObject step)
            {
                continue;
            }

            switch (Scenario.Text(step, "type"))
            {
                case "message":
                    finalMessage = Scenario.Text(step, "text");
                    writer.Write(Completed(item++, new JsonObject { ["type"] = "agent_message", ["text"] = finalMessage }));
                    break;

                case "review":
                    finalMessage = Scenario.ReviewOutput(step).ToJsonString();
                    writer.Write(Completed(item++, new JsonObject { ["type"] = "agent_message", ["text"] = finalMessage }));
                    break;

                case "rawFinal":
                    finalMessage = Scenario.Text(step, "text");
                    writer.Write(Completed(item++, new JsonObject { ["type"] = "agent_message", ["text"] = finalMessage }));
                    break;

                case "reasoning":
                    writer.Write(Completed(item++, new JsonObject { ["type"] = "reasoning", ["text"] = Scenario.Text(step, "text") }));
                    break;

                case "command":
                {
                    var id = item++;
                    writer.Write(new JsonObject
                    {
                        ["type"] = "item.started",
                        ["item"] = new JsonObject
                        {
                            ["id"] = $"item_{id}", ["type"] = "command_execution", ["command"] = Scenario.Text(step, "command"),
                            ["aggregated_output"] = "", ["exit_code"] = null, ["status"] = "in_progress",
                        },
                    });
                    await Task.Delay(Scenario.Number(step, "runsMs", 0));
                    var exitCode = Scenario.Number(step, "exitCode");
                    writer.Write(Completed(id, new JsonObject
                    {
                        ["type"] = "command_execution", ["command"] = Scenario.Text(step, "command"),
                        ["aggregated_output"] = Scenario.Text(step, "output"), ["exit_code"] = exitCode,
                        ["status"] = exitCode == 0 ? "completed" : "failed",
                    }));
                    break;
                }

                case "write":
                case "delete":
                {
                    if (sandbox == "read-only")
                    {
                        writer.Write(Completed(item++, new JsonObject { ["type"] = "error", ["message"] = "writing is not permitted in a read-only sandbox" }));
                        break;
                    }

                    var full = Path.GetFullPath(Path.Combine(cwd, Scenario.Text(step, "path")));
                    var existed = File.Exists(full);
                    Scenario.ApplyFileStep(step, cwd);
                    writer.Write(Completed(item++, new JsonObject
                    {
                        ["type"] = "file_change",
                        ["changes"] = new JsonArray
                        {
                            new JsonObject
                            {
                                ["path"] = full,
                                ["kind"] = Scenario.Text(step, "type") == "delete" ? "delete" : existed ? "update" : "add",
                            },
                        },
                        ["status"] = "completed",
                    }));
                    break;
                }

                case "approval":
                    // Headless mode cannot ask: the request is rejected and the run fails.
                    writer.Write(new JsonObject { ["type"] = "error", ["message"] = "command execution approval is not supported in exec mode" });
                    writer.Write(new JsonObject
                    {
                        ["type"] = "turn.failed",
                        ["error"] = new JsonObject { ["message"] = "command execution approval is not supported in exec mode for thread " + threadId },
                    });
                    return 1;

                case "error":
                    writer.Write(new JsonObject { ["type"] = "error", ["message"] = Scenario.Text(step, "message") });
                    break;

                case "sleep":
                    await Task.Delay(Scenario.Number(step, "ms", 100));
                    break;

                case "hang":
                    await Task.Delay(Timeout.Infinite);
                    break;

                case "fail":
                    writer.Write(new JsonObject
                    {
                        ["type"] = "turn.failed",
                        ["error"] = new JsonObject { ["message"] = Scenario.Text(step, "message", "The turn failed.") },
                    });
                    return 1;

                case "crash":
                    Console.Error.WriteLine("fatal: simulated crash");
                    return Scenario.Number(step, "exitCode", 3);

                case "garbage":
                    writer.WriteRaw(Scenario.Text(step, "text", "this is not json") + "\n");
                    break;

                case "stderr":
                    Console.Error.WriteLine(Scenario.Text(step, "text"));
                    break;
            }
        }

        var usage = scenario.Codex["execUsage"] as JsonObject ?? new JsonObject
        {
            ["input_tokens"] = 1200, ["cached_input_tokens"] = 1000, ["cache_write_input_tokens"] = 0,
            ["output_tokens"] = 300, ["reasoning_output_tokens"] = 120,
        };
        writer.Write(new JsonObject { ["type"] = "turn.completed", ["usage"] = usage.DeepClone() });

        if (outputFile is not null && finalMessage is not null)
        {
            await File.WriteAllTextAsync(outputFile, finalMessage, new UTF8Encoding(false));
        }

        return 0;

        static JsonObject Completed(int id, JsonObject body)
        {
            body["id"] = $"item_{id}";
            return new JsonObject { ["type"] = "item.completed", ["item"] = body };
        }
    }
}
