using Spectre.Console;
using Spectre.Console.Rendering;
using Yav.Core.Runs;
using Yav.Core.Text;

namespace Yav.Console.Rendering;

/// <summary>
/// What commands use to say something. Everything ends up in the screen, which owns the terminal. Tables
/// are formatted with Spectre.Console into text first and then written as one piece.
/// </summary>
public sealed class Ui
{
    private readonly Screen _screen;

    public Ui(Screen screen)
    {
        _screen = screen;
    }

    public Screen Screen => _screen;

    public bool Unicode => _screen.Options.Unicode;

    public void Blank() => _screen.WriteLine(Rendering.Line.Empty);

    public void Say(string text, Tone tone = Tone.Normal) => _screen.WriteLine(Rendering.Line.Of(text, tone));

    public void Lines(IEnumerable<Rendering.Line> lines) => _screen.WriteLines(lines);

    public void Heading(string text) => _screen.WriteLine(Rendering.Line.Of(text, Tone.Accent, bold: true));

    public void Muted(string text) => Say(text, Tone.Muted);

    public void Success(string text) => Say(text, Tone.Success);

    public void Warn(string text) => Say(text, Tone.Warning);

    public void Error(string text) => Say(text, Tone.Error);

    /// <summary>A line in the form the run uses for its stages.</summary>
    public void Note(string stage, string message, NoteLevel level = NoteLevel.Info) => _screen.WriteLine(Rendering.Line.Of(
        new Segment(RunEventFormatter.StageLabel(stage), Tone.Stage, Bold: true),
        new Segment(message, level switch
        {
            NoteLevel.Success => Tone.Success,
            NoteLevel.Warning => Tone.Warning,
            NoteLevel.Error => Tone.Error,
            _ => Tone.Normal,
        })));

    /// <summary>A name and its value, the names aligned.</summary>
    public void Pairs(IEnumerable<(string Name, string Value, Tone Tone)> pairs)
    {
        var list = pairs.ToList();
        if (list.Count == 0)
        {
            return;
        }

        var width = list.Max(p => p.Name.Length) + 2;
        _screen.WriteLines(list.Select(p => Rendering.Line.Labelled(
            new Segment(("  " + p.Name + ":").PadRight(width + 3), Tone.Muted),
            new Segment(p.Value, p.Tone))));
    }

    public void Pairs(params (string Name, string Value)[] pairs) => Pairs(pairs.Select(p => (p.Name, p.Value, Tone.Normal)));

    /// <summary>Text that did not come from YAV, kept behind the gutter.</summary>
    public void Quote(string text, Tone tone = Tone.Normal)
    {
        var gutter = Unicode ? "  │ " : "  | ";
        _screen.WriteLines(Rendering.Line.Split(text.TrimEnd('\r', '\n'), tone, gutter));
    }

    public void Table(IReadOnlyList<string> headers, IEnumerable<IReadOnlyList<string>> rows)
    {
        var table = new Table { Border = TableBorder.Simple, ShowRowSeparators = false };
        table.BorderStyle = new Style(Color.Grey);
        foreach (var header in headers)
        {
            table.AddColumn(new TableColumn(new Text(Clean(header), new Style(decoration: Decoration.Bold))));
        }

        var count = 0;
        foreach (var row in rows)
        {
            // Cells are text, never markup, so nothing in them is interpreted.
            table.AddRow(row.Select(cell => (IRenderable)new Text(Clean(cell))).ToArray());
            count++;
        }

        if (count == 0)
        {
            return;
        }

        _screen.WriteBlock(Render(table));
    }

    /// <summary>Formats something of Spectre.Console into text for this terminal.</summary>
    public string Render(IRenderable renderable)
    {
        using var writer = new StringWriter();
        var console = AnsiConsole.Create(new AnsiConsoleSettings
        {
            Ansi = _screen.Options.Color ? AnsiSupport.Yes : AnsiSupport.No,

            // The sixteen named colors only, so the terminal's own theme decides how they look.
            ColorSystem = _screen.Options.Color ? ColorSystemSupport.Standard : ColorSystemSupport.NoColors,
            Interactive = InteractionSupport.No,

            // Without this, Spectre.Console changes the profile for the build service it believes it runs on, and
            // where GITHUB_ACTIONS is set it switches ANSI on, over the choice above: a pipe would get control
            // sequences. What is written here is decided by the options of the screen alone.
            Enrichment = new ProfileEnrichment { UseDefaultEnrichers = false },
            Out = new AnsiConsoleOutput(writer),
        });
        console.Profile.Width = Math.Clamp(_screen.Width == int.MaxValue ? 200 : _screen.Width - 1, 40, 200);
        console.Profile.Capabilities.Unicode = _screen.Options.Unicode;
        console.Write(renderable);
        return writer.ToString();
    }

    private static string Clean(string text) => TerminalSanitizer.CleanLine(text);
}
