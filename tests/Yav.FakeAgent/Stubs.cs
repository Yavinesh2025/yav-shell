using System.Text.Json.Nodes;

namespace Yav.FakeAgent;

/// <summary>Answers the identity questions the adapters ask before any session: version and sign-in state.</summary>
internal static class Identity
{
    public static int PrintVersion()
    {
        var scenario = Scenario.Load();
        var flavor = Environment.GetEnvironmentVariable("YAV_FAKE_FLAVOR") ?? "codex";
        var version = flavor == "claude"
            ? Scenario.Text(scenario.Claude, "version", "2.1.284 (Claude Code)")
            : "codex-cli " + Scenario.Text(scenario.Codex, "version", "0.158.0");
        Console.Out.WriteLine(version);
        return 0;
    }

    public static int PrintCodexLogin(string[] args)
    {
        if (args.Length == 0 || args[0] != "status")
        {
            return 64;
        }

        var scenario = Scenario.Load();
        var text = Scenario.Text(scenario.Codex, "loginStatus", "Logged in using ChatGPT");
        Console.Out.WriteLine(text);
        return text.StartsWith("Not logged in", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    public static int PrintClaudeAuth(string[] args)
    {
        if (args.Length == 0 || args[0] != "status")
        {
            return 64;
        }

        var scenario = Scenario.Load();
        var auth = scenario.Claude["auth"] as JsonObject ?? new JsonObject
        {
            ["loggedIn"] = true,
            ["authMethod"] = "claude.ai",
            ["apiProvider"] = "firstParty",
            ["email"] = "user@example.invalid",
            ["orgId"] = "org-1",
            ["orgName"] = "Example",
            ["subscriptionType"] = "max",
        };
        Console.Out.WriteLine(auth.ToJsonString());
        return Scenario.Flag(auth, "loggedIn", true) ? 0 : 1;
    }
}
