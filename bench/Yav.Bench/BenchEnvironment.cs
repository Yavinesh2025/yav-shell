using System.Text;
using System.Text.Json.Nodes;
using Yav.Adapters;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Settings;
using Yav.Platform.Processes;

namespace Yav.Bench;

public enum BenchMode
{
    /// <summary>The agents are the scripted stand-in. Nothing is sent to a provider and nothing is consumed.</summary>
    Fixture,

    /// <summary>The agents are the ones that are installed. Every run consumes usage of the accounts they are signed in to.</summary>
    Live,
}

/// <summary>What every arm of the comparison uses alike: the same models, efforts and agents.</summary>
public sealed record BenchEnvironment
{
    public required BenchMode Mode { get; init; }

    public required RoleSelection ModelA { get; init; }

    public required RoleSelection ModelB { get; init; }

    /// <summary>Where yav.exe is.</summary>
    public required string Yav { get; init; }

    /// <summary>The scripted stand-in for the agents. Used in fixture mode only.</summary>
    public string? FakeAgent { get; init; }

    /// <summary>The data directory of the user, from which acknowledged account routes are taken in live mode.</summary>
    public string? UserHome { get; init; }

    public int MaxRepairCycles { get; init; } = 2;

    public static BenchEnvironment Fixture(string yav, string fakeAgent) => new()
    {
        Mode = BenchMode.Fixture,
        ModelA = new RoleSelection(CodexAppServerAdapter.AdapterId, "model-a"),
        ModelB = new RoleSelection(CodexAppServerAdapter.AdapterId, "model-b"),
        Yav = yav,
        FakeAgent = fakeAgent,
    };

    /// <summary>The models the user chose in YAV, with the account routes the user acknowledged there.</summary>
    public static BenchEnvironment Live(string yav, YavPaths user)
    {
        if (!File.Exists(user.SettingsFile))
        {
            throw new InvalidOperationException($"No settings were found in {user.Home}. Choose Model A and Model B in YAV first (/models).");
        }

        var settings = AppSettings.FromJson(File.ReadAllText(user.SettingsFile));
        if (settings.ModelA is null || settings.ModelB is null)
        {
            throw new InvalidOperationException("Model A and Model B are not both chosen in YAV. Choose them first (/models); the benchmark uses the same.");
        }

        return new BenchEnvironment { Mode = BenchMode.Live, ModelA = settings.ModelA, ModelB = settings.ModelB, Yav = yav, UserHome = user.Home };
    }

    /// <summary>Settings of an adapter for a run, with the script the stand-in plays in fixture mode.</summary>
    public AdapterSettings AdapterSettings(string flavor, string? scenario) => Mode == BenchMode.Fixture
        ? new AdapterSettings
        {
            ExecutablePath = FakeAgent!,
            Environment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["YAV_FAKE_SCENARIO"] = scenario ?? string.Empty,
                ["YAV_FAKE_FLAVOR"] = flavor,
            },
        }
        : new AdapterSettings();

    public IAgentAdapter CreateAdapter(string adapterId, string? scenario, ProcessRunner runner)
    {
        var flavor = adapterId == ClaudeCliAdapter.AdapterId ? "claude" : "codex";
        var settings = AdapterSettings(flavor, scenario);
        var options = new AdapterOptions(
            ExecutablePath: settings.ExecutablePath.Length == 0 ? null : settings.ExecutablePath,
            Environment: settings.Environment.Count == 0 ? null : settings.Environment.ToDictionary(p => p.Key, p => (string?)p.Value),
            ClientVersion: "bench");
        return adapterId switch
        {
            CodexAppServerAdapter.AdapterId => new CodexAppServerAdapter(runner, TimeProvider.System, options),
            CodexExecAdapter.AdapterId => new CodexExecAdapter(runner, TimeProvider.System, options),
            ClaudeCliAdapter.AdapterId => new ClaudeCliAdapter(runner, TimeProvider.System, options, () => null),
            _ => throw new InvalidOperationException($"'{adapterId}' is not an adapter of this version."),
        };
    }

    /// <summary>
    /// The script of the stand-in for a task: for each step Model A writes the files of the solution that
    /// is known to pass, and Model B finds nothing. It measures what YAV adds around the agents, and
    /// nothing about what a model can do.
    /// </summary>
    public static string WriteScenario(BenchTask task, string directory)
    {
        var implementer = new JsonArray();
        var reviewer = new JsonArray();
        foreach (var step in task.Steps)
        {
            var turn = new JsonArray();
            foreach (var file in Directory.EnumerateFiles(step.SolutionDirectory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            {
                turn.Add(new JsonObject
                {
                    ["type"] = "write",
                    ["path"] = Path.GetRelativePath(step.SolutionDirectory, file).Replace('\\', '/'),
                    ["content"] = File.ReadAllText(file),
                });
            }

            turn.Add(new JsonObject { ["type"] = "message", ["text"] = "Done.", ["phase"] = "final_answer" });
            implementer.Add(turn);
            reviewer.Add(new JsonArray { new JsonObject { ["type"] = "review", ["status"] = "pass" } });
        }

        var scenario = new JsonObject
        {
            ["log"] = Path.Combine(directory, "agent-received.jsonl"),
            ["codex"] = new JsonObject(),
            ["claude"] = new JsonObject(),
            ["implementer"] = implementer,
            ["reviewer"] = reviewer,
        };
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "scenario.json");
        File.WriteAllText(path, scenario.ToJsonString(), new UTF8Encoding(false));
        return path;
    }
}
