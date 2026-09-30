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

/// <summary>A measured interval. Durations come from a monotonic clock; the wall-clock start is kept for display.</summary>
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
    private readonly Dictionary<string, (TimingSpan Span, long StartTimestamp)> _open = [];

    public TimeProvider Clock => clock;

    public string? RunId => runId;

    public event Action<TimingSpan>? SpanCompleted;

    public SpanScope Start(SpanKind kind, string label, string? parallelGroup = null)
    {
        var span = new TimingSpan(Ids.NewId("s"), runId, kind, label, clock.GetUtcNow(), null, parallelGroup);
        lock (_gate)
        {
            _open[span.SpanId] = (span, clock.GetTimestamp());
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

    internal void End(string spanId)
    {
        TimingSpan finished;
        lock (_gate)
        {
            if (!_open.Remove(spanId, out var entry))
            {
                return;
            }

            finished = entry.Span with { Duration = clock.GetElapsedTime(entry.StartTimestamp) };
            _completed.Add(finished);
        }

        SpanCompleted?.Invoke(finished);
    }

    public readonly struct SpanScope(TimingRecorder owner, string spanId) : IDisposable
    {
        public void Dispose() => owner.End(spanId);
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
