using Yav.Console.Commands;
using Yav.Console.Input;
using Yav.Console.Rendering;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

public class LiveInputTests
{
    private static readonly Line Prompt = Line.Of(new Segment("YAV> ", Tone.Prompt));

    private sealed class Editor
    {
        public Editor(int width = 60)
        {
            Terminal = new VirtualTerminal(width, 12);
            Screen = new Screen(Terminal, new ScreenOptions(Rich: true, Color: true, Unicode: true));
            Input = new LiveInputLine(Screen, Keys, History, CommandCatalog.Complete);
        }

        public VirtualTerminal Terminal { get; }

        public Screen Screen { get; }

        public ScriptedKeys Keys { get; } = new();

        public InputHistory History { get; } = new();

        public LiveInputLine Input { get; }

        public async Task<InputResult> ReadAsync(CancellationToken cancellationToken = default)
        {
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            limit.CancelAfter(TimeSpan.FromSeconds(20));
            return await Input.ReadAsync(Prompt, limit.Token);
        }
    }

    [Fact]
    public async Task What_was_typed_is_returned_when_enter_is_pressed()
    {
        var editor = new Editor();
        editor.Keys.Type("Fix the login bug").Enter();

        var result = await editor.ReadAsync();

        Assert.Equal(InputOutcome.Submitted, result.Outcome);
        Assert.Equal("Fix the login bug", result.Text);
    }

    [Fact]
    public async Task The_submitted_line_stays_in_the_scrollback()
    {
        var editor = new Editor();
        editor.Keys.Type("/status").Enter();

        await editor.ReadAsync();

        Assert.Equal(["YAV> /status"], editor.Terminal.Lines);
        Assert.Equal(1, editor.Terminal.CursorRow);
        Assert.Equal(0, editor.Terminal.CursorColumn);
    }

    [Fact]
    public async Task The_line_can_be_edited_anywhere()
    {
        var editor = new Editor();
        editor.Keys
            .Type("helo wrld")
            .Press(ConsoleKey.Home).Press(ConsoleKey.RightArrow).Press(ConsoleKey.RightArrow).Press(ConsoleKey.RightArrow).Type("l")
            .Press(ConsoleKey.End).Press(ConsoleKey.LeftArrow).Press(ConsoleKey.LeftArrow).Press(ConsoleKey.LeftArrow).Type("o")
            .Press(ConsoleKey.End).Type("!!").Press(ConsoleKey.Backspace)
            .Press(ConsoleKey.Home).Type("x").Press(ConsoleKey.LeftArrow).Press(ConsoleKey.Delete)
            .Enter();

        var result = await editor.ReadAsync();

        Assert.Equal("hello world!", result.Text);
    }

    [Fact]
    public async Task The_caret_moves_by_words_with_control()
    {
        var editor = new Editor();
        editor.Keys
            .Type("rename login service")
            .Press(ConsoleKey.LeftArrow, control: true).Press(ConsoleKey.LeftArrow, control: true).Type("the ")
            .Press(ConsoleKey.RightArrow, control: true).Press(ConsoleKey.RightArrow, control: true).Type(" now")
            .Enter();

        var result = await editor.ReadAsync();

        Assert.Equal("rename the login service now", result.Text);
    }

    [Fact]
    public async Task Pasted_lines_are_never_submitted_by_the_line_breaks_they_contain()
    {
        var editor = new Editor();
        editor.Keys.Paste("first line\nsecond line\n/apply\n");
        editor.Keys.Type(" more").Enter();

        var result = await editor.ReadAsync();

        Assert.Equal(InputOutcome.Submitted, result.Outcome);
        Assert.Equal("first line\nsecond line\n/apply\n more", result.Text);
    }

    [Fact]
    public async Task A_line_break_is_typed_with_shift_enter_or_control_j()
    {
        var editor = new Editor();
        editor.Keys
            .Type("one").Press(ConsoleKey.Enter, shift: true)
            .Type("two").Press(ConsoleKey.J, control: true, character: '\n')
            .Type("three").Enter();

        var result = await editor.ReadAsync();

        Assert.Equal("one\ntwo\nthree", result.Text);
    }

    [Fact]
    public async Task A_line_feed_that_arrives_as_control_enter_is_a_line_break_and_does_not_send()
    {
        // A terminal that sends Control+J as a plain line feed is seen by the console as Control+Enter.
        var editor = new Editor();
        editor.Keys
            .Type("one").Press(ConsoleKey.Enter, control: true, character: '\n')
            .Type("two").Enter();

        var result = await editor.ReadAsync();

        Assert.Equal("one\ntwo", result.Text);
    }

    [Fact]
    public async Task Earlier_input_is_recalled_with_the_arrow_keys_and_the_draft_is_kept()
    {
        var editor = new Editor();
        editor.History.Add("first request");
        editor.History.Add("/status");
        editor.Keys
            .Type("draft")
            .Press(ConsoleKey.UpArrow).Press(ConsoleKey.UpArrow).Press(ConsoleKey.UpArrow)
            .Press(ConsoleKey.DownArrow).Press(ConsoleKey.DownArrow)
            .Type(" kept").Enter();

        var result = await editor.ReadAsync();

        Assert.Equal("draft kept", result.Text);
    }

    [Fact]
    public async Task A_recalled_line_can_be_sent_again()
    {
        var editor = new Editor();
        editor.History.Add("first request");
        editor.History.Add("/status");
        editor.Keys.Press(ConsoleKey.UpArrow).Press(ConsoleKey.UpArrow).Enter();

        var result = await editor.ReadAsync();

        Assert.Equal("first request", result.Text);
    }

    [Fact]
    public void History_keeps_no_duplicates_in_a_row_and_no_empty_lines()
    {
        var history = new InputHistory(capacity: 3);

        foreach (var entry in new[] { "a", "a", " ", "b", "c", "d" })
        {
            history.Add(entry);
        }

        Assert.Equal(["b", "c", "d"], history.Entries);
    }

    [Fact]
    public async Task Escape_empties_the_line()
    {
        var editor = new Editor();
        editor.Keys.Type("never mind").Press(ConsoleKey.Escape).Type("ok").Enter();

        Assert.Equal("ok", (await editor.ReadAsync()).Text);
    }

    [Fact]
    public async Task Control_c_empties_a_line_that_has_text_and_interrupts_on_an_empty_one()
    {
        var editor = new Editor();
        editor.Keys.Type("never mind").Press(ConsoleKey.C, control: true, character: '\u0003').Press(ConsoleKey.C, control: true, character: '\u0003');

        var result = await editor.ReadAsync();

        Assert.Equal(InputOutcome.Interrupted, result.Outcome);
        Assert.Equal(string.Empty, result.Text);
    }

    [Fact]
    public async Task The_end_of_the_input_is_reported_as_such()
    {
        var editor = new Editor();
        editor.Keys.Type("unfinished");
        editor.Keys.End();

        var result = await editor.ReadAsync();

        Assert.Equal(InputOutcome.EndOfInput, result.Outcome);
    }

    [Fact]
    public async Task A_read_that_is_cancelled_leaves_a_clean_screen_and_keeps_the_draft_for_the_next_read()
    {
        var editor = new Editor();
        editor.Keys.Type("half a sent");
        using var cancel = new CancellationTokenSource();
        var reading = editor.ReadAsync(cancel.Token);
        await WaitUntil(() => editor.Terminal.CursorLine == "YAV> half a sent");

        await cancel.CancelAsync();
        var cancelled = await reading;
        editor.Keys.Type("ence").Enter();
        var result = await editor.ReadAsync();

        Assert.Equal(InputOutcome.Cancelled, cancelled.Outcome);
        Assert.Equal("half a sentence", result.Text);
    }

    [Fact]
    public async Task Tab_completes_a_command()
    {
        var editor = new Editor();
        editor.Keys.Type("/sta").Press(ConsoleKey.Tab).Enter();

        Assert.Equal("/status", (await editor.ReadAsync()).Text);
    }

    [Fact]
    public async Task Tab_completes_as_far_as_the_candidates_agree_and_shows_them()
    {
        var editor = new Editor();
        editor.Keys.Type("/s").Press(ConsoleKey.Tab).Type("to").Press(ConsoleKey.Tab).Enter();

        var result = await editor.ReadAsync();

        Assert.Equal("/stop", result.Text);
        Assert.Contains(editor.Terminal.Lines, line => line.Contains("/settings", StringComparison.Ordinal) && line.Contains("/status", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Tab_in_a_request_is_not_a_completion()
    {
        var editor = new Editor();
        editor.Keys.Type("fix").Press(ConsoleKey.Tab).Type("it").Enter();

        Assert.Equal("fix    it", (await editor.ReadAsync()).Text);
    }

    [Fact]
    public async Task Output_of_a_run_appears_above_what_is_being_typed()
    {
        var editor = new Editor();
        editor.Keys.Type("also log it");
        var reading = editor.ReadAsync();
        await WaitUntil(() => editor.Terminal.CursorLine == "YAV> also log it");

        editor.Screen.WriteLine(Line.Of("[CODE A]  Implementing the task"));
        editor.Keys.Type(" please").Enter();
        var result = await reading;

        Assert.Equal("also log it please", result.Text);
        Assert.Equal(["[CODE A]  Implementing the task", "YAV> also log it please"], editor.Terminal.Lines);
    }

    [Fact]
    public async Task Control_characters_that_are_typed_or_pasted_do_not_reach_the_text()
    {
        var editor = new Editor();
        editor.Keys.Paste("a\u001b[2Jb\u0007c").Enter();

        var result = await editor.ReadAsync();

        Assert.Equal("a[2Jbc", result.Text);
    }

    [Fact]
    public async Task While_something_else_has_the_keyboard_keys_go_there_and_the_draft_waits()
    {
        var editor = new Editor();
        var received = new List<char>();
        var answered = new TaskCompletionSource<char>(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.Keys.Type("draft");
        var reading = editor.ReadAsync();
        await WaitUntil(() => editor.Terminal.CursorLine == "YAV> draft");

        using (editor.Input.TakeKeyboard(
            Line.Of(new Segment("Allow? [a/d] ", Tone.Warning)),
            key =>
            {
                received.Add(key.Key.KeyChar);
                if (key.Key.KeyChar is 'a' or 'd')
                {
                    answered.TrySetResult(key.Key.KeyChar);
                }
            }))
        {
            await WaitUntil(() => editor.Terminal.CursorLine == "Allow? [a/d]");
            editor.Keys.Type("xa");
            Assert.Equal('a', await answered.Task.WaitAsync(TimeSpan.FromSeconds(10)));
        }

        await WaitUntil(() => editor.Terminal.CursorLine == "YAV> draft");
        editor.Keys.Type("!").Enter();
        var result = await reading;

        Assert.Equal(['x', 'a'], received);
        Assert.Equal("draft!", result.Text);
    }

    [Fact]
    public async Task While_another_program_has_the_console_no_key_is_read()
    {
        var editor = new Editor();
        var paused = editor.Keys.Pause();
        editor.Keys.Type("typed meanwhile").Enter();

        var reading = editor.ReadAsync();
        await System.Threading.Tasks.Task.Delay(200);
        Assert.False(reading.IsCompleted);
        Assert.False(editor.Keys.KeyAvailable);

        paused.Dispose();
        Assert.Equal("typed meanwhile", (await reading).Text);
    }

    [Fact]
    public async Task Keys_that_arrive_together_are_pasted_and_keys_typed_one_after_the_other_are_not()
    {
        var editor = new Editor();

        // Decided by the detector the console uses: a pasted line break does not send the line, a typed one does.
        editor.Keys.Paste("one\ntwo").Type(" three").Enter();

        Assert.Equal("one\ntwo three", (await editor.ReadAsync()).Text);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail("The expected screen did not appear in time.");
            }

            await Task.Delay(10);
        }
    }
}

public class PasteDetectorTests
{
    private const long Millisecond = TimeSpan.TicksPerMillisecond;

    [Fact]
    public void Keys_that_are_typed_are_not_pasted()
    {
        var detector = new PasteDetector();

        Assert.False(detector.IsPasted(0 * Millisecond, moreAvailable: false));
        Assert.False(detector.IsPasted(120 * Millisecond, moreAvailable: false));
        Assert.False(detector.IsPasted(200 * Millisecond, moreAvailable: false));
    }

    [Fact]
    public void Keys_that_arrive_together_are_pasted_including_the_last_of_them()
    {
        var detector = new PasteDetector();

        Assert.True(detector.IsPasted(1000 * Millisecond, moreAvailable: true));
        Assert.True(detector.IsPasted(1000 * Millisecond, moreAvailable: true));
        Assert.True(detector.IsPasted(1001 * Millisecond, moreAvailable: false));
    }

    [Fact]
    public void A_key_that_is_pressed_after_a_paste_is_typed()
    {
        var detector = new PasteDetector();
        detector.IsPasted(1000 * Millisecond, moreAvailable: true);
        detector.IsPasted(1001 * Millisecond, moreAvailable: false);

        Assert.False(detector.IsPasted(1400 * Millisecond, moreAvailable: false));
    }

    [Fact]
    public void A_key_that_is_held_down_repeats_too_slowly_to_be_a_paste()
    {
        var detector = new PasteDetector();

        for (var i = 0; i < 10; i++)
        {
            Assert.False(detector.IsPasted(i * 30 * Millisecond, moreAvailable: false));
        }
    }

    [Fact]
    public void A_paste_that_arrives_in_pieces_is_still_one_paste()
    {
        var detector = new PasteDetector();

        Assert.True(detector.IsPasted(0, moreAvailable: true));
        Assert.True(detector.IsPasted(1 * Millisecond, moreAvailable: false));
        Assert.True(detector.IsPasted(6 * Millisecond, moreAvailable: true));
        Assert.True(detector.IsPasted(7 * Millisecond, moreAvailable: false));
    }
}
