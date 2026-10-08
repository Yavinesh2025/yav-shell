using System.Text.Json.Nodes;
using Yav.Adapters;
using Yav.Console.Composition;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

/// <summary>/models, /effort, /quality, /speed, /login, /adaptive and /optimization as the user meets them.</summary>
public class ShellModelCommandTests
{
    private const string Task = "Make the app say fixed.";

    private static async Task<ShellHarness> StartedAsync(ShellOptions? options = null, Action<ShellHarness>? arrange = null)
    {
        var shell = new ShellHarness(options ?? new ShellOptions());
        arrange?.Invoke(shell);
        shell.Start();
        await shell.WaitForPromptAsync();
        return shell;
    }

    private static AppSettings Stored(ShellHarness shell) => new SettingsStore(shell.Paths).Load().Settings;

    private static JsonArray Models(params (string Id, string[] Efforts, bool Faster)[] models)
    {
        var list = new JsonArray();
        foreach (var (id, efforts, faster) in models)
        {
            var described = new JsonArray();
            foreach (var effort in efforts)
            {
                described.Add(new JsonObject
                {
                    ["value"] = effort,
                    ["description"] = effort == "ultra" ? "Maximum reasoning with automatic task delegation" : effort + " reasoning",
                });
            }

            list.Add(new JsonObject
            {
                ["id"] = id,
                ["efforts"] = described,
                ["serviceTiers"] = faster
                    ? new JsonArray { new JsonObject { ["id"] = "priority", ["name"] = "Fast", ["description"] = "2x speed, increased usage" } }
                    : [],
            });
        }

        return list;
    }

    [Fact]
    public async Task Models_lists_what_the_providers_report_and_marks_the_two_that_are_chosen()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/models");

        shell.AssertShows(
            "Models the providers list for your accounts",
            "(codex-app-server)",
            "A model-a MODEL-A low, medium, high, xhigh Fast",
            "B model-b MODEL-B low, medium, high, xhigh, max Fast",
            "YAV lists what the providers report. It does not rank models and does not choose for you.");
    }

    [Theory]
    [InlineData("/models b claude-cli opus", "Asking Claude Code (CLI) for its version, account and models. No inference is requested.", "Model B is opus through claude-cli")]
    [InlineData("/login claude", "Asking Claude Code (CLI) for its version, account and models. No inference is requested.", "Claude Code (CLI)")]
    [InlineData("/status", "Asking Codex (app server) for its version, account and models. No inference is requested.", "  Role")]
    public async Task A_command_that_has_to_ask_an_agent_says_so_before_it_waits_for_the_answer(string command, string said, string answer)
    {
        // A real agent takes seconds to start and to answer. With Claude Code 2.1.284 it was twelve.
        await using var shell = await StartedAsync();

        // The shell reads the agents of both roles once at its start, in the background. That reading is waited for
        // and then forgotten, as it is once it is old: the command has to ask, whenever it is entered.
        await shell.WaitUntilAsync(() => shell.Services.Coordinator.Catalog.IsFresh(CodexAppServerAdapter.AdapterId), "the agents read at the start");
        shell.Services.Coordinator.Catalog.Invalidate();

        await shell.EnterAndWaitAsync(command);

        var rows = shell.Terminal.Lines.ToList();
        var asked = rows.FindIndex(row => row.Contains(said, StringComparison.Ordinal));
        var answered = rows.FindIndex(row => row.StartsWith(answer, StringComparison.Ordinal));
        Assert.True(asked >= 0, "It was not said that the agent is asked:\n" + string.Join('\n', rows));
        Assert.True(answered > asked, "The answer did not come after it:\n" + string.Join('\n', rows));
    }

    [Fact]
    public async Task What_an_agent_said_a_moment_ago_is_not_asked_for_again()
    {
        await using var shell = await StartedAsync();
        await shell.EnterAndWaitAsync("/models b claude-cli opus");

        await shell.EnterAndWaitAsync("/models b claude-cli sonnet");

        Assert.Single(shell.Terminal.Lines, row => row.Contains("Asking Claude Code (CLI)", StringComparison.Ordinal));
        shell.AssertShows("Model B is sonnet through claude-cli");
    }

    [Fact]
    public async Task A_model_is_chosen_by_naming_the_adapter_and_the_model()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/models b codex-exec model-a");

        shell.AssertShows("Model B is model-a through codex-exec, at the maximum effort the provider lists.");
        Assert.Equal(new RoleSelection(CodexExecAdapter.AdapterId, "model-a"), Stored(shell).ModelB);
        Assert.Equal("model-a", Stored(shell).ModelA!.ModelId);
    }

    [Theory]
    [InlineData("/models a codex-app-server gpt-unknown", "codex-app-server does not list a model 'gpt-unknown' for your account, so it was not chosen.")]
    [InlineData("/models a gpt-unknown", "No adapter lists a model 'gpt-unknown'.")]
    [InlineData("/models a model-b", "'model-b' is listed by codex-app-server and codex-exec. Name the adapter: /models a <adapter> model-b")]
    [InlineData("/models a nonsense model-b", "'nonsense' is not an adapter. Available: claude-cli, codex-app-server, codex-exec.")]
    [InlineData("/models a", "Usage: /models a <adapter> <model>")]
    [InlineData("/models best", "'best' is not something /models does.")]
    public async Task A_model_that_cannot_be_chosen_changes_nothing_and_no_other_model_is_taken_instead(string command, string expected)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows(expected);
        Assert.Equal(new RoleSelection(CodexAppServerAdapter.AdapterId, "model-a"), Stored(shell).ModelA);
        Assert.Equal(new RoleSelection(CodexAppServerAdapter.AdapterId, "model-b"), Stored(shell).ModelB);
    }

    [Fact]
    public async Task The_same_model_for_both_roles_is_pointed_out()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/models a codex-app-server model-b");

        shell.AssertShows("Model A and Model B are now the same model. That is not the dual-model workflow; with Quality Lock it does not run.");
    }

    [Fact]
    public async Task Swap_exchanges_the_roles()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/models swap");

        shell.AssertShows("Model A (implements) is now model-b (codex-app-server); Model B (reviews) is now model-a (codex-app-server).");
        Assert.Equal("model-b", Stored(shell).ModelA!.ModelId);
        Assert.Equal("model-a", Stored(shell).ModelB!.ModelId);
    }

    [Fact]
    public async Task Effort_shows_for_each_role_what_maximum_means_for_its_model()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/effort");

        shell.AssertShows(
            "Model A: model-a (codex-app-server)",
            "Requested: maximum = xhigh",
            "Model B: model-b (codex-app-server)",
            "Requested: maximum = max",
            "Provider's default: medium");
    }

    [Fact]
    public async Task An_effort_below_the_maximum_is_the_users_choice_and_is_said_to_be_below()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/effort a low");

        shell.AssertShows(
            "'low' is below the maximum this model lists ('xhigh'). That is your choice; /effort a maximum returns to it.",
            "Model A effort: low");
        Assert.Equal("low", Stored(shell).ModelA!.EffortPreference);

        await shell.EnterAndWaitAsync("/effort a maximum");

        shell.AssertShows("Model A effort: maximum supported");
        Assert.True(Stored(shell).ModelA!.WantsMaximum);
    }

    [Theory]
    [InlineData("/effort a ultra", "model-a does not list the effort 'ultra'. Listed: low, medium, high, xhigh. Nothing was changed, and nothing is lowered for you.")]
    [InlineData("/effort c high", "Usage: /effort a|b <value>|maximum")]
    [InlineData("/effort a", "Usage: /effort a|b <value>|maximum")]
    public async Task An_effort_the_model_does_not_list_is_refused_and_nothing_is_lowered(string command, string expected)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows(expected);
        Assert.True(Stored(shell).ModelA!.WantsMaximum);
        Assert.True(Stored(shell).ModelB!.WantsMaximum);
    }

    [Fact]
    public async Task Max_is_the_value_of_that_name_where_the_model_lists_it_and_the_maximum_where_it_does_not()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/effort b max");
        await shell.EnterAndWaitAsync("/effort a max");

        Assert.Equal("max", Stored(shell).ModelB!.EffortPreference);
        Assert.True(Stored(shell).ModelA!.WantsMaximum);
    }

    [Fact]
    public async Task An_effort_value_that_cannot_be_ranked_is_never_chosen_for_the_user_and_blocks_the_run_before_anything_is_sent()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Agents.Codex(c => c["models"] = Models(
                ("model-a", ["low", "high", "max", "ultra"], true),
                ("model-b", ["low", "high", "max"], true)));
        });

        await shell.EnterAndWaitAsync("/effort");
        shell.AssertShows(
            "Requested: maximum - not resolved: choose the exact value",
            "ultra Maximum reasoning with automatic task delegation");

        shell.Enter(Task);

        // The value is asked for; without an answer none is taken and nothing is sent.
        await shell.AnswerWhenAskedAsync("Effort for Model A - type one of the listed values, then Enter:", string.Empty);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[BLOCKED]", "effort-unranked", "The request was not sent.");
        Assert.True(Stored(shell).ModelA!.WantsMaximum);
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
        Assert.Empty(shell.Agents.CodexRequests("turn/start"));

        await shell.EnterAndWaitAsync("/effort a max");
        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[READY]");
        var started = shell.Agents.CodexRequests("thread/start");
        Assert.Contains(started, t => t["params"]!["config"]!["model_reasoning_effort"]!.GetValue<string>() == "max" && t["params"]!["model"]!.GetValue<string>() == "model-a");
    }

    [Fact]
    public async Task Quality_shows_what_is_held_and_what_a_candidate_has_to_pass()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/quality");

        shell.AssertShows(
            "Quality Lock: ON: models, effort, review and billing route are held to what you chose",
            "Policy: strict: a setting the provider did not confirm blocks the run",
            "Review by Model B: required, in its own conversation, read-only",
            "Required checks: optional (the default): a project with no approved check that is required runs on the review alone",
            "Repair cycles: 2 after the first candidate",
            "Failures that were already there: your decision: waive them, or fix them first",
            "It cannot make a model's answer correct or the same twice.");
    }

    [Theory]
    [InlineData("/quality lock off", "Quality Lock: OFF.")]
    [InlineData("/quality strict off", "Relaxed policy: a setting the provider did not confirm is shown as Requested / Unverified and the run goes on.")]
    [InlineData("/quality gates optional", "Required checks are optional, the default: a project with no approved check that is required runs on the review alone, without asking.")]
    [InlineData("/quality preexisting repair", "A required check that already failed before the task is sent to Model A for repair, within the repair limit.")]
    public async Task Quality_is_changed_only_by_the_user_and_says_what_the_change_means(string command, string expected)
    {
        await using var shell = await StartedAsync(new ShellOptions { RequireChecks = true });

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows(expected, "This applies to the next run.");
        var stored = Stored(shell);
        Assert.Equal(!command.Contains("lock", StringComparison.Ordinal), stored.QualityLock);
        Assert.Equal(!command.Contains("strict", StringComparison.Ordinal), stored.Strict);
        Assert.Equal(!command.Contains("gates", StringComparison.Ordinal), stored.RequireGates);
        Assert.Equal(command.Contains("preexisting", StringComparison.Ordinal), stored.RepairPreExistingFailures);
    }

    [Theory]
    [InlineData("/quality lock maybe")]
    [InlineData("/quality gates off")]
    [InlineData("/quality lock")]
    [InlineData("/quality review off")]
    public async Task Quality_that_is_not_understood_changes_nothing(string command)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows("Usage: /quality");
        Assert.Equal(new AppSettings().ToPolicy(), Stored(shell).ToPolicy());
    }

    [Fact]
    public async Task The_banner_and_the_status_say_when_quality_lock_is_off()
    {
        await using var shell = await StartedAsync(new ShellOptions { Configure = s => _ = s });
        await shell.EnterAndWaitAsync("/quality lock off");

        await shell.EnterAndWaitAsync("/status");

        shell.AssertShows("Quality Lock: OFF");
    }

    [Fact]
    public async Task Speed_shows_what_the_provider_offers_and_who_pays_without_changing_anything()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/speed");

        shell.AssertShows(
            "Mode: STANDARD",
            "It never changes the model.",
            "Model A: model-a (codex-app-server)",
            "Faster tier: Fast (fast)",
            "Provider says: Faster responses at a higher credit rate.",
            "Billed through: ChatGPT plan (pro)",
            "Authorized by you: no");
        Assert.Equal(ProviderSpeedMode.Standard, Stored(shell).Speed);
    }

    [Fact]
    public async Task Paid_speed_is_turned_on_only_after_the_billing_was_shown_and_the_user_said_yes()
    {
        await using var shell = await StartedAsync(arrange: s => s.WithPassingRun());

        shell.Enter("/speed provider");
        await shell.WaitForAsync("Type yes to confirm:");
        shell.AssertShows(
            "The faster tier is billed differently from standard serving, as the provider describes above. YAV does not know the amount and shows no estimate of it.",
            "Use the faster tier for the roles above, with the billing the provider describes?");
        Assert.Equal(ProviderSpeedMode.Standard, Stored(shell).Speed);

        await shell.EnterAndWaitAsync("yes");

        shell.AssertShows("Provider speed: requested for the next run.");
        Assert.Equal(ProviderSpeedMode.Provider, Stored(shell).Speed);
        Assert.True(shell.Services.Database.IsPaidSpeedAuthorized($"{CodexAppServerAdapter.AdapterId}:Subscription:openai"));

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[READY]");
        var threads = shell.Agents.CodexRequests("thread/start");
        Assert.Equal(2, threads.Count);
        Assert.All(threads, t => Assert.Equal("fast", t["params"]!["serviceTier"]!.GetValue<string>()));
        Assert.Equal(["model-a", "model-b"], threads.Select(t => t["params"]!["model"]!.GetValue<string>()).Order().ToArray());
    }

    [Theory]
    [InlineData("no")]
    [InlineData("y")]
    [InlineData("")]
    public async Task Paid_speed_stays_off_when_the_user_does_not_say_yes(string answer)
    {
        await using var shell = await StartedAsync(arrange: s => s.WithPassingRun());

        shell.Enter("/speed provider");
        await shell.WaitForAsync("Type yes to confirm:");
        await shell.EnterAndWaitAsync(answer);

        shell.AssertShows("Not confirmed. Nothing was changed.");
        Assert.Equal(ProviderSpeedMode.Standard, Stored(shell).Speed);
        Assert.False(shell.Services.Database.IsPaidSpeedAuthorized($"{CodexAppServerAdapter.AdapterId}:Subscription:openai"));

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();
        Assert.All(shell.Agents.CodexRequests("thread/start"), t => Assert.Null(t["params"]!["serviceTier"]));
    }

    [Fact]
    public async Task Speed_that_a_model_does_not_offer_is_not_turned_on_and_no_other_model_is_taken()
    {
        await using var shell = await StartedAsync(arrange: s => s.Agents.Codex(c => c["models"] = Models(
            ("model-a", ["low", "high"], true),
            ("model-b", ["low", "high"], false))));

        await shell.EnterAndWaitAsync("/speed provider");

        shell.AssertShows(
            "Faster tier: Unavailable: the provider lists none for this model and account",
            "Provider speed was not turned on: it is not available for every role. Standard speed stays in use, and no model is exchanged for a faster one.");
        shell.AssertDoesNotShow("Type yes to confirm");
        Assert.Equal(ProviderSpeedMode.Standard, Stored(shell).Speed);
        Assert.Equal("model-b", Stored(shell).ModelB!.ModelId);
    }

    [Fact]
    public async Task Standard_speed_is_returned_to_without_a_question()
    {
        await using var shell = await StartedAsync(new ShellOptions { Configure = s => _ = s });
        shell.Enter("/speed provider");
        await shell.WaitForAsync("Type yes to confirm:");
        await shell.EnterAndWaitAsync("yes");

        await shell.EnterAndWaitAsync("/speed standard");

        shell.AssertShows("Provider speed: STANDARD. Models and effort are unchanged.");
        Assert.Equal(ProviderSpeedMode.Standard, Stored(shell).Speed);
    }

    [Fact]
    public async Task Login_shows_the_route_each_agent_uses_and_how_it_is_billed()
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync("/login");

        shell.AssertShows(
            "Account route: ChatGPT plan (pro)",
            "Billing: included in the subscription",
            "For this integration: acknowledged by you",
            "  │ Usage counts against the limits of your ChatGPT plan.",
            "YAV never sees a password or a token.");
    }

    [Fact]
    public async Task A_route_that_was_not_acknowledged_runs_nothing_until_the_user_acknowledged_it()
    {
        await using var shell = await StartedAsync(new ShellOptions { AcknowledgeRoutes = false }, s => s.WithPassingRun());

        shell.Enter(Task);

        // The route is shown and asked about at once. An empty answer acknowledges nothing.
        await shell.AnswerWhenAskedAsync("Use 'ChatGPT plan (pro)' for runs of YAV, billed as stated above?", string.Empty);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[BLOCKED]", "route-unacknowledged: Review and acknowledge it with /login openai.", "Not confirmed. Nothing was changed.");
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));

        // Only what /login itself shows counts here: the refusal above showed the same route a moment ago.
        var before = shell.Terminal.Lines.Count;
        shell.Enter("/login openai");
        await shell.WaitForAskingAsync("Use 'ChatGPT plan (pro)' for runs of YAV, billed as stated above?", occurrence: 2);
        Assert.Contains("For this integration: needs your acknowledgement", ShellHarness.Flatten(string.Join(" ", shell.Terminal.Lines.Skip(before))), StringComparison.Ordinal);
        await shell.EnterAndWaitAsync("yes");

        shell.AssertShows("Acknowledged. It is asked again when the account route changes.");
        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();
        shell.AssertShows("[READY]");
    }

    [Fact]
    public async Task An_acknowledgement_is_for_one_route_and_another_route_is_asked_for_again()
    {
        // Arranged before the shell starts: the agents are read once at the start, and that reading is kept.
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Agents.Codex(c => c["account"] = new JsonObject { ["type"] = "apiKey" });
        });

        shell.Enter(Task);
        await shell.AnswerWhenAskedAsync("Use 'OpenAI API key' for runs of YAV, billed as stated above?", "no");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[BLOCKED]", "the account route 'OpenAI API key' has not been acknowledged", "Usage is billed per token to the API account");
        Assert.Empty(shell.Agents.CodexRequests("turn/start"));
    }

    [Theory]
    [InlineData("/login gemini", "'gemini' is not a provider YAV has an adapter for.")]
    [InlineData("/login codex --api-key", "YAV keeps an API key for Claude Code only")]
    public async Task Login_refuses_what_it_does_not_support(string command, string expected)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows(expected);
        Assert.False(shell.Credentials.Exists(AppServices.AnthropicKeyName));
    }

    [Fact]
    public async Task An_api_key_goes_into_the_credential_store_and_nowhere_else()
    {
        const string Key = "sk-ant-test-0123456789";
        await using var shell = await StartedAsync();

        shell.Enter("/login claude --api-key");
        await shell.WaitForAsync("API key (not shown; Enter to store, Esc to cancel):");
        shell.AssertShows("usage is billed per token to the API account the key belongs to");
        shell.Keys.Type(Key).Enter();
        await shell.WaitForAsync("The key is stored.");
        await shell.WaitForPromptAsync();

        Assert.Equal(Key, shell.Credentials.Read(AppServices.AnthropicKeyName));
        Assert.DoesNotContain("sk-ant", shell.Terminal.Raw, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-ant", File.ReadAllText(shell.Paths.SettingsFile), StringComparison.Ordinal);
        Assert.DoesNotContain(shell.History.Entries, entry => entry.Contains("sk-ant", StringComparison.Ordinal));
        foreach (var file in Directory.EnumerateFiles(shell.Paths.Home, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".db", StringComparison.Ordinal) && !f.Contains(".db-", StringComparison.Ordinal)))
        {
            Assert.DoesNotContain("sk-ant", File.ReadAllText(file), StringComparison.Ordinal);
        }

        await shell.EnterAndWaitAsync("/login claude --forget-key");

        shell.AssertShows("The API key was removed from the Windows Credential Manager.");
        Assert.False(shell.Credentials.Exists(AppServices.AnthropicKeyName));
    }

    [Fact]
    public async Task An_api_key_that_was_not_entered_stores_nothing()
    {
        await using var shell = await StartedAsync();

        shell.Enter("/login claude --api-key");
        await shell.WaitForAsync("API key (not shown; Enter to store, Esc to cancel):");
        shell.Keys.Type("sk-ant-half").Press(ConsoleKey.Escape);
        await shell.WaitForPromptAsync();

        shell.AssertShows("No key was entered. Nothing was changed.");
        Assert.False(shell.Credentials.Exists(AppServices.AnthropicKeyName));
    }

    [Theory]
    [InlineData("/adaptive on", "Adaptive mode: ON. Runs are marked Adaptive, not Strict Max.")]
    [InlineData("/optimization off", "Workflow optimizations: OFF. Requests are sent as they are")]
    public async Task Adaptive_and_optimization_are_switches_that_say_what_they_do(string command, string expected)
    {
        await using var shell = await StartedAsync();

        await shell.EnterAndWaitAsync(command);

        shell.AssertShows(expected);
        Assert.Equal(command.StartsWith("/adaptive", StringComparison.Ordinal), Stored(shell).Adaptive);
        Assert.Equal(!command.StartsWith("/optimization", StringComparison.Ordinal), Stored(shell).Optimization);
    }

    private static string EffortAsked(ShellHarness shell, string model) =>
        shell.Agents.CodexRequests("thread/start")
            .Last(t => t["params"]!["model"]!.GetValue<string>() == model)["params"]!["config"]!["model_reasoning_effort"]!.GetValue<string>();

    [Fact]
    public async Task In_adaptive_mode_the_user_is_asked_for_each_new_task_and_the_effort_they_name_is_used_for_model_a_only()
    {
        await using var shell = await StartedAsync(arrange: s => s.WithPassingRun());
        await shell.EnterAndWaitAsync("/adaptive on");

        shell.Enter(Task);
        await shell.WaitForAsync("Effort for this task, or Enter for xhigh:");
        shell.AssertShows(
            "Adaptive mode: Model A (model-a) would work on this task at effort xhigh.",
            "Listed below it: low, medium, high.",
            "A lower effort changes how the model reasons. The model stays the same, and Model B reviews at its own effort.");
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
        shell.Enter("medium");
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("Model A works at effort 'medium' instead of 'xhigh' for this task, as you approved.", "[READY]");
        Assert.Equal("medium", EffortAsked(shell, "model-a"));
        Assert.Equal("max", EffortAsked(shell, "model-b"));
        Assert.Equal("Adaptive", shell.Services.Database.GetProfile(shell.Shell.Session.LastRunId!)!.Policy.Label);
        Assert.True(Stored(shell).ModelA!.WantsMaximum);
    }

    [Theory]
    [InlineData("")]
    [InlineData("xhigh")]
    [InlineData("maximum")]
    public async Task In_adaptive_mode_a_task_runs_at_maximum_effort_when_the_user_names_no_lower_one(string answer)
    {
        await using var shell = await StartedAsync(arrange: s => s.WithPassingRun());
        await shell.EnterAndWaitAsync("/adaptive on");

        shell.Enter(Task);
        await shell.WaitForAsync("Effort for this task, or Enter for xhigh:");
        shell.Enter(answer);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows("[READY]");
        Assert.Equal("xhigh", EffortAsked(shell, "model-a"));
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("max")]
    [InlineData("lowest")]
    public async Task An_effort_that_is_not_listed_is_not_used_and_the_request_is_not_sent(string answer)
    {
        await using var shell = await StartedAsync(arrange: s => s.WithPassingRun());
        await shell.EnterAndWaitAsync("/adaptive on");

        shell.Enter(Task);
        await shell.WaitForAsync("Effort for this task, or Enter for xhigh:");
        await shell.EnterAndWaitAsync(answer);

        shell.AssertShows($"'{answer}' is not an effort model-a lists below xhigh. The request was not sent; enter it again.");
        Assert.Empty(shell.Agents.CodexRequests("thread/start"));
        Assert.Null(shell.Shell.Session.Active);
    }

    [Fact]
    public async Task A_follow_up_keeps_the_effort_of_its_task_without_asking_again_and_a_new_task_is_asked_for()
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            s.WithPassingRun();
            s.Agents
                .ImplementerTurn(Step.Write("README.md", "# App\nmore\n"), Step.Message("More."))
                .ReviewerTurn(Step.Review("pass"))
                .ImplementerTurn(Step.Write("README.md", "# App\nother\n"), Step.Message("Other."))
                .ReviewerTurn(Step.Review("pass"));
        });
        await shell.EnterAndWaitAsync("/adaptive on");
        shell.Enter(Task);
        await shell.WaitForAsync("Effort for this task, or Enter for xhigh:");
        shell.Enter("low");
        await shell.WaitForRunToEndAsync();

        shell.Enter("Say more in the README.");
        await shell.WaitForRunToEndAsync();

        Assert.Equal(1, shell.Terminal.Lines.Count(row => row.Contains("Effort for this task", StringComparison.Ordinal)));
        Assert.Equal("low", shell.Services.Database.GetProfile(shell.Shell.Session.LastRunId!)!.Implementer.RequestedEffort);
        Assert.Single(shell.Agents.CodexRequests("thread/start"), t => t["params"]!["model"]!.GetValue<string>() == "model-a");

        await shell.EnterAndWaitAsync("/new");
        shell.Enter("Something else.");
        await shell.WaitUntilAsync(
            () => shell.Terminal.Lines.Count(row => row.Contains("Effort for this task", StringComparison.Ordinal)) == 2,
            "the question for the new task");
        shell.Enter(string.Empty);
        await shell.WaitForRunToEndAsync();
        Assert.Equal("xhigh", EffortAsked(shell, "model-a"));
    }

    [Theory]
    [InlineData("Change how the password is checked.", "the request mentions 'password'")]
    [InlineData("Set the timeout in deploy/release.yml to 30.", "the request names 'deploy/release.yml', which is a protected path (deploy/**)")]
    public async Task In_adaptive_mode_a_task_that_is_risky_is_not_offered_a_lower_effort(string request, string reason)
    {
        await using var shell = await StartedAsync(arrange: s =>
        {
            var configuration = ProjectConfiguration.Empty with
            {
                Gates = [CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug")],
                ProtectedPaths = ["deploy/**"],
            };
            s.Services.Validation.TrustConfiguration(s.Project.Path, configuration, s.Services.Validation.Serialize(configuration));
            s.Agents
                .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
                .ReviewerTurn(Step.Review("pass"));
        });
        await shell.EnterAndWaitAsync("/adaptive on");

        shell.Enter(request);
        await shell.WaitForRunToEndAsync();

        shell.AssertShows($"Adaptive mode: this task stays at the effort you chose for Model A, because {reason}.");
        shell.AssertDoesNotShow("Effort for this task");
        Assert.Equal("xhigh", EffortAsked(shell, "model-a"));
    }

    [Fact]
    public async Task With_adaptive_mode_off_nothing_is_asked_and_nothing_is_lowered()
    {
        await using var shell = await StartedAsync(arrange: s => s.WithPassingRun());

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        shell.AssertDoesNotShow("Effort for this task");
        Assert.Equal("xhigh", EffortAsked(shell, "model-a"));
    }

    [Fact]
    public async Task Adaptive_is_off_unless_the_user_turned_it_on_and_a_run_says_which_policy_it_ran_under()
    {
        await using var shell = await StartedAsync(arrange: s => s.WithPassingRun());

        await shell.EnterAndWaitAsync("/adaptive");
        shell.AssertShows("Adaptive mode: OFF. Every run uses the effort you chose for each role.");

        shell.Enter(Task);
        await shell.WaitForRunToEndAsync();

        var run = shell.Services.Database.FindRun(shell.Shell.Session.LastRunId!)!;
        Assert.Equal(RunState.ReadyToApply, run.State);
        Assert.Equal("Strict Max", shell.Services.Database.GetProfile(run.RunId)!.Policy.Label);
    }
}
