using Yav.Core.Agents;
using Yav.Tests.Support;

namespace Yav.Tests.Core;

public sealed class AccountRouteTests
{
    [Theory]
    [InlineData(true, AccountRouteKind.Subscription, BillingKind.IncludedInSubscription, true)]
    [InlineData(true, AccountRouteKind.ApiKey, BillingKind.PayPerToken, false)]
    [InlineData(true, AccountRouteKind.CloudProvider, BillingKind.CloudProviderBilled, false)]
    [InlineData(true, AccountRouteKind.Gateway, BillingKind.CloudProviderBilled, false)]
    [InlineData(true, AccountRouteKind.Unknown, BillingKind.Unknown, false)]
    [InlineData(true, AccountRouteKind.Unknown, BillingKind.IncludedInSubscription, false)]
    [InlineData(true, AccountRouteKind.Subscription, BillingKind.Unknown, false)]
    [InlineData(true, AccountRouteKind.Subscription, BillingKind.PayPerToken, false)]
    [InlineData(false, AccountRouteKind.Subscription, BillingKind.IncludedInSubscription, false)]
    public void Only_a_subscription_the_agent_is_signed_in_to_is_used_without_asking(bool authenticated, AccountRouteKind route, BillingKind billing, bool expected)
    {
        var auth = new AuthStatus(authenticated, route, "route", null, "openai", billing, RoutePolicy.RequiresAcknowledgement, null, "test", Builders.Now);

        Assert.Equal(expected, auth.UsedWithoutAsking);
    }
}
