using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using PrettyPrompt.Rendering;
using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Storage;

namespace Yav.Tests.Support;

/// <summary>A confirmation a command is expected to ask, named by a part of its question, and the answer it is to get.</summary>
public sealed record ExpectedQuestion(string Question, string Answer);

/// <summary>How a run that was followed until its end ended.</summary>
/// <param name="Result">The row with which the shell reported the end: "Run &lt;id&gt;: &lt;state&gt;".</param>
/// <param name="QuestionsAnswered">How many questions of the agents were answered. None was granted.</param>
/// <param name="Stopped">True when the run was stopped because the time it was given had passed.</param>
public sealed record FollowedRun(string Result, int QuestionsAnswered, bool Stopped);

/// <summary>
/// The shell in a pseudo console, driven the way a person at the keyboard drives it: the screen is read and keys
/// are pressed. The live run and the tests of the driver use it alike, so what the live run relies on is tested
/// where no model is asked anything.
///
/// It grants nothing in anyone's name. A confirmation gets the answer the caller gave only when it is the question
/// the caller named; any other gets no, and the command fails. Whatever an agent asks for is declined.
/// </summary>
public sealed partial class ShellDriver : IDisposable
{
    // The texts the driver looks for are written by the shell. Where each one comes from is named, because a
    // change there has to reach the driver.

    /// <summary>What every confirmation of the shell ends with (ConfirmAsync in Shell\Commands.cs).</summary>
    public const string Confirmation = "Type yes to confirm:";

    /// <summary>The end of the question /login asks before it acknowledges an account route (LoginAsync in Shell\Commands.Models.cs).</summary>
    public const string RouteQuestion = "for runs of YAV, billed as stated above?";

    /// <summary>The question /test trust asks before the commands of the project become its checks (TrustGatesAsync in Shell\Commands.Run.cs).</summary>
    public const string ChecksQuestion = "Approve this configuration?";

    /// <summary>How the line begins on which a question of an agent waits for its answer (ApprovalQuestion.Prompt in Input\ApprovalQuestion.cs).</summary>
    public const string Question = "Allow ";

    /// <summary>How that line ends while nothing is typed for the question. An answer is typed after it and sent with Enter.</summary>
    public const string QuestionEnd = "then Enter:";

    /// <summary>How many questions of an agent are declined before the next one also ends the agent's turn.</summary>
    public const int QuestionsDeclined = 6;

    // What the shell answers to Control+C when no run is active (InteractiveShell). A second one would end the shell.
    private const string ControlCWithoutRun = "Control+C again, or /exit, leaves YAV.";

    /// <summary>How long a run that was stopped at its limit is given to end.</summary>
    public static readonly TimeSpan StopGrace = TimeSpan.FromMinutes(2);

    // How often Control+C is pressed while a run that was stopped has not ended.
    private static readonly TimeSpan InterruptEvery = TimeSpan.FromSeconds(5);

    // How long the prompt may be back before a /resume whose run was not taken up counts as refused.
    private static readonly TimeSpan RefusedAfter = TimeSpan.FromSeconds(5);

    // How often the record of the run is read while it is not known yet whether the shell took the run up.
    private static readonly TimeSpan LookEvery = TimeSpan.FromSeconds(1);

    private readonly int _columns;

    private ShellDriver(PseudoConsole console, string prompt, YavPaths home, int columns)
    {
        Console = console;
        Prompt = prompt;
        Home = home;
        _columns = columns;
    }

    /// <summary>Starts yav.exe on a project, with a data directory of its own and without colors.</summary>
    public static ShellDriver Start(string executable, string project, YavPaths home, int columns = 200, int rows = 60) =>
        new(
            PseudoConsole.Start(
                executable,
                [project],
                project,
                new Dictionary<string, string?> { [YavPaths.HomeVariable] = home.Home, ["NO_COLOR"] = "1", ["PROMPT"] = null },
                columns,
                rows),
            $"YAV {project}>",
            home,
            columns);

    public PseudoConsole Console { get; }

    /// <summary>The line the cursor rests on when the shell waits for input and nothing has been typed.</summary>
    public string Prompt { get; }

    public YavPaths Home { get; }

    /// <summary>What the limit of a run that is followed is measured with. A test sets a clock that moves as the test needs it.</summary>
    public TimeProvider Clock { get; set; } = TimeProvider.System;

    /// <summary>What each command added to the screen, from the row it was entered on.</summary>
    public StringBuilder Transcript { get; } = new();

    /// <summary>How many questions of agents were answered so far. None was granted.</summary>
    public int QuestionsAnswered { get; private set; }

    /// <summary>Everything the program wrote, without the sequences that move the cursor or color the text.</summary>
    public string Written => ControlSequences().Replace(Console.Raw, string.Empty);

    [GeneratedRegex(@"\u001b\[[0-?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(\u0007|\u001b\\)|\u001b[@-_]")]
    private static partial Regex ControlSequences();

    /// <summary>
    /// The row with which the shell introduces a question of an agent. The note about an answer begins with the same
    /// stage and goes on with the title the agent gave its request and a colon, so the colon tells the two apart,
    /// whatever an agent calls its request.
    /// </summary>
    [GeneratedRegex(@"^\[APPROVAL\] Model [AB] asks [^:]*$")]
    private static partial Regex QuestionRow();

    public Task WaitForPromptAsync(int seconds = 60) => Console.WaitForCursorLineAsync(Prompt, seconds);

    /// <summary>
    /// Enters a command and waits until the shell waits for the next one. A confirmation gets the answer of
    /// <paramref name="expected"/> only when it is that question, and only once. Any other confirmation is answered
    /// with no, and the command fails, naming the question that appeared.
    /// </summary>
    public async Task EnterAsync(string command, ExpectedQuestion? expected = null, int seconds = 180)
    {
        await TypeAsync(command);
        var watch = Stopwatch.StartNew();
        var answers = 0;
        var expectedAnswered = false;
        string? unexpected = null;
        while (true)
        {
            var rows = Console.Screen.Lines;
            var cursor = Console.Screen.CursorLine;
            var entered = EnteredAt(rows, Prompt, command);

            // Until the shell has taken the line, the row that shows it is the line that is being typed.
            if (entered >= 0 && cursor != rows[entered])
            {
                if (cursor == Prompt)
                {
                    break;
                }

                if (PendingConfirmation(rows, cursor, entered, answers, _columns) is { } question)
                {
                    var wanted = expected is not null && !expectedAnswered && Mentions(question, expected.Question);
                    expectedAnswered |= wanted;
                    if (!wanted)
                    {
                        unexpected ??= question;
                    }

                    answers++;
                    var answer = wanted ? expected!.Answer : "no";

                    // The answer follows the question on its row, or on the next one when the question filled its row.
                    await SendAsync(answer, shown: screen => Compact(string.Concat(screen.Lines.TakeLast(2))).EndsWith(Compact(Confirmation + answer), StringComparison.Ordinal));
                    continue;
                }
            }

            Assert.False(Console.HasExited, $"The program ended with exit code {Console.ExitCode} during '{command}':\n{Console.Screen.Text}");
            if (watch.Elapsed > TimeSpan.FromSeconds(seconds))
            {
                Assert.Fail(
                    $"'{command}' did not end within {seconds} seconds"
                    + (unexpected is null ? string.Empty : $"; it had asked \"{unexpected}\", which was answered with no")
                    + $":\n{Console.Screen.Text}");
            }

            await Task.Delay(50);
        }

        await Task.Delay(200);
        Record(command);
        if (unexpected is not null)
        {
            Assert.Fail(
                $"'{command}' asked \"{unexpected}\""
                + (expected is null ? ", and no question was expected" : $", and \"{expected.Question}\" was the question expected")
                + ". It was answered with no.");
        }
    }

    /// <summary>
    /// Continues a run with /resume and follows it until the shell reports its end. Whatever an agent asks is
    /// declined; after <see cref="QuestionsDeclined"/> questions the next one also ends the agent's turn, so that an
    /// agent that keeps asking cannot keep consuming usage. When the limit has passed, the run is stopped the way a
    /// user stops it, so that what it did until then is recorded.
    ///
    /// A run that /resume would not continue fails before anything is typed. A /resume that the shell refused
    /// without saying so fails a few seconds after the prompt came back.
    /// </summary>
    public async Task<FollowedRun> ResumeAsync(string runId, TimeSpan limit)
    {
        Resumable(Home, runId);
        var command = "/resume " + runId;
        var deadline = Clock.GetUtcNow() + limit;
        await TypeAsync(command);
        var followed = await FollowAsync(command, runId, deadline);

        // The shell reports the end before it waits for the next input.
        await WaitForPromptAsync();
        await Task.Delay(500);
        Record(command);
        return followed;
    }

    /// <summary>Leaves the shell with /exit, which has to end it with exit code 0.</summary>
    public async Task ExitAsync()
    {
        await TypeAsync("/exit");
        Assert.Equal(0, await Console.WaitForExitAsync(60));
    }

    /// <summary>
    /// The run as it is stored, read before anything is typed. A run that /resume would not continue fails here, at
    /// once: one that does not exist, one that is completed and one that is ready to apply.
    /// </summary>
    public static RunRecord Resumable(YavPaths home, string runId)
    {
        var run = StoredRun(home, runId)
            ?? throw new Xunit.Sdk.XunitException($"Run {runId} does not exist in {home.Database}, so there is nothing to continue. Nothing was typed.");
        Assert.False(
            run.State is RunState.Completed or RunState.ReadyToApply,
            $"Run {runId} is {RunStateMachine.Display(run.State)}, so /resume has nothing to continue. Nothing was typed.");
        return run;
    }

    /// <summary>The run as the data directory records it now, or null.</summary>
    public static RunRecord? StoredRun(YavPaths home, string runId)
    {
        // Opening a database that is not there would create an empty one.
        if (!File.Exists(home.Database))
        {
            return null;
        }

        var database = YavDatabase.Open(home.Database, TimeProvider.System);
        try
        {
            return database.FindRun(runId);
        }
        finally
        {
            database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    public void Dispose() => Console.Dispose();

    /// <summary>The row a command was entered on: the last that shows the prompt and the command, also when the line was broken at the edge of the console.</summary>
    internal static int EnteredAt(IReadOnlyList<string> rows, string prompt, string command)
    {
        var line = $"{prompt} {command}";
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (!rows[i].StartsWith(prompt, StringComparison.Ordinal))
            {
                continue;
            }

            var joined = new StringBuilder(rows[i]);
            for (var next = i + 1; joined.Length < line.Length && next < rows.Count; next++)
            {
                joined.Append(rows[next]);
            }

            if (joined.ToString().StartsWith(line, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// The question of a confirmation that waits for its answer and was not answered yet, or null. It is on the rows
    /// at the bottom: the row of the cursor and those above it that the question filled to the edge of the console.
    /// A confirmation that was answered stays on the screen together with its answer, so those above are counted.
    /// </summary>
    internal static string? PendingConfirmation(IReadOnlyList<string> rows, string cursor, int entered, int answered, int columns)
    {
        if (entered < 0 || rows.Count <= entered + 1 || rows[^1] != cursor)
        {
            return null;
        }

        var start = rows.Count - 1;
        while (start - 1 > entered && UnicodeWidth.GetWidth(rows[start - 1]) >= columns)
        {
            start--;
        }

        var question = string.Concat(rows.Skip(start));
        if (!Compact(question).EndsWith(Compact(Confirmation), StringComparison.Ordinal))
        {
            return null;
        }

        var before = Compact(string.Concat(rows.Skip(entered + 1).Take(start - entered - 1)));
        return Occurrences(before, Compact(Confirmation)) == answered ? question : null;
    }

    /// <summary>The newest row that introduces a question of an agent, or -1.</summary>
    internal static int LastQuestionRow(IReadOnlyList<string> rows)
    {
        for (var i = rows.Count - 1; i >= 0; i--)
        {
            if (QuestionRow().IsMatch(rows[i]))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Types a command and sends it. What is typed is added to whatever the input line holds, so it is typed on an empty one only.</summary>
    private async Task TypeAsync(string command)
    {
        await WaitForPromptAsync(10);
        var line = $"{Prompt} {command}";

        // The line the cursor is on is the whole line, or its end when the line is longer than the console is wide.
        await SendAsync(command, shown: screen => screen.CursorLine is { Length: > 0 } cursor && line.EndsWith(cursor, StringComparison.Ordinal));
    }

    /// <summary>
    /// Types text and presses Enter once the screen shows all of it. A key that arrives while earlier ones still wait
    /// to be read is taken for pasted text, and a pasted Enter does not send the line.
    /// </summary>
    private async Task SendAsync(string text, Func<PtyScreen, bool> shown)
    {
        await Console.TypeAsync(text);
        await Console.WaitUntilAsync(shown, $"'{text}' as it was typed", 10);
        await Console.EnterAsync();
    }

    private async Task<FollowedRun> FollowAsync(string command, string runId, DateTimeOffset deadline)
    {
        var end = $"Run {runId}: ";
        var answeredAt = -1;
        var taken = false;
        Stopwatch? stopping = null;
        Stopwatch? interrupted = null;
        Stopwatch? back = null;
        Stopwatch? looked = null;
        while (true)
        {
            var rows = Added(command);
            var cursor = Console.Screen.CursorLine;
            if (rows.FirstOrDefault(row => row.StartsWith(end, StringComparison.Ordinal)) is { } result)
            {
                return new FollowedRun(result, QuestionsAnswered, stopping is not null);
            }

            Assert.False(Console.HasExited, $"The program ended with exit code {Console.ExitCode} during '{command}':\n{Console.Screen.Text}");
            Assert.False(rows.Contains(ControlCWithoutRun), $"Control+C found no run to stop, so '{command}' ended without saying how:\n{Console.Screen.Text}");

            // Read after the screen: a question that is on the screen now is open when the time is found to be up.
            if (stopping is null && Clock.GetUtcNow() >= deadline)
            {
                stopping = Stopwatch.StartNew();
            }

            // Only the newest question can be waiting, and only when it was not answered yet: a question the agent
            // took back is followed by the next one, and one that was answered can stay on the line a moment longer.
            var newest = LastQuestionRow(rows);
            var open = newest > answeredAt && IsOpenQuestion(cursor);
            if (stopping is not null)
            {
                Assert.True(stopping.Elapsed < StopGrace, $"'{command}' did not end within {StopGrace.TotalMinutes:0} minutes after it was stopped:\n{Console.Screen.Text}");

                // A question holds the keyboard. It is declined in the way that also ends the turn.
                if (open)
                {
                    if (await AnswerAsync("c", newest, command))
                    {
                        answeredAt = newest;
                    }

                    continue;
                }

                if (taken && (interrupted is null || interrupted.Elapsed >= InterruptEvery))
                {
                    // Stopped the way a user stops a run, so that what was done until then is recorded.
                    interrupted = Stopwatch.StartNew();
                    await Console.ControlCAsync();
                    continue;
                }
            }
            else if (open)
            {
                if (await AnswerAsync(QuestionsAnswered >= QuestionsDeclined ? "c" : "d", newest, command))
                {
                    answeredAt = newest;
                }

                continue;
            }

            // A /resume the shell refuses says nothing about it: the prompt comes back and the run stays as it was.
            // A run the shell took up belongs to its process from the first moment on.
            if (!taken)
            {
                if (cursor == Prompt)
                {
                    if (looked is null || looked.Elapsed >= LookEvery)
                    {
                        looked = Stopwatch.StartNew();
                        taken = StoredRun(Home, runId)?.OwnerProcessId == Console.ProcessId;
                    }

                    back ??= Stopwatch.StartNew();
                    if (!taken && back.Elapsed >= RefusedAfter)
                    {
                        var run = StoredRun(Home, runId);
                        Assert.Fail(
                            $"'{command}' came back to the prompt, and after {RefusedAfter.TotalSeconds:0} seconds no run was active: run {runId} "
                            + $"is {(run is null ? "gone" : RunStateMachine.Display(run.State))} and was not taken up. What the shell wrote:\n{string.Join('\n', rows)}");
                    }
                }
                else
                {
                    back = null;
                }
            }

            await Task.Delay(100);
        }
    }

    /// <summary>True for the line on which a question of an agent waits for its answer and nothing is typed yet.</summary>
    internal static bool IsOpenQuestion(string cursor) =>
        cursor.StartsWith(Question, StringComparison.Ordinal) && cursor.EndsWith(QuestionEnd, StringComparison.Ordinal);

    /// <summary>
    /// Answers the question on the given row with a letter that declines and Enter, if that question still waits. An
    /// agent can take a question back. A letter that finds no question goes into the input line: it is taken out
    /// there again, so that it never becomes part of a line that is sent.
    /// </summary>
    private async Task<bool> AnswerAsync(string letter, int row, string command)
    {
        // Keys that follow each other at once are taken for pasted text, and pasted text answers no question.
        await Task.Delay(120);
        if (LastQuestionRow(Added(command)) != row || !IsOpenQuestion(Console.Screen.CursorLine))
        {
            return false;
        }

        Console.Press(letter);
        var watch = Stopwatch.StartNew();
        while (true)
        {
            var cursor = Console.Screen.CursorLine;
            if (cursor.StartsWith(Question, StringComparison.Ordinal) && cursor.EndsWith($"{QuestionEnd} {letter}", StringComparison.Ordinal))
            {
                await Console.EnterAsync();
                QuestionsAnswered++;
                return true;
            }

            if (cursor == $"{Prompt} {letter}")
            {
                // The question was taken back before the letter reached it. Escape empties the input line.
                await Console.PressAsync("\u001b");
                return false;
            }

            Assert.False(Console.HasExited, $"The program ended with exit code {Console.ExitCode} while a question was answered:\n{Console.Screen.Text}");
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"The letter '{letter}' was typed for a question and did not appear:\n{Console.Screen.Text}");
            await Task.Delay(50);
        }
    }

    /// <summary>The rows from the one the command was entered on.</summary>
    private List<string> Added(string command)
    {
        var rows = Console.Screen.Lines;
        var entered = EnteredAt(rows, Prompt, command);
        return entered < 0 ? [] : rows.Skip(entered).ToList();
    }

    private void Record(string command)
    {
        var rows = Console.Screen.Lines;
        var entered = EnteredAt(rows, Prompt, command);
        Transcript.AppendLine(string.Join('\n', rows.Skip(Math.Max(entered, 0))).TrimEnd()).AppendLine();
    }

    // Where a row ends, and how many blanks there are, is no part of what a question says.
    private static bool Mentions(string text, string part) => Compact(text).Contains(Compact(part), StringComparison.Ordinal);

    private static string Compact(string text) => string.Concat(text.Where(c => !char.IsWhiteSpace(c)));

    private static int Occurrences(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
        {
            count++;
        }

        return count;
    }
}
