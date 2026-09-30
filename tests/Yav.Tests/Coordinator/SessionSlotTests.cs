using Yav.Coordinator;
using Yav.Core.Agents;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>When a conversation that is kept for a task is used again, and when a new one is started.</summary>
public class SessionSlotTests
{
    private const string Workspace = @"C:\ws\w";

    private static async Task<SessionSlot> SlotAsync(Yav.Core.Profiles.RoleProfile role)
    {
        var adapter = new ScriptedAdapter((_, _) => System.Threading.Tasks.Task.CompletedTask);
        var session = await adapter.StartSessionAsync(
            new SessionRequest(role.Role, role.ModelId, role.RequestedEffort, Workspace, role.Sandbox, role.Approvals, "instructions", true, null, []),
            CancellationToken.None);
        return new SessionSlot(session, role, Workspace, resumed: false);
    }

    [Theory]
    [InlineData(AccountRouteKind.Subscription, "ChatGPT plan (pro)", true)]
    [InlineData(AccountRouteKind.Subscription, "ChatGPT plan (plus)", false)]
    [InlineData(AccountRouteKind.ApiKey, "OpenAI API key", false)]
    public async Task A_kept_conversation_is_used_again_only_on_the_account_route_it_was_opened_with(AccountRouteKind route, string label, bool used)
    {
        // The account an agent works with is fixed when its process starts. A route you changed since is a new conversation.
        var opened = Builders.Role(AgentRole.Implementer);
        var slot = await SlotAsync(opened);
        await using var session = slot.Session;

        Assert.Equal(used, slot.Matches(opened with { AccountRoute = route, AccountRouteLabel = label }, Workspace));
    }

    [Fact]
    public async Task A_kept_conversation_is_used_again_for_the_same_settings_in_the_same_workspace()
    {
        var opened = Builders.Role(AgentRole.Implementer);
        var slot = await SlotAsync(opened);
        await using var session = slot.Session;

        Assert.True(slot.Matches(opened, Workspace));
        Assert.False(slot.Matches(opened with { RequestedEffort = "low" }, Workspace));
        Assert.False(slot.Matches(opened, @"C:\ws\other"));
    }
}
