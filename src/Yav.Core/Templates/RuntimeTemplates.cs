using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Yav.Core.Agents;
using Yav.Core.Runs;

namespace Yav.Core.Templates;

public sealed record RuntimeTemplate(string Id, string Version, string Text);

/// <summary>
/// Short, versioned role instructions. They are merged through the provider's documented instruction
/// surface and never replace the agent's native or project instructions. The build specification itself
/// is never sent to a model.
/// </summary>
public static class RuntimeTemplates
{
    private static readonly Lazy<RuntimeTemplate> ImplementerTemplate = new(() => Load("model-a.implement", "v1"));
    private static readonly Lazy<RuntimeTemplate> ReviewerTemplate = new(() => Load("model-b.review", "v1"));

    public static RuntimeTemplate Implementer => ImplementerTemplate.Value;

    public static RuntimeTemplate Reviewer => ReviewerTemplate.Value;

    public static RuntimeTemplate For(AgentRole role) => role == AgentRole.Implementer ? Implementer : Reviewer;

    private static RuntimeTemplate Load(string id, string version)
    {
        var name = $"Yav.Core.Templates.{id}.{version}.md";
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Runtime template '{name}' is missing from the build.");
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var text = reader.ReadToEnd().ReplaceLineEndings("\n").Trim();
        return new RuntimeTemplate(id, version, text);
    }
}

/// <summary>
/// Builds the prompts YAV sends. Repository content and tool output are delimited as reference data so
/// that text inside them is not mistaken for an instruction or an authorization.
/// </summary>
public static partial class PromptBuilder
{
    public const string ReferenceOpen = "<reference-data>";
    public const string ReferenceClose = "</reference-data>";

    public static string ImplementationRequest(
        IReadOnlyList<Requirement> requirements,
        IReadOnlyList<StartingReference> references,
        IReadOnlyList<GateDefinition>? checks = null)
    {
        var builder = new PromptText();
        AppendRequirements(builder, requirements);
        AppendReferences(builder, references);
        AppendChecks(builder, checks);
        return builder.ToString().TrimEnd();
    }

    public static string ReviewRequest(
        IReadOnlyList<Requirement> requirements,
        Candidate candidate,
        string diff,
        bool diffTruncated,
        string? fullDiffPath,
        string? implementerSummary,
        IReadOnlyList<Finding> findingsToVerify)
    {
        var builder = new PromptText();
        AppendRequirements(builder, requirements);

        builder.AppendLine("## Candidate");
        builder.AppendLine("The working directory contains the candidate. It is read-only for this review.");
        builder.AppendLine($"Changed files ({candidate.Changes.Summary}):");
        foreach (var file in candidate.Changes.Files)
        {
            var renamed = file.RenamedFrom is null ? string.Empty : $" (same content as deleted {file.RenamedFrom})";
            builder.AppendLine($"- {file.Kind.ToString().ToLowerInvariant()}: {file.Path}{renamed}");
        }

        if (candidate.ExistingTestsTouched.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("Existing test files were modified or deleted. Check that no test was weakened:");
            foreach (var path in candidate.ExistingTestsTouched)
            {
                builder.AppendLine($"- {path}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Diff against the baseline");
        builder.AppendLine("The baseline is the project state before this task. Inspect any other source you need; do not limit the review to changed lines.");
        AppendReference(builder, diff);
        if (diffTruncated && fullDiffPath is not null)
        {
            builder.AppendLine($"The diff above is shortened. The complete diff is in: {fullDiffPath}");
        }

        if (!string.IsNullOrWhiteSpace(implementerSummary))
        {
            builder.AppendLine();
            builder.AppendLine("## Implementer's summary");
            builder.AppendLine("This is the implementer's own account. It is a claim to check, not evidence.");
            AppendReference(builder, implementerSummary.Trim());
        }

        if (findingsToVerify.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("## Earlier findings to verify");
            builder.AppendLine("This candidate is a repair. Verify that each finding below is resolved and that the affected behavior still works. "
                + "Inspect more widely when shared interfaces or dependencies changed.");
            foreach (var finding in findingsToVerify)
            {
                builder.AppendLine($"- {finding.FindingId} [{finding.Severity}] {finding.Location}: {finding.Title}");
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Required output");
        builder.AppendLine("Return only the structured result: status (pass, changes_required or unable_to_verify), summary, coverage "
            + "(what you inspected), limitations, and findings. Set optional to true for suggestions that are not defects or requirement violations.");
        return builder.ToString().TrimEnd();
    }

    public static string RepairRequest(
        IReadOnlyList<Requirement> requirements,
        IReadOnlyList<Finding> findings,
        IReadOnlyList<GateResult> failedGates,
        int cycle,
        int maxCycles,
        IReadOnlyList<GateDefinition>? checks = null)
    {
        var builder = new PromptText();
        builder.AppendLine($"The candidate did not pass independent checking (repair {cycle} of {maxCycles}). Fix every item below in one pass.");
        builder.AppendLine("The original requirements are unchanged and still apply in full:");
        builder.AppendLine();
        AppendRequirements(builder, requirements);

        if (failedGates.Count > 0)
        {
            builder.AppendLine("## Failed validation");
            foreach (var gate in failedGates)
            {
                builder.AppendLine($"### {gate.GateTitle}");
                builder.AppendLine($"Command: {gate.CommandLine}");
                builder.AppendLine($"Result: {gate.Status}" + (gate.ExitCode is null ? string.Empty : $" (exit {gate.ExitCode})"));
                if (gate.OutputPath is not null)
                {
                    builder.AppendLine($"Complete output: {gate.OutputPath}");
                }

                if (gate.FailsOnBaseline == true)
                {
                    builder.AppendLine("This check already failed before your change, on the unmodified project. It is still required for the task to be accepted.");
                }

                builder.AppendLine("End of output:");
                AppendReference(builder, gate.OutputTail);
                builder.AppendLine();
            }
        }

        if (findings.Count > 0)
        {
            builder.AppendLine("## Review findings");
            builder.AppendLine("These come from an independent reviewer. Fix real defects; if a finding is wrong, say why with evidence instead of changing code.");
            foreach (var finding in findings)
            {
                builder.AppendLine($"### {finding.FindingId} [{finding.Severity}] {finding.Title}");
                builder.AppendLine($"Location: {finding.Location}");
                var details = $"Failure scenario: {finding.FailureScenario}\nEvidence: {finding.Evidence}";
                if (finding.SuggestedCorrection is not null)
                {
                    details += $"\nSuggested correction: {finding.SuggestedCorrection}";
                }

                AppendReference(builder, details);
                builder.AppendLine();
            }
        }

        AppendChecks(builder, checks);
        builder.AppendLine("Do not weaken or remove tests to make a check pass.");
        return builder.ToString().TrimEnd();
    }

    /// <summary>Asks the implementer to go on after a turn that did not finish, without repeating what is done.</summary>
    public static string ContinuationRequest(IReadOnlyList<Requirement> requirements, IReadOnlyList<GateDefinition>? checks = null)
    {
        var builder = new PromptText();
        builder.AppendLine("Your earlier turn on this task did not finish. The workspace contains whatever you had changed up to then.");
        builder.AppendLine("Look at the state of the workspace first, do not repeat work that is already done, and complete the task.");
        builder.AppendLine("The requirements are unchanged:");
        builder.AppendLine();
        AppendRequirements(builder, requirements);
        AppendChecks(builder, checks);
        return builder.ToString().TrimEnd();
    }

    /// <summary>Text whose own line breaks are the same on every machine. What is appended is never altered.</summary>
    private sealed class PromptText
    {
        private readonly StringBuilder _text = new();

        public void AppendLine(string line) => _text.Append(line).Append('\n');

        public void AppendLine() => _text.Append('\n');

        public override string ToString() => _text.ToString();
    }

    /// <summary>
    /// Writes text that did not come from the user between the reference delimiters. Anything in it that
    /// looks like one of the delimiters is made harmless first, so the text cannot end its own quotation.
    /// </summary>
    private static void AppendReference(PromptText builder, string content)
    {
        builder.AppendLine(ReferenceOpen);
        builder.AppendLine(DelimiterLookalike().Replace(content.TrimEnd(), "&lt;$1"));
        builder.AppendLine(ReferenceClose);
    }

    [GeneratedRegex(@"<(\s*/?\s*reference-data)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DelimiterLookalike();

    private static void AppendRequirements(PromptText builder, IReadOnlyList<Requirement> requirements)
    {
        if (requirements.Count == 1)
        {
            builder.AppendLine("## Task");
            builder.AppendLine(requirements[0].Text);
            builder.AppendLine();
            return;
        }

        builder.AppendLine("## Task");
        builder.AppendLine(requirements[0].Text);
        builder.AppendLine();
        builder.AppendLine("## Follow-up requirements (all apply, in order)");
        for (var i = 1; i < requirements.Count; i++)
        {
            builder.AppendLine($"{i}. {requirements[i].Text}");
        }

        builder.AppendLine();
    }

    /// <summary>
    /// Names the required checks YAV runs itself on the candidate. An implementer that cannot run tests inside its
    /// sandbox then has no reason to ask for wider access only to run them, which ends a run nobody can answer.
    /// </summary>
    private static void AppendChecks(PromptText builder, IReadOnlyList<GateDefinition>? checks)
    {
        var required = checks?.Where(c => c.Required).ToList() ?? [];
        if (required.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Required checks");
        builder.AppendLine("When you have finished, YAV runs these checks itself, on the workspace as you leave it:");
        foreach (var check in required)
        {
            builder.AppendLine($"- {check.Title}: {check.DisplayCommand}");
        }

        builder.AppendLine("Run tests where your sandbox allows it. Do not ask for access beyond your sandbox only to run tests or these checks; "
            + "where they cannot run inside it, say so in your report.");
        builder.AppendLine();
    }

    private static void AppendReferences(PromptText builder, IReadOnlyList<StartingReference> references)
    {
        if (references.Count == 0)
        {
            return;
        }

        builder.AppendLine("## Starting references");
        builder.AppendLine("Verified paths that may help. They are a starting point, not a scope limit; read anything else you need.");
        foreach (var reference in references)
        {
            builder.AppendLine($"- {reference.Path} ({reference.Reason}; verified {reference.VerifiedAt:HH:mm:ss} UTC)");
        }

        builder.AppendLine();
    }
}

/// <summary>A path YAV verified exists and that is likely useful for the task.</summary>
public sealed record StartingReference(string Path, string Reason, DateTimeOffset VerifiedAt);
