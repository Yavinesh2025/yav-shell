using System.Text.Json;
using Yav.Core.Runs;

namespace Yav.Bench;

/// <summary>One request of a task with the checks that decide whether it was fulfilled.</summary>
/// <param name="SolutionDirectory">Files of a solution that is known to pass. Used to check the task itself and by the scripted agents.</param>
public sealed record BenchStep(string Request, IReadOnlyList<GateDefinition> Checks, string SolutionDirectory);

/// <summary>
/// A task of the benchmark: a small project as it is before the task, what is asked for, and acceptance
/// checks that were written before any agent saw the task.
/// </summary>
public sealed record BenchTask(
    string Id,
    string Title,
    string Category,
    string Language,
    IReadOnlyList<string> ProtectedPaths,
    string ProjectDirectory,
    IReadOnlyList<BenchStep> Steps)
{
    public static IReadOnlyList<BenchTask> LoadAll(string tasksDirectory) =>
        Directory.EnumerateDirectories(tasksDirectory)
            .Where(d => File.Exists(Path.Combine(d, "task.json")))
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(Load)
            .ToList();

    public static BenchTask Load(string directory)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(directory, "task.json")));
        var root = document.RootElement;
        var steps = new List<BenchStep>
        {
            new(Text(root, "request"), Checks(root.GetProperty("checks")), Path.Combine(directory, "solution")),
        };
        if (root.TryGetProperty("followUp", out var followUp))
        {
            steps.Add(new BenchStep(Text(followUp, "request"), Checks(followUp.GetProperty("checks")), Path.Combine(directory, "solution-follow-up")));
        }

        foreach (var step in steps)
        {
            if (!Directory.Exists(step.SolutionDirectory))
            {
                throw new InvalidDataException($"{directory}: the solution in '{step.SolutionDirectory}' is missing.");
            }
        }

        return new BenchTask(
            Text(root, "id"),
            Text(root, "title"),
            Text(root, "category"),
            Text(root, "language"),
            root.TryGetProperty("protectedPaths", out var paths) ? paths.EnumerateArray().Select(p => p.GetString()!).ToList() : [],
            Path.Combine(directory, "project"),
            steps);
    }

    /// <summary>The configuration of required checks for a step, as the user would approve it.</summary>
    public ProjectConfiguration Configuration(BenchStep step) =>
        ProjectConfiguration.Empty with { Gates = step.Checks, ProtectedPaths = ProtectedPaths };

    private static string Text(JsonElement element, string name) =>
        element.GetProperty(name).GetString() ?? throw new InvalidDataException($"'{name}' is missing.");

    private static List<GateDefinition> Checks(JsonElement checks) =>
        checks.EnumerateArray().Select(check => new GateDefinition(
            Id: Text(check, "id"),
            Kind: Enum.Parse<GateKind>(Text(check, "kind"), ignoreCase: true),
            Title: Text(check, "title"),
            Command: Text(check, "command"),
            Arguments: check.GetProperty("args").EnumerateArray().Select(a => a.GetString()!).ToList(),
            WorkingDirectory: string.Empty,
            TimeoutSeconds: check.TryGetProperty("timeoutSeconds", out var timeout) ? timeout.GetInt32() : 300,
            Required: true,
            Environment: new Dictionary<string, string>(),
            SuccessExitCodes: [0],
            Requires: [])).ToList();
}
