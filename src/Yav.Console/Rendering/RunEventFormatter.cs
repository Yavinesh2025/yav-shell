using System.Globalization;
using System.Text;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Core.Text;

namespace Yav.Console.Rendering;

/// <param name="Unicode">False for a terminal that can only show ASCII.</param>
/// <param name="Verbose">True when output of commands and checks is shown as it arrives.</param>
public sealed record FormatterOptions(bool Unicode, bool Verbose);

/// <summary>
/// Turns what happened in a run into lines. It shows events that took place and nothing else: no
/// percentages, no invented explanations and no promises.
/// Text that comes from an agent, a tool or a check is always indented behind a mark. A line of the
/// application itself starts in the first column, so the two cannot be mistaken for one another.
/// </summary>
public sealed class RunEventFormatter
{
    private const int StageWidth = 9;

    /// <summary>How much of the name of a tool is shown. Tools of other parties are named by them.</summary>
    private const int LongestToolName = 80;

    private readonly FormatterOptions _options;
    private readonly Dictionary<(AgentRole Role, string Item), Stream> _streams = [];
    private readonly HashSet<(AgentRole Role, string Item)> _toolsShown = [];
    private readonly Lock _gate = new();

    public RunEventFormatter(FormatterOptions options)
    {
        _options = options;
    }

    /// <summary>Text that arrives in pieces: what was shown already, and the line that is not complete yet.</summary>
    private sealed class Stream
    {
        public StringBuilder Pending { get; } = new();

        public int Shown { get; set; }

        /// <summary>Null until enough has arrived to tell whether this is a structured result, which is data and not text.</summary>
        public bool? Structured { get; set; }
    }

    private string Gutter => _options.Unicode ? "  │ " : "  | ";

    private string Dot => _options.Unicode ? "  · " : "  . ";

    public static string StageLabel(string stage) => ("[" + stage + "]").PadRight(StageWidth) + " ";

    public IEnumerable<Line> Format(RunEvent runEvent)
    {
        lock (_gate)
        {
            return (runEvent switch
            {
                StageNote note => [Note(note)],
                AgentActivity activity => FromAgent(activity),
                SettingConfirmed { Confirmation.Status: not VerificationStatus.Verified } setting => [Setting(setting)],
                ReviewCompleted review => Findings(review.Review),
                GateStarted gate => [Line.Of(new Segment("  $ ", Tone.Muted), new Segment($"{gate.Gate.Title}: {gate.Gate.DisplayCommand}", Tone.Muted))],
                GateOutput output when _options.Verbose => Quote(output.Line, Tone.Muted),
                RunFinished finished =>
                [
                    Line.Of(
                        new Segment($"Run {finished.RunId}: ", Tone.Muted),
                        new Segment(RunStateMachine.Display(finished.State), ToneOf(finished.State), Bold: true)),
                ],
                _ => Enumerable.Empty<Line>(),
            }).ToList();
        }
    }

    // A note is YAV's, but it can name what an agent asked for or did. That part is shown as it is.
    private static Line Note(StageNote note) => Line.Of(
        new Segment(StageLabel(note.Stage), Tone.Stage, Bold: true),
        new Segment(TerminalSanitizer.Visible(note.Message), note.Level switch
        {
            NoteLevel.Success => Tone.Success,
            NoteLevel.Warning => Tone.Warning,
            NoteLevel.Error => Tone.Error,
            _ => Tone.Normal,
        }));

    private static Line Setting(SettingConfirmed setting)
    {
        var confirmation = setting.Confirmation;
        var role = confirmation.Role == AgentRole.Implementer ? "Model A" : "Model B";
        var value = new Confirmed<string>(confirmation.Requested, confirmation.Effective, confirmation.Status, confirmation.Source).Describe();
        return Line.Of(new Segment("  ! ", Tone.Warning), new Segment($"{role} {confirmation.Setting}: {value}", Tone.Warning));
    }

    private static Tone ToneOf(RunState state) => state switch
    {
        RunState.ReadyToApply or RunState.Completed => Tone.Success,
        RunState.Failed or RunState.NeedsReconciliation => Tone.Error,
        RunState.Blocked or RunState.RateLimited or RunState.Interrupted => Tone.Warning,
        _ => Tone.Normal,
    };

    private IEnumerable<Line> Findings(ReviewResult review)
    {
        foreach (var finding in review.Findings)
        {
            if (finding.Optional)
            {
                yield return Line.Of(
                    new Segment("  ? ", Tone.Muted),
                    new Segment($"suggestion {finding.Location}  {finding.Title}", Tone.Muted));
                continue;
            }

            yield return Line.Of(
                new Segment("  ! ", Tone.Warning),
                new Segment($"{finding.Severity} {finding.Location}  ", Tone.Warning),
                new Segment(finding.Title));
            if (!string.IsNullOrWhiteSpace(finding.FailureScenario))
            {
                yield return Line.Of(new Segment("      ", Tone.Muted), new Segment(finding.FailureScenario, Tone.Muted));
            }
        }

        foreach (var limitation in review.Limitations)
        {
            yield return Line.Of(new Segment("  ! ", Tone.Warning), new Segment("limitation: " + limitation, Tone.Warning));
        }
    }

    /// <summary>
    /// A tool gets one line: when it begins, if the agent reports that, and otherwise when it has ended.
    /// That it failed is said in any case.
    /// </summary>
    private IEnumerable<Line> Tool(AgentRole role, ToolActivity tool)
    {
        // What a tool was given says what an agent does, so it is shown as it is, on the line of the tool.
        var given = TerminalSanitizer.VisibleSingleLine(tool.Given, 300);
        var name = TerminalSanitizer.VisibleSingleLine(tool.Tool, LongestToolName);
        var item = (role, tool.ItemId);
        switch (tool.Status)
        {
            case "inProgress":
                return [];

            case "started":
                _toolsShown.Add(item);
                break;

            case "failed":
                _toolsShown.Remove(item);
                return [Line.Of(new Segment("  ! ", Tone.Warning), new Segment(given.Length == 0 ? $"{name} failed" : $"{name} failed: {given}", Tone.Warning))];

            default:
                if (_toolsShown.Remove(item))
                {
                    return [];
                }

                break;
        }

        return [Line.Of(new Segment(Dot, Tone.Muted), new Segment(given.Length == 0 ? name : $"{name}: {given}", Tone.Muted))];
    }

    private IEnumerable<Line> FromAgent(AgentActivity activity)
    {
        var role = activity.Role;
        switch (activity.Event)
        {
            case AssistantTextDelta delta:
                return Streamed(role, delta.ItemId, delta.Text);

            case AssistantMessage message:
                return Completed(role, message);

            case ReasoningSummary reasoning:
                return [Line.Of(new Segment(Dot, Tone.Muted), new Segment("reasoning summary: " + reasoning.Text, Tone.Muted))];

            case CommandStarted started:
                return [Line.Of(new Segment("  $ ", Tone.Muted), new Segment(TerminalSanitizer.VisibleSingleLine(started.Command), Tone.Muted))];

            case CommandOutputDelta output when _options.Verbose:
                return Quote(output.Text.TrimEnd('\r', '\n'), Tone.Muted);

            case CommandCompleted completed:
            {
                var exit = completed.ExitCode is null ? completed.Status : "exit " + completed.ExitCode.Value.ToString(CultureInfo.InvariantCulture);
                var duration = completed.DurationMs is null
                    ? string.Empty
                    : string.Create(CultureInfo.InvariantCulture, $" ({completed.DurationMs.Value / 1000.0:0.0} s)");

                // Some agents report that a command failed without the code it ended with.
                var failed = completed.ExitCode is not null and not 0 || (completed.ExitCode is null && completed.Status == "failed");
                return
                [
                    Line.Of(
                        new Segment("  $ ", Tone.Muted),
                        new Segment(TerminalSanitizer.VisibleSingleLine(completed.Command), Tone.Muted),
                        new Segment($" -> {exit}{duration}", failed ? Tone.Warning : Tone.Muted)),
                ];
            }

            case FilesChanged { Status: "completed" } files:
                return files.Changes.Select(change => Line.Of(
                    new Segment(change.Kind switch
                    {
                        FileChangeKind.Add => "  + ",
                        FileChangeKind.Delete => "  - ",
                        _ => "  ~ ",
                    }, Tone.Accent),
                    new Segment(Describe(change))));

            case FilesChanged { Status: "failed" or "declined" } files:
                // A change the agent tried and did not make is said as well: otherwise nothing would show that it was tried.
                return files.Changes.Select(change => Line.Of(
                    new Segment("  ! ", Tone.Warning),
                    new Segment($"{Describe(change)}: {files.Status}", Tone.Warning)));

            case ToolActivity tool:
                return Tool(role, tool);

            case ModelRerouted rerouted:
                return [Line.Of(new Segment("  ! ", Tone.Error), new Segment($"the provider rerouted {rerouted.FromModel} to {rerouted.ToModel} ({rerouted.Reason})", Tone.Error))];

            case ProviderRetry retry:
            {
                var attempt = retry.MaxAttempts is null
                    ? retry.Attempt > 0 ? $" ({retry.Attempt})" : string.Empty
                    : $" ({retry.Attempt} of {retry.MaxAttempts})";
                return [Line.Of(new Segment(Dot, Tone.Muted), new Segment($"the provider retries{attempt}: {retry.Reason}", Tone.Muted))];
            }

            case AgentError { WillRetry: true } retried:
                return [Line.Of(new Segment(Dot, Tone.Muted), new Segment("the provider retries: " + retried.Message, Tone.Muted))];

            case AgentError error:
                return [Line.Of(new Segment("  ! ", Tone.Error), new Segment(error.Message, Tone.Error))];

            case AgentNotice notice:
                return [Line.Of(new Segment(notice.IsWarning ? "  ! " : Dot, notice.IsWarning ? Tone.Warning : Tone.Muted), new Segment(notice.Message, notice.IsWarning ? Tone.Warning : Tone.Muted))];

            case TurnCompleted or SessionEnded:
                return Remainder(role);

            default:
                return [];
        }
    }

    private IEnumerable<Line> Streamed(AgentRole role, string item, string text)
    {
        if (!_streams.TryGetValue((role, item), out var stream))
        {
            stream = new Stream();
            _streams[(role, item)] = stream;
        }

        stream.Pending.Append(text);
        var pending = stream.Pending.ToString();
        if (role == AgentRole.Reviewer && stream.Structured is null && pending.AsSpan().TrimStart() is { Length: > 0 } begun)
        {
            stream.Structured = begun[0] == '{';
        }

        if (stream.Structured == true)
        {
            // Kept until it is complete: the application validates it and shows the findings.
            return [];
        }

        var lastBreak = pending.LastIndexOf('\n');
        if (lastBreak < 0)
        {
            return [];
        }

        // Complete lines are shown; the rest waits for its end.
        var complete = pending[..lastBreak];
        stream.Pending.Clear().Append(pending[(lastBreak + 1)..]);
        stream.Shown += lastBreak + 1;
        return Quote(complete, Tone.Normal);
    }

    private IEnumerable<Line> Completed(AgentRole role, AssistantMessage message)
    {
        _streams.Remove((role, message.ItemId), out var stream);
        if (role == AgentRole.Reviewer && LooksStructured(message.Text))
        {
            // The review result is data. It is shown as findings once the application has validated it.
            return [];
        }

        if (stream is null || stream.Shown == 0)
        {
            return Quote(message.Text, Tone.Normal);
        }

        // Only what the stream has not shown yet.
        var shown = Math.Min(stream.Shown, message.Text.Length);
        var rest = message.Text[shown..];
        return rest.Length == 0 ? [] : Quote(rest, Tone.Normal);
    }

    private List<Line> Remainder(AgentRole role)
    {
        var lines = new List<Line>();
        foreach (var key in _streams.Keys.Where(k => k.Role == role).ToList())
        {
            var stream = _streams[key];
            _streams.Remove(key);
            if (stream.Pending.Length > 0 && stream.Structured != true)
            {
                lines.AddRange(Quote(stream.Pending.ToString(), Tone.Normal));
            }
        }

        return lines;
    }

    private IEnumerable<Line> Quote(string text, Tone tone)
    {
        foreach (var line in TerminalSanitizer.Clean(text).TrimEnd('\n').Split('\n'))
        {
            yield return Line.Of(new Segment(Gutter, Tone.Muted), new Segment(line, tone));
        }
    }

    /// <summary>What a change of a file is, with its paths shown as they are.</summary>
    private static string Describe(FileChange change) =>
        change.Kind.ToString().ToLowerInvariant() + " " + TerminalSanitizer.VisibleSingleLine(change.Path)
        + (change.MovedTo is null ? string.Empty : " -> " + TerminalSanitizer.VisibleSingleLine(change.MovedTo));

    private static bool LooksStructured(string text)
    {
        var trimmed = text.AsSpan().Trim();
        return trimmed.Length > 1 && trimmed[0] == '{' && trimmed.Contains("\"status\"", StringComparison.Ordinal);
    }
}
