using System.Text.Json;

namespace Yav.Core.Runs;

public enum ReviewVerdict
{
    Pass,
    ChangesRequired,
    UnableToVerify,
}

public enum FindingSeverity
{
    Blocker,
    Major,
    Minor,
}

public enum FindingCategory
{
    Defect,
    RequirementViolation,
    Regression,
    Security,
    TestAdequacy,
    Style,
}

public sealed record Finding(
    string FindingId,
    FindingSeverity Severity,
    FindingCategory Category,
    // Optional findings are suggestions. They never block acceptance and are not sent for repair unless the user asks.
    bool Optional,
    string File,
    int? Line,
    string Title,
    string FailureScenario,
    string Evidence,
    string? SuggestedCorrection,
    string? Limitation,
    // False when the reported file does not exist in the candidate or the baseline.
    bool LocationVerified)
{
    public bool Blocking => !Optional;

    public string Location => Line is null ? File : $"{File}:{Line}";
}

public sealed record ReviewResult(
    string ReviewId,
    string RunId,
    string CandidateId,
    EvidenceBinding Binding,
    ReviewVerdict Verdict,
    string Summary,
    string Coverage,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Limitations,
    // False when the reviewer's output could not be parsed or contradicted itself. Invalid output is never a pass.
    bool OutputValid,
    IReadOnlyList<string> ValidationErrors,
    string ReviewerAdapterId,
    string ReviewerModel,
    string? ReviewerSessionId,
    // True when the workspace fingerprint was unchanged after the review finished.
    bool SourceUnchangedDuringReview,
    DateTimeOffset StartedAt,
    DateTimeOffset CompletedAt)
{
    public IEnumerable<Finding> BlockingFindings => Findings.Where(f => f.Blocking);

    public IEnumerable<Finding> Suggestions => Findings.Where(f => f.Optional);

    public bool IsCleanPass => OutputValid && SourceUnchangedDuringReview && Verdict == ReviewVerdict.Pass && !BlockingFindings.Any();
}

/// <summary>The JSON Schema sent to the reviewer through the provider's structured-output facility.</summary>
public static class ReviewSchema
{
    public const string Version = "review-output/1";

    // Every property is required and additional properties are forbidden so the same schema is accepted by
    // providers that only support a strict subset of JSON Schema.
    public const string Json = """
    {
      "type": "object",
      "additionalProperties": false,
      "required": ["status", "summary", "coverage", "limitations", "findings"],
      "properties": {
        "status": { "type": "string", "enum": ["pass", "changes_required", "unable_to_verify"] },
        "summary": { "type": "string" },
        "coverage": { "type": "string" },
        "limitations": { "type": "array", "items": { "type": "string" } },
        "findings": {
          "type": "array",
          "items": {
            "type": "object",
            "additionalProperties": false,
            "required": ["severity", "category", "optional", "file", "line", "title", "failure_scenario", "evidence", "suggested_correction", "limitation"],
            "properties": {
              "severity": { "type": "string", "enum": ["blocker", "major", "minor"] },
              "category": { "type": "string", "enum": ["defect", "requirement_violation", "regression", "security", "test_adequacy", "style"] },
              "optional": { "type": "boolean" },
              "file": { "type": "string" },
              "line": { "type": ["integer", "null"] },
              "title": { "type": "string" },
              "failure_scenario": { "type": "string" },
              "evidence": { "type": "string" },
              "suggested_correction": { "type": ["string", "null"] },
              "limitation": { "type": ["string", "null"] }
            }
          }
        }
      }
    }
    """;

    public static JsonElement AsElement()
    {
        using var document = JsonDocument.Parse(Json);
        return document.RootElement.Clone();
    }
}
