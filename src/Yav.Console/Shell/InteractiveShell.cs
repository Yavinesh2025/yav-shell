using System.Diagnostics;
using Yav.Console.Commands;
using Yav.Console.Composition;
using Yav.Console.Input;
using Yav.Console.Rendering;
using Yav.Coordinator;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Timing;
using Yav.Platform.Consoles;

namespace Yav.Console.Shell;

/// <summary>A run that was started from the shell and has not ended yet.</summary>
public sealed class ActiveRun
{
    // Cancelled once, when the run has ended. It is never disposed of: a read that waits for input may be woken by it late.
    private readonly CancellationTokenSource _ended = new();
    private readonly Lock _gate = new();
    private readonly HashSet<string> _noted = new(StringComparer.Ordinal);
    private Task<RunOutcome> _task = System.Threading.Tasks.Task.FromResult<RunOutcome>(null!);
    private bool _stopRequested;
    private bool _released;

    public required string Request { get; init; }

    /// <summary>
    /// The request as it was sent, so that the guided first run can send it again unchanged once what kept it
    /// from starting is settled. Null for a run that was resumed or checked again.
    /// </summary>
    internal SentRequest? Sent { get; init; }

    public required CancellationTokenSource Stop { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>The row that shows the run still works, in a terminal that can show one. Removed when the run ends.</summary>
    public RunProgress? Progress { get; set; }

    public Task<RunOutcome> Task
    {
        get => _task;
        set
        {
            _task = value;
            if (Progress is { } progress)
            {
                _ = value.ContinueWith(
                    static (_, shown) => ((RunProgress)shown!).Dispose(),
                    progress,
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default);
            }

            // One continuation for the whole run, however many lines are read while it is active.
            _ = value.ContinueWith(
                static (_, ended) => ((CancellationTokenSource)ended!).Cancel(),
                _ended,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    /// <summary>Cancelled when the run has ended, so that a read that waits for input ends with it and its result is shown at once.</summary>
    public CancellationToken Ended => _ended.Token;

    /// <summary>Known once the run exists; a run that was refused before it started has none.</summary>
    public string? RunId { get; set; }

    public string? TaskId { get; set; }

    public bool StopRequested
    {
        get
        {
            lock (_gate)
            {
                return _stopRequested;
            }
        }
    }

    /// <summary>True once the run has said how it ended. A request that was refused before anything began says nothing.</summary>
    public bool SaidHowItEnded { get; set; }

    /// <summary>Marks the run as asked to stop, on the thread of the shell. False when it was asked before, or when the shell let go of it.</summary>
    public bool TryBeginStop()
    {
        lock (_gate)
        {
            if (_stopRequested || _released)
            {
                return false;
            }

            _stopRequested = true;
            return true;
        }
    }

    /// <summary>
    /// Asks the run to stop from any thread, such as the one on which Control+C arrives. False when it was asked
    /// before, or when the shell let go of it: its source of cancellation may be gone then.
    /// </summary>
    public bool RequestStop()
    {
        lock (_gate)
        {
            if (_stopRequested || _released)
            {
                return false;
            }

            _stopRequested = true;
            Stop.Cancel();
            return true;
        }
    }

    /// <summary>The shell has taken the result and lets go of the run.</summary>
    public void Release()
    {
        lock (_gate)
        {
            _released = true;
            Stop.Dispose();
        }
    }

    /// <summary>Remembers what the run said, so that the shell does not say it a second time.</summary>
    public void Noted(string message)
    {
        lock (_gate)
        {
            _noted.Add(message);
        }
    }

    public bool HasNoted(string message)
    {
        lock (_gate)
        {
            return _noted.Contains(message);
        }
    }
}

/// <summary>What the shell knows between two inputs.</summary>
public sealed class ShellSession
{
    public string? ProjectPath { get; set; }

    /// <summary>The task that requests continue. /new ends it.</summary>
    public string? TaskId { get; set; }

    public string? LastRunId { get; set; }

    public List<string> Attachments { get; } = [];

    public ActiveRun? Active { get; set; }

    public bool Verbose { get; set; }

    public bool ExitRequested { get; set; }

    /// <summary>True after a run ended in a way that needs a decision, so waiting requests are not started by themselves.</summary>
    public bool QueuePaused { get; set; }

    /// <summary>The effort the user approved for Model A for the task, in Adaptive mode. Null is the effort that was chosen for the role.</summary>
    public string? TaskEffort { get; set; }
}

/// <summary>
/// The shell: an inline prompt in the user's own terminal. Text without a slash is a request for Model A,
/// text with a slash is a command of YAV. Nothing the user types is ever run by the operating system unless
/// it was given to /exec or entered in /shell.
/// </summary>
public sealed partial class InteractiveShell
{
    private readonly AppServices _services;
    private readonly Screen _screen;
    private readonly Ui _ui;
    private readonly IShellInput _input;
    private readonly ConsoleHost? _host;
    private readonly ShellSession _session = new();
    private readonly long _processStarted;
    private Task<ReconciliationReport>? _reconciliation;
    private bool _interruptedOnce;
    private bool _startupMeasured;

    // The question of the guided first run that is open, if any: Control+C as a signal cancels it.
    private CancellationTokenSource? _guideQuestion;

    // Whether the last ConfirmAsync got no answer at all (Control+C, or the end of the input), as opposed to a typed one.
    private bool _confirmUnanswered;

    public InteractiveShell(AppServices services, Screen screen, IShellInput input, ConsoleHost? host, long processStartedTimestamp)
    {
        _services = services;
        _screen = screen;
        _ui = new Ui(screen);
        _input = input;
        _host = host;
        _processStarted = processStartedTimestamp;
    }

    public ShellSession Session => _session;

    private RunCoordinator Coordinator => _services.Coordinator;

    /// <param name="startedOutsideAShell">
    /// True when Windows made the console for YAV alone: opened from Explorer, the Start menu or the Run dialog. The
    /// directory it starts in then says nothing about a project, so without a path none is selected, and the first
    /// request asks for the folder.
    /// </param>
    public async Task<int> RunAsync(string? projectPath, CancellationToken cancellationToken, bool startedOutsideAShell = false)
    {
        OpenInitialProject(projectPath, startedOutsideAShell);
        Banner();
        foreach (var problem in _services.StartupProblems)
        {
            _ui.Warn(problem);
        }

        // Looking for what a crash left behind asks the providers about conversations. It must not delay the prompt.
        _reconciliation = Task.Run(() => Coordinator.ReconcileAsync(cancellationToken), cancellationToken);
        _ = _reconciliation.ContinueWith(ReportReconciliation, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        _ = Task.Run(WarmUpAgentsAsync, cancellationToken);
        RestoreLastRun();

        while (!_session.ExitRequested && !cancellationToken.IsCancellationRequested)
        {
            await CompleteRunIfEndedAsync(cancellationToken).ConfigureAwait(false);
            if (_session.ExitRequested)
            {
                break;
            }

            var active = _session.Active;
            if (active is not null && !_input.CanAsk)
            {
                // Nobody is typing: what comes from a pipe or a file is read line by line, each line after the
                // run before it has ended. The end of the input therefore lets a run finish.
                await WaitForAsync(active, cancellationToken).ConfigureAwait(false);
                continue;
            }

            // The read ends when the run does, so that its result is shown at once.
            using var wake = active is null
                ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken)
                : CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, active.Ended);

            MeasureStartup();
            InputResult input;
            try
            {
                input = await _input.ReadAsync(Prompt(), active is not null, wake.Token).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                continue;
            }

            switch (input.Outcome)
            {
                case InputOutcome.Cancelled:
                    continue;

                case InputOutcome.EndOfInput:
                    await LeaveAsync(waitForRun: true).ConfigureAwait(false);
                    continue;

                case InputOutcome.Interrupted:
                    await InterruptedAsync().ConfigureAwait(false);
                    continue;
            }

            _interruptedOnce = false;
            try
            {
                await HandleAsync(input.Text, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (IsCommandFailure(ex))
            {
                // A command that failed must not take the shell with it.
                _ui.Error($"{ex.GetType().Name}: {ex.Message}");
            }
        }

        await _input.DisposeAsync().ConfigureAwait(false);
        return 0;
    }

    /// <summary>
    /// What a command may fail with, and the guided first run as well when it records an answer, without taking the
    /// shell with it: what failed is said, and the shell asks for the next line.
    /// </summary>
    private static bool IsCommandFailure(Exception ex) =>
        ex is AgentException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException;

    /// <summary>
    /// Control+C where it does not arrive as a key: stops the run that is active, or cancels the question of the
    /// guided first run that is open, which then sends nothing. False when there is neither, which leaves the
    /// decision to whoever asked.
    /// </summary>
    public bool Interrupt()
    {
        if (_session.Active is not { } run)
        {
            if (Volatile.Read(ref _guideQuestion) is not { } question)
            {
                return false;
            }

            try
            {
                question.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The question was answered at this very moment. Nothing is left to cancel.
            }

            return true;
        }

        // The shell may let go of the run on its own thread at this very moment. Then there is nothing to stop.
        if (run.RequestStop())
        {
            _ui.Note(Stages.Stopped, "Asking the agent to stop. Its work so far is kept.");
        }

        return true;
    }

    private static async Task WaitForAsync(ActiveRun run, CancellationToken cancellationToken)
    {
        try
        {
            await run.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // How the run ended is reported where its result is taken.
            _ = ex;
        }
    }

    private Line Prompt()
    {
        var project = _session.ProjectPath;
        return project is null
            ? Line.Of(new Segment("YAV> ", Tone.Prompt))
            : Line.Of(new Segment("YAV ", Tone.Prompt), new Segment(project, Tone.Prompt), new Segment("> ", Tone.Prompt));
    }

    private void Banner()
    {
        var settings = _services.Settings;
        _ui.Lines(
        [
            Line.Of("YAV Shell", Tone.Accent, bold: true),
            Line.Of($"Project: {_session.ProjectPath ?? "none selected - use /open <path>"}"),
            Line.Of($"Code: {Describe(settings.ModelA)} | Review: {Describe(settings.ModelB)}"),
            Line.Of($"Effort: {DescribeEffort(settings)} | Quality Lock: {(settings.QualityLock ? "ON" : "OFF")}" + (settings.QualityLock ? $" ({settings.ToPolicy().Label})" : string.Empty)),
            Line.Of($"Provider speed: {(settings.Speed == ProviderSpeedMode.Provider ? "PROVIDER (requested)" : "STANDARD")} | Workspace: isolated"),
            Line.Of("Type a request, or /help. Shift+Enter starts a new line.", Tone.Muted),
            Line.Empty,
        ]);

        if (settings.ModelA is null || settings.ModelB is null)
        {
            var missing = settings.ModelA is null && settings.ModelB is null ? "Model A and Model B are"
                : settings.ModelA is null ? "Model A is" : "Model B is";
            if (_input.CanAsk)
            {
                // Said as guidance, not as a fault: the first request asks for what is missing.
                _ui.Say($"{missing} not chosen yet. Type your request: YAV lists the models your agents offer and asks you to choose. It does not choose them for you.");
            }
            else
            {
                // Nobody can be asked in this mode, so a request is refused until the models are chosen.
                _ui.Warn($"{missing} not chosen yet. YAV does not choose models for you: see /models.");
            }
        }
    }

    private static string Describe(RoleSelection? selection) =>
        selection is null ? "not chosen" : $"{selection.ModelId} ({selection.AdapterId})";

    private static string DescribeEffort(Yav.Core.Settings.AppSettings settings)
    {
        if (settings.ModelA is null || settings.ModelB is null)
        {
            return "set when the models are chosen";
        }

        return settings.ModelA.WantsMaximum && settings.ModelB.WantsMaximum
            ? "maximum supported for both"
            : $"A {settings.ModelA.EffortPreference}, B {settings.ModelB.EffortPreference}";
    }

    private void OpenInitialProject(string? projectPath, bool startedOutsideAShell)
    {
        if (projectPath is null && startedOutsideAShell)
        {
            return;
        }

        var candidate = projectPath ?? Environment.CurrentDirectory;
        string full;
        try
        {
            full = Path.GetFullPath(candidate);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ui.Error($"'{candidate}' is not a path: {ex.Message}");
            return;
        }

        if (!Directory.Exists(full))
        {
            _ui.Error($"The directory '{full}' does not exist. Select a project with /open <path>.");
            return;
        }

        if (projectPath is null && !IsSensibleProject(full))
        {
            // Started from a place that is no project, for example from the Start menu.
            return;
        }

        _session.ProjectPath = full;
    }

    /// <summary>False for places that are certainly no project: the root of a drive, the user's profile, the system.</summary>
    internal static bool IsSensibleProject(string path)
    {
        var full = Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar);
        if (Path.GetPathRoot(full)?.TrimEnd(Path.DirectorySeparatorChar) == full)
        {
            return false;
        }

        // Where a downloaded yav.exe is opened from: the folder of downloads is no project either.
        if (string.Equals(Path.TrimEndingDirectorySeparator(KnownFolders.Downloads), full, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var folder in new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Windows, Environment.SpecialFolder.System,
            Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.Desktop,
            Environment.SpecialFolder.MyDocuments,
        })
        {
            var special = Environment.GetFolderPath(folder).TrimEnd(Path.DirectorySeparatorChar);
            if (special.Length > 0 && string.Equals(special, full, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (folder is Environment.SpecialFolder.Windows or Environment.SpecialFolder.ProgramFiles or Environment.SpecialFolder.ProgramFilesX86
                && special.Length > 0 && full.StartsWith(special + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    private void RestoreLastRun()
    {
        if (_session.ProjectPath is not { } project)
        {
            return;
        }

        var last = _services.Database.ListRuns(project, 1).FirstOrDefault();
        if (last is null || last.Disposition is RunDisposition.Discarded or RunDisposition.Undone)
        {
            return;
        }

        _session.TaskId = last.TaskId;
        _session.LastRunId = last.RunId;
        _session.TaskEffort = ApprovedEffortOf(last.RunId);
        var hint = last.State switch
        {
            RunState.ReadyToApply => "/diff shows it and /apply writes it into the project.",
            RunState.Completed => "A request continues its task; /new starts an unrelated one.",
            _ => $"/resume {last.RunId} continues it, /discard removes its isolated changes.",
        };
        _ui.Muted($"Last run here: {last.RunId}, {RunStateMachine.Display(last.State)} - \"{Shorten(last.RequestText, 60)}\". {hint}");
    }

    /// <summary>The effort that was approved for the task of a run in Adaptive mode, or null.</summary>
    private string? ApprovedEffortOf(string runId)
    {
        var profile = _services.Database.GetProfile(runId);
        var chosen = _services.Settings.ModelA?.EffortPreference;
        return profile is { Policy.Adaptive: true }
            && !string.Equals(profile.Implementer.EffortPreference, chosen, StringComparison.OrdinalIgnoreCase)
            ? profile.Implementer.RequestedEffort
            : null;
    }

    private void ReportReconciliation(Task<ReconciliationReport> task)
    {
        if (task.IsCanceled)
        {
            return;
        }

        if (task.IsFaulted)
        {
            _ui.Warn("Looking for unfinished work of an earlier session failed: " + task.Exception!.GetBaseException().Message);
            return;
        }

        var report = task.Result;
        foreach (var journal in report.Journals)
        {
            _ui.Note(
                Stages.Local,
                journal.State == JournalState.Committed
                    ? $"An apply of run {journal.RunId} that was interrupted has been finished."
                    : $"An apply of run {journal.RunId} that was interrupted was rolled back ({journal.State}). The project is as it was before.",
                NoteLevel.Warning);
        }

        foreach (var run in report.Runs)
        {
            _ui.Note(
                Stages.Local,
                $"Run {run.RunId} was {RunStateMachine.Display(run.StateBefore)} when YAV stopped. {string.Join(" ", run.Observations)} "
                + run.Advice switch
                {
                    ReconciliationAdvice.DiscardOnly => $"/discard {run.RunId} removes what is left.",
                    ReconciliationAdvice.WaitForProvider => $"Try /resume {run.RunId} later.",
                    _ => $"/resume {run.RunId} continues; nothing is sent again without that.",
                },
                NoteLevel.Warning);
        }
    }

    private async Task WarmUpAgentsAsync()
    {
        var ids = new[] { _services.Settings.ModelA?.AdapterId, _services.Settings.ModelB?.AdapterId }.Where(id => id is not null).Select(id => id!);
        try
        {
            // Read once in the background, so the first request does not wait for it. No inference is involved.
            await Coordinator.Catalog.GetManyAsync(ids, refresh: false, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            _ = ex;
        }
    }

    private void MeasureStartup()
    {
        if (_startupMeasured)
        {
            return;
        }

        _startupMeasured = true;
        try
        {
            var elapsed = Stopwatch.GetElapsedTime(_processStarted);
            _services.Database.SaveSpan(new TimingSpan(
                Ids.NewId("s"), null, SpanKind.ConsoleStartup, "prompt shown", _services.Clock.GetUtcNow() - elapsed, elapsed, null));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            _ = ex;
        }
    }

    /// <summary>
    /// Measures how long it takes until something is shown in answer to what was entered. Of what was
    /// entered only the name of the command is kept; of a request nothing.
    /// </summary>
    private Task<TimingSpan> MeasureAnswer(string label)
    {
        var entered = Stopwatch.GetTimestamp();
        var at = _services.Clock.GetUtcNow();
        var measured = new TaskCompletionSource<TimingSpan>(TaskCreationOptions.RunContinuationsAsynchronously);
        _screen.NotifyNextWrite(() => measured.TrySetResult(
            new TimingSpan(Ids.NewId("s"), null, SpanKind.LocalCommand, label, at, Stopwatch.GetElapsedTime(entered), null)));
        return measured.Task;
    }

    private void Save(TimingSpan span)
    {
        try
        {
            _services.Database.SaveSpan(span);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException or Microsoft.Data.Sqlite.SqliteException)
        {
            // A measurement that could not be kept is no reason to disturb the user.
            _ = ex;
        }
    }

    private async Task HandleAsync(string text, CancellationToken cancellationToken)
    {
        var input = InputClassifier.Classify(text);
        if (input.Kind is InputKind.Empty)
        {
            return;
        }

        var answer = MeasureAnswer(input.Kind == InputKind.Command
            ? "/" + (CommandCatalog.Find(input.Command!.Name)?.Name ?? "unknown")
            : input.Kind == InputKind.Invalid ? "invalid" : "request");
        try
        {
            switch (input.Kind)
            {
                case InputKind.Invalid:
                    _ui.Warn(input.Problem!);
                    return;

                case InputKind.Command:
                    await ExecuteAsync(input.Command!, cancellationToken).ConfigureAwait(false);
                    return;

                default:
                    await RequestAsync(input.Text, cancellationToken).ConfigureAwait(false);
                    return;
            }
        }
        finally
        {
            if (answer.IsCompletedSuccessfully)
            {
                Save(answer.Result);
            }
            else
            {
                // A run answers a moment after it was started.
                _ = answer.ContinueWith(measured => Save(measured.Result), CancellationToken.None, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
            }
        }
    }

    private async Task RequestAsync(string text, CancellationToken cancellationToken)
    {
        if (_session.ProjectPath is null)
        {
            // Started from a place that is no project: whoever typed the request is asked which project it is for.
            if (!_input.CanAsk)
            {
                _ui.Warn("No project is selected, so the request was not sent. Select one with /open <path>.");
                return;
            }

            if (!await AskForProjectAsync(cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        if (_session.Active is not null)
        {
            Enqueue(text, isFollowUp: true);
            return;
        }

        if (_services.Settings.Adaptive && _session.TaskId is null)
        {
            // Asked once for each task, and only of someone who can answer. Nothing is lowered without an answer.
            var (send, effort) = await AskForEffortAsync(text, cancellationToken).ConfigureAwait(false);
            if (!send)
            {
                return;
            }

            _session.TaskEffort = effort;
        }

        await StartRunAsync(text, _session.TaskId, null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Adaptive mode: asks whether Model A may work at a lower effort for the task that begins. The effort
    /// is null when it stays as it was chosen for the role.
    /// </summary>
    private async Task<(bool Send, string? Effort)> AskForEffortAsync(string request, CancellationToken cancellationToken)
    {
        if (!_input.CanAsk || _services.Settings.ModelA is not { } selection)
        {
            return (true, null);
        }

        // A task that looks risky or uncertain is not offered a lower effort at all.
        var protectedPaths = _services.Validation.LoadConfiguration(_session.ProjectPath!).Effective.ProtectedPaths;
        var decision = AdaptivePolicy.Decide(request, protectedPaths);
        if (!decision.MayLower)
        {
            _ui.Muted($"Adaptive mode: this task stays at the effort you chose for Model A, because {decision.Reason}.");
            return (true, null);
        }

        var snapshot = await SnapshotAsync(selection.AdapterId, false, cancellationToken).ConfigureAwait(false);
        var model = snapshot?.Models.FirstOrDefault(m =>
            string.Equals(m.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase) || string.Equals(m.ResolvedModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase));
        var current = model is null ? null : selection.WantsMaximum ? ProfileResolver.HighestEffort(model.SupportedEfforts) : selection.EffortPreference;
        var lower = model is null || current is null ? [] : ProfileResolver.EffortsBelow(model.SupportedEfforts, current);
        if (lower.Count == 0)
        {
            // Nothing is listed below what was chosen, or the list cannot be ranked. There is nothing to ask.
            return (true, null);
        }

        _ui.Say($"Adaptive mode: Model A ({selection.ModelId}) would work on this task at effort {current}. Listed below it: {string.Join(", ", lower)}.", Tone.Warning);
        _ui.Muted("A lower effort changes how the model reasons. The model stays the same, and Model B reviews at its own effort.");
        var answer = await _input.AskAsync($"Effort for this task, or Enter for {current}: ", cancellationToken).ConfigureAwait(false);
        if (answer is null)
        {
            _ui.Muted("The request was not sent.");
            return (false, null);
        }

        var wanted = answer.Trim();
        if (wanted.Length == 0
            || wanted.Equals(current, StringComparison.OrdinalIgnoreCase)
            || (selection.WantsMaximum && wanted.Equals(RoleSelection.MaximumEffort, StringComparison.OrdinalIgnoreCase)))
        {
            return (true, null);
        }

        var match = lower.FirstOrDefault(e => e.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            _ui.Warn($"'{wanted}' is not an effort {selection.ModelId} lists below {current}. The request was not sent; enter it again.");
            return (false, null);
        }

        return (true, match);
    }

    private void Enqueue(string text, bool isFollowUp)
    {
        var project = _session.ProjectPath!;
        var waiting = _services.Database.GetQueue(project);
        var limit = _services.Settings.Limits.MaxQueueLength;
        if (waiting.Count >= limit)
        {
            _ui.Note(Stages.Queue, $"{waiting.Count} request(s) are waiting, which is the limit. The request was not added; see /queue and /limits.", NoteLevel.Warning);
            return;
        }

        _services.Database.Enqueue(new QueuedRequest(Ids.NewId("q"), project, text, isFollowUp, [.. _session.Attachments], _services.Clock.GetUtcNow()));
        _session.Attachments.Clear();
        _ui.Note(
            Stages.Queue,
            $"A run is active, so the request waits ({waiting.Count + 1} waiting). It starts when the run is ready. "
            + "It is not added to the running turn; /queue shows how to do that where the agent supports it.");
    }

    private Task StartRunAsync(string text, string? taskId, MechanicalEditRequest? edit, CancellationToken cancellationToken)
    {
        // What was attached goes with this request and with nothing after it.
        var sent = new SentRequest(text, taskId, [.. _session.Attachments], edit);
        _session.Attachments.Clear();
        return StartRunAsync(sent, cancellationToken);
    }

    private async Task StartRunAsync(SentRequest sent, CancellationToken cancellationToken)
    {
        if (_reconciliation is { IsCompleted: false } pending)
        {
            // An apply that a crash interrupted has to be settled before anything new touches the project.
            _ui.Muted("Finishing the check of the earlier session first...");
            try
            {
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }

        var project = _session.ProjectPath!;
        var stop = new CancellationTokenSource();
        var run = new ActiveRun { Request = sent.Text, Sent = sent, Stop = stop, StartedAt = _services.Clock.GetUtcNow() };
        var formatter = new RunEventFormatter(new FormatterOptions(_screen.Options.Unicode, _session.Verbose));
        var observer = new Cli.DelegateObserver(runEvent =>
        {
            if (runEvent is RunStarted started)
            {
                run.RunId = started.RunId;
                run.TaskId = started.TaskId;
            }

            Observe(run, runEvent);
            _screen.WriteLines(formatter.Format(runEvent));
            run.Progress?.Observe(runEvent);
        });

        var request = new RunRequest(project, sent.Text)
        {
            TaskId = sent.TaskId,
            Attachments = [.. sent.Attachments],
            MechanicalEdit = sent.Edit,
            EquivalenceGapsAcknowledged = false,

            // With Adaptive mode off nothing is lowered, whatever was approved for the task earlier.
            ImplementerEffort = _services.Settings.Adaptive ? _session.TaskEffort : null,
        };
        _session.QueuePaused = false;
        _session.Active = run;
        run.Progress = RunProgress.Start(_screen, _services.Clock);

        var configuration = _services.Configuration;
        run.Task = Task.Run(() => Coordinator.RunAsync(request, configuration, observer, _input.Approvals, stop.Token), CancellationToken.None);
    }

    /// <summary>Continues a stored run with the coordinator's own entry point for it.</summary>
    private void StartStoredRun(string description, string runId, Func<IRunObserver, IApprovalBroker, CancellationToken, Task<RunOutcome>> work)
    {
        var stop = new CancellationTokenSource();
        var run = new ActiveRun { Request = description, Stop = stop, StartedAt = _services.Clock.GetUtcNow(), RunId = runId };
        var formatter = new RunEventFormatter(new FormatterOptions(_screen.Options.Unicode, _session.Verbose));
        var observer = new Cli.DelegateObserver(runEvent =>
        {
            Observe(run, runEvent);
            _screen.WriteLines(formatter.Format(runEvent));
            run.Progress?.Observe(runEvent);
        });
        _session.QueuePaused = false;
        _session.Active = run;
        run.Progress = RunProgress.Start(_screen, _services.Clock);
        run.Task = Task.Run(() => work(observer, _input.Approvals, stop.Token), CancellationToken.None);
    }

    /// <summary>Remembers what a run has said, so that the shell does not say it again when the run ends.</summary>
    private static void Observe(ActiveRun run, RunEvent runEvent)
    {
        run.SaidHowItEnded |= runEvent is RunFinished;
        if (runEvent is StageNote note)
        {
            run.Noted(note.Message);
        }
    }

    private async Task CompleteRunIfEndedAsync(CancellationToken cancellationToken)
    {
        if (_session.Active is not { Task.IsCompleted: true } run)
        {
            return;
        }

        _session.Active = null;
        RunOutcome outcome;
        try
        {
            outcome = await run.Task.ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ui.Note(Stages.Failed, $"The run ended with an error: {ex.GetType().Name}: {ex.Message}", NoteLevel.Error);
            _session.QueuePaused = true;
            return;
        }
        finally
        {
            run.Release();
            run.Progress?.Dispose();
        }

        if (outcome.RunId.Length > 0 && _services.Database.FindRun(outcome.RunId) is not null)
        {
            _session.LastRunId = outcome.RunId;
            _session.TaskId = outcome.TaskId ?? _session.TaskId;
        }

        // A request that was refused before anything began, for something whoever typed it can settle here, is
        // sent again once that is settled. Nothing of it was stored, so nothing has to be undone first.
        IReadOnlySet<string> settledNow = new HashSet<string>(StringComparer.Ordinal);
        if (!_session.ExitRequested && run.Sent is { } sent)
        {
            (var sentAgain, settledNow) = await SettleAndSendAgainAsync(sent, outcome, cancellationToken).ConfigureAwait(false);
            if (sentAgain)
            {
                return;
            }
        }

        // A run that was refused said why in a note of its own, and that is enough.
        run.SaidHowItEnded |= outcome.Reason is { } said && run.HasNoted(said);
        if (!run.SaidHowItEnded && !string.IsNullOrWhiteSpace(outcome.Reason) && outcome.Problems.Count == 0)
        {
            // What was asked for was refused before anything began, so nothing has said why yet.
            _ui.Warn(outcome.Reason);
        }

        Advise(outcome, settledNow);

        var ready = outcome.Kind is RunOutcomeKind.ReadyToApply or RunOutcomeKind.Completed;
        _session.QueuePaused = !ready;
        if (_session.ProjectPath is { } project && !_session.ExitRequested)
        {
            var waiting = _services.Database.GetQueue(project);
            if (waiting.Count > 0 && ready)
            {
                var next = waiting[0];
                _services.Database.RemoveFromQueue(next.QueueId);
                _ui.Note(Stages.Queue, $"Starting the request that waited: \"{Shorten(next.Text, 70)}\" ({waiting.Count - 1} more waiting)");
                _session.Attachments.AddRange(next.Attachments);
                await StartRunAsync(next.Text, next.IsFollowUp ? _session.TaskId : null, null, CancellationToken.None).ConfigureAwait(false);
            }
            else if (waiting.Count > 0)
            {
                _ui.Note(Stages.Queue, $"{waiting.Count} request(s) are waiting. They are not started, because the run needs a decision first. See /queue.", NoteLevel.Warning);
            }
        }
    }

    /// <param name="settled">
    /// What the guided first run settled for the request before it stopped: its remedies are no longer what to do.
    /// </param>
    private void Advise(RunOutcome outcome, IReadOnlySet<string> settled)
    {
        foreach (var problem in outcome.Problems.Where(p => p.Severity == ProblemSeverity.Blocking && p.Remedy is not null && !settled.Contains(SetupProblems.Key(p))))
        {
            _ui.Muted($"           {problem.Code}: {problem.Remedy}");
        }

        switch (outcome.Kind)
        {
            case RunOutcomeKind.ReadyToApply:
                _ui.Muted("           /diff shows the changes, /apply writes them into the project, /discard removes them. A request continues the task.");
                break;

            case RunOutcomeKind.Blocked when outcome.RunId.Length > 0 && outcome.Candidate is not null:
                _ui.Muted($"           The candidate is kept. /diff, /review show, /test waive, then /resume {outcome.RunId}; or a request that says what to change.");
                break;

            case RunOutcomeKind.Interrupted or RunOutcomeKind.RateLimited or RunOutcomeKind.NeedsReconciliation when outcome.RunId.Length > 0:
                _ui.Muted($"           /resume {outcome.RunId} continues. /discard removes the isolated changes.");
                break;

            case RunOutcomeKind.ApprovalRequired when _input.CanAsk:
                _ui.Muted("           Nothing was granted. Send the request again to decide when asked.");
                break;

            case RunOutcomeKind.ApprovalRequired:
                // Sent again, the request would meet the same question, and again nobody could answer it.
                _ui.Muted("           Nothing was granted. Nobody can be asked in this mode: start YAV in a console to decide.");
                break;
        }
    }

    private async Task InterruptedAsync()
    {
        if (_session.Active is not null)
        {
            await StopActiveAsync().ConfigureAwait(false);
            return;
        }

        if (_interruptedOnce)
        {
            await LeaveAsync(waitForRun: false).ConfigureAwait(false);
            return;
        }

        _interruptedOnce = true;
        _ui.Muted("Control+C again, or /exit, leaves YAV.");
    }

    private async Task StopActiveAsync()
    {
        if (_session.Active is not { } run)
        {
            _ui.Muted("No run is active.");
            return;
        }

        if (!run.TryBeginStop())
        {
            _ui.Muted("The run was already asked to stop. It ends as soon as the agent has stopped.");
            return;
        }

        _ui.Note(Stages.Stopped, "Asking the agent to stop. Its work so far is kept.");
        await run.Stop.CancelAsync().ConfigureAwait(false);
    }

    private async Task LeaveAsync(bool waitForRun)
    {
        if (_session.Active is { } run)
        {
            if (run.TryBeginStop())
            {
                _ui.Note(Stages.Stopped, "Stopping the active run before leaving. It can be continued with /resume after the next start.");
                await run.Stop.CancelAsync().ConfigureAwait(false);
            }

            try
            {
                await run.Task.WaitAsync(TimeSpan.FromSeconds(waitForRun ? 60 : 30)).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or AgentException)
            {
                _ui.Warn("The run did not stop in time. It is marked for reconciliation at the next start.");
            }

            _session.ExitRequested = true;
            await CompleteRunIfEndedAsync(CancellationToken.None).ConfigureAwait(false);
        }

        _session.ExitRequested = true;
        if (_session.ProjectPath is { } project)
        {
            _services.Update(settings => settings with { LastProject = project });
        }
    }

    internal static string Shorten(string text, int length)
    {
        var line = Yav.Core.Text.TerminalSanitizer.CleanSingleLine(text);
        return line.Length <= length ? line : string.Concat(line.AsSpan(0, length - 1), "…");
    }
}
