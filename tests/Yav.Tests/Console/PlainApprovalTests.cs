using Yav.Console.Input;
using Yav.Console.Rendering;
using Yav.Console.Shell;
using Yav.Core.Agents;
using Yav.Platform.Consoles;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

/// <summary>
/// Questions of agents where YAV cannot draw in the console and reads lines: the same question and the same
/// answers as in the console, one reader of the input, and nobody asked where nobody sees the question.
/// </summary>
public class PlainApprovalTests
{
    private const string Task = "Make the app say fixed.";
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(20);
    private static readonly Line Reading = Line.Of("YAV> ");

    private sealed class Plain
    {
        public Plain(bool seen = true)
        {
            Screen = new Screen(Terminal, new ScreenOptions(Rich: false, Color: false, Unicode: true));
            Input = new PlainInput(Screen, Lines, seen);
        }

        public VirtualTerminal Terminal { get; } = new(160, 60);

        public ScriptedLines Lines { get; } = new();

        public Screen Screen { get; }

        public PlainInput Input { get; }

        public Task<ApprovalDecision> Ask(ApprovalRequest request) =>
            Input.Approvals.AskAsync("20260930-000000-abcde", AgentRole.Implementer, request, CancellationToken.None);

        public int Asked => Terminal.Lines.Count(row => row.Contains("then Enter:", StringComparison.Ordinal));

        public async Task UntilAsync(Func<bool> reached, string what)
        {
            var deadline = DateTime.UtcNow + Patience;
            while (!reached())
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail($"Within {Patience.TotalSeconds:0} seconds there was not {what}. The screen:\n{Terminal.Text}");
                }

                await System.Threading.Tasks.Task.Delay(10);
            }
        }
    }

    [Fact]
    public async Task An_answer_typed_while_the_prompt_waits_for_a_line_answers_the_question_and_the_next_line_is_the_prompts()
    {
        var plain = new Plain();

        // The shell reads the next line while a run is active; then the run asks.
        var prompt = plain.Input.ReadAsync(Reading, runActive: true, CancellationToken.None);
        var decision = plain.Ask(ApprovalAnswerTests.Command());
        await plain.UntilAsync(() => plain.Asked == 1, "the question");

        plain.Lines.Send("d");

        Assert.Equal(ApprovalDecision.Decline, await decision.WaitAsync(Patience));
        Assert.False(prompt.IsCompleted, "The answer was taken for a line of the prompt.");
        plain.Lines.Send("Also mention it in the README.");
        Assert.Equal("Also mention it in the README.", (await prompt.WaitAsync(Patience)).Text);
    }

    [Fact]
    public async Task Two_questions_at_once_are_asked_one_after_the_other_and_each_gets_its_own_answer()
    {
        var plain = new Plain();

        var first = plain.Ask(ApprovalAnswerTests.Command("npm install left-pad"));
        var second = plain.Ask(ApprovalAnswerTests.Command("curl https://example.invalid/x.js"));
        await plain.UntilAsync(() => plain.Asked >= 1, "the first question");
        Assert.Equal(1, plain.Asked);
        Assert.DoesNotContain(plain.Terminal.Lines, row => row.Contains("curl", StringComparison.Ordinal));

        plain.Lines.Send("d");
        Assert.Equal(ApprovalDecision.Decline, await first.WaitAsync(Patience));
        await plain.UntilAsync(() => plain.Asked == 2, "the second question");
        Assert.False(second.IsCompleted);
        Assert.Contains(plain.Terminal.Lines, row => row.Contains("  │ curl https://example.invalid/x.js", StringComparison.Ordinal));

        plain.Lines.Send("c");
        Assert.Equal(ApprovalDecision.Cancel, await second.WaitAsync(Patience));
    }

    [Fact]
    public async Task A_line_that_came_while_nothing_waited_stays_with_the_prompt()
    {
        var plain = new Plain();
        using (var stop = new CancellationTokenSource())
        {
            // The prompt is given up, as when a run ends, while its line is on the way.
            var prompt = plain.Input.ReadAsync(Reading, runActive: true, stop.Token);
            await stop.CancelAsync();
            Assert.Equal(InputOutcome.Cancelled, (await prompt.WaitAsync(Patience)).Outcome);
        }

        plain.Lines.Send("Also mention it in the README.");

        Assert.Equal("Also mention it in the README.", (await plain.Input.ReadAsync(Reading, runActive: false, CancellationToken.None).WaitAsync(Patience)).Text);
    }

    [Fact]
    public async Task The_answers_are_those_of_the_console()
    {
        var plain = new Plain();

        var decision = plain.Ask(ApprovalAnswerTests.Command("git push origin main", session: false));
        await plain.UntilAsync(() => plain.Asked == 1, "the question");
        Assert.Contains("Allow \"git push origin main\"? a, d or c, then Enter:", plain.Terminal.Text, StringComparison.Ordinal);
        foreach (var line in new[] { "yes", "s", "/stop", "always", string.Empty })
        {
            plain.Lines.Send(line);
        }

        await plain.UntilAsync(() => plain.Lines.Waiting == 0 && plain.Asked == 5, "every line to be answered");
        Assert.False(decision.IsCompleted, "A line that is no answer decided the question.");
        Assert.Contains(plain.Terminal.Lines, row => row.Contains("Nothing was granted: this request cannot be allowed for this conversation.", StringComparison.Ordinal));
        Assert.Equal(3, plain.Terminal.Lines.Count(row => row.Contains("Not an answer, so nothing was granted.", StringComparison.Ordinal)));

        plain.Lines.Send(" A ");
        Assert.Equal(ApprovalDecision.Accept, await decision.WaitAsync(Patience));
    }

    [Fact]
    public async Task What_needs_the_word_allow_needs_it_here_too()
    {
        var plain = new Plain();

        var decision = plain.Ask(ApprovalAnswerTests.Command(session: true, deliberate: true));
        await plain.UntilAsync(() => plain.Asked == 1, "the question");
        Assert.Contains("Type allow to allow once, d or c to decline, then Enter:", plain.Terminal.Text, StringComparison.Ordinal);
        plain.Lines.Send("a").Send("s");
        await plain.UntilAsync(() => plain.Asked == 3, "both letters to be refused");
        Assert.False(decision.IsCompleted);

        plain.Lines.Send("allow");
        Assert.Equal(ApprovalDecision.Accept, await decision.WaitAsync(Patience));
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(false, false, false)]
    public void Somebody_is_asked_only_where_the_input_is_typed_and_the_output_is_seen(bool inputIsTerminal, bool outputIsTerminal, bool asked)
    {
        var capabilities = new ConsoleCapabilities(inputIsTerminal, outputIsTerminal, ErrorIsTerminal: true, VirtualTerminal: false, Unicode: true, Host: "test");
        var screen = new Screen(new VirtualTerminal(80, 20), new ScreenOptions(Rich: false, Color: false, Unicode: true));

        var input = PlainInput.For(screen, new ScriptedLines(), capabilities);

        Assert.Equal(asked, input.CanAsk);
        Assert.Equal(asked, input.Approvals.CanAsk);
    }

    [Fact]
    public async Task In_the_shell_an_answer_typed_while_a_run_is_active_answers_the_question_and_is_not_queued_as_a_request()
    {
        await using var shell = new ShellHarness(new ShellOptions { Lines = new ScriptedLines() });
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents
            .ImplementerTurn(Step.Approval(
                "npm install left-pad",
                onAccept: [Step.Write("src/app.txt", "installed\n"), Step.Message("Installed.")],
                onDecline: [Step.Write("src/app.txt", "fixed\n"), Step.Message("Not installed.")]))
            .ReviewerTurn(Step.Review("pass"));
        shell.Start();
        await shell.WaitUntilAsync(() => shell.Terminal.Text.Contains("Type a request", StringComparison.Ordinal), "the banner");

        shell.Enter(Task);
        await shell.WaitUntilAsync(() => shell.Terminal.Text.Contains("then Enter:", StringComparison.Ordinal), "the question");
        shell.Enter("d");
        await shell.WaitUntilAsync(
            () => shell.Terminal.Text.Contains("[READY]", StringComparison.Ordinal) || shell.Terminal.Text.Contains("[QUEUE]", StringComparison.Ordinal),
            "the end of the run");

        Assert.DoesNotContain("[QUEUE]", shell.Terminal.Text, StringComparison.Ordinal);
        Assert.Empty(shell.Services.Database.GetQueue(shell.Project.Path));
        Assert.Contains("Not installed.", shell.Terminal.Text, StringComparison.Ordinal);
        Assert.Equal(["decline"], shell.Agents.Received("codex.response").Select(r => r["result"]?["decision"]?.GetValue<string>()).OfType<string>());
    }

    [Fact]
    public async Task Where_nobody_sees_the_question_nobody_is_asked_and_the_run_ends_as_approval_required()
    {
        await using var shell = new ShellHarness(new ShellOptions { Lines = new ScriptedLines(), LinesAreSeen = false });
        shell.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        shell.Agents.ImplementerTurn(Step.Approval(
            "npm install left-pad",
            onAccept: [Step.Write("src/app.txt", "installed\n"), Step.Message("Installed.")],
            onDecline: [Step.Message("Not installed.")]));
        shell.Start();

        shell.Enter(Task);
        await shell.WaitUntilAsync(() => shell.Terminal.Text.Contains("Nothing was granted", StringComparison.Ordinal), "the end of the run");

        Assert.DoesNotContain("then Enter:", shell.Terminal.Text, StringComparison.Ordinal);
        Assert.DoesNotContain("Installed.", shell.Terminal.Text, StringComparison.Ordinal);
        Assert.Equal(Yav.Core.Runs.RunState.Blocked, shell.Services.Database.FindRun(shell.Shell.Session.LastRunId!)!.State);

        // Sending the request again would meet the same question, and again nobody could answer it.
        Assert.DoesNotContain("Send the request again to decide when asked", shell.Terminal.Text, StringComparison.Ordinal);
        Assert.Contains("Nobody can be asked in this mode", shell.Terminal.Text, StringComparison.Ordinal);
    }
}
