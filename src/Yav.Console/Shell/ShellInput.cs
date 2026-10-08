using System.Text;
using Yav.Console.Input;
using Yav.Console.Rendering;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Platform.Consoles;

namespace Yav.Console.Shell;

/// <summary>How the shell gets what the user enters, whatever the console can do.</summary>
public interface IShellInput : IAsyncDisposable
{
    /// <summary>False when nobody is there to answer a question, for example when the input is a file.</summary>
    bool CanAsk { get; }

    IApprovalBroker Approvals { get; }

    Task<InputResult> ReadAsync(Line prompt, bool runActive, CancellationToken cancellationToken);

    /// <summary>Asks for one line of text. Null when there was no answer.</summary>
    Task<string?> AskAsync(string question, CancellationToken cancellationToken);

    /// <summary>Asks for text that is not shown and not remembered.</summary>
    Task<string?> AskSecretAsync(string question, CancellationToken cancellationToken);

    /// <summary>Lets go of the keyboard while another program has the console.</summary>
    IDisposable ReleaseKeyboard();

    void Remember(string text);
}

/// <summary>Input from a keyboard in a terminal that can position the cursor.</summary>
public sealed class TerminalInput : IShellInput
{
    private readonly Screen _screen;
    private readonly IKeySource _keys;
    private readonly LiveInputLine _live;
    private readonly IIdleEditor? _idle;
    private readonly InputHistory _history;
    private readonly ConsoleApprovals _approvals;
    private bool _idleFailed;

    /// <param name="idle">The editor for the time no run is active. Null uses the simple editor throughout.</param>
    /// <param name="clock">What a question for approval measures the time it has been open with. Null is the system's clock.</param>
    public TerminalInput(Screen screen, IKeySource keys, InputHistory history, IIdleEditor? idle, TimeProvider? clock = null)
    {
        _screen = screen;
        _keys = keys;
        _history = history;
        _idle = idle;
        _live = new LiveInputLine(screen, keys, history, text => InputCompletion.For(text, text.Length, null).Candidates.Select(c => c.Text));
        _approvals = new ConsoleApprovals(screen, _live, clock);
    }

    public bool CanAsk => true;

    public IApprovalBroker Approvals => _approvals;

    public async Task<InputResult> ReadAsync(Line prompt, bool runActive, CancellationToken cancellationToken)
    {
        // The rich editor cannot share the screen with a run, and it cannot take over a line that was begun.
        if (_idle is null || _idleFailed || runActive || _live.Draft.Length > 0)
        {
            return await _live.ReadAsync(prompt, cancellationToken).ConfigureAwait(false);
        }

        try
        {
            return await _idle.ReadAsync(prompt).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or NotSupportedException)
        {
            // An editor that failed must not take the shell with it. The simple editor does everything that is needed.
            _idleFailed = true;
            _screen.WriteLine(Line.Of(
                $"The line editor failed ({ex.GetType().Name}: {ex.Message}). What was being typed is lost; the simple editor is used from now on.",
                Tone.Warning));
            return await _live.ReadAsync(prompt, cancellationToken).ConfigureAwait(false);
        }
    }

    public async Task<string?> AskAsync(string question, CancellationToken cancellationToken)
    {
        var draft = _live.Draft;
        _live.Draft = string.Empty;
        try
        {
            var result = await _live.ReadAsync(Line.Of(new Segment(question, Tone.Warning)), cancellationToken).ConfigureAwait(false);
            return result.Outcome == InputOutcome.Submitted ? result.Text : null;
        }
        finally
        {
            _live.Draft = draft;
        }
    }

    public async Task<string?> AskSecretAsync(string question, CancellationToken cancellationToken)
    {
        _screen.ShowInput(new InputView(Line.Of(new Segment(question, Tone.Warning)), string.Empty, 0));
        var text = new System.Text.StringBuilder();
        try
        {
            while (true)
            {
                var stroke = await _keys.ReadAsync(cancellationToken).ConfigureAwait(false);
                if (stroke is not { } key)
                {
                    return null;
                }

                switch (key.Key.Key)
                {
                    case ConsoleKey.Enter:
                        return text.ToString();
                    case ConsoleKey.Escape:
                        return null;
                    case ConsoleKey.Backspace:
                        if (text.Length > 0)
                        {
                            text.Length--;
                        }

                        break;
                    default:
                        if (key.Key.KeyChar != '\0' && !char.IsControl(key.Key.KeyChar))
                        {
                            text.Append(key.Key.KeyChar);
                        }

                        break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        finally
        {
            _screen.HideInput();
            _screen.WriteLine(Line.Of(new Segment(question, Tone.Warning), new Segment("(not shown)", Tone.Muted)));
        }
    }

    public IDisposable ReleaseKeyboard() => _keys.Pause();

    public void Remember(string text) => _history.Add(text);

    public async ValueTask DisposeAsync()
    {
        if (_idle is not null)
        {
            await _idle.DisposeAsync().ConfigureAwait(false);
        }
    }
}

/// <summary>Input that arrives as lines: a pipe, a file, or a terminal that cannot position the cursor.</summary>
public sealed class PlainInput : IShellInput
{
    private readonly Screen _screen;
    private readonly LineSource _lines;
    private readonly bool _interactive;
    private readonly PlainApprovals _approvals;

    /// <param name="interactive">
    /// True when a person types the lines and sees what is written, so that questions can be asked. The prompt
    /// and every question are written only then.
    /// </param>
    public PlainInput(Screen screen, TextReader reader, bool interactive)
    {
        _screen = screen;
        _lines = new LineSource(reader);
        _interactive = interactive;
        _approvals = new PlainApprovals(this, screen, interactive);
    }

    public bool CanAsk => _interactive;

    public IApprovalBroker Approvals => _approvals;

    /// <summary>
    /// The input of a console YAV cannot draw in. Somebody can be asked only where the input is typed and the
    /// output is seen: a question that is written into a file is seen by nobody.
    /// </summary>
    public static PlainInput For(Screen screen, TextReader reader, ConsoleCapabilities capabilities) =>
        new(screen, reader, capabilities.Interactive);

    public async Task<InputResult> ReadAsync(Line prompt, bool runActive, CancellationToken cancellationToken)
    {
        // The line is asked for before the prompt is shown, so that what is typed at the prompt is the prompt's.
        var reading = _lines.ReadLineAsync(question: false, cancellationToken);
        Write(prompt.PlainText);
        string? line;
        try
        {
            line = await reading.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new InputResult(InputOutcome.Cancelled, string.Empty);
        }

        return line is null ? new InputResult(InputOutcome.EndOfInput, string.Empty) : new InputResult(InputOutcome.Submitted, line);
    }

    public async Task<string?> AskAsync(string question, CancellationToken cancellationToken)
    {
        if (!_interactive)
        {
            return null;
        }

        var answer = _lines.ReadLineAsync(question: false, cancellationToken);
        Write(question);
        try
        {
            return await answer.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    // Without control over the terminal the text would be shown. A secret is not asked for then.
    public Task<string?> AskSecretAsync(string question, CancellationToken cancellationToken) => Task.FromResult<string?>(null);

    public IDisposable ReleaseKeyboard() => new Nothing();

    public void Remember(string text)
    {
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    /// <summary>The answer to a question of an agent: the next line, before the prompt or a confirmation gets it. Null when the input has ended.</summary>
    private async Task<string?> AnswerAsync(string prompt, CancellationToken cancellationToken)
    {
        // Waiting before the question is shown: the line that answers it cannot go to anyone else.
        var answer = _lines.ReadLineAsync(question: true, cancellationToken);
        Write(prompt);
        return await answer.ConfigureAwait(false);
    }

    private void Write(string text)
    {
        if (!_interactive)
        {
            return;
        }

        lock (_screen.Gate)
        {
            _screen.Terminal.Write(text);
            _screen.Terminal.Flush();
        }
    }

    private sealed class Nothing : IDisposable
    {
        public void Dispose()
        {
        }
    }

    /// <summary>The same question as in the console, answered with a line. The same answers decide it.</summary>
    private sealed class PlainApprovals(PlainInput input, Screen screen, bool interactive) : IApprovalBroker
    {
        // One question at a time: an answer belongs to the question it follows.
        private readonly SemaphoreSlim _one = new(1, 1);

        public bool CanAsk => interactive;

        public async Task<ApprovalDecision> AskAsync(string runId, AgentRole role, ApprovalRequest request, CancellationToken cancellationToken)
        {
            await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var question = ApprovalQuestion.For(role, request, screen.Options.Unicode);
                screen.WriteLines(question.Lines);
                while (true)
                {
                    var answer = await input.AnswerAsync(question.Prompt(screen.Width).PlainText, cancellationToken).ConfigureAwait(false);
                    if (answer is null)
                    {
                        // The input has ended, so nobody answers any more.
                        return ApprovalDecision.Decline;
                    }

                    if (answer.Trim().Length == 0)
                    {
                        // An empty line decides nothing.
                        continue;
                    }

                    var (decision, say) = question.Decide(answer);
                    if (decision is { } decided)
                    {
                        return decided;
                    }

                    screen.WriteLine(Line.Of(new Segment(ConsoleApprovals.Indent + say, Tone.Muted)));
                }
            }
            finally
            {
                _one.Release();
            }
        }
    }
}

/// <summary>
/// Asks the user whether an agent may do what it asked for. The question is drawn by YAV, and what the agent wrote
/// is shown as it is behind the gutter. An answer is typed after the prompt, where it is shown, and sent with Enter.
/// Pasted text never answers, and an answer that was begun before the question could be read grants nothing.
/// </summary>
public sealed class ConsoleApprovals : IApprovalBroker
{
    /// <summary>How long an answer may be. More is not taken.</summary>
    public const int LongestAnswer = 32;

    /// <summary>Where lines of YAV below a question begin: under the text of its stage line.</summary>
    internal const string Indent = "           ";

    /// <summary>
    /// How long a question has to be open for keys before an answer that grants may begin. A question appears while
    /// something else is being typed, and what is typed next was not meant for it.
    /// </summary>
    public static readonly TimeSpan Settle = TimeSpan.FromMilliseconds(600);

    private readonly Screen _screen;
    private readonly LiveInputLine _input;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _one = new(1, 1);

    /// <param name="clock">What the time a question has been open is measured with. Null is the system's clock.</param>
    public ConsoleApprovals(Screen screen, LiveInputLine input, TimeProvider? clock = null)
    {
        _screen = screen;
        _input = input;
        _clock = clock ?? TimeProvider.System;
    }

    public bool CanAsk => true;

    public async Task<ApprovalDecision> AskAsync(string runId, AgentRole role, ApprovalRequest request, CancellationToken cancellationToken)
    {
        // One question at a time. A question that comes while another is open waits, and gets an answer of its own.
        await _one.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var question = ApprovalQuestion.For(role, request, _screen.Options.Unicode);
            _screen.WriteLines(question.Lines);
            var open = new OpenQuestion(this, question);
            using var withdrawn = cancellationToken.Register(() => open.Decided.TrySetCanceled(cancellationToken));
            using var held = _screen.HoldStatus();
            using (_input.TakeKeyboard(open))
            {
                return await open.Decided.Task.ConfigureAwait(false);
            }
        }
        finally
        {
            _one.Release();
        }
    }

    public static IEnumerable<Line> Describe(AgentRole role, ApprovalRequest request, bool unicode) => ApprovalQuestion.For(role, request, unicode).Lines;

    private void Say(string text) => _screen.WriteLines([Line.Of(new Segment(Indent + text, Tone.Muted))]);

    /// <summary>A question that has the keyboard: what was typed for it, and since when keys reach it.</summary>
    private sealed class OpenQuestion(ConsoleApprovals owner, ApprovalQuestion question) : IKeyboardOwner
    {
        private readonly Lock _gate = new();
        private readonly StringBuilder _answer = new();
        private long? _openedAt;
        private long _begunAt;

        public TaskCompletionSource<ApprovalDecision> Decided { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public InputView View
        {
            get
            {
                var prompt = question.Prompt(owner._screen.Width);
                lock (_gate)
                {
                    return new InputView(prompt, _answer.ToString(), _answer.Length);
                }
            }
        }

        public void Opened()
        {
            lock (_gate)
            {
                // Keys reach it from now on. What was typed before was not typed for it, and the time to read it begins now.
                _answer.Clear();
                _openedAt = owner._clock.GetTimestamp();
            }
        }

        public void Take(KeyStroke stroke)
        {
            var key = stroke.Key;
            string? say = null;
            ApprovalDecision? decided = null;
            lock (_gate)
            {
                // Pasted text never answers: not a letter of it, not a line break in it, not a control it contains.
                if (_openedAt is not { } openedAt || stroke.Pasted || Decided.Task.IsCompleted)
                {
                    return;
                }

                var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
                if ((key.Key == ConsoleKey.C && control) || key.KeyChar == '\u0003')
                {
                    // Control+C stops the run everywhere else. Here it declines and stops the turn, which grants nothing.
                    decided = ApprovalDecision.Cancel;
                }
                else if (key.Key == ConsoleKey.Escape)
                {
                    decided = ApprovalDecision.Decline;
                }
                else if (key.Key == ConsoleKey.Enter)
                {
                    // Enter with Shift or Control starts a new line elsewhere. An answer has one line, so that sends nothing.
                    if ((key.Modifiers & (ConsoleModifiers.Shift | ConsoleModifiers.Control)) == 0)
                    {
                        (decided, say) = Submit(openedAt);
                    }
                }
                else if (key.Key == ConsoleKey.Backspace)
                {
                    RemoveLast();
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar) && _answer.Length < LongestAnswer)
                {
                    if (_answer.Length == 0)
                    {
                        _begunAt = owner._clock.GetTimestamp();
                    }

                    _answer.Append(key.KeyChar);
                }
            }

            if (say is not null)
            {
                owner.Say(say);
            }

            if (decided is { } decision)
            {
                Decided.TrySetResult(decision);
            }
        }

        private (ApprovalDecision? Decision, string? Say) Submit(long openedAt)
        {
            var answer = _answer.ToString();
            if (answer.Trim().Length == 0)
            {
                // An empty answer decides nothing and says nothing.
                return (null, null);
            }

            _answer.Clear();
            var (decision, say) = question.Decide(answer);
            if (decision is ApprovalDecision.Accept or ApprovalDecision.AcceptForSession
                && owner._clock.GetElapsedTime(openedAt, _begunAt) < Settle)
            {
                return (null, "The answer was begun before the question could be read, so it was not taken. Type it again to allow.");
            }

            return (decision, say);
        }

        private void RemoveLast()
        {
            if (_answer.Length == 0)
            {
                return;
            }

            var last = _answer.Length - 1;
            if (last > 0 && char.IsLowSurrogate(_answer[last]) && char.IsHighSurrogate(_answer[last - 1]))
            {
                last--;
            }

            _answer.Length = last;
        }
    }
}
