namespace Yav.Core.Agents;

/// <summary>
/// A conversation that can say what is in effect before a prompt is sent. What it says is compared with
/// what was requested first, so that a configuration that was not honored costs nothing.
/// </summary>
public interface IReportsBeforeTurn
{
    /// <summary>
    /// Starts what has to be started for the turn and reports what the agent says it will work with.
    /// Nothing is sent to a model. Throws an <see cref="AgentException"/> when the agent ended before it
    /// said it: the turn is then not started, because what it would work with was not compared.
    /// </summary>
    Task<EarlySettings?> PrepareTurnAsync(TurnRequest request, CancellationToken cancellationToken);
}

/// <summary>What an agent says before a turn.</summary>
/// <param name="Model">Null when the agent says it only once the turn has begun.</param>
/// <param name="Effort">Null when the agent did not say, and will not say later either.</param>
/// <param name="Source">Where the effort was reported, or where it was asked for in vain.</param>
/// <param name="BoundaryProblem">
/// Why the boundary of a review cannot be confirmed for this turn, when that is known already. Null otherwise.
/// </param>
/// <param name="Account">The account the conversation says it works with. Null when it did not say.</param>
public sealed record EarlySettings(string? Model, string? Effort, string Source, string? BoundaryProblem = null, AccountSaid? Account = null);
