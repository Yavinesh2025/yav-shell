using System.Globalization;
using Yav.Console.Rendering;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Core.Text;

namespace Yav.Console.Input;

/// <summary>
/// The question the user is asked when an agent asks for something: what it is about, shown as it is, and the
/// answers that decide it. An answer is a line: a letter or a word, then Enter. The console and plain lines ask
/// the same question and take the same answers.
/// </summary>
public sealed class ApprovalQuestion
{
    /// <summary>How many lines of a command are shown. More are counted, and the request then needs the word allow.</summary>
    public const int LinesShown = 60;

    /// <summary>How many characters of a line are shown. More are counted, and the request then needs the word allow.</summary>
    public const int LongestLine = 2000;

    /// <summary>How many lines of what a request is about are shown. An agent names up to twenty files and counts the rest in one more line.</summary>
    public const int DetailsShown = 24;

    /// <summary>The word that allows once what one letter does not allow.</summary>
    public const string AllowWord = "allow";

    /// <summary>How much of what is decided the prompt names. All of it is shown above the prompt.</summary>
    private const int NamedInPrompt = 60;

    /// <summary>What a reason is cut to. It says why, not what is decided.</summary>
    private const int LongestReason = 300;

    // Lines of YAV below what an agent wrote begin here: under the text of the stage line, and never behind the gutter.
    private const string Indent = "           ";

    private readonly string _named;

    private ApprovalQuestion(IReadOnlyList<Line> lines, bool deliberate, bool session, string named)
    {
        Lines = lines;
        Deliberate = deliberate;
        CanAcceptForSession = session && !deliberate;
        _named = named;
    }

    /// <summary>What is shown before the prompt: who asks, what for, and the answers in full words.</summary>
    public IReadOnlyList<Line> Lines { get; }

    /// <summary>
    /// True when one letter does not allow it: the agent marked it so, or what is decided about could not be shown
    /// as it is, because it contains characters a terminal does not show or is longer than can be shown.
    /// </summary>
    public bool Deliberate { get; }

    /// <summary>True when it may be allowed for the rest of the conversation. Never for what needs the word allow.</summary>
    public bool CanAcceptForSession { get; }

    /// <summary>What says, below everything that is shown, that an answer is not one.</summary>
    public string NotAnAnswer => Deliberate
        ? "Not an answer, so nothing was granted. Type allow, d or c, then Enter. Escape declines."
        : $"Not an answer, so nothing was granted. Type {Letters}, then Enter. Escape declines.";

    private string Letters => CanAcceptForSession ? "a, s, d or c" : "a, d or c";

    public static ApprovalQuestion For(AgentRole role, ApprovalRequest request, bool unicode)
    {
        var gutter = unicode ? "  │ " : "  | ";
        var who = role == AgentRole.Implementer ? "Model A" : "Model B";
        var what = request.Kind switch
        {
            ApprovalKind.CommandExecution => "asks to run a command",
            ApprovalKind.FileChange => "asks to change files outside what it may change by itself",
            ApprovalKind.Permissions => "asks for more permissions",
            ApprovalKind.ToolUse => "asks to use a tool",
            _ => "asks for your input",
        };

        var shown = new Shown(gutter);
        shown.Lines.Add(Line.Of(new Segment(RunEventFormatter.StageLabel(Stages.Approval), Tone.Stage, Bold: true), new Segment($"{who} {what}", Tone.Warning, Bold: true)));
        string named;
        if (!string.IsNullOrWhiteSpace(request.Command))
        {
            // What will be run is shown line by line as it is, as far as a screen holds it; the rest is counted.
            var command = TerminalSanitizer.Visible(request.Command, out var writtenOut);
            shown.WrittenOut |= writtenOut;
            var rows = command.Split('\n');
            foreach (var row in rows.Take(LinesShown))
            {
                shown.Quote(row, Tone.Normal);
            }

            shown.LinesNotShown = Math.Max(0, rows.Length - LinesShown);
            named = request.Command;
        }
        else
        {
            shown.QuoteLine(request.Title, Tone.Normal);
            named = request.Title;
        }

        if (!string.IsNullOrWhiteSpace(request.WorkingDirectory))
        {
            shown.QuoteLine(request.WorkingDirectory, Tone.Muted, "in ");
        }

        if (!string.IsNullOrWhiteSpace(request.Reason))
        {
            var reason = TerminalSanitizer.VisibleSingleLine(request.Reason, LongestReason);
            TerminalSanitizer.Visible(request.Reason, out var writtenOut);
            shown.WrittenOut |= writtenOut;
            shown.Lines.Add(Line.Of(new Segment(gutter, Tone.Muted), new Segment("reason given: " + reason, Tone.Muted)));
        }

        // What would be changed is what is decided about, so it is shown as far as a screen holds it, and the rest is counted.
        foreach (var detail in request.Details.Take(DetailsShown))
        {
            shown.QuoteLine(detail, Tone.Muted);
        }

        if (request.Details.Count > DetailsShown)
        {
            shown.DetailsNotShown = request.Details.Count - DetailsShown;
            shown.Lines.Add(Line.Of(new Segment(gutter, Tone.Muted), new Segment($"and {shown.DetailsNotShown} more that are not shown", Tone.Warning)));
        }

        if (shown.WrittenOut)
        {
            shown.Lines.Add(Line.Of(new Segment(Indent + "This request contains characters a terminal does not show; they are written out above.", Tone.Warning, Bold: true)));
        }

        if (shown.NotShown() is { } notShown)
        {
            shown.Lines.Add(Line.Of(new Segment($"{Indent}Not all of it is shown: {notShown}. Allow only what you have read.", Tone.Warning, Bold: true)));
        }

        if (request.Deliberate)
        {
            shown.Lines.Add(Line.Of(new Segment(Indent + "The agent marks this as something that must not be allowed by one letter.", Tone.Warning)));
        }

        var deliberate = request.Deliberate || shown.WrittenOut || shown.NotShown() is not null;
        var question = new ApprovalQuestion(shown.Lines, deliberate, request.CanAcceptForSession, TerminalSanitizer.VisibleSingleLine(named));
        shown.Lines.Add(Line.Of(new Segment(question.Choices(), Tone.Muted)));
        return question;
    }

    /// <summary>
    /// The prompt, which names what is decided in a form that cannot be taken for another question. What an agent
    /// wrote stands in quotes and is cut so that the prompt fits into a row of the given width: no part of it may
    /// begin a row of its own.
    /// </summary>
    public Line Prompt(int width)
    {
        var answers = Deliberate
            ? $"Type {AllowWord} to allow once, d or c to decline, then Enter: "
            : $"{Letters}, then Enter: ";

        // Room for the quotes, the question mark and a few letters of the answer.
        var room = Math.Min(NamedInPrompt, width - 1 - answers.Length - "Allow \"\"? ".Length - 8);
        var text = room < 12 || _named.Length == 0
            ? $"Allow the request above? {answers}"
            : $"Allow \"{Cut(_named, room)}\"? {answers}";
        return Line.Of(new Segment(text, Tone.Warning, Bold: true));
    }

    /// <summary>The decision an answer makes, or none and what to say about it. The answer is compared without case and surrounding blanks.</summary>
    public (ApprovalDecision? Decision, string? Say) Decide(string answer)
    {
        var text = answer.Trim().ToLowerInvariant();
        switch (text)
        {
            case "d":
                return (ApprovalDecision.Decline, null);
            case "c":
                return (ApprovalDecision.Cancel, null);
        }

        if (Deliberate)
        {
            return text switch
            {
                AllowWord => (ApprovalDecision.Accept, null),
                "a" or "s" => (null, $"Nothing was granted: this request is allowed only by typing {AllowWord}, and only once."),
                _ => (null, NotAnAnswer),
            };
        }

        return text switch
        {
            "a" => (ApprovalDecision.Accept, null),
            "s" when CanAcceptForSession => (ApprovalDecision.AcceptForSession, null),
            "s" => (null, "Nothing was granted: this request cannot be allowed for this conversation. Type a to allow it once."),
            _ => (null, NotAnAnswer),
        };
    }

    /// <summary>The line of YAV that says in full words what can be answered.</summary>
    private string Choices() => Indent + (Deliberate
        ? $"Type {AllowWord} and press Enter to allow it once; it cannot be allowed for this conversation. "
            + "d and Enter declines, c and Enter declines and stops the turn. Escape declines, Control+C declines and stops the turn."
        : "Type a and press Enter to allow it once, "
            + (CanAcceptForSession ? "s to allow it for this conversation, " : string.Empty)
            + "d to decline, c to decline and stop the turn"
            + (CanAcceptForSession ? "." : "; it cannot be allowed for this conversation.")
            + " Escape declines, Control+C declines and stops the turn.");

    private static string Cut(string text, int length)
    {
        if (text.Length <= length)
        {
            return text;
        }

        // A character of two halves is not cut in two.
        var keep = length - 3;
        if (char.IsHighSurrogate(text[keep - 1]))
        {
            keep--;
        }

        return string.Concat(text.AsSpan(0, keep), "...");
    }

    /// <summary>The lines that are shown, and what could not be shown as it is.</summary>
    private sealed class Shown(string gutter)
    {
        public List<Line> Lines { get; } = [];

        public bool WrittenOut { get; set; }

        public int LinesNotShown { get; set; }

        /// <summary>Lines of what a request is about, such as what is written into a file, that are not shown.</summary>
        public int DetailsNotShown { get; set; }

        public int CharactersNotShown { get; private set; }

        /// <summary>A line of what an agent wrote, as it is, behind the gutter. What is longer than a line may be is counted.</summary>
        public void Quote(string visible, Tone tone, string before = "")
        {
            var text = visible;
            if (text.Length > LongestLine)
            {
                var keep = char.IsHighSurrogate(text[LongestLine - 1]) ? LongestLine - 1 : LongestLine;
                CharactersNotShown += text.Length - keep;
                text = text[..keep];
            }

            Lines.Add(Line.Of(new Segment(gutter, Tone.Muted), new Segment(before + text, tone)));
        }

        /// <summary>Text of an agent that is shown on one line: its line breaks become spaces, and nothing is left out.</summary>
        public void QuoteLine(string text, Tone tone, string before = "")
        {
            var visible = TerminalSanitizer.Visible(text, out var writtenOut);
            WrittenOut |= writtenOut;
            Quote(visible.Replace('\n', ' '), tone, before);
        }

        public string? NotShown()
        {
            var parts = new List<string>();
            if (LinesNotShown + DetailsNotShown > 0)
            {
                var lines = LinesNotShown + DetailsNotShown;
                parts.Add(lines.ToString(CultureInfo.InvariantCulture) + (lines == 1 ? " more line" : " more lines"));
            }

            if (CharactersNotShown > 0)
            {
                parts.Add($"{CharactersNotShown.ToString(CultureInfo.InvariantCulture)} more characters of lines longer than {LongestLine.ToString(CultureInfo.InvariantCulture)}");
            }

            return parts.Count == 0 ? null : string.Join(" and ", parts);
        }
    }
}
