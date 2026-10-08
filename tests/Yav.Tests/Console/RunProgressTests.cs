using Yav.Console.Rendering;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

/// <summary>The row that shows a run still works: what it says, that it moves, and that it goes when the run ends.</summary>
public class RunProgressTests
{
    private static readonly Line Prompt = Line.Of(new Segment("YAV> ", Tone.Prompt));
    private static readonly DateTimeOffset At = new(2026, 10, 8, 10, 0, 0, TimeSpan.Zero);

    private static (Screen Screen, VirtualTerminal Terminal) Rich(bool unicode = true)
    {
        var terminal = new VirtualTerminal(80, 10);
        return (new Screen(terminal, new ScreenOptions(Rich: true, Color: false, Unicode: unicode)), terminal);
    }

    private static AgentActivity Agent(AgentRole role, AgentEvent agentEvent) => new("run", At, role, agentEvent);

    [Fact]
    public void The_row_names_the_stage_the_time_and_what_the_agent_does_last()
    {
        var (screen, terminal) = Rich();
        var clock = new ManualClock();
        screen.ShowInput(new InputView(Prompt, string.Empty, 0));
        using var progress = RunProgress.Start(screen, clock, Timeout.InfiniteTimeSpan)!;

        progress.Observe(new StageNote("run", At, Stages.CodeA, "Model A works"));
        clock.Advance(TimeSpan.FromSeconds(65));
        progress.Observe(Agent(AgentRole.Implementer, new CommandStarted(At, "c1", "dotnet build", null)));

        Assert.Equal("Model A is working", progress.Stage);
        Assert.EndsWith("Model A is working · 1m 05s · $ dotnet build", terminal.Lines[^2], StringComparison.Ordinal);
        Assert.Equal("YAV>", terminal.Lines[^1]);

        progress.Observe(Agent(AgentRole.Reviewer, new ToolActivity(At, "t1", "read", string.Empty, "started", "src/app.txt")));
        Assert.EndsWith("Model B is reviewing · 1m 05s · read: src/app.txt", terminal.Lines[^2], StringComparison.Ordinal);

        progress.Observe(new StageNote("run", At, Stages.Tests, "Running the checks"));
        Assert.Contains("running checks", terminal.Lines[^2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_mark_moves_each_time_the_row_is_drawn_and_the_row_stays_one_row()
    {
        var (screen, terminal) = Rich(unicode: false);
        using var progress = RunProgress.Start(screen, new ManualClock(), Timeout.InfiniteTimeSpan)!;

        var first = terminal.Lines[^1];
        progress.Refresh();
        var second = terminal.Lines[^1];

        Assert.NotEqual(first[0], second[0]);
        Assert.Equal(first[1..], second[1..]);
        Assert.Single(terminal.Lines);
        Assert.Equal("/ starting - 0s", second);
    }

    [Fact]
    public void The_row_goes_when_the_run_finishes_and_nothing_draws_it_again()
    {
        var (screen, terminal) = Rich();
        screen.ShowInput(new InputView(Prompt, string.Empty, 0));
        var progress = RunProgress.Start(screen, new ManualClock(), Timeout.InfiniteTimeSpan)!;
        Assert.Equal(2, terminal.Lines.Count);

        progress.Observe(new RunFinished("run", At, RunState.ReadyToApply, RunDisposition.Pending, null, null));
        progress.Refresh();
        progress.Observe(new StageNote("run", At, Stages.CodeA, "late"));

        Assert.Equal(["YAV>"], terminal.Lines);
        Assert.DoesNotContain("starting", terminal.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void A_screen_that_cannot_replace_a_row_gets_no_progress()
    {
        var terminal = new VirtualTerminal(80, 10);
        var screen = new Screen(terminal, new ScreenOptions(Rich: false, Color: false, Unicode: true));

        Assert.Null(RunProgress.Start(screen, new ManualClock(), Timeout.InfiniteTimeSpan));
        screen.ShowStatus(Line.Of("working"));

        Assert.Equal(string.Empty, terminal.Raw);
    }

    [Theory]
    [InlineData(0, "0s")]
    [InlineData(59.9, "59s")]
    [InlineData(60, "1m 00s")]
    [InlineData(3_725, "62m 05s")]
    public void Elapsed_time_is_shown_in_seconds_and_then_minutes(double seconds, string shown) =>
        Assert.Equal(shown, RunProgress.Elapsed(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void The_timer_does_not_draw_the_row_while_a_draft_is_typed_or_a_question_is_answered_but_an_event_does()
    {
        var (screen, terminal) = Rich(unicode: false);
        screen.ShowInput(new InputView(Prompt, "fix th", 6));
        using var progress = RunProgress.Start(screen, new ManualClock(), Timeout.InfiniteTimeSpan)!;
        var shown = terminal.Raw.Length;

        progress.Tick();
        Assert.Equal(shown, terminal.Raw.Length);

        screen.ShowInput(new InputView(Prompt, string.Empty, 0));
        using (screen.HoldStatus())
        {
            shown = terminal.Raw.Length;
            progress.Tick();
            Assert.Equal(shown, terminal.Raw.Length);

            progress.Observe(new StageNote("run", At, Stages.CodeA, "Model A works"));
            Assert.Contains("Model A is working", terminal.Lines[^2], StringComparison.Ordinal);
        }

        var before = terminal.Lines[^2];
        progress.Tick();
        Assert.NotEqual(before, terminal.Lines[^2]);
    }
}
