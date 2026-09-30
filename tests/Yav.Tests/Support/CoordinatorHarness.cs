using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using Yav.Adapters;
using Yav.Coordinator;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Platform.Processes;
using Yav.Storage;
using Yav.Validation;
using Yav.Workspace;

namespace Yav.Tests.Support;

/// <summary>
/// The whole application below the console: real adapters talking to the scripted agent fixture, a real Git
/// project, the real workspace service, validator and database.
/// </summary>
public sealed class CoordinatorHarness : IAsyncDisposable
{
    public const string CodexId = CodexAppServerAdapter.AdapterId;
    public const string ClaudeId = ClaudeCliAdapter.AdapterId;

    private readonly TempDirectory _home = new("home");
    private readonly bool _ownsProject;

    public CoordinatorHarness(params (string Path, string Content)[] files)
        : this(GitRepo.WithFiles(files.Length > 0 ? files : [("src/app.txt", "one\n"), ("README.md", "# App\n")]).Path, null, true, null)
    {
    }

    /// <summary>A harness over an existing directory, which need not be a Git repository.</summary>
    public CoordinatorHarness(string projectPath, TimeProvider? clock = null)
        : this(projectPath, clock, false, null)
    {
    }

    /// <summary>A harness that decides which processes of earlier runs count as still alive.</summary>
    public CoordinatorHarness(Func<int, bool> processIsAlive)
        : this(GitRepo.WithFiles(("src/app.txt", "one\n"), ("README.md", "# App\n")).Path, null, true, processIsAlive)
    {
    }

    private CoordinatorHarness(string projectPath, TimeProvider? clock, bool ownsProject, Func<int, bool>? processIsAlive)
    {
        _ownsProject = ownsProject;
        ProjectPath = projectPath;
        Clock = clock ?? TimeProvider.System;
        Agents = new AgentFixture();
        Paths = new YavPaths(_home.Path);
        Paths.EnsureCreated();
        Database = YavDatabase.Open(Paths.Database, Clock);
        Runner = new ProcessRunner();
        Workspaces = new WorkspaceService(Paths, Runner, Database, Clock);
        Validation = new ValidationService(Runner, Database, Clock, "0.1.0-test");
        Codex = Agents.CodexAppServer();
        Claude = Agents.ClaudeCli();
        Exec = Agents.CodexExec();
        Adapters = new Dictionary<string, IAgentAdapter>(StringComparer.OrdinalIgnoreCase)
        {
            [Codex.Id] = Codex,
            [Claude.Id] = Claude,
            [Exec.Id] = Exec,
        };

        // The user has been through /login and the trust prompts.
        Database.AcknowledgeRoute($"{CodexId}:Subscription:openai", "test");
        Database.AcknowledgeRoute($"{CodexExecAdapter.AdapterId}:Subscription:openai", "test");
        Database.AcknowledgeRoute($"{ClaudeId}:Subscription:firstParty", "test");
        Database.SetProjectTrusted(ProjectPath, true);

        Coordinator = new RunCoordinator(
            new CoordinatorServices(Adapters, Workspaces, Validation, Database, Database, Database, Clock, "0.1.0-test"),
            new CoordinatorOptions
            {
                InterruptGrace = TimeSpan.FromSeconds(5),
                ShutdownGrace = TimeSpan.FromSeconds(3),
                ProcessIsAlive = processIsAlive,
            });
    }

    public string ProjectPath { get; }

    public TimeProvider Clock { get; }

    public AgentFixture Agents { get; }

    public YavPaths Paths { get; }

    public YavDatabase Database { get; }

    public ProcessRunner Runner { get; }

    public WorkspaceService Workspaces { get; }

    public ValidationService Validation { get; }

    public CodexAppServerAdapter Codex { get; }

    public ClaudeCliAdapter Claude { get; }

    public CodexExecAdapter Exec { get; }

    public Dictionary<string, IAgentAdapter> Adapters { get; }

    public RunCoordinator Coordinator { get; }

    public RecordingObserver Observer { get; } = new();

    public ScriptedApprovals Approvals { get; } = new();

    public RunConfiguration Configuration { get; set; } = new(
        ModelA: new RoleSelection(CodexId, "model-a"),
        ModelB: new RoleSelection(CodexId, "model-b"),
        Policy: new QualityPolicy(),
        Speed: ProviderSpeedMode.Standard,
        Limits: RunLimits.Default);

    /// <summary>Gives the runs a Claude Code adapter with less patience, for an agent that does not answer. Before the first run.</summary>
    public void UseClaude(TimeSpan startupTimeout)
    {
        var replaced = Adapters[ClaudeId];
        Adapters[ClaudeId] = Agents.ClaudeCli(startupTimeout: startupTimeout);
        replaced.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }

    public string InProject(string relative) => Path.Combine(ProjectPath, relative.Replace('/', Path.DirectorySeparatorChar));

    public string ReadProject(string relative) => File.ReadAllText(InProject(relative));

    public void WriteProject(string relative, string content)
    {
        var path = InProject(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    /// <summary>A gate that passes when the file contains the text.</summary>
    public static GateDefinition TextGate(string id, string path, string text, bool required = true, int timeoutSeconds = 60) => new(
        Id: id,
        Kind: GateKind.Test,
        Title: id,
        Command: Fixtures.FakeAgent,
        Arguments: ["tool", "expect-text", path, text],
        WorkingDirectory: string.Empty,
        TimeoutSeconds: timeoutSeconds,
        Required: required,
        Environment: new Dictionary<string, string>(),
        SuccessExitCodes: [0],
        Requires: []);

    /// <summary>A gate that passes as long as the file does not contain the text, like a suite that catches a regression.</summary>
    public static GateDefinition NoTextGate(string id, string path, string text) => ToolGate(id, "expect-no-text", path, text);

    public static GateDefinition ToolGate(string id, params string[] toolArguments) => new(
        Id: id,
        Kind: GateKind.Test,
        Title: id,
        Command: Fixtures.FakeAgent,
        Arguments: ["tool", .. toolArguments],
        WorkingDirectory: string.Empty,
        TimeoutSeconds: 60,
        Required: true,
        Environment: new Dictionary<string, string>(),
        SuccessExitCodes: [0],
        Requires: []);

    /// <summary>Approves a configuration as the user would after reviewing it.</summary>
    public ProjectConfiguration Trust(ProjectConfiguration configuration)
    {
        Validation.TrustConfiguration(ProjectPath, configuration, Validation.Serialize(configuration));
        return configuration;
    }

    public ProjectConfiguration TrustGates(params GateDefinition[] gates) => Trust(ProjectConfiguration.Empty with { Gates = gates });

    public Task<RunOutcome> RunAsync(string text, string? taskId = null, CancellationToken cancellationToken = default, WorkspaceMode? mode = null) =>
        Coordinator.RunAsync(
            new RunRequest(ProjectPath, text) { TaskId = taskId, Mode = mode },
            Configuration,
            Observer,
            Approvals,
            cancellationToken);

    /// <summary>The prompts the fixture received in threads that were opened with the given sandbox.</summary>
    public List<string> Prompts(string sandbox)
    {
        // The fixture names threads thr-<n>-<pid> in the order they were started.
        var sandboxes = new Dictionary<string, string>(StringComparer.Ordinal);
        var ordinal = 0;
        foreach (var start in Agents.CodexRequests("thread/start"))
        {
            ordinal++;
            sandboxes[$"thr-{ordinal}-{start["_pid"]?.GetValue<int>()}"] = start["params"]?["sandbox"]?.GetValue<string>() ?? string.Empty;
        }

        var prompts = new List<string>();
        foreach (var turn in Agents.CodexRequests("turn/start"))
        {
            var threadId = turn["params"]?["threadId"]?.GetValue<string>() ?? string.Empty;
            if (sandboxes.GetValueOrDefault(threadId) != sandbox)
            {
                continue;
            }

            prompts.Add(string.Join(
                '\n',
                (turn["params"]?["input"] as JsonArray ?? []).Select(i => i?["text"]?.GetValue<string>() ?? string.Empty)));
        }

        return prompts;
    }

    /// <summary>Fails with the reason and everything the run reported when it did not end as expected.</summary>
    public void AssertEnded(RunOutcomeKind expected, RunOutcome outcome)
    {
        if (outcome.Kind != expected)
        {
            Assert.Fail($"Expected the run to end as {expected}, but it ended as {outcome.Kind}: {outcome.Reason}\n{Observer.Transcript()}");
        }
    }

    public List<string> ImplementerPrompts() => Prompts("workspace-write");

    public List<string> ReviewerPrompts() => Prompts("read-only");

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync();
        foreach (var adapter in Adapters.Values)
        {
            await adapter.DisposeAsync();
        }

        Database.Dispose();
        SqliteConnection.ClearAllPools();
        Agents.Dispose();
        if (_ownsProject)
        {
            try
            {
                await Runner.RunAsync(
                    new ProcessSpec("git", ["worktree", "prune"], ProjectPath),
                    new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
                    CancellationToken.None);
            }
            catch (Exception)
            {
            }

            DeleteDirectory(ProjectPath);
        }

        _home.Dispose();
    }

    private static void DeleteDirectory(string path)
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                {
                    foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
                    {
                        var attributes = File.GetAttributes(file);
                        if ((attributes & FileAttributes.ReadOnly) != 0)
                        {
                            File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                        }
                    }

                    Directory.Delete(path, recursive: true);
                }

                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Thread.Sleep(100 * (attempt + 1));
            }
        }
    }
}

/// <summary>Keeps every event a run published, in order.</summary>
public sealed class RecordingObserver : IRunObserver
{
    private readonly ConcurrentQueue<RunEvent> _events = new();

    public IReadOnlyList<RunEvent> Events => _events.ToArray();

    public event Action<RunEvent>? Published;

    public void OnEvent(RunEvent runEvent)
    {
        _events.Enqueue(runEvent);
        Published?.Invoke(runEvent);
    }

    public IEnumerable<T> Of<T>()
        where T : RunEvent => _events.OfType<T>();

    public IEnumerable<StageNote> Notes(string? stage = null) => Of<StageNote>().Where(n => stage is null || n.Stage == stage);

    /// <summary>The stage labels in the order they first appeared.</summary>
    public List<string> StageOrder()
    {
        var order = new List<string>();
        foreach (var note in Of<StageNote>())
        {
            if (!order.Contains(note.Stage))
            {
                order.Add(note.Stage);
            }
        }

        return order;
    }

    public IEnumerable<RunState> States() => Of<StateChanged>().Select(s => s.To);

    public string Transcript() => string.Join("\n", Of<StageNote>().Select(n => $"[{n.Stage}] {n.Message}"));
}

/// <summary>Answers approval requests the way the test says the user would.</summary>
public sealed class ScriptedApprovals : IApprovalBroker
{
    private readonly ConcurrentQueue<ApprovalRequest> _asked = new();

    public bool CanAsk { get; set; } = true;

    public Func<ApprovalRequest, ApprovalDecision> Decide { get; set; } = _ => ApprovalDecision.Accept;

    /// <summary>When set, the answer is held back until the task completes, like a user who has not answered yet.</summary>
    public TaskCompletionSource<bool>? Hold { get; set; }

    /// <summary>The answer comes this long after the question.</summary>
    public TimeSpan? AnswerAfter { get; set; }

    /// <summary>Stands for a prompt that does not notice that the question was taken back.</summary>
    public bool IgnoreCancellation { get; set; }

    /// <summary>Completes when the broker has been asked.</summary>
    public TaskCompletionSource<ApprovalRequest> Asked { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public IReadOnlyList<ApprovalRequest> Requests => _asked.ToArray();

    public bool WasCancelled { get; private set; }

    public async Task<ApprovalDecision> AskAsync(string runId, AgentRole role, ApprovalRequest request, CancellationToken cancellationToken)
    {
        _asked.Enqueue(request);
        Asked.TrySetResult(request);
        if (AnswerAfter is { } delay)
        {
            await Task.Delay(delay, IgnoreCancellation ? CancellationToken.None : cancellationToken);
        }

        if (Hold is not null)
        {
            try
            {
                await Hold.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                WasCancelled = true;
                throw;
            }
        }

        return Decide(request);
    }
}
