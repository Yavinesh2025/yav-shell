using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Yav.Adapters;
using Yav.Console.Composition;
using Yav.Console.Input;
using Yav.Console.Rendering;
using Yav.Console.Shell;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Settings;

namespace Yav.Tests.Support;

/// <summary>
/// The shell as the user meets it: the real application below it, the scripted agent fixture in place of
/// the agents, a terminal that exists in memory, and keys that the test types.
/// </summary>
public sealed class ShellHarness : IAsyncDisposable
{
    private readonly TempDirectory _home;
    private readonly ObservedInput _observed;
    private readonly int _width;
    private Task<int>? _running;
    private int _entered;
    private bool _servicesDisposed;

    // False once another shell continues with the same data, project and agents, as after a restart of YAV.
    private bool _ownsEnvironment = true;

    public ShellHarness(Action<AppSettings>? configure = null, int width = 110, params (string Path, string Content)[] files)
        : this(new ShellOptions { Configure = configure, Width = width, Files = files })
    {
    }

    /// <summary>A shell that continues where another one ended: the same data, project and agents, as after a restart of YAV.</summary>
    private ShellHarness(ShellHarness earlier)
    {
        _home = earlier._home;
        _width = earlier._width;
        Project = earlier.Project;
        Agents = earlier.Agents;
        Paths = earlier.Paths;
        Credentials = earlier.Credentials;
        Services = AppServices.Create(Paths, Credentials);
        Terminal = new VirtualTerminal(_width, 50);
        Screen = new Screen(Terminal, new ScreenOptions(Rich: true, Color: true, Unicode: true));
        Input = new TerminalInput(Screen, Keys, History, idle: null, Clock);
        _observed = new ObservedInput(Input);
        Shell = new InteractiveShell(Services, Screen, _observed, host: null, Stopwatch.GetTimestamp());
    }

    public ShellHarness(ShellOptions options)
    {
        var (configure, width, files) = (options.Configure, options.Width, options.Files);
        _home = new TempDirectory("shell home");
        _width = width;
        Project = GitRepo.WithFiles(files.Length > 0 ? files : [("src/app.txt", "one\n"), ("README.md", "# App\n")]);
        Agents = new AgentFixture();
        Paths = new YavPaths(_home.Path);
        Paths.EnsureCreated();

        var settings = new AppSettings
        {
            ModelA = options.ChooseModels ? new RoleSelection(CodexAppServerAdapter.AdapterId, "model-a") : null,
            ModelB = options.ChooseModels ? new RoleSelection(CodexAppServerAdapter.AdapterId, "model-b") : null,
            RequireGates = options.RequireChecks,
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

        configure?.Invoke(settings);
        new SettingsStore(Paths).Save(settings);

        Services = AppServices.Create(Paths, Credentials);
        if (options.AcknowledgeRoutes)
        {
            Services.Database.AcknowledgeRoute($"{CodexAppServerAdapter.AdapterId}:Subscription:openai", "test");
            Services.Database.AcknowledgeRoute($"{ClaudeCliAdapter.AdapterId}:Subscription:firstParty", "test");
        }

        Services.Database.SetProjectTrusted(Project.Path, options.TrustProject);

        Terminal = new VirtualTerminal(width, 50);
        if (options.Lines is { } lines)
        {
            // A console that cannot position the cursor: lines go in, and plain lines come out.
            Lines = lines;
            Screen = new Screen(Terminal, new ScreenOptions(Rich: false, Color: false, Unicode: true));
            Input = new PlainInput(Screen, lines, options.LinesAreSeen);
        }
        else
        {
            Screen = new Screen(Terminal, new ScreenOptions(Rich: true, Color: true, Unicode: true));
            Input = new TerminalInput(Screen, Keys, History, options.IdleEditor, Clock);
        }

        _observed = new ObservedInput(Input);
        Shell = new InteractiveShell(Services, Screen, _observed, host: null, Stopwatch.GetTimestamp());
    }

    /// <summary>What a question of an agent waits with, when nothing was typed for it yet (ConsoleApprovals).</summary>
    public const string QuestionEnd = "then Enter:";

    public GitRepo Project { get; }

    public AgentFixture Agents { get; }

    public YavPaths Paths { get; }

    public AppServices Services { get; }

    public VirtualTerminal Terminal { get; }

    public Screen Screen { get; }

    public ScriptedKeys Keys { get; } = new();

    /// <summary>The lines of a shell that reads lines instead of keys. Null for a shell that reads keys.</summary>
    public ScriptedLines? Lines { get; }

    /// <summary>What the time a question of an agent has been open is measured with. It moves when the test moves it.</summary>
    public ManualClock Clock { get; } = new();

    /// <summary>What the shell remembers of what was entered.</summary>
    public InputHistory History { get; } = new();

    /// <summary>Stands for the Credential Manager, so that no test reads or changes a secret of the user.</summary>
    public MemoryCredentialStore Credentials { get; } = new();

    public IShellInput Input { get; }

    public InteractiveShell Shell { get; }

    /// <summary>The prompt as the shell shows it for the project that is selected now.</summary>
    public string Prompt => Shell.Session.ProjectPath is { } project ? $"YAV {project}> " : "YAV> ";

    public void TrustGates(params GateDefinition[] gates)
    {
        var configuration = ProjectConfiguration.Empty with { Gates = gates };
        Services.Validation.TrustConfiguration(Project.Path, configuration, Services.Validation.Serialize(configuration));
    }

    /// <summary>The usual run: the implementer changes the file, the reviewer passes it and the check accepts it.</summary>
    public ShellHarness WithPassingRun()
    {
        TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Changed app.txt to say fixed."))
            .ReviewerTurn(Step.Review("pass"));
        return this;
    }

    public void Start() => StartIn(Project.Path);

    /// <summary>Starts the shell as 'yav &lt;path&gt;' does.</summary>
    public void StartIn(string path) => _running = Task.Run(() => Shell.RunAsync(path, CancellationToken.None));

    /// <summary>
    /// Starts the shell as 'yav' alone does, in the directory of the test process: from a shell, or, with
    /// <paramref name="startedOutsideAShell"/>, from a console Windows made for it alone.
    /// </summary>
    public void StartWithoutPath(bool startedOutsideAShell) =>
        _running = Task.Run(() => Shell.RunAsync(null, CancellationToken.None, startedOutsideAShell));

    /// <summary>Waits until the shell has ended by itself and returns its exit code.</summary>
    public async Task<int> WaitForExitAsync(int seconds = 90) => await _running!.WaitAsync(TimeSpan.FromSeconds(seconds));

    /// <summary>
    /// Ends this shell as closing YAV does and returns one that starts on what it left behind. Dispose the
    /// one that is returned; this one owns nothing any more.
    /// </summary>
    public async Task<ShellHarness> RestartAsync()
    {
        Keys.End();
        if (_running is not null)
        {
            await _running.WaitAsync(TimeSpan.FromSeconds(90));
        }

        await DisposeServicesAsync();
        _ownsEnvironment = false;
        return new ShellHarness(this);
    }

    /// <summary>Types a line and presses Enter, or sends the line to a shell that reads lines.</summary>
    public ShellHarness Enter(string text)
    {
        Interlocked.Increment(ref _entered);
        if (Lines is { } lines)
        {
            lines.Send(text);
        }
        else
        {
            Keys.Type(text).Enter();
        }

        return this;
    }

    /// <summary>
    /// Waits until a question of an agent is open for keys with nothing typed for it yet, and lets the time it
    /// takes to read it pass on the clock of the question.
    /// </summary>
    public async Task WaitForQuestionAsync(int seconds = 60)
    {
        await WaitUntilAsync(
            () => Terminal.CursorLine.StartsWith("Allow ", StringComparison.Ordinal) && Terminal.CursorLine.EndsWith(QuestionEnd, StringComparison.Ordinal),
            "a question of an agent that waits for its answer",
            seconds);
        Clock.Advance(ConsoleApprovals.Settle);
    }

    /// <summary>Types an answer to the question that is open, and Enter.</summary>
    public ShellHarness Answer(string answer)
    {
        Keys.Type(answer).Enter();
        return this;
    }

    /// <summary>
    /// Waits until the shell asks a question of its own, one that begins with the text and waits for a line with
    /// nothing typed for it yet. A question that is the same text as one before is told apart by the count of rows
    /// that ask it: <paramref name="occurrence"/> is the how-manieth time it is asked.
    /// </summary>
    public async Task WaitForAskingAsync(string question, int occurrence = 1, int seconds = 60)
    {
        var text = question.TrimEnd();
        await WaitUntilAsync(
            () => Terminal.CursorLine.StartsWith(text, StringComparison.Ordinal)
                && Terminal.CursorLine.TrimEnd().EndsWith(':')
                && Terminal.Lines.Count(row => row.StartsWith(text, StringComparison.Ordinal)) >= occurrence,
            $"the question '{text}' (asked {occurrence} time(s))",
            seconds);
    }

    /// <summary>Waits for the question and answers it with a line.</summary>
    public async Task AnswerWhenAskedAsync(string question, string answer, int occurrence = 1, int seconds = 60)
    {
        await WaitForAskingAsync(question, occurrence, seconds);
        Enter(answer);
    }

    /// <summary>
    /// What the screen says as it is read: where a row ends and how many blanks separate words does not
    /// matter. The rows themselves are in <see cref="Terminal"/>.
    /// </summary>
    public string Reading => Flatten(Terminal.Text);

    public static string Flatten(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The rows at the bottom of the screen, which is where a test that failed has to look.</summary>
    private string Tail => string.Join(Environment.NewLine, Terminal.Lines.TakeLast(34));

    public bool Shows(string text) => Reading.Contains(Flatten(text), StringComparison.Ordinal);

    public void AssertShows(params string[] texts)
    {
        var reading = Reading;
        foreach (var text in texts)
        {
            Assert.True(reading.Contains(Flatten(text), StringComparison.Ordinal), $"The screen does not show '{text}'. The screen:\n{Tail}");
        }
    }

    public void AssertDoesNotShow(params string[] texts)
    {
        var reading = Reading;
        foreach (var text in texts)
        {
            Assert.False(reading.Contains(Flatten(text), StringComparison.Ordinal), $"The screen shows '{text}'. The screen:\n{Tail}");
        }
    }

    /// <summary>Enters a line and waits until the shell has dealt with it.</summary>
    public async Task EnterAndWaitAsync(string text, int seconds = 60)
    {
        Enter(text);
        await WaitForPromptAsync(seconds);
    }

    public async Task WaitForAsync(string text, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!Shows(text))
        {
            FailIfTheShellFailed();
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"'{text}' did not appear within {seconds} seconds. The screen:\n{Tail}");
            }

            await Task.Delay(20);
        }
    }

    public async Task WaitUntilAsync(Func<bool> reached, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!reached())
        {
            FailIfTheShellFailed();
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Within {seconds} seconds the screen did not show {what}. The screen:{Environment.NewLine}{Tail}");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Waits until the shell has dealt with every line that was entered and asks for the next one, showing
    /// the draft that was typed since. A run may still be active.
    /// </summary>
    public async Task WaitForPromptAsync(int seconds = 60, string draft = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!Settled(draft))
        {
            FailIfTheShellFailed();
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"The shell did not return to its prompt within {seconds} seconds. The screen:\n{Tail}");
            }

            await Task.Delay(20);
        }
    }

    /// <summary>Waits until no run is active, nothing waits to be started, and the prompt is back.</summary>
    public async Task WaitForRunToEndAsync(int seconds = 90, string draft = "")
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!(Settled(draft) && Shell.Session.Active is null && _observed.ReadingWithoutRun))
        {
            FailIfTheShellFailed();
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"The run did not end within {seconds} seconds. The screen:\n{Tail}");
            }

            await Task.Delay(20);
        }
    }

    public async Task<int> ExitAsync()
    {
        Keys.Type("/exit").Enter();
        return await _running!.WaitAsync(TimeSpan.FromSeconds(60));
    }

    public async ValueTask DisposeAsync()
    {
        Keys.End();
        Lines?.End();
        if (_running is not null)
        {
            try
            {
                await _running.WaitAsync(TimeSpan.FromSeconds(75));
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidOperationException or OperationCanceledException)
            {
            }
        }

        await DisposeServicesAsync();
        if (_ownsEnvironment)
        {
            Agents.Dispose();
            Project.Dispose();
            _home.Dispose();
        }
    }

    private async Task DisposeServicesAsync()
    {
        if (_servicesDisposed)
        {
            return;
        }

        _servicesDisposed = true;
        await Services.DisposeAsync();
        SqliteConnection.ClearAllPools();
    }

    private bool Settled(string draft) =>
        !Keys.KeyAvailable
        && _observed.WaitsAfter(Volatile.Read(ref _entered))
        && Terminal.CursorLine == (Prompt + draft).TrimEnd();

    private void FailIfTheShellFailed()
    {
        if (_running is { IsFaulted: true })
        {
            throw new InvalidOperationException("The shell ended with an error.", _running.Exception);
        }
    }

    /// <summary>
    /// Tells a test when the shell has finished with what was entered: it counts the lines the shell was
    /// given and knows whether the shell is asking for the next one.
    /// </summary>
    private sealed class ObservedInput(IShellInput inner) : IShellInput
    {
        private readonly Lock _gate = new();
        private int _given;
        private bool _reading;
        private bool _withoutRun;

        public bool CanAsk => inner.CanAsk;

        public IApprovalBroker Approvals => inner.Approvals;

        /// <summary>True while the shell asks for input and no run was active when it began to.</summary>
        public bool ReadingWithoutRun
        {
            get
            {
                lock (_gate)
                {
                    return _reading && _withoutRun;
                }
            }
        }

        /// <summary>True while the shell asks for input and has been given at least that many lines before.</summary>
        public bool WaitsAfter(int lines)
        {
            lock (_gate)
            {
                return _reading && _given >= lines;
            }
        }

        public async Task<InputResult> ReadAsync(Line prompt, bool runActive, CancellationToken cancellationToken)
        {
            lock (_gate)
            {
                _reading = true;
                _withoutRun = !runActive;
            }

            var given = false;
            try
            {
                var result = await inner.ReadAsync(prompt, runActive, cancellationToken).ConfigureAwait(false);
                given = result.Outcome == InputOutcome.Submitted;
                return result;
            }
            finally
            {
                Finished(given);
            }
        }

        public async Task<string?> AskAsync(string question, CancellationToken cancellationToken)
        {
            var answer = await inner.AskAsync(question, cancellationToken).ConfigureAwait(false);
            Finished(answer is not null);
            return answer;
        }

        public async Task<string?> AskSecretAsync(string question, CancellationToken cancellationToken)
        {
            var answer = await inner.AskSecretAsync(question, cancellationToken).ConfigureAwait(false);
            Finished(answer is not null);
            return answer;
        }

        public IDisposable ReleaseKeyboard() => inner.ReleaseKeyboard();

        public void Remember(string text) => inner.Remember(text);

        public ValueTask DisposeAsync() => inner.DisposeAsync();

        private void Finished(bool given)
        {
            lock (_gate)
            {
                _reading = false;
                if (given)
                {
                    _given++;
                }
            }
        }
    }
}

/// <summary>How a shell for a test differs from the usual one.</summary>
public sealed record ShellOptions
{
    public Action<AppSettings>? Configure { get; init; }

    public int Width { get; init; } = 110;

    /// <summary>False leaves the account routes as a new user finds them: not acknowledged.</summary>
    public bool AcknowledgeRoutes { get; init; } = true;

    /// <summary>False starts as a new user does: neither Model A nor Model B is chosen.</summary>
    public bool ChooseModels { get; init; } = true;

    public bool TrustProject { get; init; } = true;

    /// <summary>True as after /quality gates required: a project with no approved check that is required does not run.</summary>
    public bool RequireChecks { get; init; }

    /// <summary>The editor for the time no run is active. Null uses the simple editor throughout, which a test can drive key by key.</summary>
    public IIdleEditor? IdleEditor { get; init; }

    /// <summary>Lines instead of keys: the shell then reads them as it reads a console that cannot position the cursor.</summary>
    public ScriptedLines? Lines { get; init; }

    /// <summary>False when nobody sees what the shell writes, as when its output goes to a file. Only with <see cref="Lines"/>.</summary>
    public bool LinesAreSeen { get; init; } = true;

    public (string Path, string Content)[] Files { get; init; } = [];
}

public sealed class MemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public bool IsAvailable { get; set; } = true;

    public bool Exists(string name)
    {
        lock (_gate)
        {
            return _secrets.ContainsKey(name);
        }
    }

    public string? Read(string name)
    {
        lock (_gate)
        {
            return _secrets.GetValueOrDefault(name);
        }
    }

    public void Write(string name, string secret, string comment)
    {
        lock (_gate)
        {
            _secrets[name] = secret;
        }
    }

    public bool Delete(string name)
    {
        lock (_gate)
        {
            return _secrets.Remove(name);
        }
    }
}
