namespace Yav.Core.Agents;

/// <summary>
/// Token counts in categories that never overlap, so they can be added without double counting.
/// A null value means the provider did not report it; it is shown as "Unavailable", never as zero.
/// </summary>
public sealed record TokenCounts(
    long? UncachedInput,
    long? CacheRead,
    long? CacheWrite,
    long? Output,
    long? ReasoningWithinOutput)
{
    public static readonly TokenCounts Unavailable = new(null, null, null, null, null);

    /// <summary>Sum of the non-overlapping categories, or null when nothing was reported.</summary>
    public long? Total
    {
        get
        {
            if (UncachedInput is null && CacheRead is null && CacheWrite is null && Output is null)
            {
                return null;
            }

            return (UncachedInput ?? 0) + (CacheRead ?? 0) + (CacheWrite ?? 0) + (Output ?? 0);
        }
    }

    /// <summary>
    /// For providers whose input count already includes cached tokens and whose output count already
    /// includes reasoning tokens (OpenAI-style counters).
    /// </summary>
    public static TokenCounts FromInclusiveCounters(long? input, long? cachedInput, long? output, long? reasoningOutput, long? cacheWrite = null)
    {
        long? uncached = input;
        if (input is not null && cachedInput is not null)
        {
            uncached = Math.Max(0, input.Value - cachedInput.Value);
        }

        return new TokenCounts(uncached, cachedInput, cacheWrite, output, reasoningOutput);
    }

    /// <summary>
    /// For providers whose input count excludes cache reads and cache writes (Anthropic-style counters).
    /// Thinking tokens are already inside the output count.
    /// </summary>
    public static TokenCounts FromExclusiveCounters(long? input, long? cacheRead, long? cacheWrite, long? output, long? thinking) =>
        new(input, cacheRead, cacheWrite, output, thinking);

    public static TokenCounts operator +(TokenCounts a, TokenCounts b) => new(
        Add(a.UncachedInput, b.UncachedInput),
        Add(a.CacheRead, b.CacheRead),
        Add(a.CacheWrite, b.CacheWrite),
        Add(a.Output, b.Output),
        Add(a.ReasoningWithinOutput, b.ReasoningWithinOutput));

    /// <summary>Difference between two cumulative readings. A counter that went backwards is reported as unavailable.</summary>
    public static TokenCounts Delta(TokenCounts later, TokenCounts earlier) => new(
        Subtract(later.UncachedInput, earlier.UncachedInput),
        Subtract(later.CacheRead, earlier.CacheRead),
        Subtract(later.CacheWrite, earlier.CacheWrite),
        Subtract(later.Output, earlier.Output),
        Subtract(later.ReasoningWithinOutput, earlier.ReasoningWithinOutput));

    private static long? Add(long? a, long? b) => a is null && b is null ? null : (a ?? 0) + (b ?? 0);

    private static long? Subtract(long? later, long? earlier)
    {
        if (later is null)
        {
            return null;
        }

        var value = later.Value - (earlier ?? 0);
        return value < 0 ? null : value;
    }
}

public enum UsageScope
{
    /// <summary>Counts for a single request or turn.</summary>
    PerTurn,

    /// <summary>Running total for the whole provider session, including turns from earlier runs.</summary>
    CumulativeForSession,
}

public sealed record UsageSnapshot(
    string AdapterId,
    string? SessionId,
    string? Model,
    UsageScope Scope,
    TokenCounts Tokens,
    TokenCounts? LastRequest,
    decimal? ProviderCostUsd,
    ValueProvenance CostProvenance,
    long? ContextWindow,
    string Source,
    DateTimeOffset ObservedAt);

public sealed record RateLimitWindow(int? UsedPercent, int? WindowMinutes, DateTimeOffset? ResetsAt);

public sealed record RateLimitSnapshot(
    string AdapterId,
    string? LimitName,
    string? PlanType,
    RateLimitWindow? Primary,
    RateLimitWindow? Secondary,
    bool? HasCredits,
    bool? UnlimitedCredits,
    string? CreditBalance,
    bool? LimitReached,
    string Source,
    DateTimeOffset ObservedAt);

/// <summary>
/// Turns a stream of cumulative session readings into the usage that belongs to the current run,
/// so that a resumed session's earlier turns are not counted again.
/// </summary>
public sealed class SessionUsageTracker
{
    private TokenCounts? _baseline;
    private decimal? _baselineCost;
    private UsageSnapshot? _latest;
    private TokenCounts _perTurnSum = TokenCounts.Unavailable;
    private bool _sawPerTurn;

    /// <param name="knownBaseline">Cumulative totals recorded for this session by an earlier run, if any.</param>
    /// <param name="knownBaselineCost">Cumulative provider cost recorded by an earlier run, if any.</param>
    /// <param name="sessionIsNew">True when this run created the session, so the baseline is zero.</param>
    public SessionUsageTracker(TokenCounts? knownBaseline, decimal? knownBaselineCost, bool sessionIsNew)
    {
        if (sessionIsNew)
        {
            _baseline = new TokenCounts(0, 0, 0, 0, 0);
            _baselineCost = 0m;
        }
        else
        {
            _baseline = knownBaseline;
            _baselineCost = knownBaselineCost;
        }
    }

    public UsageSnapshot? Latest => _latest;

    /// <summary>False when a resumed session's earlier usage is unknown, so the run share cannot be separated.</summary>
    public bool BaselineKnown => _baseline is not null || _sawPerTurn;

    public void Observe(UsageSnapshot snapshot)
    {
        if (snapshot.Scope == UsageScope.PerTurn)
        {
            _sawPerTurn = true;
            _perTurnSum += snapshot.Tokens;
            _latest = snapshot;
            return;
        }

        if (_baseline is null && snapshot.LastRequest is not null)
        {
            // First cumulative reading of a resumed session: everything except the last request predates this run.
            _baseline = TokenCounts.Delta(snapshot.Tokens, snapshot.LastRequest);
        }

        _latest = snapshot;
    }

    /// <summary>Tokens attributable to this run, or unavailable when that cannot be determined.</summary>
    public TokenCounts RunTokens
    {
        get
        {
            if (_latest is null)
            {
                return TokenCounts.Unavailable;
            }

            if (_latest.Scope == UsageScope.PerTurn)
            {
                return _perTurnSum;
            }

            return _baseline is null ? TokenCounts.Unavailable : TokenCounts.Delta(_latest.Tokens, _baseline);
        }
    }

    /// <summary>Provider-reported cost attributable to this run, when the provider reports cost at all.</summary>
    public decimal? RunCostUsd
    {
        get
        {
            if (_latest?.ProviderCostUsd is null)
            {
                return null;
            }

            if (_latest.Scope == UsageScope.PerTurn)
            {
                return _latest.ProviderCostUsd;
            }

            if (_baselineCost is null)
            {
                return null;
            }

            var value = _latest.ProviderCostUsd.Value - _baselineCost.Value;
            return value < 0 ? null : value;
        }
    }

    /// <summary>The cumulative reading to store so that a later run can use it as its baseline.</summary>
    public (TokenCounts Tokens, decimal? Cost)? CumulativeForStorage =>
        _latest is { Scope: UsageScope.CumulativeForSession } ? (_latest.Tokens, _latest.ProviderCostUsd) : null;
}
