using System.Text;
using Yav.Console.Composition;
using Yav.Console.Output;
using Yav.Console.Rendering;
using Yav.Coordinator;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Core.Text;

namespace Yav.Console.Cli;

/// <summary>Never grants anything. A request for approval ends a non-interactive run as Approval Required.</summary>
public sealed class NoApprovals : IApprovalBroker
{
    public bool CanAsk => false;

    public Task<ApprovalDecision> AskAsync(string runId, AgentRole role, ApprovalRequest request, CancellationToken cancellationToken) =>
        Task.FromResult(ApprovalDecision.Cancel);
}

public sealed class DelegateObserver(Action<RunEvent> handler) : IRunObserver
{
    public void OnEvent(RunEvent runEvent) => handler(runEvent);
}

/// <summary>'yav run': one request without a prompt. Plain lines or JSON lines, and an exit code that says how it ended.</summary>
public static class RunCommand
{
    private const int MaxPromptFileBytes = 1024 * 1024;

    /// <param name="screen">Where the result goes: the window, or the stream the output was redirected to.</param>
    /// <param name="error">Where a problem with the command itself goes when the output is not JSON.</param>
    public static async Task<int> ExecuteAsync(CliOptions options, AppServices services, Screen screen, TextWriter error, CancellationToken stop)
    {
        int Fail(string message, int exitCode)
        {
            if (options.Json)
            {
                screen.WriteBlock(JsonOutput.Failure(message, exitCode));
            }
            else
            {
                error.WriteLine("yav: " + TerminalSanitizer.CleanSingleLine(message, 2000));
            }

            return exitCode;
        }

        var project = Path.GetFullPath(options.ProjectPath ?? Environment.CurrentDirectory);
        var text = options.Task;
        if (options.PromptFile is not null)
        {
            var read = ReadPromptFile(options.PromptFile);
            if (read.Problem is not null)
            {
                return Fail(read.Problem, ExitCodes.Invalid);
            }

            text = read.Text;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Fail("The request is empty.", ExitCodes.Invalid);
        }

        var formatter = new RunEventFormatter(new FormatterOptions(screen.Options.Unicode, Verbose: false));
        void Show(RunEvent runEvent)
        {
            if (!options.Json)
            {
                screen.WriteLines(formatter.Format(runEvent));
            }
            else if (JsonOutput.Event(runEvent) is { } line)
            {
                // One object on one line. A line of JSON is data and is never broken into rows.
                screen.WriteBlock(line);
            }
        }

        var observer = new DelegateObserver(Show);

        // Whatever a crash left behind is dealt with before anything new is started.
        var report = await services.Coordinator.ReconcileAsync(stop).ConfigureAwait(false);
        foreach (var journal in report.Journals)
        {
            Show(new StageNote(journal.RunId, services.Clock.GetUtcNow(), Stages.Local, $"An apply of run {journal.RunId} that was interrupted is now {journal.State}.", NoteLevel.Warning));
        }

        foreach (var problem in services.StartupProblems)
        {
            Show(new StageNote(string.Empty, services.Clock.GetUtcNow(), Stages.Local, problem, NoteLevel.Warning));
        }

        var outcome = await services.Coordinator.RunAsync(
            new RunRequest(project, text) { TaskId = options.TaskId },
            services.Configuration,
            observer,
            new NoApprovals(),
            stop).ConfigureAwait(false);

        DeliveryOutcome? delivery = null;
        if (options.Apply && outcome.Kind == RunOutcomeKind.ReadyToApply)
        {
            delivery = await services.Coordinator.ApplyAsync(outcome.RunId, observer, CancellationToken.None).ConfigureAwait(false);
        }

        if (options.Json)
        {
            screen.WriteBlock(JsonOutput.Result(outcome, delivery));
        }
        else
        {
            screen.WriteLines(Summary(outcome, delivery, project));
        }

        return ExitCodes.For(outcome, delivery?.Succeeded);
    }

    private static IEnumerable<Line> Summary(RunOutcome outcome, DeliveryOutcome? delivery, string project)
    {
        static Line Pair(string name, string value) => Line.Labelled(new Segment((name + ":").PadRight(10)), new Segment(value));

        yield return Line.Empty;
        yield return Pair("Outcome", RunStateMachine.Display(outcome.State) + (outcome.Kind == RunOutcomeKind.ApprovalRequired ? " (Approval Required)" : string.Empty));
        if (outcome.RunId.Length > 0)
        {
            yield return Pair("Run", outcome.RunId);
        }

        if (outcome.TaskId is not null)
        {
            yield return Pair("Task", outcome.TaskId);
        }

        if (outcome.Candidate is { } candidate)
        {
            yield return Pair("Changes", $"{candidate.Changes.Summary} (candidate {candidate.ShortFingerprint})");
        }

        // A reason can name what an agent asked for. That part is shown as it is, like what was asked for itself.
        if (outcome.Reason is { Length: > 0 } reason && outcome.Kind is not (RunOutcomeKind.ReadyToApply or RunOutcomeKind.Completed))
        {
            yield return Pair("Reason", TerminalSanitizer.VisibleSingleLine(reason, 1000));
        }

        // What an agent wrote stands in quotes in a line of YAV.
        foreach (var approval in outcome.PendingApprovals)
        {
            yield return Pair("Asked", "\"" + TerminalSanitizer.VisibleSingleLine(approval.Command ?? approval.Title, 200) + "\"");
        }

        if (delivery is not null)
        {
            yield return Pair("Apply", $"{(delivery.Succeeded ? "applied" : "not applied")}. {delivery.Message}");
        }
        else if (outcome.Kind == RunOutcomeKind.ReadyToApply)
        {
            yield return Pair("Next", $"the project was not changed. Inspect and apply in the shell: yav \"{project}\", then /diff and /apply. Or run again with --apply.");
        }
        else if (outcome.Kind is RunOutcomeKind.Blocked or RunOutcomeKind.Interrupted or RunOutcomeKind.RateLimited or RunOutcomeKind.NeedsReconciliation && outcome.RunId.Length > 0)
        {
            yield return Pair("Next", $"continue in the shell: yav \"{project}\", then /resume {outcome.RunId}");
        }
    }

    internal static (string? Text, string? Problem) ReadPromptFile(string path)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            if (!info.Exists)
            {
                return (null, $"The prompt file '{path}' does not exist.");
            }

            if (info.Length > MaxPromptFileBytes)
            {
                return (null, $"The prompt file '{path}' has {info.Length:N0} bytes. A request of more than {MaxPromptFileBytes / 1024} KB is not sent; attach material as files of the project instead.");
            }

            var bytes = File.ReadAllBytes(full);
            string text;
            try
            {
                text = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true).GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                return (null, $"The prompt file '{path}' is not UTF-8 text.");
            }

            // A byte order mark is not part of the request, and neither is the line break a file ends with.
            text = text.TrimStart('\uFEFF').TrimEnd('\r', '\n');
            return string.IsNullOrWhiteSpace(text) ? (null, $"The prompt file '{path}' is empty.") : (text, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return (null, $"The prompt file '{path}' could not be read: {ex.Message}");
        }
    }
}
