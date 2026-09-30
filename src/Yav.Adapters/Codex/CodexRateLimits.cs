using System.Text.Json;
using Yav.Core.Agents;

namespace Yav.Adapters;

/// <summary>
/// What is known about the rate limits of the account, per limit. Codex gives a complete reading when asked,
/// and afterwards sparse rolling updates, which clients are to merge into what they know: a value an update
/// does not carry was not available to it, and does not clear what was known. Several limits can be metered
/// at once, each with its own limit id; an update that names none is about the limit the reading gave as the
/// account's own.
/// </summary>
internal sealed class CodexRateLimits
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, RateLimitSnapshot> _known = new(StringComparer.Ordinal);
    private string? _ownLimit;

    /// <summary>Replaces what is known with a complete reading. Returns the account's own limit, as before.</summary>
    public RateLimitSnapshot? Replace(JsonElement result, string source, DateTimeOffset now)
    {
        lock (_gate)
        {
            _known.Clear();
            _ownLimit = null;
            RateLimitSnapshot? own = null;
            if (result.Child("rateLimits") is { } limits)
            {
                _ownLimit = limits.Text("limitId");
                own = CodexAppServerAdapter.ParseRateLimits(limits, source, now);
                _known[_ownLimit ?? string.Empty] = own;
            }

            if (result.Child("rateLimitsByLimitId") is { ValueKind: JsonValueKind.Object } byLimit)
            {
                foreach (var limit in byLimit.EnumerateObject().Where(l => l.Value.ValueKind == JsonValueKind.Object))
                {
                    _known[limit.Name] = CodexAppServerAdapter.ParseRateLimits(limit.Value, source, now);
                }
            }

            return own;
        }
    }

    /// <summary>Merges an update into what is known about its limit, and returns what is now known about that limit.</summary>
    public RateLimitSnapshot Merge(JsonElement limits, string source, DateTimeOffset now)
    {
        var update = CodexAppServerAdapter.ParseRateLimits(limits, source, now);
        lock (_gate)
        {
            var key = limits.Text("limitId") ?? _ownLimit ?? string.Empty;
            if (_known.TryGetValue(key, out var known))
            {
                // The credits are taken whole, as a window is: a balance an update gives as null is not known any
                // more, and the balance known before is not kept beside what the update says about the credits.
                var credits = limits.Child("credits") is not null;
                update = update with
                {
                    LimitName = limits.Text("limitName") ?? known.LimitName ?? update.LimitName,
                    PlanType = update.PlanType ?? known.PlanType,

                    // A window is taken whole: its share and its reset time belong to one reading.
                    Primary = update.Primary ?? known.Primary,
                    Secondary = update.Secondary ?? known.Secondary,
                    HasCredits = credits ? update.HasCredits : known.HasCredits,
                    UnlimitedCredits = credits ? update.UnlimitedCredits : known.UnlimitedCredits,
                    CreditBalance = credits ? update.CreditBalance : known.CreditBalance,

                    // Whether a limit is reached is not kept: the protocol has no value that says it no longer is,
                    // so a kept value could never be taken back. What an update does not say stays unknown.
                };
            }

            _known[key] = update;
            return update;
        }
    }
}
