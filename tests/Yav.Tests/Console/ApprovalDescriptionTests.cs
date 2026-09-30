using Yav.Console.Input;
using Yav.Console.Shell;
using Yav.Core.Agents;

namespace Yav.Tests.Console;

/// <summary>What the user is shown before deciding about something an agent asks for.</summary>
public class ApprovalDescriptionTests
{
    private const string Esc = "\u001b";

    private static List<string> Shown(ApprovalRequest request, AgentRole role = AgentRole.Implementer) =>
        ConsoleApprovals.Describe(role, request, unicode: true).Select(line => line.PlainText).ToList();

    private static ApprovalRequest Change(IReadOnlyList<string> details) =>
        new("a1", ApprovalKind.FileChange, "Change files", null, @"C:\ws", "needs to write outside", details, CanAcceptForSession: false);

    private static ApprovalRequest Command(string command, string? reason = null) =>
        new("a1", ApprovalKind.CommandExecution, "Run a command", command, @"C:\ws", reason, [], CanAcceptForSession: true);

    [Fact]
    public void Every_file_of_a_change_is_named_up_to_as_many_as_a_screen_holds()
    {
        var files = Enumerable.Range(1, 21).Select(i => i < 21 ? $"Update src/file{i}.cs" : "and 4 more").ToList();

        var lines = Shown(Change(files));

        Assert.All(files, file => Assert.Contains("  │ " + file, lines));
    }

    [Fact]
    public void What_is_not_shown_is_counted()
    {
        var files = Enumerable.Range(1, 30).Select(i => $"Update src/file{i}.cs").ToList();

        var lines = Shown(Change(files));

        Assert.Contains("  │ Update src/file24.cs", lines);
        Assert.DoesNotContain("  │ Update src/file25.cs", lines);
        Assert.Contains("  │ and 6 more that are not shown", lines);
    }

    [Fact]
    public void What_a_request_is_about_and_is_not_shown_is_not_allowed_by_one_letter()
    {
        // What is written into a file is listed line by line. Lines nobody saw are not allowed in passing.
        var content = Enumerable.Range(1, 100).Select(i => $"  line {i} of what is written").ToList();
        var request = new ApprovalRequest("a1", ApprovalKind.FileChange, "Write a file", null, @"C:\ws", null, content, CanAcceptForSession: true);

        var question = ApprovalQuestion.For(AgentRole.Implementer, request, unicode: true);
        var lines = question.Lines.Select(line => line.PlainText).ToList();

        Assert.Contains("  │ and 76 more that are not shown", lines);
        Assert.Contains(lines, line => line.StartsWith("           Not all of it is shown: 76 more lines", StringComparison.Ordinal));
        Assert.True(question.Deliberate);
        Assert.False(question.CanAcceptForSession);
        Assert.Equal((null, "Nothing was granted: this request is allowed only by typing allow, and only once."), question.Decide("a"));
        Assert.Equal((ApprovalDecision.Accept, null), question.Decide("allow"));
    }

    [Fact]
    public void What_a_request_is_about_and_is_shown_whole_is_allowed_by_one_letter()
    {
        var files = Enumerable.Range(1, ApprovalQuestion.DetailsShown).Select(i => $"Update src/file{i}.cs").ToList();

        var question = ApprovalQuestion.For(AgentRole.Implementer, Change(files), unicode: true);

        Assert.False(question.Deliberate);
        Assert.DoesNotContain(question.Lines, line => line.PlainText.Contains("Not all of it is shown", StringComparison.Ordinal));
        Assert.Equal((ApprovalDecision.Accept, null), question.Decide("a"));
    }

    [Fact]
    public void A_request_is_said_to_be_one_with_what_was_asked_the_reason_and_the_answers()
    {
        var lines = Shown(new ApprovalRequest(
            "a1", ApprovalKind.CommandExecution, "Run a command", "npm install left-pad", @"C:\ws", "needs the network", [], CanAcceptForSession: true));

        Assert.Equal("[APPROVAL] Model A asks to run a command", lines[0]);
        Assert.Contains("  │ npm install left-pad", lines);
        Assert.Contains(@"  │ in C:\ws", lines);
        Assert.Contains("  │ reason given: needs the network", lines);
        Assert.Contains("Type a and press Enter to allow it once", lines[^1], StringComparison.Ordinal);
        Assert.Contains("s to allow it for this conversation", lines[^1], StringComparison.Ordinal);
        Assert.Contains("Escape declines, Control+C declines and stops the turn.", lines[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void A_command_of_two_hundred_lines_is_shown_up_to_a_bound_the_rest_is_counted_and_one_letter_does_not_allow_it()
    {
        var command = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"echo line {i}"));

        var question = ApprovalQuestion.For(AgentRole.Implementer, Command(command), unicode: true);
        var lines = question.Lines.Select(line => line.PlainText).ToList();

        Assert.Contains("  │ echo line 60", lines);
        Assert.DoesNotContain("  │ echo line 61", lines);
        var counted = Assert.Single(lines, line => line.Contains("140 more lines", StringComparison.Ordinal));
        Assert.StartsWith("           Not all of it is shown", counted, StringComparison.Ordinal);
        Assert.True(question.Deliberate);
        Assert.False(question.CanAcceptForSession);
        Assert.Equal((null, "Nothing was granted: this request is allowed only by typing allow, and only once."), question.Decide("a"));
        Assert.Equal((ApprovalDecision.Accept, null), question.Decide(" Allow "));
    }

    [Fact]
    public void A_line_that_is_longer_than_a_line_may_be_is_cut_counted_and_one_letter_does_not_allow_it()
    {
        var question = ApprovalQuestion.For(AgentRole.Implementer, Command("echo " + new string('x', 2_495)), unicode: true);
        var lines = question.Lines.Select(line => line.PlainText).ToList();

        Assert.Contains("  │ echo " + new string('x', ApprovalQuestion.LongestLine - 5), lines);
        Assert.Contains(lines, line => line.Contains("500 more characters", StringComparison.Ordinal));
        Assert.True(question.Deliberate);
    }

    [Fact]
    public void A_command_that_is_shown_whole_needs_no_warning_and_one_letter_allows_it()
    {
        var question = ApprovalQuestion.For(AgentRole.Implementer, Command(string.Join("\n", Enumerable.Range(1, 60).Select(i => $"echo {i}"))), unicode: true);

        Assert.False(question.Deliberate);
        Assert.True(question.CanAcceptForSession);
        Assert.DoesNotContain(question.Lines, line => line.PlainText.Contains("Not all of it is shown", StringComparison.Ordinal));
        Assert.Equal((ApprovalDecision.AcceptForSession, null), question.Decide("S"));
    }

    public static TheoryData<string, ApprovalRequest, string> HiddenInEveryPart() => new()
    {
        { "command", Command($"ls {Esc}]0;x{Esc}\\"), "  │ ls \\x1B]0;x\\x1B\\" },
        { "title", new ApprovalRequest("a1", ApprovalKind.ToolUse, $"Use Bash{Esc}[8m hidden", null, null, null, [], false), "  │ Use Bash\\x1B[8m hidden" },
        { "directory", new ApprovalRequest("a1", ApprovalKind.CommandExecution, "Run", "ls", $"C:\\ws{Esc}[2K", null, [], true), "  │ in C:\\ws\\x1B[2K" },
        { "reason", Command("ls", $"because{Esc}[31m"), "  │ reason given: because\\x1B[31m" },
        { "detail", new ApprovalRequest("a1", ApprovalKind.FileChange, "Change files", null, null, null, [$"Update a.cs{Esc}[1A"], false), "  │ Update a.cs\\x1B[1A" },
    };

    [Theory]
    [MemberData(nameof(HiddenInEveryPart))]
    public void What_a_terminal_does_not_show_is_written_out_wherever_it_is_and_one_letter_does_not_allow_it(string part, ApprovalRequest request, string expected)
    {
        _ = part;

        var question = ApprovalQuestion.For(AgentRole.Implementer, request, unicode: true);
        var lines = question.Lines.Select(line => line.PlainText).ToList();

        Assert.Contains(expected, lines);
        Assert.Contains(lines, line => line.Contains("contains characters a terminal does not show; they are written out above", StringComparison.Ordinal));
        Assert.True(question.Deliberate);
        Assert.All(lines, line => Assert.DoesNotContain(Esc, line, StringComparison.Ordinal));
    }

    [Fact]
    public void The_prompt_quotes_what_is_decided_cut_to_fit_and_never_lets_it_begin_a_row()
    {
        var question = ApprovalQuestion.For(AgentRole.Implementer, Command("git push origin main " + new string('x', 300)), unicode: true);

        var wide = question.Prompt(200).PlainText;
        var narrow = question.Prompt(40).PlainText;

        Assert.StartsWith("Allow \"git push origin main xxx", wide, StringComparison.Ordinal);
        Assert.Contains("...\"? a, s, d or c, then Enter: ", wide, StringComparison.Ordinal);
        Assert.True(wide.Length < 200 - 8, wide);
        Assert.Equal("Allow the request above? a, s, d or c, then Enter: ", narrow);
    }
}
