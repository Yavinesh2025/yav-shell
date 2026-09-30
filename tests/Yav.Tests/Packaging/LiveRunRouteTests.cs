using System.Text.Json.Nodes;
using Yav.Core.Agents;
using Yav.Tests.Live;
using Yav.Tests.Support;

namespace Yav.Tests.Packaging;

/// <summary>
/// The account routes the holder of the accounts agrees to with -Acknowledge, and how the setup tells which kind of
/// route /login asks about: by the label the adapter gives the route, checked here against what the adapters report.
/// </summary>
public class LiveRunRouteTests
{
    [Theory]
    [InlineData("codex,claude", "codex:subscription,claude:subscription")]
    [InlineData(" Claude:API-Key ", "claude:api-key")]
    [InlineData("codex:subscription, claude:api-key", "codex:subscription,claude:api-key")]
    [InlineData("", "")]
    public void A_route_agreed_to_names_a_provider_and_a_kind_and_a_bare_provider_means_its_subscription(string given, string routes)
    {
        Assert.Equal(routes, string.Join(',', LiveRoute.Parse(given).Select(route => route.ToString())));
    }

    [Theory]
    [InlineData("openai", "'openai'")]
    [InlineData("anthropic", "'anthropic'")]
    [InlineData("claude:cloud", "'cloud'")]
    [InlineData("claude:", "'claude:'")]
    [InlineData("codex,claude:api-key,claude", "claude")]
    public void What_is_not_a_route_the_live_run_knows_is_refused(string given, string named)
    {
        var refused = Assert.Throws<ArgumentException>(() => LiveRoute.Parse(given));

        Assert.Contains(named, refused.Message, StringComparison.Ordinal);
    }

    private static JsonObject Claude(string? method, string? plan = null, string? keySource = null, string provider = "firstParty") => new()
    {
        ["loggedIn"] = true, ["authMethod"] = method, ["apiProvider"] = provider, ["subscriptionType"] = plan, ["apiKeySource"] = keySource,
    };

    /// <summary>The route an adapter reports in the given situation, and the provider /login names it by.</summary>
    private static async Task<(string Provider, AuthStatus Auth)> ReportedAsync(AgentFixture fixture, string situation)
    {
        switch (situation)
        {
            case "codex app-server, a ChatGPT plan":
            case "codex app-server, an API key":
            case "codex app-server, Amazon Bedrock":
            case "codex app-server, an account type nobody knows":
            {
                var type = situation switch
                {
                    "codex app-server, an API key" => "apiKey",
                    "codex app-server, Amazon Bedrock" => "amazonBedrock",
                    "codex app-server, an account type nobody knows" => "somethingNew",
                    _ => null,
                };
                if (type is not null)
                {
                    fixture.Codex(c => c["account"] = new JsonObject { ["type"] = type });
                }

                await using var appServer = fixture.CodexAppServer();
                return ("codex", await appServer.GetAuthStatusAsync(CancellationToken.None));
            }

            case "codex exec, a ChatGPT plan":
            case "codex exec, an API key":
            case "codex exec, a sign-in nobody knows":
            {
                fixture.Codex(c => c["loginStatus"] = situation switch
                {
                    "codex exec, an API key" => "Logged in using an API key",
                    "codex exec, a sign-in nobody knows" => "Logged in somehow",
                    _ => "Logged in using ChatGPT",
                });
                await using var exec = fixture.CodexExec();
                return ("codex", await exec.GetAuthStatusAsync(CancellationToken.None));
            }

            default:
            {
                var auth = situation switch
                {
                    "claude, signed in to claude.ai" => null,
                    "claude, a token with a plan" => Claude("oauth_token", plan: "pro"),
                    "claude, a key from the environment" => Claude("api_key", keySource: "ANTHROPIC_API_KEY"),
                    "claude, a key of the Claude Console" => Claude("claude.ai", keySource: "/login managed key"),
                    "claude, a key from a helper" => Claude("api_key_helper", keySource: "apiKeyHelper"),
                    "claude, a key held by YAV" => null,
                    "claude, a cloud provider" => Claude(null, provider: "bedrock"),
                    "claude, a gateway" => Claude(null, provider: "gateway"),
                    "claude, a token without a plan" => Claude("oauth_token"),
                    _ => throw new ArgumentOutOfRangeException(nameof(situation), situation, "No such situation."),
                };
                if (auth is not null)
                {
                    fixture.Claude(c => c["auth"] = auth);
                }

                // A stand-in value: the adapter only asks whether YAV holds a key.
                Func<string?>? held = situation == "claude, a key held by YAV" ? () => "stand-in" : null;
                await using var claude = fixture.ClaudeCli(held);
                return ("claude", await claude.GetAuthStatusAsync(CancellationToken.None));
            }
        }
    }

    [Theory]
    [InlineData("claude, signed in to claude.ai", "subscription")]
    [InlineData("claude, a token with a plan", "subscription")]
    [InlineData("claude, a key from the environment", "api-key")]
    [InlineData("claude, a key of the Claude Console", "api-key")]
    [InlineData("claude, a key from a helper", "api-key")]
    [InlineData("claude, a key held by YAV", "api-key")]
    [InlineData("claude, a cloud provider", null)]
    [InlineData("claude, a gateway", null)]
    [InlineData("claude, a token without a plan", null)]
    [InlineData("codex app-server, a ChatGPT plan", "subscription")]
    [InlineData("codex app-server, an API key", "api-key")]
    [InlineData("codex app-server, Amazon Bedrock", null)]
    [InlineData("codex app-server, an account type nobody knows", null)]
    [InlineData("codex exec, a ChatGPT plan", "subscription")]
    [InlineData("codex exec, an API key", "api-key")]
    [InlineData("codex exec, a sign-in nobody knows", null)]
    public async Task The_kind_of_a_route_is_told_by_its_label_and_a_route_of_another_kind_is_of_no_kind_the_live_run_acknowledges(string situation, string? kind)
    {
        using var fixture = new AgentFixture();

        var (provider, auth) = await ReportedAsync(fixture, situation);

        Assert.True(auth.Authenticated, situation);
        Assert.Equal(kind, LiveRoute.KindOf(provider, auth.RouteLabel));
        if (kind is not null)
        {
            var route = new LiveRoute(provider, kind);
            Assert.Equal(route.Route, auth.Route);

            // The question /login asks about this route is the one the setup answers with yes.
            var question = $"Use '{auth.RouteLabel}' {ShellDriver.RouteQuestion} {ShellDriver.Confirmation}";
            Assert.Contains(route.Question, question, StringComparison.Ordinal);
        }
    }
}
