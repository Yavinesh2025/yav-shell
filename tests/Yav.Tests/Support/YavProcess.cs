using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Yav.Adapters;
using Yav.Console.Composition;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Settings;

namespace Yav.Tests.Support;

/// <summary>What a run of yav.exe left behind.</summary>
public sealed record YavResult(int ExitCode, string Output, string Error, TimeSpan Elapsed)
{
    public string[] Lines => Output.ReplaceLineEndings("\n").Split('\n', StringSplitOptions.RemoveEmptyEntries);

    public List<JsonElement> Json => Lines.Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
}

/// <summary>
/// The program itself, as a user or a script starts it: yav.exe in a process of its own, with a data
/// directory of its own, a real Git project, and the scripted agent fixture in place of the agents.
/// </summary>
public sealed class YavProcess : IDisposable
{
    private readonly TempDirectory _home = new("exe home");

    public YavProcess(bool acknowledgeRoutes = true, params (string Path, string Content)[] files)
    {
        Project = GitRepo.WithFiles(files.Length > 0 ? files : [("src/app.txt", "one\n"), ("README.md", "# App\n")]);
        Agents = new AgentFixture();
        Paths = new YavPaths(_home.Path);
        Paths.EnsureCreated();

        var settings = new AppSettings
        {
            ModelA = new RoleSelection(CodexAppServerAdapter.AdapterId, "model-a"),
            ModelB = new RoleSelection(CodexAppServerAdapter.AdapterId, "model-b"),
        };
        foreach (var (id, flavor) in new[]
        {
            (CodexAppServerAdapter.AdapterId, "codex"), (CodexExecAdapter.AdapterId, "codex"), (ClaudeCliAdapter.AdapterId, "claude"),
        })
        {
            settings.Adapters[id] = new AdapterSettings
            {
                ExecutablePath = Fixtures.FakeAgent,
                Environment = Agents.Options(flavor).Environment!.ToDictionary(p => p.Key, p => p.Value!, StringComparer.OrdinalIgnoreCase),
            };
        }

        new SettingsStore(Paths).Save(settings);

        // What the user did in an earlier session: acknowledged the account route and trusted the project.
        var database = Yav.Storage.YavDatabase.Open(Paths.Database, TimeProvider.System);
        if (acknowledgeRoutes)
        {
            database.AcknowledgeRoute($"{CodexAppServerAdapter.AdapterId}:Subscription:openai", "test");
        }

        database.SetProjectTrusted(Project.Path, true);
        database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public GitRepo Project { get; }

    public AgentFixture Agents { get; }

    public YavPaths Paths { get; }

    /// <summary>The program that was built together with the tests.</summary>
    public static string Executable
    {
        get
        {
            var testBin = AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
            var configuration = Path.GetFileName(testBin);
            var path = Path.Combine(Path.GetFullPath(Path.Combine(testBin, "..", "..")), "Yav.Console", configuration, "yav.exe");
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("yav.exe was not built.", path);
            }

            return path;
        }
    }

    public void TrustGates(params GateDefinition[] gates)
    {
        var database = Yav.Storage.YavDatabase.Open(Paths.Database, TimeProvider.System);
        var validation = new Yav.Validation.ValidationService(new Yav.Platform.Processes.ProcessRunner(), database, TimeProvider.System, "test");
        var configuration = ProjectConfiguration.Empty with { Gates = gates };
        validation.TrustConfiguration(Project.Path, configuration, validation.Serialize(configuration));
        database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
    }

    public YavProcess WithPassingRun()
    {
        TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Changed app.txt to say fixed."))
            .ReviewerTurn(Step.Review("pass"));
        return this;
    }

    /// <summary>Starts yav.exe with its output redirected, gives it the input, and waits until it has ended.</summary>
    /// <param name="environment">Variables to set for yav.exe besides its data directory; null removes one.</param>
    public async Task<YavResult> RunAsync(
        string[] arguments,
        string? input = null,
        string? workingDirectory = null,
        int seconds = 120,
        Action<Process>? whileRunning = null,
        string? executable = null,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        var start = new ProcessStartInfo(executable ?? Executable)
        {
            WorkingDirectory = workingDirectory ?? Project.Path,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[YavPaths.HomeVariable] = Paths.Home;
        start.Environment.Remove("NO_COLOR");
        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        var watch = Stopwatch.StartNew();
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (input is not null)
        {
            await process.StandardInput.WriteAsync(input);
        }

        whileRunning?.Invoke(process);
        if (whileRunning is null)
        {
            process.StandardInput.Close();
        }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"yav {string.Join(' ', arguments)} did not end within {seconds} seconds.\n{await output}\n{await error}");
        }

        return new YavResult(process.ExitCode, await output, await error, watch.Elapsed);
    }

    /// <summary>The intervals that were measured for a run.</summary>
    public IReadOnlyList<Yav.Core.Timing.TimingSpan> SpansOf(string runId)
    {
        var database = Yav.Storage.YavDatabase.Open(Paths.Database, TimeProvider.System);
        var spans = database.GetSpans(runId);
        database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return spans;
    }

    public RunRecord? FindRun(string runId)
    {
        var database = Yav.Storage.YavDatabase.Open(Paths.Database, TimeProvider.System);
        var run = database.FindRun(runId);
        database.Dispose();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        return run;
    }

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Agents.Dispose();
        Project.Dispose();
        _home.Dispose();
    }
}
