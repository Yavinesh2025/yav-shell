using Yav.Core.Profiles;

namespace Yav.Tests.Core;

/// <summary>
/// Which tasks may be offered a lower effort at all. Whatever this says, a lower effort is only ever
/// used after the user approved it for the task.
/// </summary>
public class AdaptivePolicyTests
{
    private static readonly string[] NoProtectedPaths = [];

    [Theory]
    [InlineData("Rename the variable x to count in util.py.")]
    [InlineData("Fix the typo in the README.")]
    [InlineData("The greeting should end with a period.")]
    public void A_small_task_that_names_nothing_risky_may_be_offered_a_lower_effort(string request)
    {
        var decision = AdaptivePolicy.Decide(request, NoProtectedPaths);

        Assert.True(decision.MayLower, decision.Reason);
    }

    [Theory]
    [InlineData("Change how the password is checked.", "password")]
    [InlineData("Update the AUTHENTICATION middleware.", "authentication")]
    [InlineData("Write the migration for the orders table.", "migration")]
    [InlineData("Fix the payment rounding.", "payment")]
    [InlineData("Rotate the API token on start.", "token")]
    [InlineData("Delete the old records.", "delete")]
    [InlineData("Make the encryption key longer.", "encryption")]
    [InlineData("Deploy to production after the change.", "production")]
    public void A_task_that_names_something_risky_stays_at_the_effort_that_was_chosen(string request, string word)
    {
        var decision = AdaptivePolicy.Decide(request, NoProtectedPaths);

        Assert.False(decision.MayLower);
        Assert.Contains($"'{word}'", decision.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("Tokenize the input by blanks.")]
    [InlineData("Show the author of a post.")]
    [InlineData("The undeleted items are shown first.")]
    public void A_word_that_only_contains_a_risky_word_is_not_that_word(string request)
    {
        Assert.True(AdaptivePolicy.Decide(request, NoProtectedPaths).MayLower);
    }

    [Theory]
    [InlineData("Change the timeout in deploy/release.yml.", "deploy/**")]
    [InlineData("Change the timeout in DEPLOY\\release.yml.", "deploy/**")]
    [InlineData("Update .github/workflows/build.yml to use the new runner.", ".github/**")]
    [InlineData("Edit yav.project.json so that the tests run faster.", "yav.project.json")]
    public void A_task_that_names_a_protected_path_stays_at_the_effort_that_was_chosen(string request, string path)
    {
        var decision = AdaptivePolicy.Decide(request, [path]);

        Assert.False(decision.MayLower);
        Assert.Contains("protected", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_long_request_is_uncertain_and_stays_at_the_effort_that_was_chosen()
    {
        var request = string.Join(' ', Enumerable.Repeat("Change the wording of the greeting.", 40));

        var decision = AdaptivePolicy.Decide(request, NoProtectedPaths);

        Assert.False(decision.MayLower);
        Assert.Contains("long", decision.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_request_of_several_parts_is_uncertain_and_stays_at_the_effort_that_was_chosen()
    {
        var decision = AdaptivePolicy.Decide("Fix the greeting.\n\nThen rename the module.\n\nThen update the documentation.", NoProtectedPaths);

        Assert.False(decision.MayLower);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Nothing_is_offered_for_nothing(string request)
    {
        Assert.False(AdaptivePolicy.Decide(request, NoProtectedPaths).MayLower);
    }
}
