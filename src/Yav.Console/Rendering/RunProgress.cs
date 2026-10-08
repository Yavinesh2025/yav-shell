using System.Globalization;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Core.Text;

namespace Yav.Console.Rendering;

/// <summary>
/// The one row that says, while a run is active, that it still works: a mark that moves, how long the run has
/// taken, the stage it is in and the last thing an agent did. It is replaced in place and never written into
/// the scrollback, and only to a terminal that can position the cursor, so plain and JSON output stay as they are.
/// It says only what the events of the run said.
/// </summary>
public sealed class RunProgress : IDisposable
{
    /// <summary>How often the row is drawn again when nothing happens.</summary>
    public static readonly TimeSpan Period = TimeSpan.FromMilliseconds(400);

    private const int LongestActivity = 80;

    private static readonly string[] UnicodeFrames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];
    private static readonly string[] AsciiFrames = ["|", "/", "-", "\\"];

    private readonly Screen _screen;
    private readonly TimeProvider _clock;
    private readonly long _started;
    private readonly string[] _frames;
    private readonly string _separator;
    private readonly ITimer? _timer;
    private readonly Lock _gate = new();
    private int _frame;
    private bool _repairing;
    private bool _stopped;

    private RunProgress(Screen screen, TimeProvider clock, TimeSpan period)
    {
        _screen = screen;
        _clock = clock;
        _started = clock.GetTimestamp();
        _frames = screen.Options.Unicode ? UnicodeFrames : AsciiFrames;
        _separator = screen.Options.Unicode ? " · " : " - ";
        Refresh();
        _timer = period == Timeout.InfiniteTimeSpan ? null : clock.CreateTimer(_ => Tick(), null, period, period);
    }

    /// <summary>What the run is doing, as the row says it.</summary>
    public string Stage { get; private set; } = "starting";

    /// <summary>The last thing an agent or a check did. Empty until something did.</summary>
    public string Activity { get; private set; } = string.Empty;

    /// <summary>Null for a screen that cannot replace a row: there the progress is the lines of the run alone.</summary>
    /// <param name="period">How often the row moves without an event; infinite for a test that moves it itself.</param>
    public static RunProgress? Start(Screen screen, TimeProvider clock, TimeSpan? period = null) =>
        screen.Options.Rich ? new RunProgress(screen, clock, period ?? Period) : null;

    public void Observe(RunEvent runEvent)
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            switch (runEvent)
            {
                case RunFinished:
                    break;

                case StageNote note:
                    Stage = StageOf(note.Stage) ?? Stage;
                    break;

                case RepairStarted:
                    _repairing = true;
                    Stage = "Model A is repairing";
                    break;

                case GateStarted gate:
                    Stage = "running checks";
                    Activity = TerminalSanitizer.VisibleSingleLine(gate.Gate.Title, LongestActivity);
                    break;

                case ReviewCompleted or AcceptanceEvaluated:
                    Activity = string.Empty;
                    break;

                case AgentActivity activity:
                    Stage = activity.Role == AgentRole.Implementer
                        ? _repairing ? "Model A is repairing" : "Model A is working"
                        : "Model B is reviewing";
                    Activity = ActivityOf(activity.Event) ?? Activity;
                    break;
            }
        }

        if (runEvent is RunFinished)
        {
            // The result follows on lines of its own; nothing may move below it.
            Dispose();
            return;
        }

        Refresh();
    }

    /// <summary>What the timer does: moves the row, unless the user types or answers a question just now.</summary>
    public void Tick()
    {
        if (!_screen.StatusHeld)
        {
            Refresh();
        }
    }

    /// <summary>Draws the row again, one step further.</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            var frame = _frames[_frame++ % _frames.Length];
            var elapsed = Elapsed(_clock.GetElapsedTime(_started));
            var segments = new List<Segment>
            {
                new(frame + " ", Tone.Accent),
                new(Stage, Tone.Stage, Bold: true),
                new(_separator + elapsed, Tone.Muted),
            };
            if (Activity.Length > 0)
            {
                segments.Add(new Segment(_separator + Activity, Tone.Muted));
            }

            _screen.ShowStatus(Line.Of([.. segments]));
        }
    }

    /// <summary>Removes the row. Nothing is drawn after this, whatever arrives late.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_stopped)
            {
                return;
            }

            _stopped = true;
            _timer?.Dispose();
            _screen.HideStatus();
        }
    }

    public static string Elapsed(TimeSpan elapsed)
    {
        var seconds = (long)Math.Max(0, elapsed.TotalSeconds);
        return seconds < 60
            ? seconds.ToString(CultureInfo.InvariantCulture) + "s"
            : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60}m {seconds % 60:00}s");
    }

    private string? StageOf(string stage) => stage switch
    {
        Stages.Prepare => "preparing",
        Stages.CodeA => _repairing ? "Model A is repairing" : "Model A is working",
        Stages.ReviewB => "Model B is reviewing",
        Stages.Check or Stages.Tests => "running checks",
        Stages.Repair => "Model A is repairing",
        Stages.Approval => "waiting for your answer",
        Stages.Apply => "applying",
        _ => null,
    };

    private static string? ActivityOf(AgentEvent agentEvent) => agentEvent switch
    {
        CommandStarted started => "$ " + TerminalSanitizer.VisibleSingleLine(started.Command, LongestActivity),
        ToolActivity tool => TerminalSanitizer.VisibleSingleLine(tool.Given.Length == 0 ? tool.Tool : $"{tool.Tool}: {tool.Given}", LongestActivity),
        FilesChanged { Changes.Count: > 0 } files => "editing " + TerminalSanitizer.VisibleSingleLine(files.Changes[^1].Path, LongestActivity),
        ReasoningSummary => "thinking",
        AssistantTextDelta or AssistantMessage => "writing",
        _ => null,
    };
}
