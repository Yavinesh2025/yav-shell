using System.Globalization;
using System.Text;
using PrettyPrompt.Rendering;

namespace Yav.Console.Rendering;

/// <summary>What is written to: the window of the terminal, or a stream.</summary>
public interface ITerminal
{
    int Width { get; }

    int Height { get; }

    void Write(string text);

    void Flush();
}

/// <param name="Rich">The terminal understands escape sequences, so the cursor can be positioned.</param>
/// <param name="Color">Colors may be used. Off when the user asked for none.</param>
/// <param name="Unicode">Characters outside ASCII can be shown.</param>
public sealed record ScreenOptions(bool Rich, bool Color, bool Unicode);

/// <summary>The line the user is typing: a prompt, the text and where the caret is in it.</summary>
public sealed record InputView(Line Prompt, string Text, int Caret);

/// <summary>
/// The one owner of the terminal. Everything that is shown goes through here, so output from a run, from
/// a command and from the input line can never be written into one another.
/// </summary>
public sealed class Screen
{
    private const string Csi = "\u001b[";
    private const string Reset = "\u001b[0m";
    private const string NewLine = "\r\n";

    private readonly Lock _gate = new();
    private readonly List<string> _deferred = [];
    private int _suspended;
    private InputView? _input;
    private Line? _status;
    private Action? _onNextWrite;

    // Rows the input occupies on the screen right now, and the row of those the cursor is in.
    private int _inputRows;
    private int _caretRow;
    private int _caretColumn;

    // Whether the status is on the screen now, as the first of the rows of the input.
    private bool _statusDrawn;

    public Screen(ITerminal terminal, ScreenOptions options)
    {
        Terminal = terminal;
        Options = options with { Color = options.Color && options.Rich };
    }

    public ITerminal Terminal { get; }

    public ScreenOptions Options { get; }

    public int Width => Math.Max(20, Terminal.Width);

    /// <summary>The object that serializes every write to the terminal, for an editor that writes by itself.</summary>
    public Lock Gate => _gate;

    public void WriteLine(Line line) => WriteRendered(Render(line) + NewLine);

    public void WriteLines(IEnumerable<Line> lines)
    {
        var builder = new StringBuilder();
        foreach (var line in lines)
        {
            builder.Append(Render(line)).Append(NewLine);
        }

        if (builder.Length > 0)
        {
            WriteRendered(builder.ToString());
        }
    }

    /// <summary>
    /// Writes text that was formatted elsewhere, for example a table, as one piece. The caller is
    /// responsible for what it contains; text from agents and tools is never passed here unformatted.
    /// </summary>
    public void WriteBlock(string text)
    {
        var normalized = text.ReplaceLineEndings("\n").TrimEnd('\n');
        if (normalized.Length == 0)
        {
            return;
        }

        WriteRendered(normalized.Replace("\n", NewLine, StringComparison.Ordinal) + NewLine);
    }

    public void ShowInput(InputView view)
    {
        lock (_gate)
        {
            _input = view;
            if (!Options.Rich || _suspended > 0)
            {
                return;
            }

            var builder = new StringBuilder();
            AppendErase(builder);
            AppendInput(builder);
            Send(builder);
        }
    }

    public void HideInput()
    {
        lock (_gate)
        {
            _input = null;
            if (_inputRows == 0)
            {
                return;
            }

            var builder = new StringBuilder();
            AppendErase(builder);
            AppendInput(builder);
            Send(builder);
        }
    }

    /// <summary>
    /// Shows one row of status above the input, and replaces the one shown before in place, so that it never
    /// adds to the scrollback. A terminal that cannot position the cursor gets nothing: its output stays as it is.
    /// </summary>
    public void ShowStatus(Line status)
    {
        lock (_gate)
        {
            if (!Options.Rich)
            {
                return;
            }

            _status = status;
            if (_suspended > 0)
            {
                return;
            }

            var builder = new StringBuilder();
            if (_statusDrawn)
            {
                // Only its own row is written again: the input below it stays as it is, and so does the caret.
                builder.Append('\r');
                if (_caretRow > 0)
                {
                    builder.Append(Csi).Append(_caretRow.ToString(CultureInfo.InvariantCulture)).Append('A');
                }

                AppendStatus(builder, status);
                builder.Append(Csi).Append('K').Append('\r');
                if (_caretRow > 0)
                {
                    builder.Append(Csi).Append(_caretRow.ToString(CultureInfo.InvariantCulture)).Append('B');
                }

                if (_caretColumn > 0)
                {
                    builder.Append(Csi).Append(_caretColumn.ToString(CultureInfo.InvariantCulture)).Append('C');
                }

                Send(builder);
                return;
            }

            AppendErase(builder);
            AppendInput(builder);
            Send(builder);
        }
    }

    /// <summary>Removes the row of status, if one is shown, and leaves the input where it is.</summary>
    public void HideStatus()
    {
        lock (_gate)
        {
            if (_status is null)
            {
                return;
            }

            _status = null;
            if (_suspended > 0 || _inputRows == 0)
            {
                return;
            }

            var builder = new StringBuilder();
            AppendErase(builder);
            AppendInput(builder);
            Send(builder);
        }
    }

    /// <summary>
    /// Calls back once, when something is written to the terminal the next time. Used to measure how long
    /// it takes until what the user entered is answered.
    /// </summary>
    public void NotifyNextWrite(Action callback)
    {
        lock (_gate)
        {
            _onNextWrite = callback;
        }
    }

    /// <summary>Holds output back while another editor owns the cursor. It is written when the scope ends.</summary>
    public IDisposable Suspend()
    {
        lock (_gate)
        {
            if (_suspended++ == 0 && _inputRows > 0)
            {
                var builder = new StringBuilder();
                AppendErase(builder);
                Send(builder);
            }
        }

        return new Suspension(this);
    }

    private void Resume()
    {
        lock (_gate)
        {
            if (--_suspended > 0)
            {
                return;
            }

            var builder = new StringBuilder();
            foreach (var text in _deferred)
            {
                builder.Append(text);
            }

            _deferred.Clear();
            AppendInput(builder);
            Send(builder);
        }
    }

    private void WriteRendered(string text)
    {
        lock (_gate)
        {
            if (_suspended > 0)
            {
                _deferred.Add(text);
                return;
            }

            var builder = new StringBuilder();
            AppendErase(builder);
            builder.Append(text);
            AppendInput(builder);
            Send(builder);
        }
    }

    private void Send(StringBuilder builder)
    {
        if (builder.Length == 0)
        {
            return;
        }

        Terminal.Write(builder.ToString());
        Terminal.Flush();

        var callback = _onNextWrite;
        _onNextWrite = null;
        callback?.Invoke();
    }

    /// <summary>
    /// How wide a row may be. The last column stays free, because terminals differ in what they do when
    /// it is written to. A stream that is no window has no rows.
    /// </summary>
    private int RowWidth => Terminal.Width == int.MaxValue ? int.MaxValue : Width - 1;

    private string Render(Line line)
    {
        var builder = new StringBuilder();
        var rows = line.Rows(RowWidth);
        for (var i = 0; i < rows.Count; i++)
        {
            if (i > 0)
            {
                builder.Append(NewLine);
            }

            if (!Options.Color)
            {
                builder.Append(rows[i].PlainText);
                continue;
            }

            foreach (var segment in rows[i].Segments)
            {
                AppendStyled(builder, segment.Text, segment.Tone, segment.Bold);
            }
        }

        return builder.ToString();
    }

    private void AppendStyled(StringBuilder builder, string text, Tone tone, bool bold)
    {
        var code = Options.Color ? Code(tone, bold) : null;
        if (code is null)
        {
            builder.Append(text);
            return;
        }

        builder.Append(Csi).Append(code).Append('m').Append(text).Append(Reset);
    }

    /// <summary>Only the named colors of the terminal's own palette, so the user's theme decides how they look.</summary>
    private static string? Code(Tone tone, bool bold)
    {
        var color = tone switch
        {
            Tone.Muted => "90",
            Tone.Success => "32",
            Tone.Warning => "33",
            Tone.Error => "31",
            Tone.Accent => "36",
            _ => null,
        };

        var emphasized = bold || tone is Tone.Stage or Tone.Prompt;
        return (color, emphasized) switch
        {
            (null, false) => null,
            (null, true) => "1",
            (_, false) => color,
            _ => "1;" + color,
        };
    }

    private void AppendErase(StringBuilder builder)
    {
        if (_inputRows == 0)
        {
            return;
        }

        builder.Append('\r');
        if (_caretRow > 0)
        {
            builder.Append(Csi).Append(_caretRow.ToString(CultureInfo.InvariantCulture)).Append('A');
        }

        builder.Append(Csi).Append('J');
        _inputRows = 0;
        _caretRow = 0;
        _caretColumn = 0;
        _statusDrawn = false;
    }

    private readonly record struct Cell(string Text, int Width, Tone Tone, bool Bold);

    private void AppendInput(StringBuilder builder)
    {
        if (!Options.Rich)
        {
            return;
        }

        // The status is one row, cut at the edge, so that the rows to erase are known.
        var statusRows = 0;
        if (_status is { } status)
        {
            AppendStatus(builder, status);
            statusRows = 1;
            _statusDrawn = true;
            if (_input is null)
            {
                builder.Append('\r');
                _inputRows = 1;
                _caretRow = 0;
                _caretColumn = 0;
                return;
            }

            builder.Append(NewLine);
        }

        if (_input is not { } view)
        {
            return;
        }

        var cells = new List<Cell>();
        foreach (var segment in view.Prompt.Segments)
        {
            AddCells(cells, segment.Text, segment.Tone, segment.Bold);
        }

        var caretCell = -1;
        var elements = StringInfo.GetTextElementEnumerator(view.Text);
        while (elements.MoveNext())
        {
            if (caretCell < 0 && elements.ElementIndex >= view.Caret)
            {
                caretCell = cells.Count;
            }

            var element = elements.GetTextElement();
            switch (element)
            {
                case "\n" or "\r\n":
                    cells.Add(new Cell(Options.Unicode ? "⏎" : "\\", 1, Tone.Muted, false));
                    break;
                case "\r":
                    break;
                case "\t":
                    cells.Add(new Cell(" ", 1, Tone.Normal, false));
                    break;
                default:
                    if (!char.IsControl(element, 0))
                    {
                        cells.Add(new Cell(element, Math.Max(1, UnicodeWidth.GetWidth(element)), Tone.Normal, false));
                    }

                    break;
            }
        }

        if (caretCell < 0)
        {
            caretCell = cells.Count;
        }

        // Rows are broken here and not by the terminal, so their number is known when they have to be erased.
        var width = Width;
        var row = 0;
        var column = 0;
        var caretRow = 0;
        var caretColumn = 0;
        for (var i = 0; i < cells.Count; i++)
        {
            var cell = cells[i];
            if (column + cell.Width > width)
            {
                builder.Append(NewLine);
                row++;
                column = 0;
            }

            if (i == caretCell)
            {
                (caretRow, caretColumn) = (row, column);
            }

            AppendStyled(builder, cell.Text, cell.Tone, cell.Bold);
            column += cell.Width;
        }

        if (caretCell == cells.Count)
        {
            if (column >= width)
            {
                builder.Append(NewLine);
                row++;
                column = 0;
            }

            (caretRow, caretColumn) = (row, column);
        }

        builder.Append('\r');
        if (row > caretRow)
        {
            builder.Append(Csi).Append((row - caretRow).ToString(CultureInfo.InvariantCulture)).Append('A');
        }

        if (caretColumn > 0)
        {
            builder.Append(Csi).Append(caretColumn.ToString(CultureInfo.InvariantCulture)).Append('C');
        }

        _inputRows = statusRows + row + 1;
        _caretRow = statusRows + caretRow;
        _caretColumn = caretColumn;
    }

    private void AppendStatus(StringBuilder builder, Line status)
    {
        foreach (var segment in status.Rows(Width - 1)[0].Segments)
        {
            AppendStyled(builder, segment.Text, segment.Tone, segment.Bold);
        }
    }

    private static void AddCells(List<Cell> cells, string text, Tone tone, bool bold)
    {
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            var element = elements.GetTextElement();
            cells.Add(new Cell(element, Math.Max(1, UnicodeWidth.GetWidth(element)), tone, bold));
        }
    }

    private sealed class Suspension(Screen owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                owner.Resume();
            }
        }
    }
}
