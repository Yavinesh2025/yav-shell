using System.Text.Json.Nodes;
using Yav.Adapters;
using Yav.Console.Composition;
using Yav.Console.Shell;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

/// <summary>
/// The guided first run: a request that cannot start for something only the user can decide is not simply
/// refused. What is missing is asked right there, recorded as the command for it records it, and the same
/// request is sent again. Nothing is decided for the user.
/// </summary>
public class ShellSetupTests
{
    private const string Task = "Make the app say fixed.";

    private const string ModelAQuestion = "Model A - type its number, then Enter:";
    private const string ModelBQuestion = "Model B - type its number, then Enter:";
    private const string EffortAQuestion = "Effort for Model A - type one of the listed values, then Enter:";
    private const string ProjectQuestion = "Project folder - type or paste its path, then Enter:";
    private const string CodexRoute = "Use 'OpenAI API key' for runs of YAV, billed as stated above? Type yes to confirm:";
    private const string ReviewOnly = "Accept candidates of this project on the review alone? Type yes to confirm:";

    private static async Task<ShellHarness> StartedAsync(ShellOptions? options = null, Action<ShellHarness>? arrange = null)
    {
        var shell = new ShellHarness(options ?? new ShellOptions());
        arrange?.Invoke(shell);
        shell.Start();
        await shell.WaitForPromptAsync();
        return shell;
    }

    private static AppSettings Stored(ShellHarness shell) => new SettingsStore(shell.Paths).Load().Settings;

    private static void ScriptPassingTurns(ShellHarness shell) => shell.Agents
        .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Changed app.txt to say fixed."))
        .ReviewerTurn(Step.Review("pass"));

    /// <summary>A passing run whose Codex works with an API key: a route that is billed per token and needs a typed yes.</summary>
    private static void ApiKeyRoute(ShellHarness shell)
    {
        shell.WithPassingRun();
        shell.Agents.Codex(c => c["account"] = new JsonObject { ["type"] = "apiKey" });
    }

    private static string ConfigurationWithOneCheck(ShellHarness shell) =>
        shell.Services.Validation.Serialize(ProjectConfiguration.Empty with { Gates = [CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug")] });

    [Fact]
    public async Task A_first_request_asks_for_both_models_only_and_is_then_sent_through_the_subscriptions_without_asking()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false, AcknowledgeRoutes = false }, s =>
        {
            s.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
            ScriptPassingTurns(s);
        });
        shell.AssertShows("Model A and Model B are not chosen yet. Type your request: YAV lists the models your agents offer and asks you to choose.");

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "1");
        shell.AssertShows(
            "[SETUP]", "Nothing was sent. The request needs a decision of yours first; it is sent as soon as that is made.",
            "Codex (app server) (codex-app-server) - experimental interface",
            "1 model-a low, medium, high, xhigh provider default",
            "Claude Code (CLI) (claude-cli)",
            "3 opus low, medium, high, xhigh, max",
            "YAV lists what the agents report. It does not rank the models and does not choose for you.");
        await shell.AnswerWhenAskedAsync(ModelBQuestion, "3");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows(
            "Model A is model-a through codex-app-server, at the maximum effort the provider lists.",
            "Model B is opus through claude-cli, at the maximum effort the provider lists.",
            "Sending the request again.",
            "[READY]");
        Assert.Equal(new RoleSelection(CodexAppServerAdapter.AdapterId, "model-a"), Stored(shell).ModelA);
        Assert.Equal(new RoleSelection(ClaudeCliAdapter.AdapterId, "opus"), Stored(shell).ModelB);
        // A subscription the agent is signed in to is used without a question and without a record of a yes.
        shell.AssertDoesNotShow("for runs of YAV, billed as stated above?", "Whether it may be used for runs of YAV is your decision");
        Assert.False(shell.Services.Database.IsRouteAcknowledged($"{CodexAppServerAdapter.AdapterId}:Subscription:openai"));
        Assert.False(shell.Services.Database.IsRouteAcknowledged($"{ClaudeCliAdapter.AdapterId}:Subscription:firstParty"));
        Assert.Single(shell.Agents.CodexRequests("turn/start"));

        // Ready is not applied: the project is written by /apply and by nothing else.
        Assert.Equal("one\n", shell.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task An_answer_that_is_not_a_number_from_the_list_is_asked_for_again()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s => s.WithPassingRun());

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "the first one");
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "9", occurrence: 2);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "1", occurrence: 3);
        await shell.AnswerWhenAskedAsync(ModelBQuestion, "2");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("'the first one' is not a number from the list (1 to 4).", "'9' is not a number from the list (1 to 4).", "[READY]");
        Assert.Equal(new RoleSelection(CodexAppServerAdapter.AdapterId, "model-b"), Stored(shell).ModelB);
    }

    [Fact]
    public async Task Model_B_has_to_be_another_model_than_Model_A_under_Quality_Lock()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s => s.WithPassingRun());

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "1");
        await shell.AnswerWhenAskedAsync(ModelBQuestion, "1");
        await shell.AnswerWhenAskedAsync(ModelBQuestion, "2", occurrence: 2);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Model B has to be another model than Model A: a model that reviews its own work is not the dual-model workflow", "[READY]");
        Assert.Equal("model-a", Stored(shell).ModelA!.ModelId);
        Assert.Equal("model-b", Stored(shell).ModelB!.ModelId);
    }

    [Fact]
    public async Task Without_an_answer_no_model_is_chosen_and_nothing_is_sent()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s => s.WithPassingRun());

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, string.Empty);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Model A was not chosen.", "The request was not sent. Enter it again to be asked again.", "model-a-missing: Choose one with /models.");
        Assert.Null(Stored(shell).ModelA);
        Assert.Null(Stored(shell).ModelB);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));

        // Entered again, it is asked again.
        shell.Enter(Task);
        await shell.WaitForAskingAsync(ModelAQuestion, occurrence: 2);
    }

    [Fact]
    public async Task Only_the_missing_model_is_asked_for()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s =>
        {
            s.WithPassingRun();
            s.Services.Update(settings => settings with { ModelA = new RoleSelection(CodexAppServerAdapter.AdapterId, "model-a") });
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ModelBQuestion, "2");
        await shell.WaitForRunToEndAsync();

        shell.AssertDoesNotShow(ModelAQuestion);
        shell.AssertShows("[READY]");
    }

    [Fact]
    public async Task An_effort_the_model_does_not_rank_is_asked_for_exactly_and_never_chosen_for_the_user()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Agents.Codex(c => c["models"] = new JsonArray
            {
                new JsonObject { ["id"] = "model-a", ["efforts"] = new JsonArray { "low", "high", "max", "ultra" } },
                new JsonObject { ["id"] = "model-b", ["efforts"] = new JsonArray { "low", "high", "max" } },
            });
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(EffortAQuestion, "maximum");
        await shell.AnswerWhenAskedAsync(EffortAQuestion, "ULTRA", occurrence: 2);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows(
            "[BLOCKED] Model A: the provider lists effort values YAV cannot rank, so the maximum is not chosen for you.",
            "Model A, model-a, lists effort values YAV cannot rank, so the maximum is not chosen for you.",
            "'maximum' is not a value model-a lists. Listed: low, high, max, ultra.",
            "Model A effort: ultra",
            "[READY]");
        Assert.Equal("ultra", Stored(shell).ModelA!.EffortPreference);
        var started = shell.Agents.CodexRequests("thread/start");
        Assert.Contains(started, t => t["params"]!["config"]!["model_reasoning_effort"]!.GetValue<string>() == "ultra" && t["params"]!["model"]!.GetValue<string>() == "model-a");
    }

    [Fact]
    public async Task An_account_route_is_acknowledged_only_by_a_typed_yes()
    {
        await using var shell = await StartedAsync(new ShellOptions { AcknowledgeRoutes = false }, ApiKeyRoute);

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(CodexRoute, "y");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows(
            "Model A works through Codex (app server), with this account. Whether it may be used for runs of YAV is your decision:",
            "Account route: OpenAI API key",
            "Not confirmed. Nothing was changed.",
            "/login codex shows the route and asks again.",
            "The request was not sent.");
        Assert.False(shell.Services.Database.IsRouteAcknowledged($"{CodexAppServerAdapter.AdapterId}:ApiKey:openai"));
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(CodexRoute, "yes", occurrence: 2);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Acknowledged. It is asked again when the account route changes.", "[READY]");
        Assert.True(shell.Services.Database.IsRouteAcknowledged($"{CodexAppServerAdapter.AdapterId}:ApiKey:openai"));
    }

    [Fact]
    public async Task The_checks_a_project_file_names_are_shown_and_approved_only_by_a_typed_yes()
    {
        await using var shell = await StartedAsync(new ShellOptions { RequireChecks = true }, arrange: s =>
        {
            ScriptPassingTurns(s);
            s.Project.Write("yav.project.json", ConfigurationWithOneCheck(s));
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync("Approve this configuration? Type yes to confirm:", "yes");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows(
            "The project has no approved check. Its yav.project.json is not approved as it stands, and nothing of it runs before you approve it:",
            "These commands run on your machine with your rights whenever a candidate is checked.",
            "Approved. It applies from the next run",
            "[TESTS]",
            "[READY]");
        Assert.Equal(ConfigurationTrust.Trusted, shell.Services.Validation.LoadConfiguration(shell.Project.Path).Trust);
    }

    [Fact]
    public async Task Checks_the_files_suggest_are_proposed_and_approved_only_by_a_typed_yes()
    {
        await using var shell = await StartedAsync(new ShellOptions { RequireChecks = true }, arrange: s =>
        {
            s.Project.Write("package.json", """{ "name": "app", "scripts": { "test": "node test.js" } }""");

            // Model A changes nothing, so no check has to run for the request to end.
            s.Agents.ImplementerTurn(Step.Message("Nothing to change."));
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync("Approve these commands as the required checks of this project? Type yes to confirm:", "yes");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("The project has no approved check. Its files suggest these:", "npm run test", "Approved, and written to");
        var state = shell.Services.Validation.LoadConfiguration(shell.Project.Path);
        Assert.Equal(ConfigurationTrust.Trusted, state.Trust);
        Assert.Equal(["test"], state.Effective.RequiredGates.Select(g => g.Id));
        Assert.True(shell.Project.Exists("yav.project.json"));
    }

    [Fact]
    public async Task A_project_whose_files_suggest_no_checks_is_offered_acceptance_on_the_review_alone()
    {
        await using var shell = await StartedAsync(new ShellOptions { RequireChecks = true }, arrange: ScriptPassingTurns);

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ReviewOnly, "yes");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows(
            "The project has no approved check. Its files suggest none.",
            "Without an approved check that is required, nothing is run to test a candidate of this project: it can only be accepted on Model B's review.",
            "Accepted for this project. /quality gates required withdraws it.",
            "Sending the request again.",
            "[READY]");

        // The run honours the acceptance: the request sent again is not refused for the same reason.
        shell.AssertDoesNotShow("This was settled a moment ago");
        Assert.True(shell.Services.Database.IsReviewOnlyAccepted(shell.Project.Path));
        Assert.Single(shell.Agents.CodexRequests("thread/start"), t => t["params"]!["model"]!.GetValue<string>() == "model-a");
    }

    [Fact]
    public async Task Checks_that_were_declined_leave_the_review_alone_as_the_only_way_and_a_no_to_that_sends_nothing()
    {
        await using var shell = await StartedAsync(new ShellOptions { RequireChecks = true }, arrange: s =>
        {
            ScriptPassingTurns(s);
            s.Project.Write("yav.project.json", ConfigurationWithOneCheck(s));
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync("Approve this configuration? Type yes to confirm:", "no");
        await shell.AnswerWhenAskedAsync(ReviewOnly, "no");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("The request was not sent.", "gates-missing:");
        Assert.NotEqual(ConfigurationTrust.Trusted, shell.Services.Validation.LoadConfiguration(shell.Project.Path).Trust);
        Assert.False(shell.Services.Database.IsReviewOnlyAccepted(shell.Project.Path));
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Files_the_isolated_workspace_would_not_have_are_shown_and_accepted_only_by_a_typed_yes()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Project.Write(".gitignore", "build/\n");
            s.Project.CommitAll("ignore the build output");
            s.Project.Write("build/out.txt", "built\n");
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync("Accept this difference for the project? Type yes to confirm:", "yes");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows(
            "The isolated workspace would differ from the project: The project has ignored files that will be absent from the isolated workspace: build/.",
            "Accepted for this project:",
            "[READY]");
    }

    [Fact]
    public async Task Files_the_isolated_workspace_would_not_have_are_not_accepted_by_a_no()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Project.Write(".gitignore", "build/\n");
            s.Project.CommitAll("ignore the build output");
            s.Project.Write("build/out.txt", "built\n");
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync("Accept this difference for the project? Type yes to confirm:", "no");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Not confirmed. Nothing was changed.", "The request was not sent.", "workspace-gaps:");
        shell.AssertDoesNotShow("Accepted for this project:");
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task What_was_attached_goes_with_the_request_when_it_is_sent_again()
    {
        await using var shell = await StartedAsync(
            new ShellOptions { AcknowledgeRoutes = false, Files = [("src/app.txt", "one\n"), ("docs/spec notes.md", "The app says fixed.\n")] },
            ApiKeyRoute);
        await shell.EnterAndWaitAsync("/attach docs/spec notes.md");

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(CodexRoute, "yes");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Sending the request again.", "[READY]");
        // The implementer's turn is the one that carries the request; the reviewer's turn follows it.
        var prompt = shell.Agents.CodexRequests("turn/start").Select(t => t["params"]!["input"]![0]!["text"]!.GetValue<string>()).First(p => p.Contains(Task, StringComparison.Ordinal));
        Assert.Contains("spec notes.md", prompt, StringComparison.Ordinal);
        Assert.Empty(shell.Shell.Session.Attachments);
    }

    [Fact]
    public async Task A_request_typed_without_a_project_asks_for_its_folder_and_is_then_sent()
    {
        await using var shell = new ShellHarness().WithPassingRun();
        shell.StartIn(Path.Combine(shell.Paths.Home, "no such project"));
        await shell.WaitForPromptAsync();

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ProjectQuestion, "\"" + shell.Project.Path + "\"");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("No project is selected, so the request has nowhere to go yet.", $"Project: {shell.Project.Path}", "[READY]");
        Assert.Equal(shell.Project.Path, shell.Shell.Session.ProjectPath);
    }

    [Fact]
    public async Task A_folder_that_does_not_exist_is_asked_for_again_and_no_answer_sends_nothing()
    {
        await using var shell = new ShellHarness().WithPassingRun();
        var missing = Path.Combine(shell.Paths.Home, "no such project");
        shell.StartIn(missing);
        await shell.WaitForPromptAsync();

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ProjectQuestion, missing);
        await shell.AnswerWhenAskedAsync(ProjectQuestion, string.Empty, occurrence: 2);
        await shell.WaitForPromptAsync();

        shell.AssertShows($"The directory '{missing}' does not exist. The project was not changed.", "The request was not sent. /open <path> selects a project.");
        Assert.Null(shell.Shell.Session.ProjectPath);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Nothing_is_asked_when_nobody_can_answer()
    {
        // Lines from a file, and output nobody sees: the input of 'yav < commands.txt > out.txt'.
        await using var shell = new ShellHarness(new ShellOptions { ChooseModels = false, AcknowledgeRoutes = false, Lines = new ScriptedLines(), LinesAreSeen = false });
        shell.Start();

        shell.Enter(Task);
        await shell.WaitUntilAsync(() => shell.Terminal.Text.Contains("model-a-missing: Choose one with /models.", StringComparison.Ordinal), "the end of the refused run");

        shell.AssertShows("[BLOCKED]", "No implementation model (Model A) is selected.", "Model A and Model B are not chosen yet. YAV does not choose models for you: see /models.");
        shell.AssertDoesNotShow("[SETUP]", ModelAQuestion, "asks you to choose");
        Assert.Null(Stored(shell).ModelA);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Codex_that_is_not_signed_in_is_offered_its_own_sign_in_and_Claude_Code_is_only_told_how_to_sign_in()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s =>
        {
            s.WithPassingRun();
            s.Agents.Codex(c =>
            {
                c["account"] = null;
                c["loginStatus"] = "Not logged in";
            });
            s.Agents.Claude(c => c["auth"] = new JsonObject { ["loggedIn"] = false });
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync("Start Codex's own sign-in now?", "no");
        await shell.WaitForRunToEndAsync();

        // Anthropic does not permit a third party to offer Claude.ai login: YAV says how, and starts nothing.
        shell.AssertShows(
            "Codex (app server) is installed but not signed in, so it lists no models.",
            "Claude Code (CLI) is installed but not signed in, so it lists no models.",
            "Sign in with Claude Code itself: open a console, run claude and follow its sign-in (or run claude auth login), then type the request again.",
            "The request was not sent.");
        shell.AssertDoesNotShow("Start Claude Code's own sign-in now?", "The sign-in ended");
        Assert.Single(shell.Terminal.Lines, row => row.StartsWith("Start ", StringComparison.Ordinal));
        Assert.Null(Stored(shell).ModelA);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task A_role_whose_Claude_Code_is_not_signed_in_is_told_how_to_sign_in_and_nothing_is_asked_or_started()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Services.Update(settings => settings with { ModelB = new RoleSelection(ClaudeCliAdapter.AdapterId, "opus") });
            s.Agents.Claude(c => c["auth"] = new JsonObject { ["loggedIn"] = false });
        });

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows(
            "[BLOCKED]", "the anthropic agent is not signed in.",
            "Claude Code (CLI) is not signed in, and Model B works through it.",
            "Sign in with Claude Code itself: open a console, run claude and follow its sign-in (or run claude auth login), then type the request again.",
            "The request was not sent.");
        shell.AssertDoesNotShow("Type yes to confirm:", "The sign-in ended");
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task The_first_run_marks_the_experimental_interface_as_models_does()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s => s.WithPassingRun());

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, string.Empty);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Codex (app server) (codex-app-server) - experimental interface", "Claude Code (CLI) (claude-cli)");
        shell.AssertDoesNotShow("Claude Code (CLI) (claude-cli) - experimental interface");
    }

    [Fact]
    public void The_folder_of_downloads_is_no_project_but_a_folder_in_it_is()
    {
        var downloads = KnownFolders.Downloads;

        Assert.False(InteractiveShell.IsSensibleProject(downloads));
        Assert.False(InteractiveShell.IsSensibleProject(downloads + Path.DirectorySeparatorChar));
        Assert.True(InteractiveShell.IsSensibleProject(Path.Combine(downloads, "my project")));
        Assert.True(Path.IsPathFullyQualified(downloads));
    }

    [Fact]
    public async Task Started_from_a_shell_without_a_path_the_directory_it_starts_in_is_the_project()
    {
        var directory = Environment.CurrentDirectory;

        // Wide enough that the prompt with the long directory of the test run stays on one row.
        await using var shell = new ShellHarness(new ShellOptions { Width = 400 }).WithPassingRun();

        shell.StartWithoutPath(startedOutsideAShell: false);
        await shell.WaitForPromptAsync();

        Assert.Equal(InteractiveShell.IsSensibleProject(directory) ? Path.GetFullPath(directory) : null, shell.Shell.Session.ProjectPath);
    }

    [Fact]
    public async Task Started_outside_a_shell_without_a_path_no_project_is_selected_and_the_first_request_asks_for_it()
    {
        await using var shell = new ShellHarness().WithPassingRun();

        shell.StartWithoutPath(startedOutsideAShell: true);
        await shell.WaitForPromptAsync();

        Assert.Null(shell.Shell.Session.ProjectPath);
        shell.AssertShows("Project: none selected - use /open <path>");

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ProjectQuestion, shell.Project.Path);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows($"Project: {shell.Project.Path}", "[READY]");
    }

    [Fact]
    public async Task A_refusal_for_something_no_answer_can_settle_asks_nothing()
    {
        // The agent lists no models, so the models that were chosen cannot be verified. Strict policy refuses that,
        // and no question would change it.
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Agents.Codex(c => c["models"] = new JsonArray());
        });

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[BLOCKED]", "model-unverified");
        shell.AssertDoesNotShow("[SETUP]");
    }

    [Fact]
    public async Task Quality_shows_that_the_project_is_accepted_on_the_review_alone_and_gates_required_withdraws_it()
    {
        await using var shell = await StartedAsync(new ShellOptions { RequireChecks = true }, arrange: s => s.Services.Database.AcceptReviewOnly(s.Project.Path, "test"));

        await shell.EnterAndWaitAsync("/quality");
        shell.AssertShows("This project: review only, as you accepted: no check is approved for it, so a candidate is accepted on the review alone");

        await shell.EnterAndWaitAsync("/quality gates required");

        shell.AssertShows("Withdrawn for this project as well: it does not run again until an approved check is required for it, or you accept the review alone once more.");
        Assert.False(shell.Services.Database.IsReviewOnlyAccepted(shell.Project.Path));
        await shell.EnterAndWaitAsync("/quality");
        Assert.Single(shell.Terminal.Lines, row => row.Contains("This project:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_project_whose_approved_checks_are_all_optional_is_offered_acceptance_on_the_review_alone()
    {
        await using var shell = await StartedAsync(new ShellOptions { RequireChecks = true }, arrange: s =>
        {
            ScriptPassingTurns(s);
            s.TrustGates(CoordinatorHarness.TextGate("lint", "src/app.txt", "fixed", required: false));
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ReviewOnly, "yes");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("The one check approved for the project is optional, and a run does not run an optional check.", "[READY]");
        await shell.EnterAndWaitAsync("/quality");
        shell.AssertShows("This project: review only, as you accepted: none of its approved checks is required, and a run does not run an optional one");
    }

    [Fact]
    public async Task A_failure_while_an_answer_is_recorded_is_said_and_the_shell_goes_on()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s => s.WithPassingRun());
        var settings = shell.Paths.SettingsFile;
        File.SetAttributes(settings, File.GetAttributes(settings) | FileAttributes.ReadOnly);
        try
        {
            shell.Enter(Task);
            await shell.AnswerWhenAskedAsync(ModelAQuestion, "1");
            await shell.WaitForRunToEndAsync();

            Assert.True(shell.Shows("UnauthorizedAccessException:") || shell.Shows("IOException:"), shell.Terminal.Text);
            shell.AssertShows("The request was not sent. Enter it again to be asked again.");
            Assert.Empty(shell.Agents.CodexRequests("thread/start"));

            // The shell is still there and answers.
            await shell.EnterAndWaitAsync("/quality");
            shell.AssertShows("Quality Lock");
        }
        finally
        {
            File.SetAttributes(settings, File.GetAttributes(settings) & ~FileAttributes.ReadOnly);
        }
    }

    [Fact]
    public async Task What_was_attached_before_a_project_was_selected_goes_with_the_request()
    {
        await using var shell = new ShellHarness(new ShellOptions { Width = 400 }).WithPassingRun();
        shell.StartIn(Path.Combine(shell.Paths.Home, "no such project"));
        await shell.WaitForPromptAsync();
        await shell.EnterAndWaitAsync("/attach " + Path.Combine(shell.Project.Path, "README.md"));

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ProjectQuestion, shell.Project.Path);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[READY]");
        var prompt = shell.Agents.CodexRequests("turn/start").Select(t => t["params"]!["input"]![0]!["text"]!.GetValue<string>()).First(p => p.Contains(Task, StringComparison.Ordinal));
        Assert.Contains("README.md", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_drive_is_not_taken_for_the_project_of_a_request()
    {
        // No model is chosen: should the drive be taken after all, the request stops at the first question.
        await using var shell = new ShellHarness(new ShellOptions { ChooseModels = false }).WithPassingRun();
        shell.StartIn(Path.Combine(shell.Paths.Home, "no such project"));
        await shell.WaitForPromptAsync();
        var drive = Path.GetPathRoot(shell.Project.Path)!;

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ProjectQuestion, drive);
        await shell.AnswerWhenAskedAsync(ProjectQuestion, string.Empty, occurrence: 2);
        await shell.WaitForPromptAsync();

        shell.AssertShows($"'{drive}' is a drive, the profile, the Desktop, Documents, Downloads or a system folder, not a project", "The request was not sent. /open <path> selects a project.");
        Assert.Null(shell.Shell.Session.ProjectPath);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Without_a_project_nothing_is_asked_when_nobody_can_answer()
    {
        await using var shell = new ShellHarness(new ShellOptions { Lines = new ScriptedLines(), LinesAreSeen = false }).WithPassingRun();
        shell.StartIn(Path.Combine(shell.Paths.Home, "no such project"));

        shell.Enter(Task);
        await shell.WaitUntilAsync(() => shell.Terminal.Text.Contains("No project is selected, so the request was not sent.", StringComparison.Ordinal), "the refused request");

        shell.AssertDoesNotShow("[SETUP]", ProjectQuestion);
        Assert.Null(shell.Shell.Session.ProjectPath);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Three_answers_that_are_not_on_the_list_choose_nothing_and_send_nothing()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s => s.WithPassingRun());

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "0");
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "5", occurrence: 2);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "x", occurrence: 3);
        await shell.WaitForRunToEndAsync(seconds: 30);

        Assert.Equal(3, shell.Terminal.Lines.Count(r => r.StartsWith(ModelAQuestion, StringComparison.Ordinal)));
        shell.AssertShows("Model A was not chosen.", "The request was not sent. Enter it again to be asked again.");
        Assert.Null(Stored(shell).ModelA);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Control_C_at_a_question_chooses_nothing_and_sends_nothing()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s => s.WithPassingRun());

        shell.Enter(Task);
        await shell.WaitForAskingAsync(ModelAQuestion);
        shell.Keys.Press(ConsoleKey.C, control: true, character: '\u0003');
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Model A was not chosen.", "The request was not sent. Enter it again to be asked again.");
        Assert.Null(Stored(shell).ModelA);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Control_C_at_the_approval_of_the_checks_does_not_lead_to_the_review_alone()
    {
        await using var shell = await StartedAsync(new ShellOptions { RequireChecks = true }, arrange: s =>
        {
            ScriptPassingTurns(s);
            s.Project.Write("yav.project.json", ConfigurationWithOneCheck(s));
        });

        shell.Enter(Task);
        await shell.WaitForAskingAsync("Approve this configuration? Type yes to confirm:");
        shell.Keys.Press(ConsoleKey.C, control: true, character: '\u0003');
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("The request was not sent. Enter it again to be asked again.");
        shell.AssertDoesNotShow(ReviewOnly);
        Assert.False(shell.Services.Database.IsReviewOnlyAccepted(shell.Project.Path));
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Control_C_as_a_signal_cancels_the_question_that_is_open_and_YAV_goes_on()
    {
        // Plain input: Control+C does not arrive as a key there, but as a signal that asks Interrupt.
        await using var shell = new ShellHarness(new ShellOptions { ChooseModels = false, Lines = new ScriptedLines() }).WithPassingRun();
        shell.Start();

        shell.Enter(Task);
        await shell.WaitForAskingAsync(ModelAQuestion);
        Assert.True(shell.Shell.Interrupt());
        await shell.WaitForAsync("The request was not sent. Enter it again to be asked again.");

        // Nothing is open any more: the decision is left to whoever asked, as before.
        Assert.False(shell.Shell.Interrupt());
        Assert.Null(Stored(shell).ModelA);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Model_A_has_to_be_another_model_than_a_Model_B_that_stays_under_Quality_Lock()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s =>
        {
            s.WithPassingRun();
            s.Services.Update(settings => settings with { ModelB = new RoleSelection(CodexAppServerAdapter.AdapterId, "model-b") });
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "2");
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "1", occurrence: 2);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Model A has to be another model than Model B: a model that reviews its own work is not the dual-model workflow", "[READY]");
        shell.AssertDoesNotShow("single-model", ModelBQuestion);
        Assert.Equal("model-a", Stored(shell).ModelA!.ModelId);
    }

    [Fact]
    public async Task A_refusal_that_no_question_settled_is_not_sent_again()
    {
        // An exact effort for a model that lists no effort values: no question of the guide changes that.
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Services.Update(settings => settings with { ModelA = new RoleSelection(CodexAppServerAdapter.AdapterId, "model-a", "high") });
            s.Agents.Codex(c => c["models"] = new JsonArray
            {
                new JsonObject { ["id"] = "model-a", ["efforts"] = new JsonArray() },
                new JsonObject { ["id"] = "model-b", ["efforts"] = new JsonArray { "low", "high" } },
            });
        });

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Nothing here could be settled by an answer, so the request was not sent again.", "effort-unsupported:");
        shell.AssertDoesNotShow("Sending the request again.", "This was settled a moment ago");
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public void A_request_is_asked_for_until_the_rounds_are_used_up_and_not_when_what_was_settled_comes_back()
    {
        var gates = new ProfileProblem(null, ProblemSeverity.Blocking, "gates-missing", "No checks.", null);
        var model = new ProfileProblem(AgentRole.Implementer, ProblemSeverity.Blocking, "model-a-missing", "No model.", null);
        var other = new ProfileProblem(null, ProblemSeverity.Blocking, "workspace-unsupported", "Merge conflicts.", null);
        var none = new HashSet<string>(StringComparer.Ordinal);
        var gatesSettled = new HashSet<string>(StringComparer.Ordinal) { "gates-missing" };

        Assert.Equal(SetupStep.Ask, SetupProblems.Next([model], none, 0).Step);
        Assert.Equal(SetupStep.Ask, SetupProblems.Next([model], none, SetupProblems.MaxRounds - 1).Step);
        var (step, open) = SetupProblems.Next([model], none, SetupProblems.MaxRounds);
        Assert.Equal(SetupStep.TooManyRounds, step);
        Assert.Equal(["model-a-missing:Implementer"], open.Select(SetupProblems.Key));

        var (again, recurring) = SetupProblems.Next([gates, model], gatesSettled, 1);
        Assert.Equal(SetupStep.Recurring, again);
        Assert.Equal(["gates-missing"], recurring.Select(SetupProblems.Key));

        Assert.Equal(SetupStep.Nothing, SetupProblems.Next([other], none, 0).Step);
    }

    [Fact]
    public async Task A_decline_keeps_what_was_attached_and_does_not_advise_what_was_settled_before_it()
    {
        await using var shell = await StartedAsync(new ShellOptions { ChooseModels = false }, s => s.WithPassingRun());
        await shell.EnterAndWaitAsync("/attach README.md");

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync(ModelAQuestion, "1");
        await shell.AnswerWhenAskedAsync(ModelBQuestion, string.Empty);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows(
            "Model B was not chosen.",
            "The request was not sent. Enter it again to be asked again.",
            "The file that was attached stays attached to the next request.",
            "model-b-missing:");
        shell.AssertDoesNotShow("model-a-missing:");
        Assert.Single(shell.Shell.Session.Attachments, a => a.EndsWith("README.md", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("model-a", Stored(shell).ModelA!.ModelId);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public void Only_blocking_problems_an_answer_can_settle_are_open_and_a_settled_one_is_not_open_again()
    {
        var problems = new List<ProfileProblem>
        {
            new(AgentRole.Implementer, ProblemSeverity.Blocking, "model-a-missing", "No implementation model.", null),
            new(AgentRole.Reviewer, ProblemSeverity.Blocking, "route-unacknowledged", "Not acknowledged.", null),
            new(AgentRole.Implementer, ProblemSeverity.Blocking, "route-unacknowledged", "Not acknowledged.", null),
            new(null, ProblemSeverity.Blocking, "workspace-unsupported", "Merge conflicts.", null),
            new(null, ProblemSeverity.Warning, "gates-untrusted", "Not approved.", null),
            new(null, ProblemSeverity.Info, "gates-missing", "Said as information only.", null),
        };
        var settled = new HashSet<string>(StringComparer.Ordinal) { "route-unacknowledged:Reviewer" };

        var open = SetupProblems.Open(problems, settled);
        var recurring = SetupProblems.Recurring(problems, settled);

        Assert.Equal(["model-a-missing:Implementer", "route-unacknowledged:Implementer"], open.Select(SetupProblems.Key));
        Assert.Equal(["route-unacknowledged:Reviewer"], recurring.Select(SetupProblems.Key));
        Assert.Equal("gates-missing", SetupProblems.Key(problems[5]));
        Assert.False(SetupProblems.CanSettle("workspace-unsupported"));
        Assert.True(SetupProblems.CanSettle("workspace-gaps"));
        // Choosing again does not install an agent that is not found: the refusal says what to do.
        Assert.False(SetupProblems.CanSettle("adapter-not-found"));
        Assert.True(SetupProblems.CanSettle("model-unavailable"));
    }
}
