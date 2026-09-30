using System.Reflection;
using Yav.Adapters;
using Yav.Coordinator;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Settings;
using Yav.Platform.Processes;
using Yav.Platform.Security;
using Yav.Storage;
using Yav.Validation;
using Yav.Workspace;

namespace Yav.Console.Composition;

/// <summary>
/// The one place where the modules are put together. Everything else receives what it needs and knows
/// the other modules only through the contracts in Yav.Core.
/// </summary>
public sealed class AppServices : IAsyncDisposable
{
    /// <summary>The name under which an Anthropic API key is kept in the Windows Credential Manager.</summary>
    public const string AnthropicKeyName = "anthropic-api-key";

    private AppServices(
        YavPaths paths,
        SettingsStore settingsStore,
        AppSettings settings,
        IReadOnlyList<string> startupProblems,
        YavDatabase database,
        ProcessRunner runner,
        ICredentialStore credentials,
        WorkspaceService workspaces,
        ValidationService validation,
        IReadOnlyDictionary<string, IAgentAdapter> adapters,
        RunCoordinator coordinator)
    {
        Paths = paths;
        SettingsStore = settingsStore;
        Settings = settings;
        StartupProblems = startupProblems;
        Database = database;
        Runner = runner;
        Credentials = credentials;
        Workspaces = workspaces;
        Validation = validation;
        Adapters = adapters;
        Coordinator = coordinator;
    }

    public static string Version { get; } =
        typeof(AppServices).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    public YavPaths Paths { get; }

    public SettingsStore SettingsStore { get; }

    /// <summary>The settings as they are now. A run that is active keeps the ones it started with.</summary>
    public AppSettings Settings { get; private set; }

    public IReadOnlyList<string> StartupProblems { get; }

    public YavDatabase Database { get; }

    public ProcessRunner Runner { get; }

    public ICredentialStore Credentials { get; }

    public WorkspaceService Workspaces { get; }

    public ValidationService Validation { get; }

    public IReadOnlyDictionary<string, IAgentAdapter> Adapters { get; }

    public RunCoordinator Coordinator { get; }

    public TimeProvider Clock => TimeProvider.System;

    public RunConfiguration Configuration => RunConfiguration.From(Settings);

    /// <param name="credentialStore">Where secrets are kept. Null uses the Windows Credential Manager.</param>
    public static AppServices Create(YavPaths paths, ICredentialStore? credentialStore = null)
    {
        paths.EnsureCreated();
        var settingsStore = new SettingsStore(paths);
        var loaded = settingsStore.Load();
        var settings = loaded.Settings;
        var clock = TimeProvider.System;

        var database = YavDatabase.Open(paths.Database, clock);
        database.MaxEventsPerRun = Math.Clamp(settings.Retention.MaxEventsPerRun, 100, 100_000);

        var runner = new ProcessRunner();
        var credentials = credentialStore ?? new WindowsCredentialStore(paths.CredentialScope);
        var workspaces = new WorkspaceService(paths, runner, database, clock);
        var validation = new ValidationService(runner, database, clock, Version);

        AdapterOptions Options(string adapterId)
        {
            var adapter = settings.AdapterFor(adapterId);
            return new AdapterOptions(
                ExecutablePath: string.IsNullOrWhiteSpace(adapter.ExecutablePath) ? null : adapter.ExecutablePath,
                Environment: adapter.Environment.Count == 0
                    ? null
                    : adapter.Environment.ToDictionary(p => p.Key, p => (string?)p.Value, StringComparer.OrdinalIgnoreCase),
                ClientVersion: Version,
                DiagnosticsDirectory: paths.Logs);
        }

        var adapters = new Dictionary<string, IAgentAdapter>(StringComparer.OrdinalIgnoreCase);
        CodexAppServerAdapter? appServer = null;
        if (settings.AdapterFor(CodexAppServerAdapter.AdapterId).Enabled)
        {
            appServer = new CodexAppServerAdapter(runner, clock, Options(CodexAppServerAdapter.AdapterId));
            adapters[appServer.Id] = appServer;
        }

        if (settings.AdapterFor(CodexExecAdapter.AdapterId).Enabled)
        {
            // This mode cannot list models itself. The list comes from the app server, which says so in its source.
            var exec = new CodexExecAdapter(
                runner, clock, Options(CodexExecAdapter.AdapterId),
                appServer is null ? null : appServer.ListModelsAsync);
            adapters[exec.Id] = exec;
        }

        if (settings.AdapterFor(ClaudeCliAdapter.AdapterId).Enabled)
        {
            var claude = new ClaudeCliAdapter(
                runner, clock, Options(ClaudeCliAdapter.AdapterId),
                () => credentials.IsAvailable ? credentials.Read(AnthropicKeyName) : null);
            adapters[claude.Id] = claude;
        }

        var coordinator = new RunCoordinator(
            new CoordinatorServices(adapters, workspaces, validation, database, database, database, clock, Version),
            new CoordinatorOptions { ProcessIsAlive = pid => ProcessRunner.IsProcessAlive(pid, "yav") });

        return new AppServices(
            paths, settingsStore, settings, loaded.Problems, database, runner, credentials, workspaces, validation, adapters, coordinator);
    }

    /// <summary>Changes the settings and writes them. Returns false when they could only be changed for this session.</summary>
    public bool Update(Func<AppSettings, AppSettings> change)
    {
        Settings = change(Settings);
        return SettingsStore.Save(Settings);
    }

    public async ValueTask DisposeAsync()
    {
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        foreach (var adapter in Adapters.Values)
        {
            try
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is AgentException or IOException or InvalidOperationException)
            {
                // Its processes belong to a job object and end with this process in any case.
                _ = ex;
            }
        }

        Database.Dispose();
    }
}
