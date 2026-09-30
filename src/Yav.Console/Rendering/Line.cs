using System.Globalization;
using System.Text;
using PrettyPrompt.Rendering;
using Yav.Core.Text;

namespace Yav.Console.Rendering;

/// <summary>What a piece of text means. How it looks is decided by the output, from the terminal's own palette.</summary>
public enum Tone
{
    Normal,
    Muted,
    Success,
    Warning,
    Error,
    Accent,

    /// <summary>The stage label at the start of a status line.</summary>
    Stage,

    /// <summary>The project prompt.</summary>
    Prompt,
}

public readonly record struct Segment(string Text, Tone Tone = Tone.Normal, bool Bold = false);

/// <summary>
/// One line of output. A line never contains a line break or a control sequence: text is cleaned when the
/// line is built, so nothing that reaches the terminal can move the cursor or change the display by itself.
/// A line that is wider than the window is broken into rows here and not by the terminal, because a row
/// the terminal begins starts in the first column, where only the application's own lines may start.
/// </summary>
public sealed class Line
{
    /// <summary>A first segment that is wider than this is text and not a mark.</summary>
    private const int WidestMark = 16;

    /// <summary>What is left for text on a row, however narrow the window is.</summary>
    private const int NarrowestText = 8;

    private static readonly Segment DefaultHang = new("  ");

    private Line(IReadOnlyList<Segment> segments, bool marked = false)
    {
        Segments = segments;
        (Hang, MarkLength) = HangOf(segments, marked);
    }

    private readonly record struct Cell(string Text, int Width, Tone Tone, bool Bold);

    public static Line Empty { get; } = new([]);

    public IReadOnlyList<Segment> Segments { get; }

    /// <summary>What every row after the first begins with. It is never empty.</summary>
    public Segment Hang { get; }

    /// <summary>How many of the segments at the start are the mark of the line: one or none.</summary>
    private int MarkLength { get; }

    public string PlainText
    {
        get
        {
            var builder = new StringBuilder();
            foreach (var segment in Segments)
            {
                builder.Append(segment.Text);
            }

            return builder.ToString();
        }
    }

    public static Line Of(string text, Tone tone = Tone.Normal, bool bold = false) => new([new Segment(Clean(text), tone, bold)]);

    public static Line Of(params Segment[] segments) => new(Cleaned(segments));

    /// <summary>A line whose first segment is a label, so that further rows continue below the text that follows it.</summary>
    public static Line Labelled(Segment label, params Segment[] text) => new(Cleaned([label, .. text]), marked: true);

    /// <summary>Text of several lines as lines, each cleaned.</summary>
    public static IEnumerable<Line> Split(string text, Tone tone = Tone.Normal, string prefix = "", Tone prefixTone = Tone.Muted)
    {
        foreach (var line in TerminalSanitizer.Clean(text).Split('\n'))
        {
            yield return prefix.Length == 0
                ? Of(line, tone)
                : Of(new Segment(prefix, prefixTone), new Segment(line, tone));
        }
    }

    /// <summary>The width of text in columns of the terminal.</summary>
    public static int WidthOf(string text)
    {
        var width = 0;
        var elements = StringInfo.GetTextElementEnumerator(text);
        while (elements.MoveNext())
        {
            width += Math.Max(1, UnicodeWidth.GetWidth(elements.GetTextElement()));
        }

        return width;
    }

    /// <summary>
    /// The line as rows that are at most that wide. Rows end between words where that is possible. Every
    /// row after the first begins with <see cref="Hang"/>.
    /// </summary>
    public IReadOnlyList<Line> Rows(int width)
    {
        if (width == int.MaxValue || WidthOf(PlainText) <= width)
        {
            return [this];
        }

        var cells = new List<Cell>();
        var textStart = 0;
        for (var i = 0; i < Segments.Count; i++)
        {
            var segment = Segments[i];
            var elements = StringInfo.GetTextElementEnumerator(segment.Text);
            while (elements.MoveNext())
            {
                var element = elements.GetTextElement();
                cells.Add(new Cell(element, Math.Max(1, UnicodeWidth.GetWidth(element)), segment.Tone, segment.Bold));
            }

            if (i < MarkLength)
            {
                textStart = cells.Count;
            }
        }

        var hangWidth = WidthOf(Hang.Text);
        var rows = new List<Line>();
        var start = 0;
        while (start < cells.Count)
        {
            var first = rows.Count == 0;
            var lead = first ? cells.Take(textStart).Sum(c => c.Width) : hangWidth;
            var available = Math.Max(width, lead + NarrowestText) - (first ? 0 : hangWidth);

            // The mark is never a place to end a row.
            var from = first ? textStart : start;
            var end = start;
            var used = 0;
            var lastBlank = -1;
            while (end < cells.Count && used + cells[end].Width <= available)
            {
                if (end > from && IsBlank(cells[end]))
                {
                    lastBlank = end;
                }

                used += cells[end].Width;
                end++;
            }

            var next = end;
            if (end < cells.Count)
            {
                if (IsBlank(cells[end]))
                {
                    next = end + 1;
                }
                else if (lastBlank > from + ((end - from) / 2))
                {
                    // Between words, unless that would leave most of the row empty.
                    end = lastBlank;
                    next = lastBlank + 1;
                }

                while (next < cells.Count && IsBlank(cells[next]))
                {
                    next++;
                }

                while (end > from + 1 && IsBlank(cells[end - 1]))
                {
                    end--;
                }
            }

            rows.Add(Row(cells, start, end, first ? null : Hang));
            start = next;
        }

        return rows;
    }

    public override string ToString() => PlainText;

    private static bool IsBlank(Cell cell) => cell.Text == " ";

    private static Line Row(List<Cell> cells, int start, int end, Segment? hang)
    {
        var segments = new List<Segment>();
        if (hang is { } lead)
        {
            segments.Add(lead);
        }

        var builder = new StringBuilder();
        for (var i = start; i < end; i++)
        {
            builder.Append(cells[i].Text);
            var last = i == end - 1;
            if (last || cells[i + 1].Tone != cells[i].Tone || cells[i + 1].Bold != cells[i].Bold)
            {
                segments.Add(new Segment(builder.ToString(), cells[i].Tone, cells[i].Bold));
                builder.Clear();
            }
        }

        return new Line(segments);
    }

    private static (Segment Hang, int MarkLength) HangOf(IReadOnlyList<Segment> segments, bool marked)
    {
        if (segments.Count == 0)
        {
            return (DefaultHang, 0);
        }

        var first = segments[0];
        var width = WidthOf(first.Text);
        var isMark = segments.Count > 1 && (marked || first.Tone == Tone.Stage || !first.Text.Any(char.IsLetterOrDigit));
        if (!isMark || width > WidestMark || width < 2)
        {
            // Text that is indented stays indented.
            var indentation = first.Text.Length - first.Text.TrimStart(' ').Length;
            return (indentation is > 2 and <= WidestMark ? new Segment(new string(' ', indentation)) : DefaultHang, 0);
        }

        // Text that is quoted stays quoted on every row: the mark that says so is repeated.
        var quotes = first.Text.StartsWith(' ') && (first.Text.Contains('│') || first.Text.Contains('|'));
        return (quotes ? first : new Segment(new string(' ', width)), 1);
    }

    private static List<Segment> Cleaned(IEnumerable<Segment> segments) =>
        segments.Select(s => s with { Text = Clean(s.Text) }).Where(s => s.Text.Length > 0).ToList();

    private static string Clean(string text) => TerminalSanitizer.CleanLine(text);
}
