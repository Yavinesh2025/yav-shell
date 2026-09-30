using System.Globalization;
using Yav.Console.Rendering;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Core.Timing;

namespace Yav.Console.Shell;

public sealed partial class InteractiveShell
{
    private const string Unavailable = "Unavailable";

    private async Task HistoryAsync(Yav.Console.Commands.ParsedCommand command, CancellationToken cancellationToken)
    {
        var args = command.Arguments;
        var verb = args.Count == 0 ? string.Empty : args[0].ToLowerInvariant();
        switch (verb)
        {
            case "":
            {
                var runs = _services.Database.ListRuns(_session.ProjectPath, 25);
                if (runs.Count == 0)
                {
                    _ui.Say(_session.ProjectPath is null ? "No run is stored." : "No run is stored for this project.");
                    return;
                }

                _ui.Table(
                    ["Run", "When", "State", "Models", "Request"],
                    runs.Select(r => (IReadOnlyList<string>)
                    [
                        r.RunId,
                        r.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture),
                        RunStateMachine.Display(r.State) + (r.Disposition == RunDisposition.Pending ? string.Empty : $" ({r.Disposition})"),
                        $"{r.ModelA ?? "?"} / {r.ModelB ?? "?"}",
                        Shorten(r.RequestText, 50),
                    ]));
                _ui.Muted("/history <run-id> shows what happened in a run. /history export <run-id> <file>, /history delete <run-id>, /history prune.");
                return;
            }

            case "export":
            {
                // The run, then the file. The file may contain blanks.
                var file = args.Count < 3 ? string.Empty : PathAfter(command, args[1]);
                if (file.Length == 0 || FindRun(args[1]) is not { } exported)
                {
                    _ui.Warn("Usage: /history export <run-id> <file>");
                    return;
                }

                await ExportAsync(exported, file, cancellationToken).ConfigureAwait(false);
                return;
            }

            case "delete":
            {
                if (args.Count != 2 || FindRun(args[1]) is not { } doomed)
                {
                    _ui.Warn("Usage: /history delete <run-id>");
                    return;
                }

                if (doomed.State == RunState.ReadyToApply)
                {
                    _ui.Warn($"Run {doomed.RunId} is ready and was not applied. Apply it or /discard {doomed.RunId} first.");
                    return;
                }

                if (!await ConfirmAsync($"Delete everything YAV stored about run {doomed.RunId}? The project is not touched.", cancellationToken).ConfigureAwait(false))
                {
                    return;
                }

                _services.Database.DeleteRun(doomed.RunId);
                if (_session.LastRunId == doomed.RunId)
                {
                    _session.LastRunId = null;
                }

                _ui.Say($"Run {doomed.RunId} was deleted from the history.");
                return;
            }

            case "prune":
            {
                var days = _services.Settings.Retention.KeepRunsDays;
                var report = _services.Database.ApplyRetention(TimeSpan.FromDays(days), _services.Clock.GetUtcNow());
                _ui.Say($"Removed {report.RunsRemoved} run(s) and {report.EventsRemoved} event(s) older than {days} days. Runs that are ready to apply or need attention are kept.");
                return;
            }
        }

        if (FindRun(args[0]) is not { } run)
        {
            return;
        }

        DescribeRun(run);
        var events = _services.Database.GetEvents(run.RunId, 400);
        _ui.Blank();
        foreach (var entry in events.Where(e => e.Type is not ("agent.configured" or "state")))
        {
            var time = entry.At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var fromAgent = entry.Type.StartsWith("agent.", StringComparison.Ordinal);
            _ui.Lines(
            [
                fromAgent
                    ? Line.Of(new Segment(time + " ", Tone.Muted), new Segment(_ui.Unicode ? "  │ " : "  | ", Tone.Muted), new Segment(entry.Summary))
                    : Line.Of(new Segment(time + " ", Tone.Muted), new Segment(RunEventFormatter.StageLabel(entry.Stage), Tone.Stage, Bold: true), new Segment(entry.Summary)),
            ]);
        }

        _ui.Muted($"{events.Count} event(s). Complete output of checks and the complete diff are files in the run's evidence directory; /history export writes everything to one file.");
    }

    private async Task ExportAsync(RunRecord run, string file, CancellationToken cancellationToken)
    {
        if (!TryResolveFile(file, out var target))
        {
            return;
        }

        var text = new System.Text.StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"YAV Shell {Yav.Console.Composition.AppServices.Version} - run {run.RunId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"Project:  {run.ProjectPath}");
        text.AppendLine(CultureInfo.InvariantCulture, $"State:    {RunStateMachine.Display(run.State)} ({run.Disposition})");
        text.AppendLine(CultureInfo.InvariantCulture, $"Started:  {run.CreatedAt:O}");
        text.AppendLine();
        text.AppendLine("Request:");
        text.AppendLine(Yav.Core.Text.TerminalSanitizer.Clean(run.RequestText));
        text.AppendLine();

        text.AppendLine("Settings (requested / in effect / status / reported by):");
        foreach (var c in _services.Database.GetConfirmations(run.RunId))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {(c.Role == AgentRole.Implementer ? "A" : "B")} {c.Setting}: {c.Requested} / {c.Effective ?? "not reported"} / {c.Status} / {c.Source}");
        }

        text.AppendLine();
        text.AppendLine("Usage:");
        foreach (var u in _services.Database.GetUsage(run.RunId, null))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"  {(u.Role == AgentRole.Implementer ? "A" : "B")} {u.Model}: {Tokens(u.Tokens.Total)} tokens, {u.Turns} turn(s), route {u.BillingRoute}, source {u.Source}");
        }

        text.AppendLine();
        text.AppendLine("Events:");
        foreach (var entry in _services.Database.GetEvents(run.RunId, 100_000))
        {
            text.AppendLine(CultureInfo.InvariantCulture, $"{entry.At:O} [{entry.Stage}] {entry.Type}: {entry.Summary}");
            if (!string.IsNullOrEmpty(entry.Detail))
            {
                foreach (var line in entry.Detail.Split('\n'))
                {
                    text.AppendLine("    " + line.TrimEnd('\r'));
                }
            }
        }

        if (!await WriteNewFileAsync(target, text.ToString(), cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        _ui.Say($"Run {run.RunId} was written to {target}.");
        _ui.Muted("It contains your request and what the agents said. Credentials are never part of the history. Read it before you pass it on.");
    }

    private static string Tokens(long? value) => value is null ? Unavailable : value.Value.ToString("N0", CultureInfo.InvariantCulture);

    private void Usage(IReadOnlyList<string> args)
    {
        var all = args.Count > 0 && args[0].Equals("all", StringComparison.OrdinalIgnoreCase);
        IReadOnlyList<UsageRecord> records;
        string title;
        if (all)
        {
            records = _services.Database.GetUsage(null, _services.Clock.GetUtcNow().AddDays(-30));
            title = "Usage of the last 30 days, all projects";
        }
        else
        {
            if (FindRun(args.FirstOrDefault()) is not { } run)
            {
                return;
            }

            records = _services.Database.GetUsage(run.RunId, null);
            title = $"Usage of run {run.RunId}";
        }

        _ui.Heading(title);
        if (records.Count == 0)
        {
            _ui.Say("No turn of a model was recorded.");
            return;
        }

        _ui.Table(
            ["Role", "Model", "Input", "Cache read", "Cache write", "Output", "of it reasoning", "Total", "Turns", "Retries", "Charge"],
            records
                .GroupBy(r => (r.Role, r.Model, r.AdapterId))
                .Select(g =>
                {
                    var tokens = g.Aggregate(TokenCounts.Unavailable, (sum, r) => sum + r.Tokens);
                    var costs = g.Where(r => r.ProviderCostUsd is not null).ToList();
                    var charge = costs.Count == 0
                        ? Unavailable
                        : string.Create(CultureInfo.InvariantCulture, $"${costs.Sum(r => r.ProviderCostUsd!.Value):0.0000} ({costs[0].CostProvenance.ToString().ToLowerInvariant()})");
                    return (IReadOnlyList<string>)
                    [
                        g.Key.Role == AgentRole.Implementer ? "A" : "B",
                        g.Key.Model ?? "?",
                        Tokens(tokens.UncachedInput), Tokens(tokens.CacheRead), Tokens(tokens.CacheWrite), Tokens(tokens.Output),
                        Tokens(tokens.ReasoningWithinOutput), Tokens(tokens.Total),
                        g.Sum(r => r.Turns).ToString(CultureInfo.InvariantCulture),
                        g.Sum(r => r.Retries).ToString(CultureInfo.InvariantCulture),
                        charge,
                    ];
                }));

        var known = records.Where(r => r.Tokens.Total is not null).ToList();
        _ui.Pairs(
            ("Both roles together", known.Count == 0 ? Unavailable : Tokens(known.Sum(r => r.Tokens.Total!.Value)) + " tokens"
                + (known.Count < records.Count ? " (without the roles that reported nothing)" : string.Empty)),
            ("Billing routes", string.Join("; ", records.Select(r => $"{(r.Role == AgentRole.Implementer ? "A" : "B")}: {r.BillingRoute}").Distinct())),
            ("Reported by", string.Join("; ", records.Select(r => r.Source).Distinct())),
            ("Last reading", records.Max(r => r.ObservedAt).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)));

        if (records.Any(r => !r.RunShareKnown))
        {
            _ui.Warn("For a conversation that was continued, the provider reported only a running total, and what it had used before is not known. Its share of this run is Unavailable.");
        }

        _ui.Muted("Input excludes cache reads and writes; reasoning is part of output. Nothing is counted twice. What a provider did not report is Unavailable, never 0.");
        _ui.Muted("Usage of a subscription is counted against its limits; YAV does not turn it into an amount of money. A charge is shown only where the agent reported one.");
    }

    private void Latency(IReadOnlyList<string> args)
    {
        // Read before anything is written, so that the answer to this command is not among what it reports.
        var answers = _services.Database.GetSpans(null)
            .Where(s => s.Kind == SpanKind.LocalCommand && s.Duration is not null)
            .TakeLast(200).Select(s => s.Duration!.Value).OrderBy(d => d).ToList();

        var startups = _services.Database.GetSpans(null).Where(s => s.Kind == SpanKind.ConsoleStartup && s.Duration is not null).ToList();
        if (startups.Count > 0)
        {
            var durations = startups.Select(s => s.Duration!.Value).OrderBy(d => d).ToList();
            _ui.Heading("Console");
            _ui.Pairs(
                ("Start to prompt, this time", Describe(startups[^1].Duration!.Value)),
                ("Median of the last " + durations.Count, Describe(durations[durations.Count / 2])),
                ("Slowest", Describe(durations[^1])),
                ("Development target", "1 s on the reference machine; a target, not a promise"));
        }

        if (answers.Count > 0)
        {
            _ui.Heading("Commands");
            _ui.Pairs(
                ($"Answered, median of the last {answers.Count}", Describe(answers[answers.Count / 2])),
                ("Slowest", Describe(answers[^1])),
                ("Development target", "200 ms; a target, not a promise"));
        }

        var runId = args.FirstOrDefault() ?? _session.LastRunId;
        if (runId is null || _services.Database.FindRun(runId) is not { } run)
        {
            if (runId is not null)
            {
                _ui.Warn($"Run '{runId}' does not exist.");
            }

            return;
        }

        var spans = _services.Database.GetSpans(run.RunId);
        if (spans.Count == 0)
        {
            _ui.Say($"No time was measured for run {run.RunId}.");
            return;
        }

        var events = _services.Database.GetEvents(run.RunId, 100_000);
        var firstAction = events.FirstOrDefault(e => e.Type.StartsWith("agent.", StringComparison.Ordinal) && e.Type is not "agent.configured")?.At;
        var accepted = events.LastOrDefault(e => e.Type == "acceptance" && e.Summary.Contains("accepted", StringComparison.Ordinal) && !e.Summary.Contains("not accepted", StringComparison.Ordinal))?.At;
        var ended = run.State is RunState.Implementing or RunState.Checking or RunState.Repairing or RunState.Preparing or RunState.AwaitingApproval
            ? (DateTimeOffset?)null
            : spans.Where(s => s.EndedAt is not null).Max(s => s.EndedAt);
        var report = TimingMath.Report(spans, run.CreatedAt, ended, firstAction, accepted);

        _ui.Blank();
        _ui.Heading($"Run {run.RunId}");
        _ui.Pairs(
            ("Elapsed", report.Elapsed is null ? Unavailable : Describe(report.Elapsed.Value)),
            ("of it waiting for you", Describe(report.ApprovalWaiting)),
            ("Active", report.ActiveTime is null ? Unavailable : Describe(report.ActiveTime.Value)),
            ("of it in tools the agents ran", Describe(report.InTools)),
            ("Not part of any stage", report.BetweenStages is null ? Unavailable : Describe(report.BetweenStages.Value) + ", between the stages: keeping records and reporting"),
            ("Until the first action of an agent", report.TimeToFirstAction is null ? Unavailable : Describe(report.TimeToFirstAction.Value)),
            ("Until an accepted result", report.TimeToAcceptedResult is null ? "no candidate was accepted" : Describe(report.TimeToAcceptedResult.Value)),
            ("Took longest", report.Bottleneck is { } longest ? Name(longest) : Unavailable));

        _ui.Table(
            ["Stage", "Times", "Elapsed", "Work", "Note"],
            report.ByKind.Select(k => (IReadOnlyList<string>)
            [
                Name(k.Kind),
                k.Count.ToString(CultureInfo.InvariantCulture),
                Describe(k.WallClock),
                Describe(k.Work),
                k.Kind switch
                {
                    SpanKind.Implementation or SpanKind.Review or SpanKind.Repair => "provider time and tools",
                    SpanKind.ToolActivity => "within the turns, from when an agent reports the start of a tool to when it reports its end",
                    _ => string.Empty,
                },
            ]));
        _ui.Muted("Provider time is what is left of a turn without its tools: waiting, reasoning and answering are not told apart.");

        var groups = spans.Where(s => s.ParallelGroup is not null && s.Duration is not null).GroupBy(s => s.ParallelGroup).Where(g => g.Count() > 1).ToList();
        foreach (var group in groups)
        {
            var wall = TimingMath.WallClock(group);
            var work = TimingMath.Work(group);
            _ui.Muted($"  {string.Join(" and ", group.Select(s => Name(s.Kind).ToLowerInvariant()))} ran side by side: {Describe(work)} of work in {Describe(wall)}.");
        }

        _ui.Muted("Stages that overlap are not added up as elapsed time. These are measurements of this run, not a forecast for another.");
    }

    private static string Name(SpanKind kind) => kind switch
    {
        SpanKind.ConsoleStartup => "Console start",
        SpanKind.LocalPreparation => "Preparation",
        SpanKind.AgentInitialization => "Starting the agents",
        SpanKind.Implementation => "Implementation (Model A)",
        SpanKind.ToolActivity => "Tools",
        SpanKind.Review => "Review (Model B)",
        SpanKind.Tests => "Required checks",
        SpanKind.ApprovalWaiting => "Waiting for you",
        SpanKind.Repair => "Repair (Model A)",
        SpanKind.Apply => "Apply",
        SpanKind.Freeze => "Freezing the candidate",
        SpanKind.Acceptance => "Evaluating the evidence",
        _ => "Local command",
    };

    private void Limits(IReadOnlyList<string> args)
    {
        var limits = _services.Settings.Limits;
        if (args.Count == 0)
        {
            _ui.Heading("Limits of a run");
            _ui.Pairs(
                ("Elapsed time", limits.MaxElapsedMinutes == 0 ? "no limit" : $"{limits.MaxElapsedMinutes} minutes"),
                ("Repair cycles", $"{limits.MaxRepairCycles} after the first candidate"),
                ("Tokens", limits.MaxRunTokens == 0 ? "no limit" : limits.MaxRunTokens.ToString("N0", CultureInfo.InvariantCulture)),
                ("Rate limit", limits.StopAtRateLimitPercent == 0 ? "no limit" : $"no new turn from {limits.StopAtRateLimitPercent}% of the provider's window"),
                ("Waiting requests", limits.MaxQueueLength.ToString(CultureInfo.InvariantCulture)));
            _ui.Muted("Set with /limits minutes|repairs|tokens|ratelimit|queue <n>; 0 means no limit, except for repairs and queue.");
            _ui.Muted("A limit keeps YAV from starting another turn. A turn that is running is not cut off, and usage a provider reports late can exceed the limit.");
            _ui.Muted("A limit is a threshold YAV watches. It is not a cap on what a provider charges.");
            return;
        }

        if (args.Count != 2 || !long.TryParse(args[1], NumberStyles.None, CultureInfo.InvariantCulture, out var value))
        {
            _ui.Warn("Usage: /limits minutes|repairs|tokens|ratelimit|queue <n>");
            return;
        }

        LimitSettings? changed = args[0].ToLowerInvariant() switch
        {
            "minutes" when value <= 7 * 24 * 60 => limits with { MaxElapsedMinutes = (int)value },
            "repairs" when value <= 10 => limits with { MaxRepairCycles = (int)value },
            "tokens" => limits with { MaxRunTokens = value },
            "ratelimit" when value <= 100 => limits with { StopAtRateLimitPercent = (int)value },
            "queue" when value <= 1000 => limits with { MaxQueueLength = (int)value },
            _ => null,
        };
        if (changed is null)
        {
            _ui.Warn("That value is outside what is allowed: minutes up to 10080, repairs up to 10, ratelimit up to 100, queue up to 1000.");
            return;
        }

        Save(s => s with { Limits = changed });
        _ui.Say($"Limit '{args[0].ToLowerInvariant()}' is {(value == 0 && args[0] is not ("repairs" or "queue") ? "off" : value.ToString("N0", CultureInfo.InvariantCulture))}.");
        _ui.Muted("This applies to the next run, and to a run that is continued with /resume.");
    }
}
