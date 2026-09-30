using System.Text;
using System.Text.Json;
using Yav.Core.Runs;

namespace Yav.Validation;

/// <summary>
/// Reads and writes the project configuration. Reading is strict: anything unknown or out of range is an
/// error, because a configuration decides which commands run and what counts as passing.
/// </summary>
internal static class ConfigurationParser
{
    private static readonly string[] RootProperties =
    [
        "schemaVersion", "gates", "prepare", "protectedPaths", "replicateIgnored", "allowSecrets", "testPaths", "validation", "$schema",
    ];

    private static readonly string[] GateProperties =
    [
        "id", "kind", "title", "command", "args", "cwd", "timeoutSeconds", "required", "env", "successExitCodes", "requires",
    ];

    private static readonly string[] RequirementPrefixes = ["env:", "tool:", "file:", "tcp:", "manual:"];

    public static ProjectConfiguration Parse(string json, out IReadOnlyList<string> errors)
    {
        var problems = new List<string>();
        errors = problems;

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            problems.Add("The configuration is not valid JSON: " + ex.Message);
            return ProjectConfiguration.Empty;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add("The configuration must be a JSON object.");
                return ProjectConfiguration.Empty;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (!RootProperties.Contains(property.Name, StringComparer.Ordinal))
                {
                    problems.Add($"Unknown setting '{property.Name}'.");
                }
            }

            if (root.TryGetProperty("schemaVersion", out var version)
                && (version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var number) || number != 1))
            {
                problems.Add("schemaVersion must be 1; this version of YAV Shell does not know any other.");
            }

            var gates = ReadGates(root, "gates", problems);
            var prepare = ReadGates(root, "prepare", problems);
            foreach (var duplicate in gates.Concat(prepare).GroupBy(g => g.Id, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1))
            {
                problems.Add($"The id '{duplicate.Key}' is used more than once.");
            }

            var execution = ProjectConfiguration.ExecutionCopy;
            if (root.TryGetProperty("validation", out var validation))
            {
                if (validation.ValueKind != JsonValueKind.Object)
                {
                    problems.Add("validation must be an object.");
                }
                else if (validation.TryGetProperty("execution", out var mode))
                {
                    var text = mode.ValueKind == JsonValueKind.String ? mode.GetString() : null;
                    if (text is ProjectConfiguration.ExecutionCopy or ProjectConfiguration.ExecutionCandidate)
                    {
                        execution = text;
                    }
                    else
                    {
                        problems.Add("validation.execution must be \"copy\" or \"candidate\".");
                    }
                }
            }

            var testPaths = ReadStrings(root, "testPaths", problems);
            return new ProjectConfiguration(
                SchemaVersion: 1,
                Gates: gates,
                Prepare: prepare,
                ProtectedPaths: ReadStrings(root, "protectedPaths", problems) ?? [],
                ReplicateIgnored: ReadStrings(root, "replicateIgnored", problems) ?? [],
                AllowSecrets: ReadStrings(root, "allowSecrets", problems) ?? [],
                TestPaths: testPaths ?? ProjectConfiguration.DefaultTestPaths,
                ValidationExecution: execution);
        }
    }

    public static string Serialize(ProjectConfiguration configuration)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schemaVersion", configuration.SchemaVersion);
            WriteGates(writer, "gates", configuration.Gates);
            WriteGates(writer, "prepare", configuration.Prepare);
            WriteStrings(writer, "protectedPaths", configuration.ProtectedPaths);
            WriteStrings(writer, "replicateIgnored", configuration.ReplicateIgnored);
            WriteStrings(writer, "allowSecrets", configuration.AllowSecrets);
            WriteStrings(writer, "testPaths", configuration.TestPaths);
            writer.WriteStartObject("validation");
            writer.WriteString("execution", configuration.ValidationExecution);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray()).ReplaceLineEndings("\n") + "\n";
    }

    private static List<GateDefinition> ReadGates(JsonElement root, string name, List<string> problems)
    {
        var gates = new List<GateDefinition>();
        if (!root.TryGetProperty(name, out var array))
        {
            return gates;
        }

        if (array.ValueKind != JsonValueKind.Array)
        {
            problems.Add($"{name} must be an array.");
            return gates;
        }

        var index = 0;
        foreach (var item in array.EnumerateArray())
        {
            var label = $"{name}[{index++}]";
            var gate = ReadGate(item, label, name == "prepare", problems);
            if (gate is not null)
            {
                gates.Add(gate);
            }
        }

        return gates;
    }

    private static GateDefinition? ReadGate(JsonElement item, string label, bool isPrepare, List<string> problems)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{label} must be an object.");
            return null;
        }

        var before = problems.Count;
        foreach (var property in item.EnumerateObject())
        {
            if (!GateProperties.Contains(property.Name, StringComparer.Ordinal))
            {
                problems.Add($"{label}: unknown setting '{property.Name}'.");
            }
        }

        var id = ReadString(item, "id");
        if (string.IsNullOrEmpty(id) || id.Length > 40 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            problems.Add($"{label}.id is required and may contain only letters, digits, '-' and '_'.");
        }

        var command = ReadString(item, "command");
        if (string.IsNullOrWhiteSpace(command))
        {
            problems.Add($"{label}.command is required.");
        }

        var kind = isPrepare ? GateKind.Custom : GateKind.Custom;
        if (item.TryGetProperty("kind", out var kindElement))
        {
            var text = kindElement.ValueKind == JsonValueKind.String ? kindElement.GetString() : null;
            var parsed = text?.ToLowerInvariant() switch
            {
                "build" => GateKind.Build,
                "lint" => GateKind.Lint,
                "typecheck" => GateKind.TypeCheck,
                "test" => GateKind.Test,
                "smoke" => GateKind.Smoke,
                "integration" => GateKind.Integration,
                "custom" => GateKind.Custom,
                _ => (GateKind?)null,
            };
            if (parsed is null)
            {
                problems.Add($"{label}.kind must be build, lint, typecheck, test, smoke, integration or custom.");
            }
            else
            {
                kind = parsed.Value;
            }
        }

        var arguments = new List<string>();
        if (item.TryGetProperty("args", out var args))
        {
            if (args.ValueKind != JsonValueKind.Array || args.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String))
            {
                problems.Add($"{label}.args must be an array of strings. Arguments are passed as a list, never as one command line.");
            }
            else
            {
                arguments.AddRange(args.EnumerateArray().Select(a => a.GetString()!));
            }
        }

        var cwd = (ReadString(item, "cwd") ?? string.Empty).Replace('\\', '/').Trim('/');
        if (cwd.Length > 0 && (cwd.Contains(':') || cwd.Split('/').Any(s => s is ".." or "." or "")))
        {
            problems.Add($"{label}.cwd must be a path inside the project, written relative to its root.");
        }

        var timeout = 1800;
        if (item.TryGetProperty("timeoutSeconds", out var timeoutElement))
        {
            if (timeoutElement.ValueKind != JsonValueKind.Number || !timeoutElement.TryGetInt32(out timeout) || timeout < 1 || timeout > 86_400)
            {
                problems.Add($"{label}.timeoutSeconds must be between 1 and 86400.");
            }
        }

        var required = true;
        if (item.TryGetProperty("required", out var requiredElement))
        {
            if (requiredElement.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                required = requiredElement.GetBoolean();
            }
            else
            {
                problems.Add($"{label}.required must be true or false.");
            }
        }

        var environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (item.TryGetProperty("env", out var env))
        {
            if (env.ValueKind != JsonValueKind.Object || env.EnumerateObject().Any(p => p.Value.ValueKind != JsonValueKind.String))
            {
                problems.Add($"{label}.env must be an object with string values.");
            }
            else
            {
                foreach (var property in env.EnumerateObject())
                {
                    if (property.Name.Length == 0 || property.Name.Contains('='))
                    {
                        problems.Add($"{label}.env: '{property.Name}' is not a valid variable name.");
                    }
                    else
                    {
                        environment[property.Name] = property.Value.GetString()!;
                    }
                }
            }
        }

        var success = new List<int> { 0 };
        if (item.TryGetProperty("successExitCodes", out var codes))
        {
            if (codes.ValueKind != JsonValueKind.Array || codes.GetArrayLength() == 0
                || codes.EnumerateArray().Any(c => c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out _)))
            {
                problems.Add($"{label}.successExitCodes must be a non-empty array of integers.");
            }
            else
            {
                success = codes.EnumerateArray().Select(c => c.GetInt32()).ToList();
            }
        }

        var requires = ReadStrings(item, "requires", problems, label) ?? [];
        foreach (var requirement in requires)
        {
            if (!RequirementPrefixes.Any(p => requirement.StartsWith(p, StringComparison.Ordinal) && requirement.Length > p.Length))
            {
                problems.Add($"{label}.requires: '{requirement}' is not understood. Use env:NAME, tool:NAME, file:PATH, tcp:HOST:PORT or manual:TEXT.");
            }
        }

        if (problems.Count > before)
        {
            return null;
        }

        return new GateDefinition(
            Id: id!,
            Kind: kind,
            Title: ReadString(item, "title") is { Length: > 0 } title ? title : id!,
            Command: command!,
            Arguments: arguments,
            WorkingDirectory: cwd,
            TimeoutSeconds: timeout,
            Required: required,
            Environment: environment,
            SuccessExitCodes: success,
            Requires: requires);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static List<string>? ReadStrings(JsonElement element, string name, List<string> problems, string? label = null)
    {
        if (!element.TryGetProperty(name, out var array))
        {
            return null;
        }

        var display = label is null ? name : $"{label}.{name}";
        if (array.ValueKind != JsonValueKind.Array || array.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String))
        {
            problems.Add($"{display} must be an array of strings.");
            return null;
        }

        return array.EnumerateArray().Select(a => a.GetString()!).ToList();
    }

    private static void WriteGates(Utf8JsonWriter writer, string name, IReadOnlyList<GateDefinition> gates)
    {
        writer.WriteStartArray(name);
        foreach (var gate in gates)
        {
            writer.WriteStartObject();
            writer.WriteString("id", gate.Id);
            writer.WriteString("kind", gate.Kind.ToString().ToLowerInvariant());
            writer.WriteString("title", gate.Title);
            writer.WriteString("command", gate.Command);
            WriteStrings(writer, "args", gate.Arguments);
            writer.WriteString("cwd", gate.WorkingDirectory);
            writer.WriteNumber("timeoutSeconds", gate.TimeoutSeconds);
            writer.WriteBoolean("required", gate.Required);
            writer.WriteStartObject("env");
            foreach (var (key, value) in gate.Environment.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
            writer.WriteStartArray("successExitCodes");
            foreach (var code in gate.SuccessExitCodes)
            {
                writer.WriteNumberValue(code);
            }

            writer.WriteEndArray();
            WriteStrings(writer, "requires", gate.Requires);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteStrings(Utf8JsonWriter writer, string name, IReadOnlyList<string> values)
    {
        writer.WriteStartArray(name);
        foreach (var value in values)
        {
            writer.WriteStringValue(value);
        }

        writer.WriteEndArray();
    }
}
