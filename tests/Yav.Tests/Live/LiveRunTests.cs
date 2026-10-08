using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Xunit.Abstractions;
using Xunit.Sdk;
using Yav.Adapters;
using Yav.Bench;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Platform.Processes;
using Yav.Storage;
using Yav.Tests.Support;
using Yav.Validation;

namespace Yav.Tests.Live;

/// <summary>
/// Reported as skipped unless the named part of a live run was asked for. A part that asks the models also needs to be
/// told that the usage was authorized, which scripts\live-run.ps1 says only after it checked -IAuthorizeUsage.
/// </summary>
public sealed class LiveRunFactAttribute : FactAttribute
{
    public const string Variable = "YAV_LIVE_RUN";

    public const string Authorized = "YAV_LIVE_USAGE_AUTHORIZED";

    public LiveRunFactAttribute(string part, bool asksModels = false)
    {
        AsksModels = asksModels;
        Skip = SkipReason(part, asksModels, Environment.GetEnvironmentVariable);
    }

    public bool AsksModels { get; }

    public static string? SkipReason(string part, bool asksModels, Func<string, string?> variable)
    {
        if (!string.Equals(variable(Variable), part, StringComparison.OrdinalIgnoreCase))
        {
            return $@"Part '{part}' of a run with the agents that are installed. scripts\live-run.ps1 runs it.";
        }

        return asksModels && variable(Authorized) != "1"
            ? $@"Part '{part}' asks the models. scripts\live-run.ps1 -IAuthorizeUsage runs it."
            : null;
    }
}

/// <summary>A decision about a question of an agent, as the data of the run records it.</summary>
internal sealed record LiveDecision(string Decision, string DecidedBy, string Title, string? Command)
{
    // The decisions that give an agent what it asked for (ApprovalDecision in Core\Agents).
    public bool Granted => Decision is "Accept" or "AcceptForSession";
}

/// <summary>
/// What the record of the part that continues a run says about the questions of the agents. It says that nothing was
/// granted only when the decisions recorded in the data of the run say so.
/// </summary>
internal static class LiveRunRecords
{
    /// <summary>The newest decision recorded for the run, so that the decisions of a part are told from earlier ones.</summary>
    public static long LastDecision(YavPaths home, string runId) =>
        Query(home, "SELECT COALESCE(MAX(id), 0) FROM approvals WHERE run_id = $run;", runId, 0, reader => reader.GetInt64(0))?.FirstOrDefault() ?? 0;

    /// <summary>The decisions recorded for the run after the given one, oldest first; null when they cannot be read.</summary>
    public static IReadOnlyList<LiveDecision>? Decisions(YavPaths home, string runId, long after) => Query(
        home,
        "SELECT decision, decided_by, title, command FROM approvals WHERE run_id = $run AND id > $after ORDER BY id;",
        runId,
        after,
        reader => new LiveDecision(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));

    public static string Note(int answered, IReadOnlyList<LiveDecision>? decisions)
    {
        var note = $"Questions of the agents that were answered: {answered}.";
        if (decisions is null)
        {
            return note + " What was decided about them could not be read from the data of the run, so it is not known here whether anything was granted. /history in the shell shows it.";
        }

        var granted = decisions.Where(decision => decision.Granted).ToList();
        if (granted.Count > 0)
        {
            return note + " GRANTED, as recorded for the run while this part ran: "
                + string.Join("; ", granted.Select(decision => $"{decision.Title}: {decision.Command} ({decision.Decision}, decided by {decision.DecidedBy})"))
                + ". The driver never grants anything, so something else answered.";
        }

        var kinds = string.Join(", ", decisions.GroupBy(decision => decision.Decision).Select(kind => $"{kind.Key} {kind.Count()}"));
        return note + $" Decisions recorded for the run while this part ran: {decisions.Count}{(decisions.Count == 0 ? string.Empty : $" ({kinds})")}. None was granted.";
    }

    private static List<T>? Query<T>(YavPaths home, string sql, string runId, long after, Func<Microsoft.Data.Sqlite.SqliteDataReader, T> read)
    {
        // Opening a database that is not there would create an empty one.
        if (!File.Exists(home.Database))
        {
            return null;
        }

        var connection = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder
        {
            DataSource = home.Database,
            Mode = Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        try
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$run", runId);
            command.Parameters.AddWithValue("$after", after);
            using var reader = command.ExecuteReader();
            var rows = new List<T>();
            while (reader.Read())
            {
                rows.Add(read(reader));
            }

            return rows;
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
            return null;
        }
        finally
        {
            connection.Dispose();
        }
    }
}

/// <summary>
/// What a part of a live run is told. scripts\live-run.ps1 sets the variables before it starts the part; a test of
/// the part gives them directly, so that no variable of the process is changed. Each is read when the part needs it.
/// </summary>
internal sealed class LivePart(IReadOnlyDictionary<string, string?> variables)
{
    public static LivePart FromEnvironment() => new(
        Environment.GetEnvironmentVariables().Keys.OfType<string>()
            .Where(name => name.StartsWith("YAV_LIVE_", StringComparison.OrdinalIgnoreCase))
            .ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.OrdinalIgnoreCase));

    public string? Get(string variable) => variables.TryGetValue(variable, out var value) ? value : null;

    public string Need(string variable) =>
        Get(variable) is { Length: > 0 } value
            ? value
            : throw new InvalidOperationException($"{variable} is not set. scripts\\live-run.ps1 sets it.");

    /// <summary>The directory of the run, where the records of every part are kept.</summary>
    public string Directory => Need("YAV_LIVE_DIR");

    public YavPaths Home => new(Path.Combine(Directory, "home"));

    public string Project => Path.Combine(Directory, "project");

    /// <summary>The name the records of this part are kept under: "setup", or "resume-2", "inspect-1" for a part that can be repeated.</summary>
    public string Record => Need("YAV_LIVE_RECORD");
}

/// <summary>
/// An account route the holder of the accounts agreed to: a provider and the kind of route, as -Acknowledge of
/// scripts\live-run.ps1 names it ("codex", "claude:api-key"). A provider named alone means its subscription.
/// </summary>
internal sealed record LiveRoute(string Provider, string Kind)
{
    public const string Subscription = "subscription";

    // How /login names a route of each kind: the start of the label the adapter gives it (GetAuthStatusAsync of the
    // adapters). Only kinds whose label says how they are billed are here. A route of any other kind, a cloud provider
    // or one whose account the agent does not report, is never acknowledged by the live run.
    private static readonly Dictionary<(string Provider, string Kind), (AccountRouteKind Route, string Label)> Kinds = new()
    {
        [("codex", Subscription)] = (AccountRouteKind.Subscription, "ChatGPT plan"),
        [("codex", "api-key")] = (AccountRouteKind.ApiKey, "OpenAI API key"),
        [("claude", Subscription)] = (AccountRouteKind.Subscription, "Claude subscription ("),
        [("claude", "api-key")] = (AccountRouteKind.ApiKey, "Anthropic API key ("),
    };

    /// <summary>The routes agreed to, one for each provider. What is not a route the live run knows is refused.</summary>
    public static IReadOnlyList<LiveRoute> Parse(string? text)
    {
        var routes = new List<LiveRoute>();
        foreach (var given in (text ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = given.ToLowerInvariant().Split(':', 2);
            var provider = parts[0];
            var kind = parts.Length == 2 ? parts[1] : Subscription;
            if (provider is not ("codex" or "claude"))
            {
                throw new ArgumentException($"'{given}' does not name a provider the live run knows the account of: codex or claude.");
            }

            if (!Kinds.ContainsKey((provider, kind)))
            {
                throw new ArgumentException(
                    $"'{given}': '{kind}' is not a kind of route the live run acknowledges: {Subscription} (the default) or api-key.");
            }

            if (routes.Any(route => route.Provider == provider))
            {
                throw new ArgumentException($"More than one route of {provider} is named. Name the one route of it you agree to.");
            }

            routes.Add(new LiveRoute(provider, kind));
        }

        return routes;
    }

    /// <summary>The kind of the route a label of the provider names, or null for a route the live run never acknowledges.</summary>
    public static string? KindOf(string provider, string label) => Kinds
        .Where(known => known.Key.Provider == provider && label.StartsWith(known.Value.Label, StringComparison.Ordinal))
        .Select(known => known.Key.Kind)
        .FirstOrDefault();

    public AccountRouteKind Route => Kinds[(Provider, Kind)].Route;

    /// <summary>The part of the question of /login that names a route of this kind, and no route of another.</summary>
    public string Question => $"Use '{Kinds[(Provider, Kind)].Label}";

    public override string ToString() => $"{Provider}:{Kind}";
}

/// <summary>
/// The parts of a run with real models that need a person at a console: choosing the models,
/// acknowledging the account routes, approving the checks, and afterwards looking at what the run left
/// and applying it. They drive the real program in the pseudo console of Windows, through the
/// <see cref="ShellDriver"/> whose own tests use the scripted stand-in for the agents.
///
/// Setting up and looking at the result ask no model anything. The request itself is given to 'yav run' by
/// scripts\live-run.ps1; that, and continuing a run that stopped, is what consumes usage and what needs
/// the authorization of the account holder, which the script checks and passes on in YAV_LIVE_USAGE_AUTHORIZED.
/// An account route is acknowledged here only when it is of the kind YAV_LIVE_ACKNOWLEDGED names for its
/// provider, and the checks of the project are approved only when YAV_LIVE_TRUST_CHECKS says so; the script sets
/// both from what the account holder said. The task is taken only as it was approved at setup
/// (YAV_LIVE_TASK_HASH).
/// </summary>
[Trait("Category", "Live")]
[Collection(nameof(Yav.Tests.EndToEnd.InteractiveConsoleTests))]
public partial class LiveRunTests(ITestOutputHelper output)
{
    private static readonly ProcessRunner Runner = new();

    private static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
            {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }

    private static BenchTask TaskOfTheRun(LivePart part) =>
        ApprovedTask(Path.Combine(Repository, "bench", "tasks", part.Get("YAV_LIVE_TASK") is { Length: > 0 } task ? task : "01-small-edit"), part.Need("YAV_LIVE_TASK_HASH"));

    /// <summary>
    /// The task as it was approved at setup. Its request is sent and its checks are run, so the file is taken only while
    /// it is what the script recorded; it is looked at again after it was read, in case it was changed meanwhile.
    /// </summary>
    internal static BenchTask ApprovedTask(string directory, string approved)
    {
        var file = Path.Combine(directory, "task.json");
        void Check()
        {
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), approved, StringComparison.OrdinalIgnoreCase))
            {
                throw new XunitException($"{file} is not what was approved at setup: it was changed since. Nothing was done with it.");
            }
        }

        Check();
        var task = BenchTask.Load(directory);
        Check();
        return task;
    }

    /// <summary>
    /// What the part inspect types, in this order. /apply names the run: without it the shell applies the newest run of
    /// the project, which is another one when the holder sent a follow-up by hand.
    /// </summary>
    internal static IReadOnlyList<string> InspectCommands(bool apply, string? runId) =>
        ["/history", "/status", "/diff --stat", "/diff", "/review show", "/usage", "/latency", .. (apply ? new[] { $"/apply {runId}", "/status" } : [])];

    /// <summary>
    /// Whether what the checks of the task say about the project passes, and a line that says why. Only after /apply is
    /// the change in the project; then every check has to end with 0.
    /// </summary>
    internal static (bool Passed, string Verdict) Judge(IReadOnlyList<(string Check, int ExitCode)> results, bool apply)
    {
        var failed = string.Join(", ", results.Where(result => result.ExitCode != 0).Select(result => $"{result.Check}: exit code {result.ExitCode}"));
        if (!apply)
        {
            return (true, failed.Length == 0
                ? "Every check passes in the project, which was not changed: nothing was applied."
                : $"Checks that do not pass in the project, which was not changed because nothing was applied: {failed}.");
        }

        return failed.Length == 0
            ? (true, "After /apply every check passes in the project.")
            : (false, $"After /apply these checks do not pass in the project: {failed}.");
    }

    private static ShellDriver Start(LivePart part) => ShellDriver.Start(part.Need("YAV_LIVE_EXE"), part.Project, part.Home);

    /// <summary>
    /// Keeps what the console showed, under the name of the record of this part. It is kept also when the part
    /// failed, then with the screen as it was, so that whatever happened is in the record.
    /// </summary>
    private void Keep(LivePart part, ShellDriver? driver, bool completed, params string[] notes)
    {
        var screens = new StringBuilder();
        if (driver is not null)
        {
            screens.Append(driver.Transcript);
            if (!completed)
            {
                screens.AppendLine("The screen when the part stopped:").AppendLine(driver.Console.Screen.Text).AppendLine();
            }
        }

        foreach (var note in notes.Where(note => note.Length > 0))
        {
            screens.AppendLine(note);
        }

        File.WriteAllText(Path.Combine(part.Directory, part.Record + "-screens.txt"), screens.ToString(), new UTF8Encoding(false));
        if (driver is not null)
        {
            File.WriteAllText(Path.Combine(part.Directory, part.Record + "-written.txt"), driver.Written, new UTF8Encoding(false));
            output.WriteLine(driver.Console.Screen.Text);
        }
    }

    private static async Task GitAsync(string project, params string[] arguments)
    {
        var git = Runner.Resolve("git") ?? throw new FileNotFoundException("Git was not found on PATH.");
        var result = await Runner.RunAsync(
            new ProcessSpec(git, arguments, project, new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0" }),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(60)),
            CancellationToken.None);
        Assert.True(result.Succeeded, $"git {string.Join(' ', arguments)}: {result.StandardError}");
    }

    /// <summary>The project of the task as a repository of its own, with the checks of the task as the project proposes them.</summary>
    private static async Task CreateProjectAsync(string project, BenchTask task)
    {
        Assert.False(Directory.Exists(project), $"{project} exists already. A live run gets a directory of its own.");
        foreach (var file in Directory.EnumerateFiles(task.ProjectDirectory, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(project, Path.GetRelativePath(task.ProjectDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        File.WriteAllText(
            Path.Combine(project, "yav.project.json"),
            ConfigurationParser.Serialize(task.Configuration(task.Steps[0])),
            new UTF8Encoding(false));
        await GitAsync(project, "init", "--quiet", "--initial-branch=main");
        await GitAsync(project, "config", "user.email", "live-run@example.invalid");
        await GitAsync(project, "config", "user.name", "YAV live run");
        await GitAsync(project, "config", "commit.gpgsign", "false");
        await GitAsync(project, "config", "core.autocrlf", "false");
        await GitAsync(project, "add", "-A");
        await GitAsync(project, "commit", "--quiet", "--no-verify", "-m", "The project before the task");
    }

    /// <summary>The provider whose account route a model of the given adapter is billed through, as /login names it.</summary>
    private static string ProviderOf(string model) => model.Split(' ', 2)[0].ToLowerInvariant() switch
    {
        CodexAppServerAdapter.AdapterId or CodexExecAdapter.AdapterId => "codex",
        ClaudeCliAdapter.AdapterId => "claude",
        var adapter => throw new InvalidOperationException($"'{adapter}' is not an adapter the live run knows the account of."),
    };

    // The row on which /login asks about a route, and what was answered there.
    [GeneratedRegex(@"Use '(?<route>[^\r\n]+?)' (?<rest>[^\r\n]*)")]
    private static partial Regex RouteAsked();

    // The row on which /login shows the route of an agent (ShowRoute in Shell\Commands.Models.cs).
    [GeneratedRegex(@"Account route: (?<route>[^\r\n]+)")]
    private static partial Regex ShownRoute();

    /// <summary>The route the newest question of /login in the transcript named, when it was answered with no.</summary>
    private static string? DeclinedRoute(string transcript)
    {
        var asked = RouteAsked().Matches(transcript).LastOrDefault(match => match.Groups["rest"].Value.Contains(ShellDriver.RouteQuestion, StringComparison.Ordinal));
        return asked is not null && asked.Groups["rest"].Value.TrimEnd().EndsWith(" no", StringComparison.Ordinal) ? asked.Groups["route"].Value : null;
    }

    /// <summary>
    /// Acknowledges the account route of a provider with /login. Yes is typed only when the question names a route of the
    /// kind the holder agreed to. Any other question gets no, and the setup stops, naming the route that was found.
    /// </summary>
    private static async Task<string?> AcknowledgeAsync(ShellDriver driver, LiveRoute route)
    {
        var before = driver.Transcript.Length;
        try
        {
            if (route.Kind == "subscription")
            {
                // A subscription the agent is signed in to is used without asking, so /login asks nothing about it. Any
                // question it asks all the same is answered with no.
                await driver.EnterAsync("/login " + route.Provider);
                var shown = driver.Transcript.ToString(before, driver.Transcript.Length - before);
                var label = ShownRoute().Matches(shown).Select(m => m.Groups["route"].Value.Trim())
                    .FirstOrDefault(found => LiveRoute.KindOf(route.Provider, found) == "subscription");
                Assert.True(
                    label is not null && shown.Contains("your subscription, used without asking", StringComparison.Ordinal),
                    $"/login {route.Provider} showed no subscription that is used without asking, and you agreed to the {route.Kind} of {route.Provider}. The setup stops.\n{shown}");
                return label;
            }

            await driver.EnterAsync("/login " + route.Provider, new ExpectedQuestion(route.Question, "yes"));
            return null;
        }
        catch (XunitException ex) when (DeclinedRoute(driver.Transcript.ToString()) is { } found)
        {
            var kind = LiveRoute.KindOf(route.Provider, found);
            throw new XunitException(
                $"/login {route.Provider} asked to acknowledge the route '{found}', "
                + (kind is null ? "which is of no kind the live run acknowledges" : $"which is the {kind} of {route.Provider}")
                + $", and you agreed to the {route.Kind} of {route.Provider} (-Acknowledge {route}). It was answered with no, nothing was "
                + "acknowledged, and the setup stops."
                + (kind is null ? string.Empty : $" If that is the route you agree to, name it: -Acknowledge {route.Provider}:{kind}.")
                + $"\n{ex.Message}");
        }
    }

    [LiveRunFact("setup")]
    public Task The_shell_is_set_up_the_way_a_user_sets_it_up() => SetUpAsync(LivePart.FromEnvironment());

    internal async Task SetUpAsync(LivePart part)
    {
        var task = TaskOfTheRun(part);
        var modelA = part.Need("YAV_LIVE_MODEL_A");
        var modelB = part.Need("YAV_LIVE_MODEL_B");

        // Every route the two models are billed through has to be one the holder agreed to, before anything is done.
        var agreed = LiveRoute.Parse(part.Get("YAV_LIVE_ACKNOWLEDGED"));
        var routes = new[] { modelA, modelB }.Select(ProviderOf).Distinct(StringComparer.Ordinal)
            .Select(provider => agreed.FirstOrDefault(route => route.Provider == provider)
                ?? throw new XunitException(
                    $"The models are billed through the account route of {provider}, and you agreed to no route of {provider} "
                    + $"(-Acknowledge names {(agreed.Count == 0 ? "none" : string.Join(", ", agreed))}). Nothing was done."))
            .ToList();

        // Approved checks run on this machine with the rights of the user. The account holder decides that, not the driver.
        Assert.True(
            part.Get("YAV_LIVE_TRUST_CHECKS") == "1",
            $"Setting up approves the checks of task {task.Id}, which then run on this machine with your rights: "
            + string.Join("; ", task.Steps[0].Checks.Select(check => $"{check.Id}: {check.Command} {string.Join(' ', check.Arguments)}"))
            + @". scripts\live-run.ps1 -TrustChecks approves them. Nothing was done.");
        var home = part.Home;
        home.EnsureCreated();
        await CreateProjectAsync(part.Project, task);

        using var driver = Start(part);
        var usedWithoutAsking = new Dictionary<string, string>(StringComparer.Ordinal);
        var completed = false;
        try
        {
            await driver.WaitForPromptAsync();
            await driver.EnterAsync("/models a " + modelA);
            await driver.EnterAsync("/effort a " + part.Need("YAV_LIVE_EFFORT_A"));
            await driver.EnterAsync("/models b " + modelB);
            await driver.EnterAsync("/effort b " + part.Need("YAV_LIVE_EFFORT_B"));
            foreach (var route in routes)
            {
                // Anything else /login asks, such as whether to start the agent's own sign-in, gets no and ends the setup.
                if (await AcknowledgeAsync(driver, route) is { } label)
                {
                    usedWithoutAsking[route.Provider] = label;
                }
            }

            await driver.EnterAsync("/test trust", new ExpectedQuestion(ShellDriver.ChecksQuestion, "yes"));
            await driver.EnterAsync("/test list");
            await driver.EnterAsync("/effort");
            await driver.EnterAsync("/status");
            await driver.EnterAsync("/doctor", seconds: 300);
            await driver.ExitAsync();
            completed = true;
        }
        finally
        {
            Keep(part, driver, completed);
        }

        var settings = AppSettings.FromJson(File.ReadAllText(home.SettingsFile));
        Assert.Equal(modelA, $"{settings.ModelA?.AdapterId} {settings.ModelA?.ModelId}");
        Assert.Equal(modelB, $"{settings.ModelB?.AdapterId} {settings.ModelB?.ModelId}");
        Assert.Equal(part.Need("YAV_LIVE_EFFORT_A"), settings.ModelA!.EffortPreference);
        Assert.Equal(part.Need("YAV_LIVE_EFFORT_B"), settings.ModelB!.EffortPreference);

        using var database = YavDatabase.Open(home.Database, TimeProvider.System);
        var acknowledged = database.ListAcknowledgements().Where(a => a.Kind == "route").ToList();
        output.WriteLine("acknowledged: " + string.Join(", ", acknowledged.Select(a => a.Subject)));

        // Checked again where it is recorded: the kind of a route is part of the key the shell acknowledged it under.
        var recorded = new JsonArray();
        foreach (var model in new[] { settings.ModelA!, settings.ModelB! })
        {
            var route = routes.Single(r => r.Provider == ProviderOf(model.AdapterId));
            var found = acknowledged.Where(a => a.Subject.StartsWith(model.AdapterId + ":", StringComparison.Ordinal)).ToList();
            if (usedWithoutAsking.TryGetValue(route.Provider, out var shownLabel))
            {
                // Nothing is acknowledged for a subscription: the label is the one /login showed.
                Assert.Empty(found);
                if (!recorded.Any(r => r!["provider"]!.GetValue<string>() == route.Provider))
                {
                    recorded.Add(new JsonObject
                    {
                        ["provider"] = route.Provider,
                        ["kind"] = route.Kind,
                        ["label"] = shownLabel,
                        ["route"] = $"{model.AdapterId}:{route.Route}:used without asking",
                    });
                }

                continue;
            }

            Assert.True(found.Count > 0, $"No account route of {model.AdapterId} was acknowledged. See {part.Record}-screens.txt.");
            foreach (var acknowledgement in found)
            {
                Assert.True(
                    acknowledgement.Subject.Split(':')[1] == route.Route.ToString(),
                    $"The route acknowledged for {model.AdapterId} is {acknowledgement.Subject}, which is not the {route.Kind} of {route.Provider} you agreed to.");
            }

            if (!recorded.Any(r => r!["provider"]!.GetValue<string>() == route.Provider))
            {
                // The shell records a route with the label it showed, followed by what the provider says of it.
                recorded.Add(new JsonObject
                {
                    ["provider"] = route.Provider,
                    ["kind"] = route.Kind,
                    ["label"] = found[0].Statement.Split("; ", 2)[0],
                    ["route"] = found[0].Subject,
                });
            }
        }

        File.WriteAllText(
            Path.Combine(part.Directory, part.Record + "-routes.json"),
            recorded.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
            new UTF8Encoding(false));

        var checks = new ValidationService(Runner, database, TimeProvider.System, "live run").LoadConfiguration(part.Project);
        Assert.Equal(ConfigurationTrust.Trusted, checks.Trust);
        Assert.Equal(task.Steps[0].Checks.Select(c => c.Id), checks.Effective.RequiredGates.Select(g => g.Id));
    }

    /// <summary>
    /// Continues a run that stopped because nobody could answer an agent, in the shell, where somebody can.
    /// This asks the models, so it needs the same authorization as the run itself.
    ///
    /// What an agent asks for is declined, every time: a program that presses "allow" would be a way around
    /// the rule that only a person grants access. Who wants to grant something starts yav and answers at the
    /// keyboard. After <see cref="ShellDriver.QuestionsDeclined"/> questions the next one also ends the agent's
    /// turn, so that an agent that keeps asking cannot keep consuming usage.
    /// </summary>
    [LiveRunFact("resume", asksModels: true)]
    public Task A_run_that_waits_for_an_answer_is_continued_and_nothing_is_granted() => ResumeAsync(LivePart.FromEnvironment());

    internal async Task ResumeAsync(LivePart part)
    {
        // Checked again here, before anything is read or started: whoever runs this part asks the models.
        if (part.Get(LiveRunFactAttribute.Authorized) != "1")
        {
            throw new XunitException(@"Continuing a run asks the models, and scripts\live-run.ps1 did not say that the usage was authorized (-IAuthorizeUsage). Nothing was done.");
        }

        var runId = part.Need("YAV_LIVE_RUN_ID");
        var minutes = int.TryParse(part.Get("YAV_LIVE_MINUTES"), out var given) && given > 0 ? given : 30;
        ShellDriver? driver = null;
        var completed = false;
        var failure = string.Empty;
        IReadOnlyList<LiveDecision>? decisions = null;
        var before = LiveRunRecords.LastDecision(part.Home, runId);
        try
        {
            // Read before anything is started: a run that /resume would not continue fails here, and nothing is typed.
            ShellDriver.Resumable(part.Home, runId);
            driver = Start(part);
            await driver.WaitForPromptAsync();
            var followed = await driver.ResumeAsync(runId, TimeSpan.FromMinutes(minutes));
            await driver.EnterAsync("/status");
            await driver.ExitAsync();
            completed = true;
            Assert.False(followed.Stopped, $"The run was stopped after {minutes} minutes. What it had done until then is recorded.");
        }
        catch (Exception ex)
        {
            failure = "The part failed: " + ex.Message;
            throw;
        }
        finally
        {
            // What the record says about the questions is what the data of the run says was decided.
            decisions = LiveRunRecords.Decisions(part.Home, runId, before);
            Keep(part, driver, completed, failure, LiveRunRecords.Note(driver?.QuestionsAnswered ?? 0, decisions));
            driver?.Dispose();
        }

        Assert.True(decisions is not null, $"What was decided about the questions of the agents could not be read. See {part.Record}-screens.txt.");
        Assert.True(!decisions.Any(decision => decision.Granted), $"Something was granted while the run was continued. See {part.Record}-screens.txt.");
    }

    [LiveRunFact("inspect")]
    public Task What_the_run_left_is_looked_at_and_applied_the_way_a_user_does_it() => InspectAsync(LivePart.FromEnvironment());

    internal async Task InspectAsync(LivePart part)
    {
        var task = TaskOfTheRun(part);
        var apply = part.Get("YAV_LIVE_APPLY") == "1";
        var runId = apply ? part.Need("YAV_LIVE_RUN_ID") : null;
        using var driver = Start(part);
        var completed = false;
        try
        {
            await driver.WaitForPromptAsync();
            foreach (var command in InspectCommands(apply, runId))
            {
                await driver.EnterAsync(command, seconds: command.StartsWith("/apply", StringComparison.Ordinal) ? 300 : 180);
            }

            await driver.ExitAsync();
            completed = true;
        }
        finally
        {
            Keep(part, driver, completed);
        }

        await JudgeAsync(part, task.Steps[0].Checks, apply, runId);
    }

    /// <summary>
    /// What decides is what the checks of the task say about the project now, not what the run said. The verdict is
    /// recorded before what each check said. With -Apply the run has to be completed and every check has to end with 0.
    /// </summary>
    internal async Task JudgeAsync(LivePart part, IReadOnlyList<GateDefinition> checks, bool apply, string? runId)
    {
        var judgement = new StringBuilder();
        var results = new List<(string Check, int ExitCode)>();
        foreach (var check in checks)
        {
            var executable = Runner.Resolve(check.Command) ?? throw new FileNotFoundException(check.Command);
            var result = await Runner.RunAsync(
                new ProcessSpec(executable, check.Arguments, part.Project, new Dictionary<string, string?> { ["PYTHONDONTWRITEBYTECODE"] = "1" }),
                new CaptureOptions(Timeout: TimeSpan.FromSeconds(check.TimeoutSeconds)),
                CancellationToken.None);
            results.Add((check.Id, result.ExitCode));
            judgement.AppendLine($"{check.Id}: exit code {result.ExitCode}").AppendLine((result.StandardOutput + result.StandardError).Trim()).AppendLine();
        }

        // The verdict comes first: the script shows the beginning of the record.
        var (passed, verdict) = Judge(results, apply);
        File.WriteAllText(Path.Combine(part.Directory, part.Record + "-judgement.txt"), verdict + "\n\n" + judgement, new UTF8Encoding(false));
        output.WriteLine(verdict + "\n\n" + judgement);

        var problems = new List<string>();
        if (apply)
        {
            // A refused /apply ends at the prompt like one that went through. Only the record of the run tells them apart.
            var run = ShellDriver.StoredRun(part.Home, runId!);
            if (run?.State != RunState.Completed)
            {
                problems.Add($"/apply did not apply run {runId}: it is {(run is null ? "not recorded" : RunStateMachine.Display(run.State))}. See {part.Record}-screens.txt.");
            }
        }

        if (!passed)
        {
            problems.Add($"{verdict} See {part.Record}-judgement.txt.");
        }

        Assert.True(problems.Count == 0, string.Join(" ", problems));
    }
}
