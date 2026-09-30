using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;

namespace Yav.Coordinator;

/// <param name="Confirmations">One entry per setting, stating what was requested and what the provider reported.</param>
/// <param name="Violations">Reasons the turn must not run, or must be stopped. Empty when the run profile is honored.</param>
public sealed record SettingsVerification(IReadOnlyList<ProfileConfirmation> Confirmations, IReadOnlyList<string> Violations);

/// <summary>
/// Compares what a provider reports as in effect with the run profile. A setting is only Verified when the
/// provider reported it back; nothing is assumed from the fact that YAV asked for it.
/// </summary>
public static class SettingsVerifier
{
    private const string ReadOnly = "read-only";
    private const string WorkspaceWrite = "workspace-write";

    /// <summary>The ways providers name their ordinary serving tier.</summary>
    private static readonly string[] StandardTiers = ["default", "auto", "standard"];

    /// <param name="workspace">The directory the agent was started in. Null when whoever asks does not know it.</param>
    public static SettingsVerification Verify(
        RoleProfile role,
        QualityPolicy policy,
        EffectiveSettings effective,
        DateTimeOffset now,
        string? workspace = null)
    {
        var name = Name(role);
        var confirmations = new List<ProfileConfirmation>();
        var violations = new List<string>();

        // A difference from the profile stops the run while Quality Lock is on; a missing confirmation only under strict policy.
        var enforce = policy.QualityLock;
        var strict = policy.QualityLock && policy.Strict;

        void Record(string setting, string? requested, string? reported, VerificationStatus status, string? source = null) =>
            confirmations.Add(new ProfileConfirmation(role.Role, setting, requested, reported, status, source ?? effective.Source, now));

        VerifyModelAndEffort(role, policy, effective.Model, effective.Effort, effective.EffortSource, Record, violations);
        VerifySandbox(role, name, effective, Record, violations);
        VerifyReach(role, name, effective, enforce, Record, violations);
        VerifyServiceTier(role, name, effective, enforce, strict, Record, violations);
        VerifyAccount(role, name, effective.CredentialSource, effective.Account, Record, violations);
        VerifyDirectory(name, workspace, effective.WorkingDirectory, Record, violations);

        return new SettingsVerification(confirmations, violations);
    }

    /// <summary>
    /// The candidate is what is in the workspace, and the sandbox of an agent is drawn around the directory
    /// it works in. An agent that works somewhere else stops the run, whatever the quality policy says.
    /// </summary>
    private static void VerifyDirectory(
        string name,
        string? workspace,
        string? reported,
        Action<string, string?, string?, VerificationStatus, string?> record,
        List<string> violations)
    {
        if (workspace is null)
        {
            return;
        }

        if (reported is null)
        {
            record(ProfileSettings.WorkingDirectory, workspace, null, VerificationStatus.RequestedUnverified, null);
            return;
        }

        if (WorkspacePaths.SameDirectory(workspace, reported))
        {
            record(ProfileSettings.WorkingDirectory, workspace, reported, VerificationStatus.Verified, null);
            return;
        }

        record(ProfileSettings.WorkingDirectory, workspace, reported, VerificationStatus.Mismatch, null);
        violations.Add(
            $"{name}: the agent was started in the workspace '{workspace}' but reports that it works in '{reported}'. "
            + "What it changes there would not be the candidate, and what is limited to the workspace would not limit it.");
    }

    /// <summary>
    /// What an agent says before a prompt is sent. A turn that would not run as requested is not started, so
    /// it costs nothing. Everything else is compared when the turn has begun, as for every agent.
    /// </summary>
    public static SettingsVerification VerifyBeforeTurn(RoleProfile role, QualityPolicy policy, EarlySettings early, DateTimeOffset now)
    {
        var confirmations = new List<ProfileConfirmation>();
        var violations = new List<string>();

        void Record(string setting, string? requested, string? reported, VerificationStatus status, string? source = null) =>
            confirmations.Add(new ProfileConfirmation(role.Role, setting, requested, reported, status, source ?? early.Source, now));

        VerifyModelAndEffort(role, policy, early.Model, early.Effort, early.Source, Record, violations, modelFollows: early.Model is null);
        if (role.Role == AgentRole.Reviewer && early.BoundaryProblem is { } problem)
        {
            // As for a boundary that was not confirmed: whatever the quality policy says.
            Record(ProfileSettings.Sandbox, ReadOnly, null, VerificationStatus.RequestedUnverified);
            violations.Add($"{Name(role)}: {problem} The read-only boundary of the review is not confirmed, so the review is not started. A prompt alone is not enforcement.");
        }

        VerifyAccount(role, Name(role), null, early.Account, Record, violations);

        return new SettingsVerification(confirmations, violations);
    }

    private static string Name(RoleProfile role) => role.Role == AgentRole.Implementer ? "Model A" : "Model B";

    /// <param name="modelFollows">True when the agent names its model only once the turn has begun, so that none is expected yet.</param>
    private static void VerifyModelAndEffort(
        RoleProfile role,
        QualityPolicy policy,
        string? model,
        string? effort,
        string? effortSource,
        Action<string, string?, string?, VerificationStatus, string?> record,
        List<string> violations,
        bool modelFollows = false)
    {
        var name = Name(role);
        var enforce = policy.QualityLock;
        var strict = policy.QualityLock && policy.Strict;

        void Compare(string setting, string requested, string? reported, string? alsoAccepted = null, string? source = null)
        {
            if (reported is null)
            {
                record(setting, requested, null, VerificationStatus.RequestedUnverified, source);
                if (strict)
                {
                    violations.Add(
                        $"{name} {setting} '{requested}' is Requested / Unverified: the agent did not report what is in effect. "
                        + "Strict policy does not run an unconfirmed configuration; relax it with /quality strict off.");
                }

                return;
            }

            var honored = Same(requested, reported) || (alsoAccepted is not null && Same(alsoAccepted, reported));
            record(setting, requested, reported, honored ? VerificationStatus.Verified : VerificationStatus.Mismatch, source);
            if (!honored && enforce)
            {
                violations.Add($"{name} {setting}: '{requested}' was requested but the provider reports '{reported}'. Nothing was changed on your behalf.");
            }
        }

        if (!modelFollows)
        {
            Compare(ProfileSettings.Model, role.ModelId, model, role.ResolvedModelId);
        }

        if (role.RequestedEffort.Length == 0 && role.EffortSupport == VerificationStatus.Verified)
        {
            // The model is known to take no effort. Nothing was requested, so nothing can have been lowered.
            record(ProfileSettings.Effort, string.Empty, effort ?? string.Empty, VerificationStatus.Verified, "the model has no effort setting");
        }
        else if (role.RequestedEffort.Length == 0)
        {
            // What was wanted could not be turned into a value, so none was passed on. Whatever is in effect
            // is shown as what it is, and it is not called verified: nobody can say that it is what was wanted.
            record(ProfileSettings.Effort, role.EffortPreference, effort, VerificationStatus.RequestedUnverified, effortSource);
            if (strict)
            {
                violations.Add(
                    $"{name} {ProfileSettings.Effort} '{role.EffortPreference}' is Requested / Unverified: it could not be turned into a value of the provider, "
                    + "so none was requested. Strict policy does not run an unconfirmed configuration; set the exact value with /effort.");
            }
        }
        else
        {
            Compare(ProfileSettings.Effort, role.RequestedEffort, effort, source: effortSource);
        }
    }

    private static void VerifySandbox(
        RoleProfile role,
        string name,
        EffectiveSettings effective,
        Action<string, string?, string?, VerificationStatus, string?> record,
        List<string> violations)
    {
        var requested = role.Sandbox == SandboxLevel.ReadOnly ? ReadOnly : WorkspaceWrite;
        var reported = effective.Sandbox;

        if (role.Role == AgentRole.Reviewer)
        {
            // The reviewer's boundary is enforced, never assumed, whatever the quality policy says.
            if (reported is not null && Same(reported, ReadOnly))
            {
                record(ProfileSettings.Sandbox, requested, reported, VerificationStatus.Verified, null);
                return;
            }

            record(ProfileSettings.Sandbox, requested, reported, reported is null ? VerificationStatus.RequestedUnverified : VerificationStatus.Mismatch, null);
            violations.Add(reported is null
                ? $"{name}: the agent did not confirm a read-only boundary for the review, so the review is not started. A prompt alone is not enforcement."
                : $"{name}: a read-only review was requested but the agent reports '{reported}'. The reviewer must not be able to change the candidate.");
            return;
        }

        if (reported is null)
        {
            record(ProfileSettings.Sandbox, requested, null, VerificationStatus.Unavailable, "the agent reports no operating-system sandbox");
            return;
        }

        if (Same(reported, requested))
        {
            record(ProfileSettings.Sandbox, requested, reported, VerificationStatus.Verified, null);
            return;
        }

        record(ProfileSettings.Sandbox, requested, reported, VerificationStatus.Mismatch, null);
        violations.Add(Same(reported, ReadOnly)
            ? $"{name}: a writable workspace was requested but the agent reports a read-only sandbox, so it cannot write the change. "
              + "On Windows this happens when the agent's own sandbox is not set up; see /doctor."
            : $"{name}: access limited to the workspace was requested but the agent reports '{reported}'. Access is never widened to make a run work.");
    }

    /// <summary>
    /// What reaches beyond the sandbox, and who decides what the agent asks for beyond it. Only what an agent
    /// says is recorded; most agents say nothing about it.
    /// </summary>
    private static void VerifyReach(
        RoleProfile role,
        string name,
        EffectiveSettings effective,
        bool enforce,
        Action<string, string?, string?, VerificationStatus, string?> record,
        List<string> violations)
    {
        const string You = "you";
        if (effective.ApprovalsReviewer is { } reviewer)
        {
            var user = Same(reviewer, "user");
            record(ProfileSettings.ApprovalsReviewer, You, user ? You : reviewer, user ? VerificationStatus.Verified : VerificationStatus.Mismatch, null);
            if (!user)
            {
                // Whatever the quality policy says: what is granted without the user was not granted by anybody YAV knows.
                violations.Add(
                    $"{name}: the agent reports that '{reviewer}' decides what it asks for, so access would be granted or refused without you. "
                    + "In YAV only you decide about access.");
            }
        }

        if (effective.Widening is not { } widening)
        {
            return;
        }

        const string Nothing = "nothing";
        record(ProfileSettings.NetworkAccess, null, widening.NetworkAccess ? "on" : "off", VerificationStatus.Verified, null);
        if (widening.AdditionalWritableRoots.Count == 0)
        {
            record(ProfileSettings.WritableOutside, Nothing, Nothing, VerificationStatus.Verified, null);
            return;
        }

        var folders = string.Join("; ", widening.AdditionalWritableRoots);
        record(ProfileSettings.WritableOutside, Nothing, folders, VerificationStatus.Mismatch, null);
        if (enforce)
        {
            violations.Add(
                $"{name}: access limited to the workspace was requested, but the agent reports that it may also write to {folders}. "
                + "That comes from the agent's own configuration. Access is never widened to make a run work.");
        }
    }

    private static void VerifyServiceTier(
        RoleProfile role,
        string name,
        EffectiveSettings effective,
        bool enforce,
        bool strict,
        Action<string, string?, string?, VerificationStatus, string?> record,
        List<string> violations)
    {
        var reported = effective.ServiceTier;
        var reportedStandard = reported is null || reported.Length == 0 || StandardTiers.Any(t => Same(t, reported));

        if (role.ServiceTier is null)
        {
            if (reportedStandard)
            {
                record(ProfileSettings.ServiceTier, "standard", reported ?? "standard", VerificationStatus.Verified, null);
                return;
            }

            record(ProfileSettings.ServiceTier, "standard", reported, VerificationStatus.Mismatch, null);
            if (enforce)
            {
                violations.Add(
                    $"{name}: standard serving was requested but the agent reports the tier '{reported}', which can be billed differently. "
                    + "It comes from the agent's own configuration. Authorize it with /speed provider, or set the agent back to its default tier.");
            }

            return;
        }

        if (reported is not null && Same(reported, role.ServiceTier))
        {
            record(ProfileSettings.ServiceTier, role.ServiceTier, reported, VerificationStatus.Verified, null);
            return;
        }

        if (reported is null)
        {
            record(ProfileSettings.ServiceTier, role.ServiceTier, null, VerificationStatus.RequestedUnverified, null);
            if (strict)
            {
                violations.Add($"{name}: the tier '{role.ServiceTier}' is Requested / Unverified: the agent did not report which tier is active.");
            }

            return;
        }

        record(ProfileSettings.ServiceTier, role.ServiceTier, reported, VerificationStatus.Mismatch, null);
        if (enforce)
        {
            violations.Add($"{name}: the tier '{role.ServiceTier}' was requested but the agent reports '{reported}'. Provider speed is not active.");
        }
    }

    /// <summary>
    /// The route that was shown before the run, against what the conversation says it works with. Another
    /// route stops the run whatever the quality policy says: what is paid, and to whom, is not a matter of
    /// quality, and nobody agreed to the other route.
    /// </summary>
    private static void VerifyAccount(
        RoleProfile role,
        string name,
        string? credentialSource,
        AccountSaid? account,
        Action<string, string?, string?, VerificationStatus, string?> record,
        List<string> violations)
    {
        const string Never = "A different billing route is never used silently. Check /login and /usage.";
        var shown = role.AccountRoute;
        var comparable = shown is AccountRouteKind.Subscription or AccountRouteKind.ApiKey or AccountRouteKind.CloudProvider or AccountRouteKind.Gateway;

        if (account is { Route: { } said } && comparable)
        {
            record(ProfileSettings.CredentialSource, role.AccountRouteLabel, account.Description, said == shown ? VerificationStatus.Verified : VerificationStatus.Mismatch, account.Source);
            if (said != shown)
            {
                violations.Add(
                    $"{name}: the billing route shown before the run was '{role.AccountRouteLabel}', but the agent reports that it works with {account.Description}. "
                    + Never + (account.Note is null ? string.Empty : " " + account.Note));
            }

            return;
        }

        // No route follows from what was said, or what was shown is not a route that can be compared. What is
        // left is the name of a key, or that there is none.
        var keyConfirmed = false;
        if (credentialSource is { } reported)
        {
            var reportsKey = !Same(reported, "none") && reported.Length > 0;
            bool? expectsKey = shown switch
            {
                AccountRouteKind.Subscription => false,
                AccountRouteKind.ApiKey => true,
                _ => null,
            };

            if (expectsKey is { } expected && expected != reportsKey)
            {
                record(ProfileSettings.CredentialSource, role.AccountRouteLabel, reported, VerificationStatus.Mismatch, null);
                violations.Add(
                    $"{name}: the billing route shown before the run was '{role.AccountRouteLabel}', but the agent reports the credential source '{reported}'. "
                    + Never);
                return;
            }

            // A key that is used where a key was shown is confirmed. That none is used confirms nothing: a
            // subscription, a token and a cloud provider all name no key.
            keyConfirmed = expectsKey == true;
        }

        if ((account?.Description ?? credentialSource) is { } known)
        {
            record(
                ProfileSettings.CredentialSource, role.AccountRouteLabel, known,
                keyConfirmed ? VerificationStatus.Verified : VerificationStatus.RequestedUnverified, account?.Source);
        }

        // Most agents say nothing about it. The route shown before the run stays what was read from the account.
    }

    private static bool Same(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);
}
