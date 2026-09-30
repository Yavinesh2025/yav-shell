using System.Text;
using System.Threading.Channels;
using Yav.Console.Input;
using Yav.Console.Rendering;

namespace Yav.Tests.Support;

/// <summary>
/// A terminal that exists in memory. It understands the escape sequences YAV writes and nothing else: a
/// sequence it does not know fails the test, so that YAV never writes something whose effect is not tested.
/// Wrapping at the right edge behaves like a real terminal, including the wrap that is pending after the
/// last column was written.
/// The shell writes from its own threads while a test reads, so both take the same lock: a test never sees a
/// row that is half written or a list of rows that grows while it is read.
/// </summary>
public sealed class VirtualTerminal : ITerminal
{
    /// <summary>What the second cell of a wide character holds.</summary>
    private const char WideFiller = '\0';

    private readonly Lock _gate = new();
    private readonly List<char[]> _rows = [];

    // Rows that ended because the right edge was reached, not because a line ended.
    private readonly HashSet<int> _wrapped = [];
    private readonly StringBuilder _raw = new();
    private int _width;
    private int _top;
    private int _row;
    private int _column;
    private bool _pendingWrap;
    private bool _cursorVisible = true;

    public VirtualTerminal(int width = 40, int height = 8)
    {
        _width = width;
        Height = height;
        for (var i = 0; i < height; i++)
        {
            _rows.Add(NewRow());
        }
    }

    public int Width
    {
        get
        {
            lock (_gate)
            {
                return _width;
            }
        }

        set
        {
            lock (_gate)
            {
                _width = value;
            }
        }
    }

    public int Height { get; }

    public bool CursorVisible
    {
        get
        {
            lock (_gate)
            {
                return _cursorVisible;
            }
        }
    }

    /// <summary>Everything that was written, exactly as it arrived.</summary>
    public string Raw
    {
        get
        {
            lock (_gate)
            {
                return _raw.ToString();
            }
        }
    }

    public int CursorColumn
    {
        get
        {
            lock (_gate)
            {
                return _column;
            }
        }
    }

    /// <summary>The cursor's row, counted from the first line that was ever written.</summary>
    public int CursorRow
    {
        get
        {
            lock (_gate)
            {
                return _top + _row;
            }
        }
    }

    /// <summary>Every line that has text, from the first line ever written, without trailing blanks.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                var lines = _rows.Select(Read).ToList();
                while (lines.Count > 0 && lines[^1].Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                return lines;
            }
        }
    }

    public string Text => string.Join('\n', Lines);

    /// <summary>
    /// The rows on the screen that the terminal began by itself because the right edge was reached. Such
    /// a row starts in the first column with whatever text happens to be there, so YAV must not cause one
    /// outside the line the user is typing.
    /// </summary>
    public IReadOnlyList<string> RowsTheTerminalBegan
    {
        get
        {
            lock (_gate)
            {
                return _wrapped.Order().Select(row => Read(_rows[row + 1])).ToList();
            }
        }
    }

    /// <summary>The line the cursor is on.</summary>
    public string CursorLine
    {
        get
        {
            lock (_gate)
            {
                return Read(_rows[_top + _row]);
            }
        }
    }

    public void Flush()
    {
    }

    public void Write(string text)
    {
        lock (_gate)
        {
            _raw.Append(text);
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '\u001b':
                        i = Escape(text, i);
                        break;

                    case '\r':
                        _column = 0;
                        _pendingWrap = false;
                        break;

                    case '\n':
                        LineFeed();
                        break;

                    case '\b':
                        _column = Math.Max(0, _column - 1);
                        _pendingWrap = false;
                        break;

                    default:
                        if (char.IsControl(c))
                        {
                            throw new InvalidOperationException($"The control character U+{(int)c:X4} was written to the terminal.");
                        }

                        if (char.IsHighSurrogate(c) && i + 1 < text.Length)
                        {
                            Put(text.Substring(i, 2));
                            i++;
                        }
                        else
                        {
                            Put(c.ToString());
                        }

                        break;
                }
            }
        }
    }

    private void Put(string element)
    {
        var width = Math.Max(1, PrettyPrompt.Rendering.UnicodeWidth.GetWidth(element));
        if (_pendingWrap || _column + width > _width)
        {
            _wrapped.Add(_top + _row);
            _column = 0;
            _pendingWrap = false;
            LineFeed();
        }

        var row = _rows[_top + _row];
        row[_column] = element[0];
        for (var i = 1; i < width; i++)
        {
            row[_column + i] = WideFiller;
        }

        _column += width;
        if (_column >= _width)
        {
            _column = _width - 1;
            _pendingWrap = true;
        }
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        if (_row < Height - 1)
        {
            _row++;
            return;
        }

        // The window scrolls: what was on top moves into the scrollback.
        _rows.Add(NewRow());
        _top++;
    }

    private int Escape(string text, int start)
    {
        if (start + 1 >= text.Length || text[start + 1] != '[')
        {
            throw new InvalidOperationException("An escape sequence other than CSI was written: " + Show(text, start));
        }

        var end = start + 2;
        while (end < text.Length && !(text[end] >= '@' && text[end] <= '~'))
        {
            end++;
        }

        if (end >= text.Length)
        {
            throw new InvalidOperationException("An escape sequence was not finished: " + Show(text, start));
        }

        var parameters = text[(start + 2)..end];
        var command = text[end];
        int Count() => parameters.Length == 0 ? 1 : int.Parse(parameters, System.Globalization.CultureInfo.InvariantCulture);

        _pendingWrap = false;
        switch (command)
        {
            case 'm':
                break;
            case 'A':
                _row = Math.Max(0, _row - Count());
                break;
            case 'B':
                _row = Math.Min(Height - 1, _row + Count());
                break;
            case 'C':
                _column = Math.Min(_width - 1, _column + Count());
                break;
            case 'D':
                _column = Math.Max(0, _column - Count());
                break;
            case 'G':
                _column = Math.Clamp(Count() - 1, 0, _width - 1);
                break;
            case 'K' when parameters is "" or "0":
                Array.Fill(_rows[_top + _row], ' ', _column, _width - _column);
                _wrapped.Remove(_top + _row);
                break;
            case 'J' when parameters is "" or "0":
                Array.Fill(_rows[_top + _row], ' ', _column, _width - _column);
                _wrapped.Remove(_top + _row);
                for (var r = _top + _row + 1; r < _rows.Count; r++)
                {
                    Array.Fill(_rows[r], ' ');
                    _wrapped.Remove(r);
                }

                break;
            case 'h' when parameters == "?25":
                _cursorVisible = true;
                break;
            case 'l' when parameters == "?25":
                _cursorVisible = false;
                break;
            default:
                throw new InvalidOperationException("An escape sequence the tests do not know was written: " + Show(text, start));
        }

        return end;
    }

    /// <summary>The row as it is read: a wide character is one character, although it fills two cells.</summary>
    private static string Read(char[] row) =>
        new string(row).Replace(WideFiller.ToString(), string.Empty, StringComparison.Ordinal).TrimEnd();

    private char[] NewRow()
    {
        var row = new char[Math.Max(_width, 400)];
        Array.Fill(row, ' ');
        return row;
    }

    private static string Show(string text, int start) =>
        "ESC" + text.Substring(start + 1, Math.Min(12, text.Length - start - 1)).Replace("\u001b", "ESC", StringComparison.Ordinal);
}

/// <summary>
/// Keys that a test typed, delivered one by one as a console delivers them. Whether a key was pasted is decided
/// by the detector the console uses, from when the keys arrived: typed keys arrive a tenth of a second apart,
/// pasted text arrives at once. While reading is paused no key is delivered, as the console keeps its keys for
/// the program that has it then.
/// Not covered: a console that is read late sees typed keys waiting together and takes them for pasted text;
/// here the time of arrival alone decides. Keys typed during a pause reach YAV afterwards, where in a console
/// the other program would have read them.
/// </summary>
public sealed class ScriptedKeys : IKeySource
{
    // How far apart typed keys arrive. Only the paste detector reads this time.
    private static readonly long Typing = TimeSpan.FromMilliseconds(100).Ticks;

    private readonly Channel<Arrival> _keys = Channel.CreateUnbounded<Arrival>();
    private readonly PasteDetector _paste = new();
    private readonly Lock _gate = new();
    private long _arrived;
    private int _paused;
    private TaskCompletionSource _resumed = Resumed();

    /// <param name="More">True when more keys arrived together with this one, as they do when text is pasted.</param>
    private readonly record struct Arrival(ConsoleKeyInfo Key, long At, bool More);

    public bool KeyAvailable
    {
        get
        {
            lock (_gate)
            {
                return _paused == 0 && _keys.Reader.Count > 0;
            }
        }
    }

    public bool Paused
    {
        get
        {
            lock (_gate)
            {
                return _paused > 0;
            }
        }
    }

    public ScriptedKeys Type(string text)
    {
        foreach (var c in text)
        {
            Add(Character(c), NextArrival(), more: false);
        }

        return this;
    }

    /// <summary>Text that arrives at once, as it does when it is pasted.</summary>
    public ScriptedKeys Paste(string text)
    {
        var at = NextArrival();
        var keys = text.Replace("\r\n", "\n", StringComparison.Ordinal);
        for (var i = 0; i < keys.Length; i++)
        {
            var c = keys[i];

            // Every key but the last finds the next one already there when it is read.
            Add(c == '\n' ? new ConsoleKeyInfo('\r', ConsoleKey.Enter, false, false, false) : Character(c), at, more: i < keys.Length - 1);
        }

        return this;
    }

    public ScriptedKeys Press(ConsoleKey key, bool shift = false, bool alt = false, bool control = false, char character = '\0')
    {
        if (character == '\0')
        {
            character = key switch
            {
                ConsoleKey.Enter => '\r',
                ConsoleKey.Escape => '\u001b',
                ConsoleKey.Backspace => '\b',
                ConsoleKey.Tab => '\t',
                _ => '\0',
            };
        }

        Add(new ConsoleKeyInfo(character, key, shift, alt, control), NextArrival(), more: false);
        return this;
    }

    public ScriptedKeys Enter() => Press(ConsoleKey.Enter);

    public void End() => _keys.Writer.TryComplete();

    public async ValueTask<KeyStroke?> ReadAsync(CancellationToken cancellationToken)
    {
        await ResumedAsync(cancellationToken);
        Arrival arrival;
        try
        {
            arrival = await _keys.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }

        // A key that arrived while reading was paused is delivered when reading goes on, not before.
        await ResumedAsync(CancellationToken.None);
        bool pasted;
        lock (_gate)
        {
            pasted = _paste.IsPasted(arrival.At, arrival.More);
        }

        return new KeyStroke(arrival.Key, pasted);
    }

    public IDisposable Pause()
    {
        lock (_gate)
        {
            if (_paused++ == 0)
            {
                _resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        return new Resume(this);
    }

    private static TaskCompletionSource Resumed()
    {
        var resumed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        resumed.SetResult();
        return resumed;
    }

    private static ConsoleKeyInfo Character(char c)
    {
        var key = c switch
        {
            >= 'a' and <= 'z' => ConsoleKey.A + (c - 'a'),
            >= 'A' and <= 'Z' => ConsoleKey.A + (c - 'A'),
            >= '0' and <= '9' => ConsoleKey.D0 + (c - '0'),
            ' ' => ConsoleKey.Spacebar,
            _ => ConsoleKey.None,
        };
        return new ConsoleKeyInfo(c, key, char.IsUpper(c), false, false);
    }

    private long NextArrival()
    {
        lock (_gate)
        {
            _arrived += Typing;
            return _arrived;
        }
    }

    private void Add(ConsoleKeyInfo key, long at, bool more) => _keys.Writer.TryWrite(new Arrival(key, at, more));

    private async Task ResumedAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task resumed;
            lock (_gate)
            {
                if (_paused == 0)
                {
                    return;
                }

                resumed = _resumed.Task;
            }

            await resumed.WaitAsync(cancellationToken);
        }
    }

    private void EndPause()
    {
        TaskCompletionSource? resumed = null;
        lock (_gate)
        {
            if (--_paused == 0)
            {
                resumed = _resumed;
            }
        }

        resumed?.TrySetResult();
    }

    private sealed class Resume(ScriptedKeys owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.EndPause();
            }
        }
    }
}
