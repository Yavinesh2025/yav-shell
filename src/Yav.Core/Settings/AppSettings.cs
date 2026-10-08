using System.Text.Json;
using System.Text.Json.Serialization;
using Yav.Core.Profiles;

namespace Yav.Core.Settings;

public sealed record AdapterSettings
{
    /// <summary>Full path of the agent executable. Empty means "find it on PATH".</summary>
    public string ExecutablePath { get; init; } = string.Empty;

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Variables added to the environment of the agent's process, for example the location of its own
    /// configuration. Never a credential: those are kept in the operating system's credential store.
    /// </summary>
    public Dictionary<string, string> Environment { get; init; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed record RetentionSettings
{
    public int KeepRunsDays { get; init; } = 30;

    public int KeepWorkspacesDays { get; init; } = 7;

    public int MaxEventsPerRun { get; init; } = 5000;
}

public sealed record LimitSettings
{
    /// <summary>Zero means no limit.</summary>
    public int MaxElapsedMinutes { get; init; }

    public int MaxRepairCycles { get; init; } = 2;

    public int MaxQueueLength { get; init; } = 20;

    /// <summary>Zero means no limit.</summary>
    public long MaxRunTokens { get; init; }

    /// <summary>Stop starting new turns when the provider reports this percentage of a rate-limit window used. Zero means no limit.</summary>
    public int StopAtRateLimitPercent { get; init; }
}

/// <summary>
/// Trusted application settings, stored outside every project. Secrets are never stored here; they live in
/// the Windows Credential Manager.
/// </summary>
public sealed record AppSettings
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; init; } = CurrentSchemaVersion;

    public RoleSelection? ModelA { get; init; }

    public RoleSelection? ModelB { get; init; }

    public bool QualityLock { get; init; } = true;

    public bool Strict { get; init; } = true;

    /// <summary>
    /// True after the user typed /quality gates required: a project with no approved check that is required does not
    /// run. False, the default, runs such a project on the review of Model B alone and says so. It is kept as
    /// "requireChecks": the "requireGates" that 0.2.0 wrote into every settings.json was true for everyone who never
    /// chose, so it is not read, and a choice of required made then is made again with /quality gates required.
    /// </summary>
    [JsonPropertyName("requireChecks")]
    public bool RequireGates { get; init; }

    public bool Adaptive { get; init; }

    public bool Optimization { get; init; } = true;

    /// <summary>
    /// False: a required check that already failed before the task is your decision. True: it is sent to
    /// Model A like any other failure, for projects whose acceptance checks are written before the change.
    /// </summary>
    public bool RepairPreExistingFailures { get; init; }

    public ProviderSpeedMode Speed { get; init; } = ProviderSpeedMode.Standard;

    public LimitSettings Limits { get; init; } = new();

    public RetentionSettings Retention { get; init; } = new();

    public Dictionary<string, AdapterSettings> Adapters { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>"powershell", "pwsh" or "cmd". Used by /shell and /exec.</summary>
    public string Shell { get; init; } = "pwsh";

    public string? LastProject { get; init; }

    /// <summary>YAV telemetry. Off by default; YAV sends nothing anywhere unless this is turned on.</summary>
    public bool Telemetry { get; init; }

    public bool PlainOutput { get; init; }

    /// <summary>
    /// True after the user, in a console that was open already, answered no when 'yav' offered to install itself. It
    /// is not offered in such a console again; opened from Explorer it still is, and 'yav install' still installs.
    /// </summary>
    public bool InstallOfferDeclined { get; init; }

    public QualityPolicy ToPolicy() => new(
        QualityLock: QualityLock,
        Strict: Strict,
        RequireReview: true,
        RequireGates: RequireGates,
        MaxRepairCycles: Math.Clamp(Limits.MaxRepairCycles, 0, 10),
        Adaptive: Adaptive,
        Optimization: Optimization,
        RepairPreExistingFailures: RepairPreExistingFailures);

    public AdapterSettings AdapterFor(string adapterId) =>
        Adapters.TryGetValue(adapterId, out var settings) ? settings : new AdapterSettings();

    public string ToJson() => JsonSerializer.Serialize(this, SettingsJson.Default.AppSettings);

    public static AppSettings FromJson(string json) =>
        JsonSerializer.Deserialize(json, SettingsJson.Default.AppSettings) ?? new AppSettings();
}

[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class SettingsJson : JsonSerializerContext;

/// <summary>Where YAV keeps its own data. Everything is under one per-user directory outside every project.</summary>
public sealed class YavPaths
{
    public const string HomeVariable = "YAV_HOME";

    public YavPaths(string home)
    {
        Home = Path.GetFullPath(home);
    }

    public string Home { get; }

    public string Database => Path.Combine(Home, "yav.db");

    public string SettingsFile => Path.Combine(Home, "settings.json");

    public string HistoryFile => Path.Combine(Home, "history.txt");

    public string Logs => Path.Combine(Home, "logs");

    public string Workspaces => Path.Combine(Home, "workspaces");

    public string Blobs => Path.Combine(Home, "blobs");

    public string Exports => Path.Combine(Home, "exports");

    public string Schemas => Path.Combine(Home, "schemas");

    /// <summary>The data directory that is used when none was named.</summary>
    public static YavPaths Default =>
        new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "YavShell"));

    /// <summary>
    /// Under what the secrets of this data directory are kept in the credential store. Null for the default
    /// directory. Every other directory has a scope of its own, so that a second installation, or a test,
    /// neither reads nor replaces the secrets of the first.
    /// </summary>
    public string? CredentialScope
    {
        get
        {
            var home = Normalize(Home);
            if (string.Equals(home, Normalize(Default.Home), StringComparison.Ordinal))
            {
                return null;
            }

            var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(home));
            return Convert.ToHexStringLower(hash)[..12];
        }
    }

    public static YavPaths Resolve()
    {
        var overridden = Environment.GetEnvironmentVariable(HomeVariable);
        return string.IsNullOrWhiteSpace(overridden) ? Default : new YavPaths(overridden);
    }

    private static string Normalize(string path) =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant();

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Workspaces);
        Directory.CreateDirectory(Blobs);
    }
}
