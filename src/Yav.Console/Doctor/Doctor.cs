using System.Globalization;
using System.Runtime.InteropServices;
using Yav.Adapters;
using Yav.Console.Composition;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Platform.Consoles;

namespace Yav.Console.Doctor;

public enum CheckStatus
{
    Ok,

    /// <summary>A fact worth knowing that needs no action.</summary>
    Info,
    Warning,

    /// <summary>Something that keeps YAV, or a run, from working.</summary>
    Problem,
}

public sealed record DoctorCheck(string Area, string Name, CheckStatus Status, string Detail, string? Remedy = null);

public sealed record DoctorReport(IReadOnlyList<DoctorCheck> Checks, DateTimeOffset At)
{
    public bool HasProblems => Checks.Any(c => c.Status == CheckStatus.Problem);

    public int Count(CheckStatus status) => Checks.Count(c => c.Status == status);
}

/// <summary>
/// Looks at what YAV needs and reports what it finds. It asks the agents for their version, account and
/// models, which costs no usage, and it changes nothing.
/// </summary>
public static class DoctorChecks
{
    private const int Windows11Build = 22000;

    /// <summary>The package of App Installer, whose aliases stand in for programs that are not installed.</summary>
    private const string AppInstaller = "Microsoft.DesktopAppInstaller_";

    public static async Task<DoctorReport> CollectAsync(AppServices services, ConsoleCapabilities? console, string? projectPath, CancellationToken cancellationToken)
    {
        var checks = new List<DoctorCheck>();
        Platform(checks, console);
        if (StorePrograms(name => services.Runner.Resolve(name)) is { } store)
        {
            checks.Add(store);
        }

        Storage(checks, services);
        Tools(checks, services.Runner);

        // The agents are asked side by side: each of them starts a program of its own, and some start slowly.
        var agents = services.Adapters.Values.OrderBy(a => a.Id, StringComparer.Ordinal).Select(async adapter =>
        {
            var found = new List<DoctorCheck>();
            await AgentAsync(found, services, adapter, cancellationToken).ConfigureAwait(false);
            return found;
        }).ToList();
        foreach (var found in await Task.WhenAll(agents).ConfigureAwait(false))
        {
            checks.AddRange(found);
        }

        if (services.Adapters.Count == 0)
        {
            checks.Add(new DoctorCheck("Agents", "Adapters", CheckStatus.Problem, "Every adapter is turned off in the settings.", "Turn one on with /settings."));
        }

        await ConfigurationAsync(checks, services, projectPath, cancellationToken).ConfigureAwait(false);
        Attention(checks, services);

        foreach (var problem in services.StartupProblems)
        {
            checks.Add(new DoctorCheck("Settings", "settings.json", CheckStatus.Warning, problem));
        }

        return new DoctorReport(checks, services.Clock.GetUtcNow());
    }

    /// <summary>
    /// What the doctor says about the Windows it runs on. The build alone does not tell Windows 11 from Windows
    /// Server: Windows Server 2025 has the build 26100, as Windows 11 24H2 has.
    /// </summary>
    internal static DoctorCheck WindowsVersion(int build, bool server)
    {
        if (server)
        {
            return new DoctorCheck(
                "System", "Windows", CheckStatus.Info,
                $"Windows Server (build {build}). YAV is built and tested for Windows 11 x64; Windows Server is not what it is made for.");
        }

        return build >= Windows11Build
            ? new DoctorCheck("System", "Windows", CheckStatus.Ok, $"Windows 11 (build {build})")
            : new DoctorCheck(
                "System", "Windows", CheckStatus.Warning,
                $"Windows build {build}. YAV is built and tested for Windows 11 x64; Windows 10 is not assumed to work.",
                "Use Windows 11, or treat problems on this system as untested territory.");
    }

    private static void Platform(List<DoctorCheck> checks, ConsoleCapabilities? console)
    {
        checks.Add(new DoctorCheck("System", "YAV Shell", CheckStatus.Ok, $"version {AppServices.Version}, {RuntimeInformation.ProcessArchitecture}, {AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar)}"));

        // Where the runtime comes from tells whether this installation depends on anything that is installed separately.
        var runtime = RuntimeEnvironment.GetRuntimeDirectory().TrimEnd(Path.DirectorySeparatorChar);
        var bundled = string.Equals(runtime, AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase);
        checks.Add(new DoctorCheck(
            "System", "Runtime", CheckStatus.Ok,
            $"{RuntimeInformation.FrameworkDescription} in {runtime} ({(bundled ? "part of this installation" : "installed separately; a package of YAV brings its own")})"));

        checks.Add(WindowsVersion(Environment.OSVersion.Version.Build, Yav.Platform.WindowsEdition.IsServer() == true));

        if (RuntimeInformation.ProcessArchitecture != Architecture.X64)
        {
            checks.Add(new DoctorCheck("System", "Architecture", CheckStatus.Warning, $"{RuntimeInformation.ProcessArchitecture}: only x64 is tested."));
        }

        if (console is null)
        {
            return;
        }

        if (console.Interactive && console.Rich)
        {
            checks.Add(new DoctorCheck("System", "Console", CheckStatus.Ok, $"{console.Host}: escape sequences and UTF-8 are available"));
        }
        else if (!console.Interactive)
        {
            checks.Add(new DoctorCheck("System", "Console", CheckStatus.Info, "Input or output is redirected: plain text is written and nothing is asked interactively."));
        }
        else
        {
            checks.Add(new DoctorCheck(
                "System", "Console", CheckStatus.Warning,
                $"{console.Host} does not process escape sequences. YAV uses plain text and reads whole lines.",
                "Use Windows Terminal, or a current Windows console host."));
        }
    }

    /// <summary>
    /// The programs agents and checks are likely to start that come from the Microsoft Store on this machine.
    /// Null when none does. Looked up by name, as an agent would.
    /// </summary>
    /// <param name="readAlias">What an app execution alias starts. By default it is read from the alias.</param>
    public static DoctorCheck? StorePrograms(Func<string, string?> resolve, Func<string, Yav.Platform.Processes.AppExecutionAliasTarget?>? readAlias = null)
    {
        readAlias ??= Yav.Platform.Processes.AppExecutionAlias.Read;

        // Where Python is missing, Windows puts aliases of App Installer in its place, which only lead to the Store.
        // They start no Python, and nothing that keeps running.
        bool StandsIn(string path) =>
            path.Contains(@"\" + AppInstaller, StringComparison.OrdinalIgnoreCase)
            || readAlias(path)?.PackageFamily.StartsWith(AppInstaller, StringComparison.OrdinalIgnoreCase) == true;

        var found = new[] { "pwsh", "python", "python3" }
            .Select(name => (Name: name, Path: resolve(name)))
            .Where(program => program.Path is not null && Yav.Platform.Processes.ExecutableResolver.IsPackaged(program.Path) && !StandsIn(program.Path))
            .Select(program => $"{program.Name} ({program.Path})")
            .ToList();
        if (found.Count == 0)
        {
            return null;
        }

        return new DoctorCheck(
            "System", "Programs of the Store", CheckStatus.Warning,
            $"From the Microsoft Store on this machine: {string.Join(", ", found)}. Windows takes a program of the Store out of the group of programs "
            + "YAV ends together. When an agent or a check starts one, it and what it starts keeps running when the run is stopped or runs out of time. "
            + "Started by YAV itself, it is ended with the run.",
            "Install the program without the Store (for PowerShell 7: the ZIP package), or look for what is left after you stopped a run.");
    }

    private static void Storage(List<DoctorCheck> checks, AppServices services)
    {
        var home = services.Paths.Home;
        try
        {
            var probe = Path.Combine(home, ".write-test-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            var drive = new DriveInfo(Path.GetPathRoot(home)!);
            var free = drive.AvailableFreeSpace / (1024.0 * 1024 * 1024);
            var status = free < 1 ? CheckStatus.Warning : CheckStatus.Ok;
            checks.Add(new DoctorCheck(
                "Storage", "Data directory", status,
                string.Create(CultureInfo.InvariantCulture, $"{home} is writable; {free:0.0} GB free on {drive.Name}"),
                free < 1 ? "Isolated workspaces are copies of your project. Free some space, or remove old runs with /history prune." : null));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            checks.Add(new DoctorCheck("Storage", "Data directory", CheckStatus.Problem, $"{home} cannot be written: {ex.Message}", "Set YAV_HOME to a directory you can write to."));
        }

        checks.Add(new DoctorCheck("Storage", "Database", CheckStatus.Ok, $"{services.Paths.Database}, format {services.Database.SchemaVersion}"));
        checks.Add(services.Credentials.IsAvailable
            ? new DoctorCheck("Storage", "Credential store", CheckStatus.Ok, "Windows Credential Manager"
                + (services.Credentials.Exists(AppServices.AnthropicKeyName) ? "; an Anthropic API key is stored" : "; no API key is stored"))
            : new DoctorCheck("Storage", "Credential store", CheckStatus.Warning, "The Windows Credential Manager is not available, so no API key can be kept."));
    }

    private static void Tools(List<DoctorCheck> checks, IProcessRunner runner)
    {
        var git = runner.Resolve("git");
        checks.Add(git is null
            ? new DoctorCheck(
                "Tools", "Git", CheckStatus.Warning,
                "Git was not found on PATH. Projects are worked on in a protected copy; ignore files are not honored and concurrent edits cannot be merged.",
                "Install Git for Windows.")
            : new DoctorCheck("Tools", "Git", CheckStatus.Ok, git));
    }

    private static async Task AgentAsync(List<DoctorCheck> checks, AppServices services, IAgentAdapter adapter, CancellationToken cancellationToken)
    {
        var area = adapter.DisplayName;
        AdapterSnapshot? snapshot;
        try
        {
            snapshot = await services.Coordinator.Catalog.GetAsync(adapter.Id, refresh: true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentException or IOException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            checks.Add(new DoctorCheck(area, "Agent", CheckStatus.Problem, "The agent could not be examined: " + ex.Message));
            return;
        }

        if (snapshot is null)
        {
            return;
        }

        var detection = snapshot.Detection;
        if (!detection.Found)
        {
            checks.Add(new DoctorCheck(area, "Installation", CheckStatus.Warning, detection.Problems.FirstOrDefault() ?? "Not installed.", "Install it, or choose models of another agent with /models."));
            return;
        }

        checks.Add(new DoctorCheck(
            area, "Installation",
            detection.Problems.Count > 0 ? CheckStatus.Problem : detection.VersionTested ? CheckStatus.Ok : CheckStatus.Warning,
            $"{detection.ExecutablePath}, version {detection.Version ?? "unknown"}"
            + (detection.VersionTested ? string.Empty : $" (tested with {detection.TestedVersions})")
            + (detection.Problems.Count > 0 ? ". " + string.Join(" ", detection.Problems) : string.Empty)));

        if (detection.Maturity == AdapterMaturity.Experimental)
        {
            checks.Add(new DoctorCheck(area, "Interface", CheckStatus.Info, "Experimental: " + (detection.MaturityNote ?? "the provider labels this interface experimental") + "."));
        }

        foreach (var limitation in adapter.Capabilities.Limitations)
        {
            checks.Add(new DoctorCheck(area, "Limitation", CheckStatus.Info, limitation));
        }

        if (snapshot.Auth is not { } auth)
        {
            checks.Add(new DoctorCheck(
                area, "Account", CheckStatus.Problem,
                "The account could not be read. " + string.Join(" ", services.Coordinator.Catalog.ErrorsFor(adapter.Id)),
                $"Run /login {adapter.Provider}."));
            return;
        }

        if (!auth.Authenticated)
        {
            checks.Add(new DoctorCheck(area, "Account", CheckStatus.Warning, "Not signed in. " + auth.PolicyNote, $"Run /login {ProviderWord(adapter)}."));
            return;
        }

        checks.Add(new DoctorCheck(area, "Account", CheckStatus.Ok, $"{auth.RouteLabel}; billing: {Describe(auth.Billing)} ({auth.Source})"));
        switch (auth.Policy)
        {
            case RoutePolicy.NotPermitted:
                checks.Add(new DoctorCheck(area, "Route", CheckStatus.Problem, "This account route is not permitted for this integration. " + auth.PolicyNote, $"Run /login {ProviderWord(adapter)}."));
                break;
            case RoutePolicy.RequiresAcknowledgement when !snapshot.RouteAcknowledged:
                checks.Add(new DoctorCheck(area, "Route", CheckStatus.Warning, "Not acknowledged yet. " + auth.PolicyNote, $"Run /login {ProviderWord(adapter)} --acknowledge."));
                break;
            case RoutePolicy.RequiresAcknowledgement when auth.UsedWithoutAsking:
                checks.Add(new DoctorCheck(area, "Route", CheckStatus.Ok, "The subscription the agent is signed in to; used without asking."));
                break;
            case RoutePolicy.RequiresAcknowledgement:
                checks.Add(new DoctorCheck(area, "Route", CheckStatus.Ok, "Acknowledged by you."));
                break;
        }

        checks.Add(snapshot.Models.Count > 0
            ? new DoctorCheck(area, "Models", CheckStatus.Ok, $"{snapshot.Models.Count(m => !m.Hidden)} listed for this account ({snapshot.Models[0].Source})")
            : new DoctorCheck(
                area, "Models", adapter.Capabilities.Has(AdapterFeatures.ModelListing) ? CheckStatus.Warning : CheckStatus.Info,
                "No model list is available, so a chosen model stays Requested / Unverified. "
                + string.Join(" ", services.Coordinator.Catalog.ErrorsFor(adapter.Id))));

        if (adapter is ISandboxReporting reporting)
        {
            var sandbox = await reporting.GetSandboxStatusAsync(cancellationToken).ConfigureAwait(false);
            checks.Add(new DoctorCheck(
                area, "Sandbox", sandbox.OperatingSystemEnforced ? CheckStatus.Ok : CheckStatus.Warning,
                $"{sandbox.State}: {sandbox.Detail}",
                sandbox.OperatingSystemEnforced ? null : "Until it is set up, a run with this agent as Model A is blocked, because it could not write."));
        }

        if (adapter.Capabilities.Has(AdapterFeatures.RateLimitReporting))
        {
            try
            {
                var limits = await adapter.GetRateLimitsAsync(cancellationToken).ConfigureAwait(false);
                checks.Add(limits?.Primary?.UsedPercent is { } used
                    ? new DoctorCheck(
                        area, "Limits", used >= 90 ? CheckStatus.Warning : CheckStatus.Ok,
                        $"{used}% of the current window used" + (limits.Primary.ResetsAt is { } resets ? $", resets {resets.ToLocalTime():g}" : string.Empty)
                        + (limits.Secondary?.UsedPercent is { } second ? $"; {second}% of the longer window" : string.Empty))
                    : new DoctorCheck(area, "Limits", CheckStatus.Info, "Unavailable: the provider reports no limits for this account."));
            }
            catch (AgentException ex)
            {
                checks.Add(new DoctorCheck(area, "Limits", CheckStatus.Info, "Unavailable: " + ex.Message));
            }
        }
    }

    private static async Task ConfigurationAsync(List<DoctorCheck> checks, AppServices services, string? projectPath, CancellationToken cancellationToken)
    {
        var settings = services.Settings;
        checks.Add(new DoctorCheck(
            "Configuration", "Quality Lock", settings.QualityLock ? CheckStatus.Ok : CheckStatus.Warning,
            settings.QualityLock
                ? $"ON, {settings.ToPolicy().Label}; repair cycles: {settings.Limits.MaxRepairCycles}; required checks: {(settings.RequireGates ? "required" : "optional")}"
                : "OFF: models, effort and review are not held to what you chose."));

        if (settings.ModelA is null || settings.ModelB is null)
        {
            checks.Add(new DoctorCheck(
                "Configuration", "Models", CheckStatus.Warning,
                "Model A and Model B are not both chosen yet. YAV does not choose models for you.",
                "Choose them with /models."));
            return;
        }

        if (projectPath is null || !Directory.Exists(projectPath))
        {
            checks.Add(new DoctorCheck("Configuration", "Models", CheckStatus.Info, $"A: {settings.ModelA.ModelId} ({settings.ModelA.AdapterId}); B: {settings.ModelB.ModelId} ({settings.ModelB.AdapterId}). Open a project to check a run."));
            return;
        }

        try
        {
            var preflight = await services.Coordinator
                .PreflightAsync(new Yav.Coordinator.RunRequest(projectPath, "doctor"), services.Configuration, cancellationToken)
                .ConfigureAwait(false);
            if (preflight.CanRun)
            {
                var profile = preflight.Resolution.Profile!;
                checks.Add(new DoctorCheck(
                    "Configuration", "Run", CheckStatus.Ok,
                    $"A run in {projectPath} can start: A {profile.Implementer.ModelId} at {Effort(profile.Implementer)}, B {profile.Reviewer.ModelId} at {Effort(profile.Reviewer)}, "
                    + $"checks: {(profile.RequiredGateIds.Count == 0 ? "none" : string.Join(", ", profile.RequiredGateIds))}"));
            }

            foreach (var problem in preflight.Problems.Where(p => p.Severity != ProblemSeverity.Info))
            {
                checks.Add(new DoctorCheck(
                    "Configuration", problem.Code,
                    problem.Severity == ProblemSeverity.Blocking ? CheckStatus.Problem : CheckStatus.Warning,
                    problem.Message, problem.Remedy));
            }
        }
        catch (Exception ex) when (ex is AgentException or IOException or InvalidOperationException)
        {
            checks.Add(new DoctorCheck("Configuration", "Run", CheckStatus.Warning, "The configuration could not be checked against the project: " + ex.Message));
        }
    }

    private static void Attention(List<DoctorCheck> checks, AppServices services)
    {
        var waiting = services.Database.ListRuns(null, 500).Where(r => RunStateMachine.NeedsAttention(r.State) && r.Disposition == RunDisposition.Pending).ToList();
        if (waiting.Count > 0)
        {
            var newest = waiting[0];
            checks.Add(new DoctorCheck(
                "Runs", "Needing attention", CheckStatus.Info,
                $"{waiting.Count} run(s) stopped before they were ready; the newest is {newest.RunId} ({RunStateMachine.Display(newest.State)}).",
                "See /history, continue with /resume <run-id> or remove with /discard <run-id>."));
        }

        var unfinished = services.Database.FindUnfinished();
        if (unfinished.Count > 0)
        {
            checks.Add(new DoctorCheck(
                "Runs", "Unfinished applies", CheckStatus.Problem,
                $"{unfinished.Count} apply operation(s) did not finish: {string.Join(", ", unfinished.Select(j => j.RunId))}.",
                "They are finished or rolled back when YAV starts. If this stays, the files involved are in use."));
        }
    }

    private static string Effort(RoleProfile role) => role.RequestedEffort.Length == 0 ? "its only effort" : "effort " + role.RequestedEffort;

    private static string ProviderWord(IAgentAdapter adapter) => adapter.Provider == "anthropic" ? "claude" : "codex";

    public static string Describe(BillingKind billing) => billing switch
    {
        BillingKind.IncludedInSubscription => "included in the subscription, counted against its limits",
        BillingKind.PayPerToken => "billed per token",
        BillingKind.Credits => "drawn from purchased credits",
        BillingKind.CloudProviderBilled => "billed by the cloud provider",
        _ => "Unavailable",
    };
}
