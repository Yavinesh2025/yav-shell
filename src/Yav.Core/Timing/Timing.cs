namespace Yav.Core.Timing;

public enum SpanKind
{
    ConsoleStartup,
    LocalPreparation,
    AgentInitialization,
    Implementation,
    ToolActivity,
    Review,
    Tests,
    ApprovalWaiting,
    Repair,
    Apply,
    Freeze,
    LocalCommand,

    // Deciding whether a candidate is accepted: the evidence, and whether the repository was left alone.
    Acceptance,
}

/// <summary>
/// A measured interval. Durations come from a monotonic clock; the wall-clock start is kept for display. A span
/// that is given the times it began and ended, as a tool is by the reports of its agent, lasts from one to the other.
/// </summary>
public sealed record TimingSpan(
    string SpanId,
    string? RunId,
    SpanKind Kind,
    string Label,
    DateTimeOffset StartedAt,
    TimeSpan? Duration,
    // Spans that ran at the same time share a group, so they are never added up as elapsed time.
    string? ParallelGroup)
{
    public DateTimeOffset? EndedAt => Duration is null ? null : StartedAt + Duration.Value;
}

public sealed class TimingRecorder(TimeProvider clock, string? runId)
{
    private readonly Lock _gate = new();
    private readonly List<TimingSpan> _completed = [];
    // Before: how long the span had been going when it was started, for one that is given the time it began.
    private readonly Dictionary<string, (TimingSpan Span, long StartTimestamp, TimeSpan Before)> _open = [];

    public TimeProvider Clock => clock;

    public string? RunId => runId;

    public event Action<TimingSpan>? SpanCompleted;

    /// <param name="startedAt">
    /// When the span began, if that was before now: when an agent reported the start of a tool, for one that is
    /// taken up only now. Null, or a time still to come, begins the span now.
    /// </param>
    public SpanScope Start(SpanKind kind, string label, string? parallelGroup = null, DateTimeOffset? startedAt = null)
    {
        var now = clock.GetUtcNow();
        var began = startedAt is { } given && given < now ? given : now;
        var span = new TimingSpan(Ids.NewId("s"), runId, kind, label, began, null, parallelGroup);
        lock (_gate)
        {
            _open[span.SpanId] = (span, clock.GetTimestamp(), now - began);
        }

        return new SpanScope(this, span.SpanId);
    }

    public IReadOnlyList<TimingSpan> Completed
    {
        get
        {
            lock (_gate)
            {
                return _completed.ToArray();
            }
        }
    }

    internal void End(string spanId, DateTimeOffset? endedAt = null)
    {
        TimingSpan finished;
        lock (_gate)
        {
            if (!_open.Remove(spanId, out var entry))
            {
                return;
            }

            var measured = entry.Before + clock.GetElapsedTime(entry.StartTimestamp);

            // An end that is given is taken when the span can have ended then: not before it began, and not after now.
            var given = endedAt - entry.Span.StartedAt;
            finished = entry.Span with { Duration = given >= TimeSpan.Zero && given <= measured ? given : measured };
            _completed.Add(finished);
        }

        SpanCompleted?.Invoke(finished);
    }

    public readonly struct SpanScope(TimingRecorder owner, string spanId) : IDisposable
    {
        public void Dispose() => owner.End(spanId);

        /// <summary>
        /// Ends the span at a time that has passed: when an agent reported the end of a tool, for one that is taken
        /// up only now. A time before the span began, or one still to come, is not taken; the span ends now.
        /// </summary>
        public void End(DateTimeOffset endedAt) => owner.End(spanId, endedAt);
    }
}

public sealed record KindTotal(SpanKind Kind, int Count, TimeSpan Work, TimeSpan WallClock);

public sealed record LatencyReport(
    TimeSpan? Elapsed,
    TimeSpan? ActiveTime,
    TimeSpan ApprovalWaiting,
    TimeSpan? TimeToFirstAction,
    TimeSpan? TimeToAcceptedResult,
    IReadOnlyList<KindTotal> ByKind,
    SpanKind? Bottleneck)
{
    /// <summary>Time of the run that no stage accounts for. It is shown, so that the stages never look like the whole.</summary>
    public TimeSpan? BetweenStages { get; init; }

    /// <summary>
    /// The time the agents spent in tools they ran, such as commands. It lies within their turns; what is
    /// left of a turn is the time of the provider, which is not told apart any further.
    /// </summary>
    public TimeSpan InTools { get; init; }
}

public static class TimingMath
{
    /// <summary>
    /// Wall-clock time covered by the spans. Overlapping spans are merged first, so two checks that ran
    /// side by side for a minute count as one minute, not two.
    /// </summary>
    public static TimeSpan WallClock(IEnumerable<TimingSpan> spans)
    {
        var intervals = spans
            .Where(s => s.Duration is not null)
            .Select(s => (Start: s.StartedAt, End: s.StartedAt + s.Duration!.Value))
            .OrderBy(i => i.Start)
            .ToList();

        var total = TimeSpan.Zero;
        DateTimeOffset? currentStart = null;
        DateTimeOffset currentEnd = default;
        foreach (var (start, end) in intervals)
        {
            if (currentStart is null)
            {
                currentStart = start;
                currentEnd = end;
            }
            else if (start <= currentEnd)
            {
                if (end > currentEnd)
                {
                    currentEnd = end;
                }
            }
            else
            {
                total += currentEnd - currentStart.Value;
                currentStart = start;
                currentEnd = end;
            }
        }

        if (currentStart is not null)
        {
            total += currentEnd - currentStart.Value;
        }

        return total;
    }

    /// <summary>Sum of the span durations. This is work performed, not elapsed time.</summary>
    public static TimeSpan Work(IEnumerable<TimingSpan> spans) =>
        spans.Aggregate(TimeSpan.Zero, (sum, span) => sum + (span.Duration ?? TimeSpan.Zero));

    public static LatencyReport Report(
        IReadOnlyList<TimingSpan> spans,
        DateTimeOffset? runStarted,
        DateTimeOffset? runEnded,
        DateTimeOffset? firstAction,
        DateTimeOffset? accepted)
    {
        var byKind = spans
            .Where(s => s.Duration is not null)
            .GroupBy(s => s.Kind)
            .Select(g => new KindTotal(g.Key, g.Count(), Work(g), WallClock(g)))
            .OrderByDescending(k => k.WallClock)
            .ToList();

        var waiting = WallClock(spans.Where(s => s.Kind == SpanKind.ApprovalWaiting));

        // A run begins with the first thing that was measured for it. The project and the agents are
        // checked before the run is recorded, and that time belongs to the run.
        var ofTheRun = spans.Where(s => s.Kind != SpanKind.ConsoleStartup).ToList();
        if (runStarted is not null && ofTheRun.Count > 0 && ofTheRun.Min(s => s.StartedAt) is var first && first < runStarted)
        {
            runStarted = first;
        }

        TimeSpan? elapsed = runStarted is not null && runEnded is not null ? runEnded - runStarted : null;
        TimeSpan? active = elapsed is null ? null : elapsed.Value - waiting;
        if (active is not null && active.Value < TimeSpan.Zero)
        {
            active = TimeSpan.Zero;
        }

        TimeSpan? between = elapsed is null ? null : elapsed.Value - WallClock(ofTheRun);
        if (between is not null && between.Value < TimeSpan.Zero)
        {
            between = TimeSpan.Zero;
        }

        var bottleneck = byKind
            .Where(k => k.Kind is not (SpanKind.ApprovalWaiting or SpanKind.ConsoleStartup))
            .Select(k => (SpanKind?)k.Kind)
            .FirstOrDefault();

        return new LatencyReport(
            Elapsed: elapsed,
            ActiveTime: active,
            ApprovalWaiting: waiting,
            TimeToFirstAction: runStarted is not null && firstAction is not null ? firstAction - runStarted : null,
            TimeToAcceptedResult: runStarted is not null && accepted is not null ? accepted - runStarted : null,
            ByKind: byKind,
            Bottleneck: bottleneck)
        {
            BetweenStages = between,
            InTools = WallClock(spans.Where(s => s.Kind == SpanKind.ToolActivity)),
        };
    }
}
