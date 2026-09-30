using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Yav.Adapters;
using Yav.Console.Composition;
using Yav.Core.Profiles;
using Yav.Core.Settings;
using Yav.Tests.Live;
using Yav.Tests.Support;

namespace Yav.Tests.Packaging;

/// <summary>
/// A program in the place of yav.exe, for the tests of scripts\live-run.ps1 as a whole. It asks no model and starts
/// nothing: it records how it was started, writes the lines it is given as its output, waits when it is told to, and
/// ends with the exit code it is given. It is built from the source below by the C# compiler of the .NET Framework
/// that comes with Windows, because the tests may not build anything with the SDK.
/// </summary>
internal static class StandInYav
{
    // C# 5: what that compiler understands. What it does is set by variables, so that one program serves every test.
    private const string Source = """
        using System;
        using System.IO;
        using System.Text;
        using System.Threading;

        static class StandIn
        {
            static int Main(string[] args)
            {
                var record = Environment.GetEnvironmentVariable("YAV_STANDIN_RECORD");
                if (!string.IsNullOrEmpty(record))
                {
                    var text = new StringBuilder();
                    text.Append("pid=").Append(System.Diagnostics.Process.GetCurrentProcess().Id).Append('\n');
                    text.Append("home=").Append(Environment.GetEnvironmentVariable("YAV_HOME")).Append('\n');
                    text.Append("cwd=").Append(Environment.CurrentDirectory).Append('\n');
                    foreach (var argument in args)
                    {
                        text.Append("arg=").Append(argument).Append('\n');
                    }

                    // The variables it is told to show, each as it found it: "env:NAME=value", or "env:NAME" when it is not set.
                    var shown = Environment.GetEnvironmentVariable("YAV_STANDIN_SHOW") ?? string.Empty;
                    foreach (var name in shown.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
                    {
                        var value = Environment.GetEnvironmentVariable(name);
                        text.Append("env:").Append(name).Append(value == null ? string.Empty : "=" + value).Append('\n');
                    }

                    File.AppendAllText(record, text.ToString(), new UTF8Encoding(false));
                }

                var output = Environment.GetEnvironmentVariable("YAV_STANDIN_OUTPUT");
                if (!string.IsNullOrEmpty(output) && File.Exists(output))
                {
                    var bytes = File.ReadAllBytes(output);
                    using (var stream = Console.OpenStandardOutput())
                    {
                        stream.Write(bytes, 0, bytes.Length);
                        stream.Flush();
                    }
                }

                if (Environment.GetEnvironmentVariable("YAV_STANDIN_HANG") == "1")
                {
                    // Until it is ended from outside; a test that failed to end it leaves it for ten minutes at most.
                    Thread.Sleep(TimeSpan.FromMinutes(10));
                }

                int code;
                return int.TryParse(Environment.GetEnvironmentVariable("YAV_STANDIN_EXIT"), out code) ? code : 0;
            }
        }
        """;

    private static readonly Lazy<string> Built = new(Build, LazyThreadSafetyMode.ExecutionAndPublication);

    public static string Executable => Built.Value;

    private static string Build()
    {
        var compiler = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Microsoft.NET", "Framework64", "v4.0.30319", "csc.exe");
        if (!File.Exists(compiler))
        {
            throw new FileNotFoundException("The C# compiler of the .NET Framework, which comes with Windows, was not found.", compiler);
        }

        // Named after the source, so that a program built from another source is never taken for this one.
        var directory = Path.Combine(Path.GetTempPath(), "yav-tests", "live run stand-in " + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Source)))[..12]);
        var program = Path.Combine(directory, "yav.exe");
        if (File.Exists(program))
        {
            return program;
        }

        Directory.CreateDirectory(directory);
        var source = Path.Combine(directory, "StandIn.cs");
        File.WriteAllText(source, Source, new UTF8Encoding(false));
        var building = Path.Combine(directory, $"yav-{Guid.NewGuid():N}.exe");
        var start = new ProcessStartInfo(compiler)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "/nologo", "/target:exe", "/optimize", "/out:" + building, source })
        {
            start.ArgumentList.Add(argument);
        }

        using var compiling = Process.Start(start)!;
        var said = compiling.StandardOutput.ReadToEndAsync();
        var complained = compiling.StandardError.ReadToEndAsync();
        if (!compiling.WaitForExit(120_000) || compiling.ExitCode != 0)
        {
            throw new InvalidOperationException($"The stand-in for yav.exe could not be built: {said.Result}{complained.Result}");
        }

        try
        {
            File.Move(building, program);
        }
        catch (IOException) when (File.Exists(program))
        {
            // Built at the same time by another test run; either program is the same.
            File.Delete(building);
        }

        return program;
    }
}

/// <summary>
/// The directory of a run as the setup leaves it, below artifacts\live-run of the repository, where the script
/// continues runs: a project, a data directory, and live-run.json that names the stand-in as the program.
/// </summary>
internal sealed class ScriptRun : IDisposable
{
    /// <param name="parent">Where the directory is made instead of artifacts\live-run, for a directory the setup did not make.</param>
    public ScriptRun(string task = "01-small-edit", bool setUp = true, string? parent = null)
    {
        Directory = Path.Combine(parent ?? Path.Combine(StandInRun.Repository, "artifacts", "live-run"), $"tëst 日本 {Guid.NewGuid().ToString("N")[..8]}");
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, "project"));
        System.IO.Directory.CreateDirectory(Path.Combine(Directory, "home"));
        Setup = new JsonObject
        {
            ["task"] = task,
            ["program"] = StandInYav.Executable,
            ["modelA"] = "codex-app-server model-a",
            ["modelB"] = "claude-cli opus",
            ["effortA"] = "maximum",
            ["effortB"] = "maximum",
            ["acknowledged"] = new JsonArray("codex:subscription", "claude:subscription"),
            ["checksTrusted"] = true,
            ["taskHash"] = StandInRun.TaskHash(task),
            ["time"] = "2026-09-30T10:00:00.0000000Z",
            ["setUp"] = setUp,
        };
        SaveSetup();
    }

    public string Directory { get; }

    /// <summary>What live-run.json records; <see cref="SaveSetup"/> writes it again after a change.</summary>
    public JsonObject Setup { get; }

    /// <summary>Where the stand-in records each of its starts.</summary>
    public string Started => Path.Combine(Directory, "stand-in-started.txt");

    public void SaveSetup() => Write("live-run.json", Setup.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

    /// <summary>The variables that tell the stand-in what to do when the script starts it.</summary>
    public Dictionary<string, string?> StandIn(int exitCode = 0, bool hang = false, params string[] lines)
    {
        var output = Write("stand-in-output.jsonl", string.Concat(lines.Select(line => line + "\n")));
        return new Dictionary<string, string?>
        {
            ["YAV_STANDIN_RECORD"] = Started,
            ["YAV_STANDIN_OUTPUT"] = output,
            ["YAV_STANDIN_EXIT"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["YAV_STANDIN_HANG"] = hang ? "1" : "0",
        };
    }

    /// <summary>The process ids the stand-in recorded, one for each time it was started.</summary>
    public List<int> Starts => System.IO.File.Exists(Started)
        ? System.IO.File.ReadAllLines(Started).Where(line => line.StartsWith("pid=", StringComparison.Ordinal)).Select(line => int.Parse(line[4..], System.Globalization.CultureInfo.InvariantCulture)).ToList()
        : [];

    public string File(string name) => Path.Combine(Directory, name);

    public string Write(string name, string content)
    {
        System.IO.File.WriteAllText(File(name), content, new UTF8Encoding(false));
        return File(name);
    }

    public string Read(string name) => System.IO.File.ReadAllText(File(name));

    public bool Exists(string name) => System.IO.File.Exists(File(name));

    public void Dispose()
    {
        // A stand-in that still runs because a test failed is ended, and only when it is still the stand-in.
        foreach (var id in Starts)
        {
            try
            {
                using var process = Process.GetProcessById(id);
                if (string.Equals(process.MainModule?.FileName, StandInYav.Executable, StringComparison.OrdinalIgnoreCase))
                {
                    process.Kill();
                    process.WaitForExit(10_000);
                }
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                // It has ended already.
            }
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (System.IO.Directory.Exists(Directory))
                {
                    System.IO.Directory.Delete(Directory, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }
}

/// <summary>
/// The directory of a live run whose data directory has the scripted stand-in in place of every agent, so that the
/// parts of the live run can drive the real program where no model is asked anything. The parts are given what
/// scripts\live-run.ps1 would give them, without a variable of the process being changed.
/// </summary>
internal sealed class StandInRun : IDisposable
{
    private readonly TempDirectory _directory = new("live run");

    public StandInRun()
    {
        Agents = new AgentFixture();
        Home = new YavPaths(Path.Combine(_directory.Path, "home"));
        Home.EnsureCreated();

        var settings = new AppSettings();
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

        new SettingsStore(Home).Save(settings);
    }

    public string Directory => _directory.Path;

    public AgentFixture Agents { get; }

    public YavPaths Home { get; }

    public string Project => Path.Combine(Directory, "project");

    public static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
            {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }

    /// <summary>The hash of a task file as the setup records it.</summary>
    public static string TaskHash(string task) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(Repository, "bench", "tasks", task, "task.json"))));

    /// <summary>What the script gives the setup, with the routes the holder agreed to.</summary>
    public LivePart Setup(string acknowledged, string modelA = "codex-app-server model-a", string modelB = "claude-cli opus") => Part(new()
    {
        ["YAV_LIVE_RUN"] = "setup",
        ["YAV_LIVE_RECORD"] = "setup",
        ["YAV_LIVE_MODEL_A"] = modelA,
        ["YAV_LIVE_MODEL_B"] = modelB,
        ["YAV_LIVE_EFFORT_A"] = "maximum",
        ["YAV_LIVE_EFFORT_B"] = "maximum",
        ["YAV_LIVE_ACKNOWLEDGED"] = acknowledged,
        ["YAV_LIVE_TRUST_CHECKS"] = "1",
    });

    public LivePart Part(Dictionary<string, string?> variables)
    {
        var all = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["YAV_LIVE_DIR"] = Directory,
            ["YAV_LIVE_EXE"] = YavProcess.Executable,
            ["YAV_LIVE_TASK"] = "01-small-edit",
            ["YAV_LIVE_TASK_HASH"] = TaskHash("01-small-edit"),
            ["YAV_LIVE_MINUTES"] = "5",
        };
        foreach (var (name, value) in variables)
        {
            all[name] = value;
        }

        return new LivePart(all);
    }

    /// <summary>
    /// A run of the project that stopped because Model A asked for something and nobody could answer, as 'yav run --json'
    /// leaves it. The stand-in knows the conversation afterwards, as a provider does, so that /resume continues it; there
    /// Model A asks again and, declined, fixes the file without what it asked for.
    /// </summary>
    public async Task<string> StoppedRunAsync()
    {
        System.IO.Directory.CreateDirectory(Path.Combine(Project, "src"));
        File.WriteAllText(Path.Combine(Project, "src", "app.txt"), "bug\n", new UTF8Encoding(false));
        foreach (var arguments in new[]
        {
            new[] { "init", "--quiet", "--initial-branch=main" }, ["config", "user.email", "someone@example.invalid"], ["config", "user.name", "YAV Tests"],
            ["config", "commit.gpgsign", "false"], ["config", "core.autocrlf", "false"], ["add", "-A"], ["commit", "--quiet", "--no-verify", "-m", "initial"],
        })
        {
            var git = await new Yav.Platform.Processes.ProcessRunner().RunAsync(
                new Yav.Core.Ports.ProcessSpec("git", arguments, Project), new Yav.Core.Ports.CaptureOptions(Timeout: TimeSpan.FromSeconds(60)), CancellationToken.None);
            Assert.True(git.Succeeded, $"git {string.Join(' ', arguments)}: {git.StandardError}");
        }

        var store = new SettingsStore(Home);
        store.Save(store.Load().Settings with
        {
            ModelA = new RoleSelection(CodexAppServerAdapter.AdapterId, "model-a"),
            ModelB = new RoleSelection(CodexAppServerAdapter.AdapterId, "model-b"),
        });
        var database = Yav.Storage.YavDatabase.Open(Home.Database, TimeProvider.System);
        try
        {
            // What the holder did at setup: acknowledged the route, trusted the project and approved its checks.
            database.AcknowledgeRoute($"{CodexAppServerAdapter.AdapterId}:Subscription:openai", "test");
            database.SetProjectTrusted(Project, true);
            var validation = new Yav.Validation.ValidationService(new Yav.Platform.Processes.ProcessRunner(), database, TimeProvider.System, "test");
            var configuration = Yav.Core.Runs.ProjectConfiguration.Empty with { Gates = [CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug")] };
            validation.TrustConfiguration(Project, configuration, validation.Serialize(configuration));
        }
        finally
        {
            database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }

        // Every turn is given before the first start: the stand-in reads what it is to do when it starts.
        Agents
            .ImplementerTurn(Step.Approval("npm install left-pad", onDecline: [Step.Message("Not installed.")]))
            .ImplementerTurn(Step.Approval("npm install left-pad", onDecline: [Step.Write("src/app.txt", "fixed\n"), Step.Message("Fixed without the package.")]))
            .ReviewerTurn(Step.Review("pass"));

        var start = new ProcessStartInfo(YavProcess.Executable)
        {
            WorkingDirectory = Project,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "run", "--project", Project, "--task", "Make the app say fixed.", "--json" })
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[YavPaths.HomeVariable] = Home.Home;
        using var yav = Process.Start(start)!;
        yav.StandardInput.Close();
        var output = yav.StandardOutput.ReadToEndAsync();
        var error = yav.StandardError.ReadToEndAsync();
        using (var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120)))
        {
            await yav.WaitForExitAsync(timeout.Token);
        }

        var result = System.Text.Json.JsonDocument.Parse((await output).Trim().Split('\n')[^1]).RootElement;
        Assert.True(result.GetProperty("outcome").GetString() == "approval_required", $"yav run: {await output}{await error}");
        var conversation = Agents.CodexRequests("turn/start")[0]["params"]!["threadId"]!.GetValue<string>();
        Agents.Codex(c =>
        {
            c["knownThreads"] = new JsonArray { conversation };
            c["threadStates"] = new JsonObject { [conversation] = "interrupted" };
        });
        return result.GetProperty("runId").GetString()!;
    }

    /// <summary>The account routes the data directory records as acknowledged, as "subject|statement".</summary>
    public List<string> AcknowledgedRoutes()
    {
        if (!File.Exists(Home.Database))
        {
            return [];
        }

        var database = Yav.Storage.YavDatabase.Open(Home.Database, TimeProvider.System);
        try
        {
            return database.ListAcknowledgements().Where(a => a.Kind == "route").Select(a => a.Subject + "|" + a.Statement).ToList();
        }
        finally
        {
            database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    public string Read(string relative) => _directory.Read(relative);

    public bool Exists(string relative) => _directory.Exists(relative);

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Agents.Dispose();
        _directory.Dispose();
    }
}
