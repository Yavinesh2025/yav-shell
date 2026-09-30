using Yav.Console.Rendering;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

public class ScreenTests
{
    private static readonly Line Prompt = Line.Of(new Segment("YAV C:\\p", Tone.Prompt), new Segment("> ", Tone.Prompt));

    private static (Screen Screen, VirtualTerminal Terminal) Rich(int width = 40, int height = 8)
    {
        var terminal = new VirtualTerminal(width, height);
        return (new Screen(terminal, new ScreenOptions(Rich: true, Color: true, Unicode: true)), terminal);
    }

    private static (Screen Screen, VirtualTerminal Terminal) Plain()
    {
        var terminal = new VirtualTerminal(80, 8);
        return (new Screen(terminal, new ScreenOptions(Rich: false, Color: false, Unicode: false)), terminal);
    }

    private static string WithoutBlanks(string text) => text.Replace(" ", string.Empty, StringComparison.Ordinal);

    [Fact]
    public void Lines_appear_in_the_order_they_were_written()
    {
        var (screen, terminal) = Rich();

        screen.WriteLine(Line.Of("[PREPARE] Project rules and workspace verified"));
        screen.WriteLine(Line.Of("[CODE A]  Implementing the task"));

        Assert.Equal(["[PREPARE] Project rules and workspace", "  verified", "[CODE A]  Implementing the task"], terminal.Lines);
    }

    [Fact]
    public void Text_that_is_quoted_stays_behind_its_mark_on_every_row_it_needs()
    {
        var (screen, terminal) = Rich(width: 40);
        var text = new string('x', 35) + "[APPROVAL] Model A asks to run a command";

        screen.WriteLine(Line.Of(new Segment("  │ ", Tone.Muted), new Segment(text)));

        Assert.True(terminal.Lines.Count > 1);
        Assert.All(terminal.Lines, row => Assert.StartsWith("  │ ", row, StringComparison.Ordinal));
        Assert.Equal(WithoutBlanks(text), WithoutBlanks(string.Concat(terminal.Lines.Select(row => row[4..]))));
    }

    [Fact]
    public void A_status_line_continues_below_its_own_text()
    {
        var (screen, terminal) = Rich(width: 40);

        screen.WriteLine(Line.Of(
            new Segment(RunEventFormatter.StageLabel("QUEUE"), Tone.Stage, Bold: true),
            new Segment("A run is active, so the request waits until it is ready")));

        Assert.Equal(
            [
                "[QUEUE]   A run is active, so the",
                "          request waits until it is",
                "          ready",
            ],
            terminal.Lines);
    }

    [Theory]
    [MemberData(nameof(WideLines))]
    public void Only_the_first_row_of_a_line_begins_in_the_first_column(int width, string mark, string text)
    {
        var (screen, terminal) = Rich(width, height: 30);

        screen.WriteLine(mark.Length == 0 ? Line.Of(text) : Line.Of(new Segment(mark, Tone.Muted), new Segment(text)));

        var rows = terminal.Lines;
        Assert.All(rows.Skip(1), row => Assert.True(row.StartsWith(' ') || row.StartsWith("  │ ", StringComparison.Ordinal), $"'{row}' at width {width}"));
        Assert.All(rows, row => Assert.True(PrettyPrompt.Rendering.UnicodeWidth.GetWidth(row) < width, $"'{row}' is wider than {width - 1}"));
        Assert.Equal(WithoutBlanks(mark + text), WithoutBlanks(rows[0] + string.Concat(rows.Skip(1).Select(row => row.StartsWith("  │ ", StringComparison.Ordinal) ? row[4..] : row))));
        Assert.DoesNotContain(terminal.Lines, row => row.TrimStart().Length == 0);
    }

    public static TheoryData<int, string, string> WideLines()
    {
        var data = new TheoryData<int, string, string>();
        var texts = new[]
        {
            "[APPROVAL] Model A asks to run a command and [READY] changes are available, Approve? [a/s/d/c]",
            new string('x', 150) + "[READY]",
            "word " + new string('y', 90) + " [DONE] tail",
            "日本語のテキストは幅が二倍です。[READY] 日本語のテキストは幅が二倍です。日本語のテキストは幅が二倍です。",
            "a b c d e f g h i j k l m n o p q r s t u v w x y z a b c d e f g h i j k l m n o p q r s t u v w x y z",
        };
        foreach (var width in new[] { 20, 21, 33, 40, 41, 79, 80, 81, 120 })
        {
            foreach (var mark in new[] { string.Empty, "  │ ", "  $ ", "  ! ", "      " })
            {
                foreach (var text in texts)
                {
                    data.Add(width, mark, text);
                }
            }
        }

        return data;
    }

    [Fact]
    public void A_line_that_is_indented_continues_with_the_same_indentation()
    {
        var (screen, terminal) = Rich(width: 40);

        screen.WriteLine(Line.Of("           /diff shows the changes, /apply writes them into the project", Tone.Muted));

        Assert.Equal(
            [
                "           /diff shows the changes,",
                "           /apply writes them into the",
                "           project",
            ],
            terminal.Lines);
    }

    [Fact]
    public void A_line_that_fits_is_written_as_it_is()
    {
        var (screen, terminal) = Rich(width: 40);

        screen.WriteLine(Line.Of(new Segment("  │ ", Tone.Muted), new Segment("    indented   code  with   blanks")));

        Assert.Equal(["  │     indented   code  with   blanks"], terminal.Lines);
    }

    [Fact]
    public void A_stream_that_is_no_window_gets_every_line_unbroken()
    {
        using var writer = new StringWriter();
        var screen = new Screen(new Yav.Console.Output.StreamTerminal(writer), new ScreenOptions(Rich: false, Color: false, Unicode: true));
        var text = string.Join(' ', Enumerable.Repeat("word", 3000));

        screen.WriteLine(Line.Of(new Segment("  │ ", Tone.Muted), new Segment(text)));

        Assert.Equal("  │ " + text + "\r\n", writer.ToString());
    }

    [Fact]
    public void Output_that_arrives_while_the_user_types_appears_above_the_input_and_leaves_it_intact()
    {
        var (screen, terminal) = Rich();
        screen.WriteLine(Line.Of("[CODE A]  Implementing the task"));
        screen.ShowInput(new InputView(Prompt, "also log it", 11));

        screen.WriteLine(Line.Of("[CHECK]   Reviewing and testing"));
        screen.WriteLine(Line.Of("[TESTS]   Required checks passed"));

        Assert.Equal(
            [
                "[CODE A]  Implementing the task",
                "[CHECK]   Reviewing and testing",
                "[TESTS]   Required checks passed",
                "YAV C:\\p> also log it",
            ],
            terminal.Lines);
        Assert.Equal("YAV C:\\p> also log it", terminal.CursorLine);
        Assert.Equal("YAV C:\\p> also log it".Length, terminal.CursorColumn);
    }

    [Fact]
    public void The_cursor_returns_to_where_the_user_was_editing()
    {
        var (screen, terminal) = Rich();
        screen.ShowInput(new InputView(Prompt, "also log it", 4));

        screen.WriteLine(Line.Of("[CHECK]   Reviewing and testing"));

        Assert.Equal("YAV C:\\p> also log it", terminal.CursorLine);
        Assert.Equal("YAV C:\\p> also".Length, terminal.CursorColumn);
    }

    [Fact]
    public void Input_that_is_longer_than_the_window_is_wide_is_kept_together_when_output_arrives()
    {
        var (screen, terminal) = Rich(width: 20);
        const string Typed = "rename the login service everywhere";
        screen.ShowInput(new InputView(Prompt, Typed, Typed.Length));

        screen.WriteLine(Line.Of("[CODE A]  working"));
        screen.WriteLine(Line.Of("[CHECK]   checking"));

        Assert.Equal("[CODE A]  working", terminal.Lines[0]);
        Assert.Equal("[CHECK]   checking", terminal.Lines[1]);
        Assert.Equal("YAV C:\\p> " + Typed, string.Concat(terminal.Lines.Skip(2)));
        Assert.Equal(5, terminal.Lines.Count);
    }

    [Fact]
    public void Editing_redraws_the_input_in_place()
    {
        var (screen, terminal) = Rich();
        screen.WriteLine(Line.Of("first"));
        screen.ShowInput(new InputView(Prompt, "abc", 3));
        screen.ShowInput(new InputView(Prompt, "abcd", 4));
        screen.ShowInput(new InputView(Prompt, "ab", 2));

        Assert.Equal(["first", "YAV C:\\p> ab"], terminal.Lines);
        Assert.Equal("YAV C:\\p> ab".Length, terminal.CursorColumn);
    }

    [Fact]
    public void Input_that_shrinks_by_a_row_leaves_nothing_behind()
    {
        var (screen, terminal) = Rich(width: 20);
        screen.ShowInput(new InputView(Prompt, "this text needs two rows", 24));

        screen.ShowInput(new InputView(Prompt, "short", 5));

        Assert.Equal(["YAV C:\\p> short"], terminal.Lines);
    }

    [Fact]
    public void Removing_the_input_leaves_a_clean_line_for_what_follows()
    {
        var (screen, terminal) = Rich();
        screen.ShowInput(new InputView(Prompt, "typed", 5));

        screen.HideInput();
        screen.WriteLine(Line.Of("next"));

        Assert.Equal(["next"], terminal.Lines);
    }

    [Fact]
    public void Characters_that_are_two_columns_wide_are_counted_as_such()
    {
        var (screen, terminal) = Rich();
        screen.ShowInput(new InputView(Prompt, "日本語 ok", 3));

        screen.WriteLine(Line.Of("note"));

        Assert.Equal("YAV C:\\p> ".Length + 6, terminal.CursorColumn);
    }

    [Fact]
    public void A_line_break_in_the_input_is_shown_as_a_mark_on_the_same_line()
    {
        var (screen, terminal) = Rich();

        screen.ShowInput(new InputView(Prompt, "first\nsecond", 12));

        Assert.Equal(["YAV C:\\p> first⏎second"], terminal.Lines);
    }

    [Fact]
    public void While_another_editor_owns_the_cursor_output_waits_and_is_written_afterwards_in_order()
    {
        var (screen, terminal) = Rich();
        screen.WriteLine(Line.Of("before"));

        using (screen.Suspend())
        {
            screen.WriteLine(Line.Of("first while editing"));
            screen.WriteLine(Line.Of("second while editing"));
            Assert.Equal(["before"], terminal.Lines);
        }

        Assert.Equal(["before", "first while editing", "second while editing"], terminal.Lines);
    }

    [Fact]
    public void Text_from_an_agent_cannot_move_the_cursor_clear_the_screen_or_set_the_title()
    {
        var (screen, terminal) = Rich();
        screen.WriteLine(Line.Of("kept"));

        screen.WriteLine(Line.Of("a\u001b[2J\u001b[1;1Hb\u001b]0;title\u0007c\u001b]52;c;aGk=\u0007d\rOVERWRITE"));

        Assert.Equal("kept", terminal.Lines[0]);
        Assert.DoesNotContain("\u001b[2J", terminal.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("\u001b]", terminal.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain('\u0007', terminal.Raw);
        Assert.StartsWith("abcd", terminal.Lines[1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_terminal_that_cannot_position_the_cursor_gets_plain_lines_and_nothing_else()
    {
        var (screen, terminal) = Plain();

        screen.WriteLine(Line.Of(new Segment("[READY]   ", Tone.Stage, Bold: true), new Segment("Changes available", Tone.Success)));
        screen.ShowInput(new InputView(Prompt, "typed", 5));
        screen.WriteLine(Line.Of("second"));
        screen.HideInput();

        Assert.DoesNotContain('\u001b', terminal.Raw);
        Assert.Equal(["[READY]   Changes available", "second"], terminal.Lines);
    }

    [Fact]
    public void Without_color_the_text_is_the_same_and_no_color_is_written()
    {
        var terminal = new VirtualTerminal();
        var screen = new Screen(terminal, new ScreenOptions(Rich: true, Color: false, Unicode: true));

        screen.WriteLine(Line.Of(new Segment("[FAILED]  ", Tone.Stage), new Segment("it broke", Tone.Error)));

        Assert.Equal(["[FAILED]  it broke"], terminal.Lines);
        Assert.DoesNotContain("m", terminal.Raw.Replace("[FAILED]  it broke", string.Empty, StringComparison.Ordinal), StringComparison.Ordinal);
    }

    [Fact]
    public void Colors_come_from_the_terminals_own_palette()
    {
        var (screen, terminal) = Rich();

        screen.WriteLine(Line.Of(new Segment("warning", Tone.Warning), new Segment(" error", Tone.Error), new Segment(" fine", Tone.Success)));

        // The sixteen named colors, which every theme defines. No fixed RGB value is ever written.
        Assert.Contains("\u001b[33m", terminal.Raw, StringComparison.Ordinal);
        Assert.Contains("\u001b[31m", terminal.Raw, StringComparison.Ordinal);
        Assert.Contains("\u001b[32m", terminal.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("38;2;", terminal.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("38;5;", terminal.Raw, StringComparison.Ordinal);
        Assert.EndsWith("\u001b[0m\r\n", terminal.Raw, StringComparison.Ordinal);
    }

    [Fact]
    public void A_block_that_was_formatted_elsewhere_is_written_as_a_whole_above_the_input()
    {
        var (screen, terminal) = Rich();
        screen.ShowInput(new InputView(Prompt, "typed", 5));

        screen.WriteBlock("Model   Effort\n------  ------\nopus    max\n");

        Assert.Equal(["Model   Effort", "------  ------", "opus    max", "YAV C:\\p> typed"], terminal.Lines);
    }

    [Fact]
    public async Task Writers_on_several_threads_never_interleave_within_a_line()
    {
        var (screen, terminal) = Rich(width: 60, height: 50);
        screen.ShowInput(new InputView(Prompt, "typing", 6));

        await Task.WhenAll(Enumerable.Range(0, 8).Select(writer => Task.Run(() =>
        {
            for (var i = 0; i < 25; i++)
            {
                screen.WriteLine(Line.Of($"writer {writer} line {i:00} " + new string((char)('a' + writer), 20)));
            }
        })));

        var lines = terminal.Lines;
        Assert.Equal(201, lines.Count);
        Assert.Equal("YAV C:\\p> typing", lines[^1]);
        Assert.All(lines.Take(200), line => Assert.Matches(@"^writer (\d) line \d\d ([a-h])\2{19}$", line));
    }
}
