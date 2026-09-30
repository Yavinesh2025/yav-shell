using System.Text.Json;
using System.Text.Json.Nodes;
using Xunit.Abstractions;
using Yav.Tests.Live;

namespace Yav.Tests.Packaging;

/// <summary>
/// The parts of the live run that drive the shell (<see cref="LiveRunTests"/>), run against the real program with the
/// scripted stand-in in place of the agents. What they decide from what the shell shows is tested here, where no
/// model is asked anything.
/// </summary>
[Collection(nameof(Yav.Tests.EndToEnd.InteractiveConsoleTests))]
public sealed class LiveRunPartTests(ITestOutputHelper output)
{
    /// <summary>Claude Code signed in with a key from its environment: a route billed per token.</summary>
    private static JsonObject ApiKey => new()
    {
        ["loggedIn"] = true, ["authMethod"] = "api_key", ["apiProvider"] = "firstParty", ["apiKeySource"] = "ANTHROPIC_API_KEY",
    };

    [Fact]
    public async Task A_route_of_another_kind_than_the_one_agreed_to_is_answered_with_no_and_the_setup_stops()
    {
        using var run = new StandInRun();
        run.Agents.Claude(c => c["auth"] = ApiKey);

        // A bare provider name agrees to its subscription, and Claude Code works with a key.
        var failure = await Assert.ThrowsAnyAsync<Exception>(() => new LiveRunTests(output).SetUpAsync(run.Setup("codex,claude")));

        Assert.Contains("Anthropic API key (configured in Claude Code)", failure.Message, StringComparison.Ordinal);
        Assert.Contains("subscription", failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(run.AcknowledgedRoutes(), route => route.StartsWith("claude-cli:", StringComparison.Ordinal));
        Assert.False(run.Exists("setup-routes.json"));
    }

    [Fact]
    public async Task A_route_of_the_kind_agreed_to_is_acknowledged_and_recorded_with_the_label_the_shell_showed()
    {
        using var run = new StandInRun();
        run.Agents.Claude(c => c["auth"] = ApiKey);

        await new LiveRunTests(output).SetUpAsync(run.Setup("codex,claude:api-key"));

        var routes = JsonDocument.Parse(run.Read("setup-routes.json")).RootElement.EnumerateArray()
            .Select(r => $"{r.GetProperty("provider").GetString()}|{r.GetProperty("kind").GetString()}|{r.GetProperty("label").GetString()}")
            .ToList();
        Assert.Equal(["codex|subscription|ChatGPT plan (pro)", "claude|api-key|Anthropic API key (configured in Claude Code)"], routes);
        Assert.Contains(run.AcknowledgedRoutes(), route => route.StartsWith("claude-cli:ApiKey:", StringComparison.Ordinal));
        Assert.Contains(run.AcknowledgedRoutes(), route => route.StartsWith("codex-app-server:Subscription:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_that_waits_for_an_answer_is_continued_and_its_record_says_what_the_data_of_the_run_says_was_decided()
    {
        using var run = new StandInRun();
        var runId = await run.StoppedRunAsync();
        var part = run.Part(new()
        {
            [LiveRunFactAttribute.Variable] = "resume", ["YAV_LIVE_RECORD"] = "resume-1", ["YAV_LIVE_RUN_ID"] = runId, [LiveRunFactAttribute.Authorized] = "1",
        });

        await new LiveRunTests(output).ResumeAsync(part);

        // 'yav run' cancelled the first question itself; the record counts only what was decided while the part ran.
        var note = run.Read("resume-1-screens.txt").ReplaceLineEndings("\n").Split('\n').Single(row => row.StartsWith("Questions of the agents", StringComparison.Ordinal));
        Assert.Contains("Decisions recorded for the run while this part ran: 1 (Decline 1). None was granted.", note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_route_the_models_need_that_was_not_agreed_to_stops_the_setup_before_anything_is_done()
    {
        using var run = new StandInRun();

        var failure = await Assert.ThrowsAnyAsync<Exception>(() => new LiveRunTests(output).SetUpAsync(run.Setup("codex")));

        Assert.Contains("claude", failure.Message, StringComparison.Ordinal);
        Assert.False(Directory.Exists(run.Project), "The project was created, so the setup went on although a route was missing.");
        Assert.Empty(run.AcknowledgedRoutes());
    }
}
