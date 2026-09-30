using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Yav.FakeAgent;

/// <summary>
/// What the fixture should do, read from the file named by YAV_FAKE_SCENARIO. Everything the fixture
/// receives is appended to the scenario's log so a test can assert on what YAV actually sent.
/// </summary>
internal sealed class Scenario
{
    private static readonly Lock Gate = new();
    private readonly JsonObject _root;
    private readonly string? _path;

    private Scenario(JsonObject root, string? path)
    {
        _root = root;
        _path = path;
    }

    public static Scenario Load()
    {
        var path = Environment.GetEnvironmentVariable("YAV_FAKE_SCENARIO");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return new Scenario([], null);
        }

        var root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? [];
        return new Scenario(root, path);
    }

    public JsonObject Section(string name) => _root[name] as JsonObject ?? [];

    public JsonObject Codex => Section("codex");

    public JsonObject Claude => Section("claude");

    /// <summary>The script for the next turn of a role, or an empty script when none is left.</summary>
    public JsonArray NextTurn(string role)
    {
        var scripts = _root[role] as JsonArray ?? [];
        var index = NextIndex(role);
        return index < scripts.Count && scripts[index] is JsonArray script ? script : DefaultTurn(role);
    }

    private static JsonArray DefaultTurn(string role) => role == "reviewer"
        ? [new JsonObject { ["type"] = "review", ["status"] = "pass" }]
        : [new JsonObject { ["type"] = "message", ["text"] = "Nothing to do.", ["phase"] = "final_answer" }];

    private int NextIndex(string role)
    {
        if (_path is null)
        {
            return 0;
        }

        // Turns may run in separate processes, so the position is kept in a file next to the scenario.
        var statePath = _path + ".state";
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(statePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
                JsonObject state;
                if (stream.Length == 0)
                {
                    state = [];
                }
                else
                {
                    using var reader = new StreamReader(stream, Encoding.UTF8, false, 1024, leaveOpen: true);
                    state = JsonNode.Parse(reader.ReadToEnd()) as JsonObject ?? [];
                }

                var index = state[role]?.GetValue<int>() ?? 0;
                state[role] = index + 1;
                stream.SetLength(0);
                stream.Position = 0;
                var bytes = Encoding.UTF8.GetBytes(state.ToJsonString());
                stream.Write(bytes);
                return index;
            }
            catch (IOException) when (attempt < 50)
            {
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>Records something the fixture received.</summary>
    public void Log(string channel, JsonNode? payload)
    {
        var path = _root["log"]?.GetValue<string>();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var line = new JsonObject
        {
            ["channel"] = channel,
            ["pid"] = Environment.ProcessId,
            ["payload"] = payload?.DeepClone(),
        }.ToJsonString();

        lock (Gate)
        {
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.AppendAllText(path, line + "\n", new UTF8Encoding(false));
                    return;
                }
                catch (IOException) when (attempt < 50)
                {
                    Thread.Sleep(20);
                }
            }
        }
    }

    public static string Text(JsonNode? node, string name, string fallback = "") =>
        (node as JsonObject)?[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : fallback;

    public static int Number(JsonNode? node, string name, int fallback = 0) =>
        (node as JsonObject)?[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

    public static bool Flag(JsonNode? node, string name, bool fallback = false) =>
        (node as JsonObject)?[name] is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    /// <summary>The structured review result a "review" step stands for.</summary>
    public static JsonObject ReviewOutput(JsonObject step)
    {
        if (step["output"] is JsonObject explicitOutput)
        {
            return (JsonObject)explicitOutput.DeepClone();
        }

        var status = Text(step, "status", "pass");
        var findings = new JsonArray();
        if (step["findings"] is JsonArray given)
        {
            foreach (var finding in given)
            {
                findings.Add(finding?.DeepClone());
            }
        }
        else if (status == "changes_required")
        {
            findings.Add(new JsonObject
            {
                ["severity"] = "major",
                ["category"] = "defect",
                ["optional"] = false,
                ["file"] = Text(step, "file", "src/app.txt"),
                ["line"] = 1,
                ["title"] = Text(step, "title", "The change does not handle the empty case"),
                ["failure_scenario"] = "An empty input produces the wrong result.",
                ["evidence"] = "Line 1 assumes the input has at least one element.",
                ["suggested_correction"] = "Handle the empty input first.",
                ["limitation"] = null,
            });
        }

        var limitations = new JsonArray();
        if (status == "unable_to_verify")
        {
            limitations.Add(Text(step, "limitation", "The integration environment is not available."));
        }

        return new JsonObject
        {
            ["status"] = status,
            ["summary"] = Text(step, "summary", "Reviewed the candidate."),
            ["coverage"] = Text(step, "coverage", "Read the changed files and their callers."),
            ["limitations"] = limitations,
            ["findings"] = findings,
        };
    }

    /// <summary>Performs a file step relative to the working directory, as an agent editing the workspace would.</summary>
    public static void ApplyFileStep(JsonObject step, string workingDirectory)
    {
        var relative = Text(step, "path");
        var full = Path.GetFullPath(Path.Combine(workingDirectory, relative));
        switch (Text(step, "type"))
        {
            case "write":
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, Text(step, "content"), new UTF8Encoding(false));
                break;
            case "delete":
                if (File.Exists(full))
                {
                    File.Delete(full);
                }

                break;
        }
    }
}

/// <summary>Writes one JSON object per line to standard output, without a byte order mark.</summary>
internal sealed class JsonLineWriter
{
    private readonly Stream _output = Console.OpenStandardOutput();
    private readonly Lock _gate = new();

    public void Write(JsonNode node)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n");
        lock (_gate)
        {
            _output.Write(bytes);
            _output.Flush();
        }
    }

    public void WriteRaw(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_gate)
        {
            _output.Write(bytes);
            _output.Flush();
        }
    }

    /// <summary>Writes a line in several pieces, to exercise the reader's handling of partial frames.</summary>
    public void WriteInPieces(JsonNode node, int pieceSize)
    {
        var bytes = Encoding.UTF8.GetBytes(node.ToJsonString() + "\n");
        lock (_gate)
        {
            for (var offset = 0; offset < bytes.Length; offset += pieceSize)
            {
                _output.Write(bytes, offset, Math.Min(pieceSize, bytes.Length - offset));
                _output.Flush();
                Thread.Sleep(3);
            }
        }
    }
}

internal static class JsonHelpers
{
    public static JsonObject? ParseObject(string line)
    {
        try
        {
            return JsonNode.Parse(line) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
