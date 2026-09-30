namespace Yav.Core;

/// <summary>
/// How much YAV knows about a setting. A value is only <see cref="Verified"/> when the provider
/// reported it back; a value YAV merely asked for is never shown as verified.
/// </summary>
public enum VerificationStatus
{
    /// <summary>The provider or tool reported this exact value as in effect.</summary>
    Verified,

    /// <summary>YAV requested the value but has no confirmation that it is in effect.</summary>
    RequestedUnverified,

    /// <summary>The provider reported a different value than the one requested.</summary>
    Mismatch,

    /// <summary>The provider does not support the requested value for this model or route.</summary>
    Unsupported,

    /// <summary>The information cannot be obtained from this provider or integration.</summary>
    Unavailable,
}

/// <summary>A requested setting together with what was confirmed and where the confirmation came from.</summary>
public sealed record Confirmed<T>(T? Requested, T? Effective, VerificationStatus Status, string Source)
{
    public bool IsVerified => Status == VerificationStatus.Verified;

    public static Confirmed<T> RequestedOnly(T requested, string source) =>
        new(requested, default, VerificationStatus.RequestedUnverified, source);

    public static Confirmed<T> Unavailable(string source) =>
        new(default, default, VerificationStatus.Unavailable, source);

    /// <summary>Compares the requested value with the value the provider reported.</summary>
    public static Confirmed<T> Compare(T requested, T? reported, string source, IEqualityComparer<T>? comparer = null)
    {
        if (reported is null)
        {
            return new Confirmed<T>(requested, default, VerificationStatus.RequestedUnverified, source);
        }

        comparer ??= EqualityComparer<T>.Default;
        return comparer.Equals(requested, reported)
            ? new Confirmed<T>(requested, reported, VerificationStatus.Verified, source)
            : new Confirmed<T>(requested, reported, VerificationStatus.Mismatch, source);
    }

    public string Describe()
    {
        return Status switch
        {
            VerificationStatus.Verified => $"{Effective} (Verified)",
            VerificationStatus.RequestedUnverified => $"{Requested} (Requested / Unverified)",
            VerificationStatus.Mismatch => $"{Effective} (MISMATCH: requested {Requested})",
            VerificationStatus.Unsupported => $"{Requested} (Unsupported)",
            _ => "Unavailable",
        };
    }
}

/// <summary>How a number shown to the user was obtained.</summary>
public enum ValueProvenance
{
    /// <summary>Reported by the provider or tool.</summary>
    Reported,

    /// <summary>Calculated by YAV from reported values and versioned rates.</summary>
    Estimated,

    /// <summary>Measured locally by YAV.</summary>
    Measured,

    /// <summary>Not available. Never displayed as zero.</summary>
    Unavailable,
}
