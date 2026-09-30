using System.Globalization;
using System.Text;
using Yav.Console.Rendering;

namespace Yav.Console.Input;

public enum InputOutcome
{
    Submitted,

    /// <summary>Control+C on an empty line.</summary>
    Interrupted,
    EndOfInput,
    Cancelled,
}

public sealed record InputResult(InputOutcome Outcome, string Text);

/// <summary>What was entered before, newest last.</summary>
public sealed class InputHistory
{
    private readonly int _capacity;
    private readonly List<string> _entries = [];
    private readonly Lock _gate = new();

    public InputHistory(int capacity = 500)
    {
        _capacity = Math.Max(1, capacity);
    }

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_gate)
            {
                return _entries.ToArray();
            }
        }
    }

    public void Add(string entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return;
        }

        lock (_gate)
        {
            if (_entries.Count > 0 && string.Equals(_entries[^1], entry, StringComparison.Ordinal))
            {
                return;
            }

            _entries.Add(entry);
            if (_entries.Count > _capacity)
            {
                _entries.RemoveRange(0, _entries.Count - _capacity);
            }
        }
    }
}

/// <summary>
/// Tells pasted text from typed text by how the keys arrive: pasted keys arrive together. A line break
/// inside pasted text must never submit the input.
/// </summary>
public sealed class PasteDetector
{
    // Keys that are held down repeat about every thirty milliseconds; pasted keys follow each other at once.
    private static readonly long Window = TimeSpan.FromMilliseconds(15).Ticks;

    private long _last = long.MinValue;
    private bool _inBurst;

    /// <param name="timestampTicks">When the key was read, in ticks of 100 nanoseconds.</param>
    /// <param name="moreAvailable">True when another key was already waiting when this one was read.</param>
    public bool IsPasted(long timestampTicks, bool moreAvailable)
    {
        var close = _last != long.MinValue && timestampTicks - _last <= Window;
        var pasted = moreAvailable || (_inBurst && close);
        _inBurst = pasted;
        _last = timestampTicks;
        return pasted;
    }
}

/// <summary>Something that has the keyboard for a while instead of the line that is being written, such as a question.</summary>
public interface IKeyboardOwner
{
    /// <summary>What the input line shows while it has the keyboard: its prompt and what was typed for it.</summary>
    InputView View { get; }

    /// <summary>
    /// Keys reach it from this moment on. That is when it is given the keyboard while keys are read, or when
    /// reading begins while it has the keyboard; keys that were typed before that are thrown away, not given to it.
    /// </summary>
    void Opened();

    /// <summary>A key that was read while it has the keyboard.</summary>
    void Take(KeyStroke stroke);
}

/// <summary>
/// The input line that is used while a run is active. It lives at the bottom of the output: what the run
/// reports is written above it and what was typed stays as it is.
/// </summary>
public sealed class LiveInputLine
{
    private const string Indentation = "    ";

    private readonly Screen _screen;
    private readonly IKeySource _keys;
    private readonly InputHistory _history;
    private readonly Func<string, IEnumerable<string>>? _complete;
    private readonly Lock _gate = new();
    private readonly StringBuilder _text = new();
    private int _caret;
    private Line _prompt = Line.Empty;
    private IKeyboardOwner? _owner;
    private bool _reading;

    // Where in the history the user is; -1 is the line being written.
    private int _recalled = -1;
    private string _draft = string.Empty;

    public LiveInputLine(Screen screen, IKeySource keys, InputHistory history, Func<string, IEnumerable<string>>? complete = null)
    {
        _screen = screen;
        _keys = keys;
        _history = history;
        _complete = complete;
    }

    /// <summary>The text that is being written, for example to keep it when the shell changes how it reads.</summary>
    public string Draft
    {
        get
        {
            lock (_gate)
            {
                return _text.ToString();
            }
        }

        set
        {
            lock (_gate)
            {
                _text.Clear().Append(value);
                _caret = _text.Length;
            }
        }
    }

    public async Task<InputResult> ReadAsync(Line prompt, CancellationToken cancellationToken)
    {
        IKeyboardOwner? waiting;
        lock (_gate)
        {
            _prompt = prompt;
            _reading = true;
            _recalled = -1;
            waiting = _owner;
        }

        try
        {
            if (waiting is not null)
            {
                // Something took the keyboard while nothing read it. What was typed meanwhile was typed before it
                // could be seen, so it is thrown away instead of being given to it.
                if (await DiscardWaitingKeysAsync(cancellationToken).ConfigureAwait(false) is { } ended)
                {
                    return ended;
                }

                lock (_gate)
                {
                    waiting = ReferenceEquals(_owner, waiting) ? waiting : null;
                }

                waiting?.Opened();
            }

            Show();
            while (true)
            {
                KeyStroke? stroke;
                try
                {
                    stroke = await _keys.ReadAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // What was typed so far is kept for the next read.
                    _screen.HideInput();
                    return new InputResult(InputOutcome.Cancelled, string.Empty);
                }

                if (stroke is not { } key)
                {
                    _screen.HideInput();
                    return new InputResult(InputOutcome.EndOfInput, string.Empty);
                }

                IKeyboardOwner? owner;
                lock (_gate)
                {
                    owner = _owner;
                }

                if (owner is not null)
                {
                    owner.Take(key);
                    Show();
                    continue;
                }

                if (Handle(key) is { } result)
                {
                    return result;
                }

                Show();
            }
        }
        finally
        {
            lock (_gate)
            {
                _reading = false;
            }
        }
    }

    /// <summary>
    /// Gives the keyboard to something that needs an answer now, such as a request for approval. What
    /// was being typed is put aside and comes back when the scope ends. It is shown, and keys reach it, only
    /// while keys are read.
    /// </summary>
    public IDisposable TakeKeyboard(IKeyboardOwner owner)
    {
        bool reading;
        lock (_gate)
        {
            _owner = owner;
            reading = _reading;
        }

        if (reading)
        {
            // Keys are read now, so they reach it from this moment on.
            owner.Opened();
            Show();
        }

        return new Release(this, owner);
    }

    /// <summary>Gives the keyboard to a handler that is given every key, below a prompt of its own.</summary>
    public IDisposable TakeKeyboard(Line prompt, Action<KeyStroke> handler) => TakeKeyboard(new Handler(prompt, handler));

    private void ReleaseKeyboard(IKeyboardOwner owner)
    {
        bool reading;
        lock (_gate)
        {
            if (!ReferenceEquals(_owner, owner))
            {
                return;
            }

            _owner = null;
            reading = _reading;
        }

        if (reading)
        {
            Show();
        }
        else
        {
            _screen.HideInput();
        }
    }

    /// <summary>Reads the keys that are already waiting and forgets them. A result when the input ended or reading was cancelled meanwhile.</summary>
    private async Task<InputResult?> DiscardWaitingKeysAsync(CancellationToken cancellationToken)
    {
        while (_keys.KeyAvailable)
        {
            KeyStroke? stroke;
            try
            {
                stroke = await _keys.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                _screen.HideInput();
                return new InputResult(InputOutcome.Cancelled, string.Empty);
            }

            if (stroke is null)
            {
                _screen.HideInput();
                return new InputResult(InputOutcome.EndOfInput, string.Empty);
            }
        }

        return null;
    }

    private void Show()
    {
        IKeyboardOwner? owner;
        InputView view;
        lock (_gate)
        {
            owner = _owner;
            view = new InputView(_prompt, _text.ToString(), _caret);
        }

        _screen.ShowInput(owner?.View ?? view);
    }

    private InputResult? Handle(KeyStroke stroke)
    {
        var key = stroke.Key;
        var control = (key.Modifiers & ConsoleModifiers.Control) != 0;
        var shift = (key.Modifiers & ConsoleModifiers.Shift) != 0;

        lock (_gate)
        {
            if (stroke.Pasted)
            {
                // Pasted text is text. Nothing in it is a command to the editor, least of all a submit.
                if (key.Key == ConsoleKey.Enter || key.KeyChar is '\r' or '\n')
                {
                    Insert("\n");
                }
                else if (key.KeyChar == '\t')
                {
                    Insert(Indentation);
                }
                else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
                {
                    Insert(key.KeyChar.ToString());
                }

                return null;
            }

            switch (key.Key)
            {
                // Control+J reaches a program as that key or, from a terminal that sends a plain line
                // feed, as Control+Enter.
                case ConsoleKey.Enter when shift || control:
                case ConsoleKey.J when control:
                    Insert("\n");
                    return null;

                case ConsoleKey.Enter:
                    return Submit();

                case ConsoleKey.C when control:
                    if (_text.Length > 0)
                    {
                        Clear();
                        return null;
                    }

                    _screen.HideInput();
                    return new InputResult(InputOutcome.Interrupted, string.Empty);

                case ConsoleKey.Escape:
                    Clear();
                    return null;

                case ConsoleKey.Backspace:
                    if (_caret > 0)
                    {
                        var start = control ? WordLeft() : Previous();
                        _text.Remove(start, _caret - start);
                        _caret = start;
                    }

                    return null;

                case ConsoleKey.Delete:
                    if (_caret < _text.Length)
                    {
                        var end = control ? WordRight() : Next();
                        _text.Remove(_caret, end - _caret);
                    }

                    return null;

                case ConsoleKey.LeftArrow:
                    _caret = control ? WordLeft() : Previous();
                    return null;

                case ConsoleKey.RightArrow:
                    _caret = control ? WordRight() : Next();
                    return null;

                case ConsoleKey.Home:
                    _caret = 0;
                    return null;

                case ConsoleKey.End:
                    _caret = _text.Length;
                    return null;

                case ConsoleKey.UpArrow:
                    Recall(-1);
                    return null;

                case ConsoleKey.DownArrow:
                    Recall(+1);
                    return null;

                case ConsoleKey.Tab:
                    Complete();
                    return null;
            }

            if (key.KeyChar == '\u0003')
            {
                // Control+C as some terminals deliver it.
                if (_text.Length > 0)
                {
                    Clear();
                    return null;
                }

                _screen.HideInput();
                return new InputResult(InputOutcome.Interrupted, string.Empty);
            }

            if (key.KeyChar == '\n')
            {
                Insert("\n");
            }
            else if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                Insert(key.KeyChar.ToString());
            }

            return null;
        }
    }

    private InputResult Submit()
    {
        var text = _text.ToString();
        var prompt = _prompt;
        Clear();
        _draft = string.Empty;
        _recalled = -1;

        // The line moves from the input into the output, where it stays.
        _screen.HideInput();
        _screen.WriteLines(Echo(prompt, text));
        _history.Add(text);
        return new InputResult(InputOutcome.Submitted, text);
    }

    private static IEnumerable<Line> Echo(Line prompt, string text)
    {
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        yield return Line.Of([.. prompt.Segments, new Segment(lines[0])]);

        var indent = new string(' ', Math.Min(prompt.PlainText.Length, 12));
        foreach (var line in lines.Skip(1))
        {
            yield return Line.Of(new Segment(indent, Tone.Muted), new Segment(line));
        }
    }

    private void Insert(string value)
    {
        _text.Insert(_caret, value);
        _caret += value.Length;
        _recalled = -1;
    }

    private void Clear()
    {
        _text.Clear();
        _caret = 0;
    }

    private void Recall(int direction)
    {
        var entries = _history.Entries;
        if (entries.Count == 0)
        {
            return;
        }

        // Positions count back from the newest entry; -1 is the line that was being written.
        var position = _recalled - direction;
        if (position < -1 || position >= entries.Count)
        {
            return;
        }

        if (_recalled == -1)
        {
            _draft = _text.ToString();
        }

        _recalled = position;
        var text = position == -1 ? _draft : entries[entries.Count - 1 - position];
        _text.Clear().Append(text);
        _caret = _text.Length;
    }

    private void Complete()
    {
        var text = _text.ToString();
        var isCommandName = text.StartsWith('/') && !text.Any(char.IsWhiteSpace) && _caret == text.Length;
        if (!isCommandName || _complete is null)
        {
            Insert(Indentation);
            return;
        }

        var candidates = _complete(text).ToList();
        if (candidates.Count == 0)
        {
            return;
        }

        var common = candidates[0];
        foreach (var candidate in candidates.Skip(1))
        {
            var length = 0;
            while (length < common.Length && length < candidate.Length && char.ToLowerInvariant(common[length]) == char.ToLowerInvariant(candidate[length]))
            {
                length++;
            }

            common = common[..length];
        }

        if (common.Length > text.Length)
        {
            _text.Clear().Append(common);
            _caret = _text.Length;
        }

        if (candidates.Count > 1)
        {
            _screen.WriteLine(Line.Of(string.Join("  ", candidates), Tone.Muted));
        }
    }

    private int Previous()
    {
        if (_caret == 0)
        {
            return 0;
        }

        var text = _text.ToString();
        var previous = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext() && elements.ElementIndex < _caret)
        {
            previous = elements.ElementIndex;
        }

        return previous;
    }

    private int Next()
    {
        if (_caret >= _text.Length)
        {
            return _text.Length;
        }

        var text = _text.ToString();
        return _caret + StringInfo.GetNextTextElementLength(text, _caret);
    }

    private int WordLeft()
    {
        var position = _caret;
        while (position > 0 && char.IsWhiteSpace(_text[position - 1]))
        {
            position--;
        }

        while (position > 0 && !char.IsWhiteSpace(_text[position - 1]))
        {
            position--;
        }

        return position;
    }

    private int WordRight()
    {
        var position = _caret;
        while (position < _text.Length && !char.IsWhiteSpace(_text[position]))
        {
            position++;
        }

        while (position < _text.Length && char.IsWhiteSpace(_text[position]))
        {
            position++;
        }

        return position;
    }

    private sealed class Release(LiveInputLine line, IKeyboardOwner owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                line.ReleaseKeyboard(owner);
            }
        }
    }

    /// <summary>Gives every key to a handler, below a prompt that does not change.</summary>
    private sealed class Handler(Line prompt, Action<KeyStroke> take) : IKeyboardOwner
    {
        public InputView View => new(prompt, string.Empty, 0);

        public void Opened()
        {
        }

        public void Take(KeyStroke stroke) => take(stroke);
    }
}
