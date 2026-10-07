using Yav.Console.Commands;
using Yav.Console.Rendering;
using Yav.Coordinator;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Runs;

namespace Yav.Console.Shell;

public sealed partial class InteractiveShell
{
    private const int DiffLinesShown = 400;

    private Cli.DelegateObserver CommandObserver()
    {
        var formatter = new RunEventFormatter(new FormatterOptions(_screen.Options.Unicode, _session.Verbose));
        return new Cli.DelegateObserver(runEvent => _screen.WriteLines(formatter.Format(runEvent)));
    }

    private async Task SettleEarlierSessionAsync(CancellationToken cancellationToken)
    {
        if (_reconciliation is { IsCompleted: false } pending)
        {
            try
            {
                await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _ = ex;
            }
        }
    }

    private async Task ResumeAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (FindRun(args.FirstOrDefault()) is not { } run)
        {
            return;
        }

        await SettleEarlierSessionAsync(cancellationToken).ConfigureAwait(false);
        if (!string.Equals(Path.GetFullPath(run.ProjectPath), _session.ProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            _ui.Say($"Run {run.RunId} belongs to {run.ProjectPath}. That project is selected now.", Tone.Accent);
            _session.ProjectPath = Path.GetFullPath(run.ProjectPath);
        }

        _session.TaskId = run.TaskId;
        _session.LastRunId = run.RunId;
        _session.TaskEffort = ApprovedEffortOf(run.RunId);
        var configuration = _services.Configuration;
        StartStoredRun(
            $"resume {run.RunId}", run.RunId,
            (observer, approvals, stop) => Coordinator.ResumeAsync(run.RunId, configuration, observer, approvals, stop));
    }

    private async Task QueueAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!RequireProject(out var project))
        {
            return;
        }

        var args = command.Arguments;
        var verb = args.Count == 0 ? string.Empty : args[0].ToLowerInvariant();
        var waiting = _services.Database.GetQueue(project);
        switch (verb)
        {
            case "":
                if (waiting.Count == 0)
                {
                    _ui.Say("No request is waiting.");
                }
                else
                {
                    _ui.Table(
                        ["#", "Waiting since", "Continues the task", "Request"],
                        waiting.Select((q, i) => (IReadOnlyList<string>)
                        [
                            (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture),
                            q.QueuedAt.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture),
                            q.IsFollowUp ? "yes" : "no",
                            Shorten(q.Text, 80),
                        ]));
                    if (_session.QueuePaused && _session.Active is null)
                    {
                        _ui.Warn("They are not started by themselves, because the last run needs a decision. A request you enter now runs at once.");
                    }
                }

                _ui.Muted("/queue add <text>, /queue remove <n>, /queue clear, /queue steer <n> (adds a waiting request to the running turn).");
                return;

            case "add":
            {
                var text = command.RawArguments[(command.RawArguments.IndexOf("add", StringComparison.OrdinalIgnoreCase) + 3)..].Trim();
                if (text.Length == 0)
                {
                    _ui.Warn("Usage: /queue add <text>");
                    return;
                }

                if (_session.Active is null)
                {
                    await StartRunAsync(text, _session.TaskId, null, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Enqueue(text, isFollowUp: true);
                }

                return;
            }

            case "clear":
                _ui.Say($"{_services.Database.ClearQueue(project)} waiting request(s) removed.");
                return;

            case "remove" or "steer":
                if (args.Count != 2 || !int.TryParse(args[1], out var number) || number < 1 || number > waiting.Count)
                {
                    _ui.Warn($"Usage: /queue {verb} <n>, where <n> is a number from /queue.");
                    return;
                }

                var entry = waiting[number - 1];
                if (verb == "remove")
                {
                    _services.Database.RemoveFromQueue(entry.QueueId);
                    _ui.Say($"Removed: \"{Shorten(entry.Text, 70)}\"");
                    return;
                }

                await SteerAsync(entry, cancellationToken).ConfigureAwait(false);
                return;

            default:
                _ui.Warn("Usage: /queue [add <text> | remove <n> | clear | steer <n>]");
                return;
        }
    }

    private async Task SteerAsync(QueuedRequest entry, CancellationToken cancellationToken)
    {
        if (_session.Active?.RunId is not { } runId)
        {
            _ui.Warn("No run is active, so there is no turn to add the request to. It starts as soon as you enter it, or with /queue add.");
            return;
        }

        var accepted = await Coordinator.SteerAsync(runId, entry.Text, cancellationToken).ConfigureAwait(false);
        if (!accepted)
        {
            _ui.Warn("The request was not added: the agent that implements cannot take input during a turn, or it is not implementing right now. The request keeps waiting.");
            return;
        }

        // The run itself reports the new requirement.
        _services.Database.RemoveFromQueue(entry.QueueId);
    }

    private async Task<(RunRecord Run, IsolatedWorkspace Workspace, Candidate Candidate)?> CurrentCandidateAsync(string? runId, CancellationToken cancellationToken)
    {
        if (FindRun(runId) is not { } run)
        {
            return null;
        }

        var candidate = run.CurrentCandidateId is null ? null : _services.Database.FindCandidate(run.CurrentCandidateId);
        if (candidate is null)
        {
            _ui.Say($"Run {run.RunId} has no candidate yet: nothing was frozen for checking.");
            return null;
        }

        var task = _services.Database.FindTask(run.TaskId);
        var workspace = task?.WorkspaceId is null ? null : await _services.Workspaces.FindAsync(task.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            _ui.Warn($"The isolated workspace of run {run.RunId} no longer exists.");
            return null;
        }

        return (run, workspace, candidate);
    }

    /// <summary>
    /// A literal replacement the user spelled out. YAV makes it in the isolated workspace without Model A;
    /// the result is reviewed by Model B and checked like any other change, and written by /apply only.
    /// </summary>
    private async Task ReplaceAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!RequireProject(out var project))
        {
            return;
        }

        // The first three words are the file, the text and the replacement, whatever they look like.
        var words = command.Arguments;
        var usage = "Usage: " + CommandCatalog.Find("replace")!.Usage;
        if (words.Count < 3)
        {
            _ui.Warn(usage);
            return;
        }

        int? count = null;
        var all = false;
        for (var i = 3; i < words.Count; i++)
        {
            switch (words[i].ToLowerInvariant())
            {
                case "--all":
                    all = true;
                    break;
                case "--count":
                    if (i + 1 >= words.Count || !int.TryParse(words[++i], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 1)
                    {
                        _ui.Warn("--count needs a number of 1 or more.");
                        return;
                    }

                    count = number;
                    break;
                default:
                    _ui.Warn(usage);
                    return;
            }
        }

        if (all && count is not null)
        {
            _ui.Warn("--count and --all contradict each other. Say how often the text occurs, or that every occurrence is meant.");
            return;
        }

        var (file, text, replacement) = (words[0], words[1], words[2]);
        if (text.Length == 0)
        {
            _ui.Warn("The text to replace is empty.");
            return;
        }

        if (string.Equals(text, replacement, StringComparison.Ordinal))
        {
            _ui.Warn("The replacement is the text that is there already. Nothing was started.");
            return;
        }

        if (!IsInside(project, file))
        {
            _ui.Warn($"'{file}' is not a file inside the project. /replace changes files of the project only, named from {project}.");
            return;
        }

        var expected = all ? (int?)null : count ?? 1;
        var where = expected switch
        {
            null => "wherever it occurs.",
            1 => "where it occurs exactly once.",
            _ => $"in exactly {expected} places.",
        };
        var edit = new MechanicalEditRequest(file, text, replacement, expected, AllOccurrences: expected != 1);
        await StartRunAsync($"Replace \"{text}\" with \"{replacement}\" in {file.Replace('\\', '/')}, {where}", _session.TaskId, edit, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True for a path that is written from the project and stays in it.</summary>
    private static bool IsInside(string project, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Contains(':'))
        {
            return false;
        }

        try
        {
            var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(project)) + Path.DirectorySeparatorChar;
            return Path.GetFullPath(Path.Combine(root, path)).StartsWith(root, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    /// <summary>What follows a word of the command, as one path. It may contain blanks and it may be quoted.</summary>
    private static string PathAfter(ParsedCommand command, string word)
    {
        var raw = command.RawArguments;
        var at = raw.IndexOf(word, StringComparison.OrdinalIgnoreCase);
        return at < 0 ? string.Empty : raw[(at + word.Length)..].Trim().Trim('"');
    }

    /// <summary>Writes a file the user asked for. A file that exists is never replaced.</summary>
    private async Task<bool> WriteNewFileAsync(string target, string text, CancellationToken cancellationToken)
    {
        if (File.Exists(target) || Directory.Exists(target))
        {
            _ui.Warn($"'{target}' exists already. It was not overwritten; name another file.");
            return false;
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            await File.WriteAllTextAsync(target, text, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            _ui.Error($"'{target}' could not be written: {ex.Message}");
            return false;
        }
    }

    private bool TryResolveFile(string path, out string target)
    {
        try
        {
            target = Path.GetFullPath(path, _session.ProjectPath ?? Environment.CurrentDirectory);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ui.Error($"'{path}' is not a path: {ex.Message}");
            target = string.Empty;
            return false;
        }
    }

    private async Task DiffAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var args = command.Arguments;
        var flags = args.Where(a => a.StartsWith("--", StringComparison.Ordinal)).Select(a => a.ToLowerInvariant()).ToList();
        var positional = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToList();
        var export = flags.Contains("--export");
        var runId = export ? null : positional.FirstOrDefault();
        if (await CurrentCandidateAsync(runId, cancellationToken).ConfigureAwait(false) is not { } current)
        {
            return;
        }

        var (run, workspace, candidate) = current;
        _ui.Say($"Run {run.RunId}, candidate {candidate.ShortFingerprint}: {candidate.Changes.Summary}, compared with the state the task started from", Tone.Accent);
        if (candidate.Changes.IsEmpty)
        {
            return;
        }

        if (flags.Contains("--stat"))
        {
            _ui.Table(
                ["Change", "File", "Before", "After"],
                candidate.Changes.Files.Select(f => (IReadOnlyList<string>)
                [
                    f.Kind.ToString().ToLowerInvariant() + (f.IsBinary ? " (binary)" : string.Empty),
                    f.Path + (f.RenamedFrom is null ? string.Empty : $"  (same content as deleted {f.RenamedFrom})"),
                    f.BaselineLength is null ? "-" : $"{f.BaselineLength:N0} B",
                    f.CandidateLength is null ? "-" : $"{f.CandidateLength:N0} B",
                ]));
            return;
        }

        var diff = await _services.Workspaces.DiffAsync(workspace, candidate, int.MaxValue, cancellationToken).ConfigureAwait(false);
        if (export)
        {
            var file = PathAfter(command, "--export");
            if (file.Length == 0)
            {
                _ui.Warn("Usage: /diff --export <file>");
                return;
            }

            if (TryResolveFile(file, out var target) && await WriteNewFileAsync(target, diff.Text, cancellationToken).ConfigureAwait(false))
            {
                _ui.Say($"The diff was written to {target} ({diff.Text.Length:N0} characters).");
            }

            return;
        }

        var lines = diff.Text.Split('\n');
        var shown = flags.Contains("--full") ? lines.Length : Math.Min(lines.Length, DiffLinesShown);
        _ui.Lines(lines.Take(shown).Select(DiffLine));
        if (shown < lines.Length)
        {
            _ui.Muted($"... {lines.Length - shown} more line(s). /diff --full shows all, /diff --export <file> writes them to a file. The complete diff: {diff.FullDiffPath}");
        }

        if (candidate.ProtectedPathsTouched.Count > 0)
        {
            _ui.Warn("Protected paths were changed: " + string.Join(", ", candidate.ProtectedPathsTouched) + ". Approve each with /review approve <path>.");
        }

        if (candidate.ExistingTestsTouched.Count > 0)
        {
            _ui.Warn("Existing tests were changed or deleted: " + string.Join(", ", candidate.ExistingTestsTouched));
        }
    }

    private static Line DiffLine(string text)
    {
        var line = text.TrimEnd('\r');
        var tone = line switch
        {
            _ when line.StartsWith("+++", StringComparison.Ordinal) || line.StartsWith("---", StringComparison.Ordinal) => Tone.Accent,
            _ when line.StartsWith('+') => Tone.Success,
            _ when line.StartsWith('-') => Tone.Error,
            _ when line.StartsWith("@@", StringComparison.Ordinal) => Tone.Accent,
            _ when line.StartsWith(' ') => Tone.Normal,
            _ => Tone.Muted,
        };

        // Every line of a diff starts with its own mark, so none of them can pass for a line of the application.
        return Line.Of(new Segment(line.Length == 0 ? " " : line, tone));
    }

    private async Task TestAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        if (!RequireProject(out var project))
        {
            return;
        }

        var args = command.Arguments;
        var verb = args.Count == 0 ? string.Empty : args[0].ToLowerInvariant();
        var state = _services.Validation.LoadConfiguration(project);
        switch (verb)
        {
            case "list":
                ListGates(state);
                return;

            case "detect":
                await DetectGatesAsync(project, state, cancellationToken).ConfigureAwait(false);
                return;

            case "trust":
                await TrustGatesAsync(project, state, cancellationToken).ConfigureAwait(false);
                return;

            case "waive":
            {
                if (args.Count < 3 || FindRun(null) is not { } run)
                {
                    _ui.Warn("Usage: /test waive <gate> <reason>. The reason is recorded with the run.");
                    return;
                }

                var reason = string.Join(' ', args.Skip(2));
                if (!state.Effective.Gates.Any(g => g.Id.Equals(args[1], StringComparison.OrdinalIgnoreCase)))
                {
                    _ui.Warn($"'{args[1]}' is not a check of this project. /test list shows them.");
                    return;
                }

                if (!Coordinator.WaiveGate(run.RunId, state.Effective.Gates.First(g => g.Id.Equals(args[1], StringComparison.OrdinalIgnoreCase)).Id, reason))
                {
                    _ui.Warn($"Run {run.RunId} has no candidate the waiver could apply to.");
                    return;
                }

                _ui.Say($"Recorded: check '{args[1]}' is waived for the current candidate of run {run.RunId}, because: {reason}", Tone.Warning);
                _ui.Muted($"It applies to this candidate only. /resume {run.RunId} evaluates the candidate again.");
                return;
            }
        }

        if (state.Effective.Gates.Count == 0)
        {
            _ui.Warn("No check is approved for this project. /test detect proposes some from what the project contains.");
            return;
        }

        if (verb.Length > 0)
        {
            var gate = state.Effective.Gates.FirstOrDefault(g => g.Id.Equals(verb, StringComparison.OrdinalIgnoreCase));
            if (gate is null)
            {
                _ui.Warn($"'{args[0]}' is neither a check of this project nor something /test does. /test list shows the checks.");
                return;
            }

            await RunGatesLocallyAsync(project, state, [gate], cancellationToken).ConfigureAwait(false);
            return;
        }

        var last = _session.LastRunId is null ? null : _services.Database.FindRun(_session.LastRunId);
        if (last is { CurrentCandidateId: not null } && last.State != RunState.Completed)
        {
            await SettleEarlierSessionAsync(cancellationToken).ConfigureAwait(false);
            var configuration = _services.Configuration;
            StartStoredRun(
                $"checks of {last.RunId}", last.RunId,
                (observer, approvals, stop) => Coordinator.RecheckAsync(last.RunId, RecheckScope.Tests, configuration, observer, approvals, stop));
            return;
        }

        await RunGatesLocallyAsync(project, state, state.Effective.RequiredGates.ToList(), cancellationToken).ConfigureAwait(false);
    }

    private void ListGates(ProjectConfigurationState state)
    {
        if (state.Effective.Gates.Count == 0)
        {
            _ui.Say("No check is approved for this project.");
        }
        else
        {
            _ui.Table(
                ["Check", "Kind", "Required", "Command", "Limit", "Needs"],
                state.Effective.Gates.Select(g => (IReadOnlyList<string>)
                [
                    g.Id, g.Kind.ToString(), g.Required ? "yes" : "no", g.DisplayCommand, $"{g.TimeoutSeconds}s",
                    g.Requires.Count == 0 ? "-" : string.Join(", ", g.Requires),
                ]));
            _ui.Muted($"Checks run in {(state.Effective.ValidationExecution == ProjectConfiguration.ExecutionCandidate ? "the candidate workspace, which is verified afterwards" : "a disposable copy of the candidate")}.");
        }

        switch (state.Trust)
        {
            case ConfigurationTrust.Untrusted:
                _ui.Warn($"{state.FilePath} is not approved. Nothing of it is used. /test trust shows it and asks.");
                break;
            case ConfigurationTrust.Changed:
                _ui.Warn($"{state.FilePath} differs from what you approved. The approved version above applies. /test trust shows the file and asks.");
                break;
            case ConfigurationTrust.Invalid:
                _ui.Warn($"{state.FilePath} cannot be used: {string.Join(" ", state.Errors)}");
                break;
        }
    }

    private void ShowConfiguration(ProjectConfiguration configuration)
    {
        _ui.Table(
            ["Check", "Kind", "Required", "Command", "Limit"],
            configuration.Gates.Select(g => (IReadOnlyList<string>)[g.Id, g.Kind.ToString(), g.Required ? "yes" : "no", g.DisplayCommand, $"{g.TimeoutSeconds}s"]));
        if (configuration.Prepare.Count > 0)
        {
            _ui.Say("Run once in a new workspace, before Model A:");
            foreach (var prepare in configuration.Prepare)
            {
                _ui.Say("  " + prepare.DisplayCommand);
            }
        }

        var pairs = new List<(string, string)>();
        if (configuration.ProtectedPaths.Count > 0)
        {
            pairs.Add(("Protected paths", string.Join(", ", configuration.ProtectedPaths)));
        }

        if (configuration.ReplicateIgnored.Count > 0)
        {
            pairs.Add(("Ignored files copied", string.Join(", ", configuration.ReplicateIgnored)));
        }

        if (configuration.AllowSecrets.Count > 0)
        {
            pairs.Add(("Copied although they look like secrets", string.Join(", ", configuration.AllowSecrets)));
        }

        pairs.Add(("Checks run in", configuration.ValidationExecution));
        _ui.Pairs([.. pairs]);
    }

    /// <summary>Proposes checks from what the project contains and approves them when the user confirms. True when they were approved.</summary>
    private async Task<bool> DetectGatesAsync(string project, ProjectConfigurationState state, CancellationToken cancellationToken)
    {
        var proposals = _services.Validation.Detect(project);
        if (proposals.Count == 0)
        {
            _ui.Say($"Nothing in the project's root tells which checks it has. Write them into {_services.Validation.ConfigurationFileName}; docs\\user-guide.md shows the format.");
            return false;
        }

        _ui.Say("Proposed from what the project contains. Nothing runs until you approve it:", Tone.Accent);
        _ui.Table(["Check", "Command", "Because"], proposals.Select(p => (IReadOnlyList<string>)[p.Gate.Id, p.Gate.DisplayCommand, p.Reason]));
        if (state.Effective.Gates.Count > 0)
        {
            _ui.Warn("This project has approved checks already. Approving the proposal replaces them.");
        }

        if (!await ConfirmAsync("Approve these commands as the required checks of this project?", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var configuration = state.Effective with { Gates = proposals.Select(p => p.Gate).ToList() };
        var json = _services.Validation.Serialize(configuration);
        _services.Validation.TrustConfiguration(project, configuration, json);
        var file = Path.Combine(project, _services.Validation.ConfigurationFileName);
        if (!File.Exists(file))
        {
            await File.WriteAllTextAsync(file, json, new System.Text.UTF8Encoding(false), cancellationToken).ConfigureAwait(false);
            _ui.Say($"Approved, and written to {file} so that it can be kept with the project.");
        }
        else
        {
            _ui.Say($"Approved. {file} was left as it is; /test trust compares it with what is approved.");
        }

        return true;
    }

    /// <summary>Shows the project's configuration the user has not approved, and approves it when they confirm. True when it was approved.</summary>
    private async Task<bool> TrustGatesAsync(string project, ProjectConfigurationState state, CancellationToken cancellationToken)
    {
        if (state.Trust == ConfigurationTrust.Invalid)
        {
            _ui.Warn($"{state.FilePath} cannot be used: {string.Join(" ", state.Errors)}");
            return false;
        }

        if (state.Pending is not { } pending)
        {
            _ui.Say(state.Trust == ConfigurationTrust.Trusted
                ? "The checks of this project are approved and the file has not changed since."
                : $"This project has no {_services.Validation.ConfigurationFileName}. /test detect proposes checks.");
            return false;
        }

        _ui.Say($"{state.FilePath} says:", Tone.Accent);
        ShowConfiguration(pending);
        if (state.Trust == ConfigurationTrust.Changed)
        {
            _ui.Say("Approved so far:", Tone.Accent);
            ShowConfiguration(state.Effective);
        }

        _ui.Warn("These commands run on your machine with your rights whenever a candidate is checked. Approve them only when you know what they do.");
        if (!await ConfirmAsync("Approve this configuration?", cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        _services.Validation.TrustConfiguration(project, pending, _services.Validation.Serialize(pending));
        _ui.Success("Approved. It applies from the next run; results of earlier checks are stale now, because the configuration they were produced with has changed.");
        return true;
    }

    private async Task RunGatesLocallyAsync(string project, ProjectConfigurationState state, IReadOnlyList<GateDefinition> gates, CancellationToken cancellationToken)
    {
        _ui.Note(Stages.Local, $"Running {gates.Count} check(s) in the project itself, as you. This is not evidence for a candidate.");
        var evidence = Path.Combine(_services.Paths.Logs, "local-checks");
        var binding = new EvidenceBinding("project", 0, "local", _services.Validation.ComputeEnvironmentFingerprint(gates, state.TrustedHash));
        foreach (var gate in gates)
        {
            _ui.Say($"  $ {gate.Title}: {gate.DisplayCommand}", Tone.Muted);
            var result = await _services.Validation.RunGateAsync(
                gate,
                new GateRunContext("local", project, binding, evidence, IsBaselineRun: false),
                _session.Verbose ? line => _ui.Quote(line, Tone.Muted) : null,
                cancellationToken).ConfigureAwait(false);
            _ui.Note(
                Stages.Tests,
                $"{gate.Title}: {result.Status}"
                + (result.ExitCode is null ? string.Empty : $" (exit {result.ExitCode})")
                + $" in {Describe(TimeSpan.FromMilliseconds(result.DurationMs))}"
                + (result.Limitation is null ? string.Empty : $". {result.Limitation}"),
                result.Status == GateStatus.Passed ? NoteLevel.Success : NoteLevel.Warning);
            if (result.Status != GateStatus.Passed && result.OutputTail.Length > 0)
            {
                _ui.Quote(string.Join('\n', result.OutputTail.TrimEnd().Split('\n').TakeLast(15)), Tone.Muted);
                _ui.Muted($"  The complete output: {result.OutputPath}");
            }
        }
    }

    private async Task ReviewAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var args = command.Arguments;
        var verb = args.Count == 0 ? string.Empty : args[0].ToLowerInvariant();
        if (FindRun(null) is not { } run)
        {
            return;
        }

        switch (verb)
        {
            case "show":
            {
                var candidate = run.CurrentCandidateId is null ? null : _services.Database.FindCandidate(run.CurrentCandidateId);
                var review = candidate is null ? null : _services.Database.GetLatestReview(run.RunId, candidate.CandidateId);
                if (review is null)
                {
                    _ui.Say($"Run {run.RunId} has no review of its current candidate.");
                    return;
                }

                _ui.Say($"Review of candidate {candidate!.ShortFingerprint} by {review.ReviewerModel} ({review.ReviewerAdapterId}): {RunPublisherText.Describe(review)}", Tone.Accent);
                // In full: this is where a review is read. What the reviewer wrote stands behind the gutter.
                foreach (var (title, written) in new[] { ("Summary", review.Summary), ("What was inspected", review.Coverage) })
                {
                    if (!string.IsNullOrWhiteSpace(written))
                    {
                        _ui.Muted("  " + title);
                        _ui.Quote(written);
                    }
                }

                _ui.Lines(new RunEventFormatter(new FormatterOptions(_screen.Options.Unicode, false)).Format(new ReviewCompleted(run.RunId, review.CompletedAt, review)));
                foreach (var finding in review.BlockingFindings)
                {
                    _ui.Say($"  {finding.FindingId}  {finding.Location}", Tone.Warning);
                    _ui.Quote($"Evidence: {finding.Evidence}" + (finding.SuggestedCorrection is null ? string.Empty : $"\nSuggested: {finding.SuggestedCorrection}"), Tone.Muted);
                }

                foreach (var error in review.ValidationErrors)
                {
                    _ui.Warn("  " + error);
                }

                _ui.Muted("A passing review does not say that the required checks passed. /status shows both.");
                return;
            }

            case "approve":
            {
                var (path, _) = SplitPath(command with { RawArguments = command.RawArguments[(command.RawArguments.IndexOf("approve", StringComparison.OrdinalIgnoreCase) + 7)..] });
                if (path.Length == 0)
                {
                    _ui.Warn("Usage: /review approve <path>");
                    return;
                }

                if (Coordinator.ApproveProtectedPath(run.RunId, path.Replace('\\', '/')))
                {
                    _ui.Say($"Recorded: the change to the protected path '{path}' is approved for the current candidate of run {run.RunId}.", Tone.Warning);
                    _ui.Muted($"/resume {run.RunId} evaluates the candidate again.");
                }
                else
                {
                    _ui.Warn($"'{path}' is not a protected path that the current candidate of run {run.RunId} changes. /diff names them.");
                }

                return;
            }

            case "":
                break;

            default:
                _ui.Warn("Usage: /review [show | approve <path>]");
                return;
        }

        if (run.CurrentCandidateId is null)
        {
            _ui.Say($"Run {run.RunId} has no candidate to review yet.");
            return;
        }

        await SettleEarlierSessionAsync(cancellationToken).ConfigureAwait(false);
        var configuration = _services.Configuration;
        StartStoredRun(
            $"review of {run.RunId}", run.RunId,
            (observer, approvals, stop) => Coordinator.RecheckAsync(run.RunId, RecheckScope.Review, configuration, observer, approvals, stop));
    }

    private async Task ApplyAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var merge = args.Any(a => a.Equals("--merge", StringComparison.OrdinalIgnoreCase));
        if (FindRun(args.FirstOrDefault(a => !a.StartsWith("--", StringComparison.Ordinal))) is not { } run)
        {
            return;
        }

        await SettleEarlierSessionAsync(cancellationToken).ConfigureAwait(false);
        if (merge)
        {
            var configuration = _services.Configuration;
            _session.LastRunId = run.RunId;
            StartStoredRun(
                $"merge for {run.RunId}", run.RunId,
                (observer, approvals, stop) => Coordinator.MergeAndRecheckAsync(run.RunId, configuration, observer, approvals, stop));
            return;
        }

        var delivery = await Coordinator.ApplyAsync(run.RunId, CommandObserver(), cancellationToken).ConfigureAwait(false);
        if (delivery.Succeeded)
        {
            _session.LastRunId = run.RunId;
            _session.TaskId = run.TaskId;
            return;
        }

        if (delivery.Conflicts.Count == 0 && delivery.Decision is null)
        {
            _ui.Warn(delivery.Message);
        }
    }

    private async Task DiscardAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (FindRun(args.FirstOrDefault()) is not { } run)
        {
            return;
        }

        var candidate = run.CurrentCandidateId is null ? null : _services.Database.FindCandidate(run.CurrentCandidateId);
        _ui.Say($"Run {run.RunId}: \"{Shorten(run.RequestText, 70)}\"" + (candidate is null ? string.Empty : $", {candidate.Changes.Summary}"));
        if (!await ConfirmAsync("Remove the isolated workspace of this task and what it changed there? The project is not touched.", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var delivery = await Coordinator.DiscardAsync(run.RunId, CommandObserver(), cancellationToken).ConfigureAwait(false);
        if (!delivery.Succeeded)
        {
            _ui.Warn(delivery.Message);
            return;
        }

        if (_session.TaskId == run.TaskId)
        {
            _session.TaskId = null;
            _session.LastRunId = null;
        }
    }

    private async Task UndoAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        if (!RequireProject(out var project))
        {
            return;
        }

        await SettleEarlierSessionAsync(cancellationToken).ConfigureAwait(false);
        var skip = args.Any(a => a.Equals("--skip-edited", StringComparison.OrdinalIgnoreCase));
        var outcome = await Coordinator.UndoAsync(project, skip, CommandObserver(), cancellationToken).ConfigureAwait(false);
        if (!outcome.Succeeded && outcome.Conflicts.Count == 0)
        {
            _ui.Say(outcome.Message);
        }
    }
}
