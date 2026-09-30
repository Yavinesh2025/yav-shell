using System.Text.Json;

namespace Yav.Core.Runs;

public sealed record ParsedReview(
    ReviewVerdict Verdict,
    string Summary,
    string Coverage,
    IReadOnlyList<Finding> Findings,
    IReadOnlyList<string> Limitations,
    bool OutputValid,
    IReadOnlyList<string> ValidationErrors,
    IReadOnlyList<string> Notes);

/// <summary>
/// Validates the reviewer's structured output in application code. The provider's structured-output
/// facility is used when available, but its result is never trusted without this check, and anything
/// invalid, incomplete or self-contradictory is reported as Unable to Verify rather than as a pass.
/// </summary>
public static class ReviewOutputParser
{
    /// <param name="structured">The object returned by the provider's structured-output facility, if any.</param>
    /// <param name="finalMessage">The reviewer's final message, used only when it is itself a JSON object.</param>
    /// <param name="pathExists">Returns true when a workspace-relative path exists in the candidate or the baseline.</param>
    public static ParsedReview Parse(JsonElement? structured, string? finalMessage, Func<string, bool> pathExists)
    {
        var errors = new List<string>();
        var notes = new List<string>();

        JsonElement root;
        JsonDocument? owned = null;
        try
        {
            if (structured is { ValueKind: JsonValueKind.Object } provided)
            {
                root = provided;
            }
            else if (TryParseMessage(finalMessage, out owned, out var parseError))
            {
                root = owned!.RootElement;
                notes.Add("The provider returned no structured object; the final message was parsed as JSON.");
            }
            else
            {
                errors.Add(parseError ?? "The reviewer returned no structured output.");
                return Invalid(errors, notes);
            }

            return ParseRoot(root, pathExists, errors, notes);
        }
        finally
        {
            owned?.Dispose();
        }
    }

    private static ParsedReview ParseRoot(JsonElement root, Func<string, bool> pathExists, List<string> errors, List<string> notes)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("The review output is not a JSON object.");
            return Invalid(errors, notes);
        }

        ReviewVerdict? verdict = null;
        if (!TryGetString(root, "status", out var status))
        {
            errors.Add("Missing required field 'status'.");
        }
        else
        {
            verdict = status switch
            {
                "pass" => ReviewVerdict.Pass,
                "changes_required" => ReviewVerdict.ChangesRequired,
                "unable_to_verify" => ReviewVerdict.UnableToVerify,
                _ => null,
            };
            if (verdict is null)
            {
                errors.Add($"Unknown status '{Shorten(status)}'. Expected pass, changes_required or unable_to_verify.");
            }
        }

        if (!TryGetString(root, "summary", out var summary))
        {
            errors.Add("Missing required field 'summary'.");
            summary = string.Empty;
        }

        if (!TryGetString(root, "coverage", out var coverage))
        {
            errors.Add("Missing required field 'coverage'.");
            coverage = string.Empty;
        }

        var limitations = new List<string>();
        if (!root.TryGetProperty("limitations", out var limitationsElement) || limitationsElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Missing required array 'limitations'.");
        }
        else
        {
            foreach (var item in limitationsElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                {
                    limitations.Add(item.GetString()!.Trim());
                }
                else if (item.ValueKind != JsonValueKind.String)
                {
                    errors.Add("Every entry of 'limitations' must be a string.");
                }
            }
        }

        var findings = new List<Finding>();
        if (!root.TryGetProperty("findings", out var findingsElement) || findingsElement.ValueKind != JsonValueKind.Array)
        {
            errors.Add("Missing required array 'findings'.");
        }
        else
        {
            var index = 0;
            foreach (var item in findingsElement.EnumerateArray())
            {
                index++;
                var finding = ParseFinding(item, index, pathExists, errors, notes);
                if (finding is not null)
                {
                    findings.Add(finding);
                }
            }
        }

        if (verdict is not null && errors.Count == 0)
        {
            var blocking = findings.Count(f => f.Blocking);
            switch (verdict)
            {
                case ReviewVerdict.Pass when blocking > 0:
                    errors.Add($"The status is pass but {blocking} blocking finding(s) were reported.");
                    break;
                case ReviewVerdict.Pass when string.IsNullOrWhiteSpace(coverage):
                    errors.Add("The status is pass but 'coverage' does not say what was inspected.");
                    break;
                case ReviewVerdict.ChangesRequired when blocking == 0:
                    errors.Add("The status is changes_required but no blocking finding was reported.");
                    break;
                case ReviewVerdict.UnableToVerify when limitations.Count == 0:
                    errors.Add("The status is unable_to_verify but no limitation was given.");
                    break;
            }
        }

        if (errors.Count > 0)
        {
            return new ParsedReview(ReviewVerdict.UnableToVerify, summary, coverage, findings, limitations, false, errors, notes);
        }

        return new ParsedReview(verdict!.Value, summary, coverage, findings, limitations, true, errors, notes);
    }

    private static Finding? ParseFinding(JsonElement item, int index, Func<string, bool> pathExists, List<string> errors, List<string> notes)
    {
        var label = $"findings[{index}]";
        if (item.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{label} is not an object.");
            return null;
        }

        var before = errors.Count;

        FindingSeverity severity = default;
        if (!TryGetString(item, "severity", out var severityText) || !TryParseSeverity(severityText, out severity))
        {
            errors.Add($"{label}.severity must be blocker, major or minor.");
        }

        FindingCategory category = default;
        if (!TryGetString(item, "category", out var categoryText) || !TryParseCategory(categoryText, out category))
        {
            errors.Add($"{label}.category is missing or unknown.");
        }

        var optional = false;
        if (!item.TryGetProperty("optional", out var optionalElement) || optionalElement.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            errors.Add($"{label}.optional must be true or false.");
        }
        else
        {
            optional = optionalElement.GetBoolean();
        }

        if (!item.TryGetProperty("file", out var fileElement) || fileElement.ValueKind != JsonValueKind.String)
        {
            errors.Add($"{label}.file must be a string.");
        }

        var file = fileElement.ValueKind == JsonValueKind.String ? fileElement.GetString()! : string.Empty;

        int? line = null;
        if (!item.TryGetProperty("line", out var lineElement))
        {
            errors.Add($"{label}.line is missing (use null when unknown).");
        }
        else if (lineElement.ValueKind == JsonValueKind.Number && lineElement.TryGetInt32(out var parsedLine) && parsedLine > 0)
        {
            line = parsedLine;
        }
        else if (lineElement.ValueKind != JsonValueKind.Null)
        {
            errors.Add($"{label}.line must be a positive integer or null.");
        }

        if (!TryGetString(item, "title", out var title) || string.IsNullOrWhiteSpace(title))
        {
            errors.Add($"{label}.title is missing.");
        }

        TryGetString(item, "failure_scenario", out var scenario);
        TryGetString(item, "evidence", out var evidence);
        var correction = GetNullableString(item, "suggested_correction");
        var limitation = GetNullableString(item, "limitation");

        if (category == FindingCategory.Style && !optional)
        {
            // A style preference never blocks. A violated project rule must be reported as requirement_violation.
            optional = true;
            notes.Add($"{label} is a style finding and was treated as optional.");
        }

        if (!optional)
        {
            if (string.IsNullOrWhiteSpace(scenario))
            {
                errors.Add($"{label} is blocking but has no concrete failure_scenario.");
            }

            if (string.IsNullOrWhiteSpace(evidence))
            {
                errors.Add($"{label} is blocking but has no evidence.");
            }
        }

        if (errors.Count > before)
        {
            return null;
        }

        var normalized = NormalizePath(file);
        var located = normalized.Length > 0 && IsSafeRelativePath(normalized) && pathExists(normalized);
        if (normalized.Length > 0 && !located)
        {
            notes.Add($"{label} names '{Shorten(file)}', which was not found in the candidate or the baseline.");
        }

        return new Finding(
            FindingId: $"F{index}",
            Severity: severity,
            Category: category,
            Optional: optional,
            File: normalized,
            Line: line,
            Title: title.Trim(),
            FailureScenario: (scenario ?? string.Empty).Trim(),
            Evidence: (evidence ?? string.Empty).Trim(),
            SuggestedCorrection: string.IsNullOrWhiteSpace(correction) ? null : correction.Trim(),
            Limitation: string.IsNullOrWhiteSpace(limitation) ? null : limitation.Trim(),
            LocationVerified: located);
    }

    private static bool TryParseMessage(string? message, out JsonDocument? document, out string? error)
    {
        document = null;
        error = null;
        if (string.IsNullOrWhiteSpace(message))
        {
            error = "The reviewer returned no structured output and no final message.";
            return false;
        }

        var text = message.Trim();
        if (text.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBreak = text.IndexOf('\n');
            var lastFence = text.LastIndexOf("```", StringComparison.Ordinal);
            if (firstBreak < 0 || lastFence <= firstBreak)
            {
                error = "The reviewer's final message is not valid JSON.";
                return false;
            }

            text = text[(firstBreak + 1)..lastFence].Trim();
        }

        if (!text.StartsWith('{'))
        {
            error = "The reviewer's final message is prose, not the required structured result.";
            return false;
        }

        try
        {
            document = JsonDocument.Parse(text, new JsonDocumentOptions { AllowTrailingCommas = false, CommentHandling = JsonCommentHandling.Disallow });
            return true;
        }
        catch (JsonException ex)
        {
            error = $"The reviewer's output is not valid JSON: {ex.Message}";
            return false;
        }
    }

    private static ParsedReview Invalid(List<string> errors, List<string> notes) =>
        new(ReviewVerdict.UnableToVerify, string.Empty, string.Empty, [], [], false, errors, notes);

    private static bool TryGetString(JsonElement element, string name, out string value)
    {
        if (element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static string? GetNullableString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;

    private static bool TryParseSeverity(string text, out FindingSeverity severity)
    {
        switch (text)
        {
            case "blocker": severity = FindingSeverity.Blocker; return true;
            case "major": severity = FindingSeverity.Major; return true;
            case "minor": severity = FindingSeverity.Minor; return true;
            default: severity = default; return false;
        }
    }

    private static bool TryParseCategory(string text, out FindingCategory category)
    {
        switch (text)
        {
            case "defect": category = FindingCategory.Defect; return true;
            case "requirement_violation": category = FindingCategory.RequirementViolation; return true;
            case "regression": category = FindingCategory.Regression; return true;
            case "security": category = FindingCategory.Security; return true;
            case "test_adequacy": category = FindingCategory.TestAdequacy; return true;
            case "style": category = FindingCategory.Style; return true;
            default: category = default; return false;
        }
    }

    internal static string NormalizePath(string path)
    {
        var text = path.Trim().Replace('\\', '/');
        while (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        return text;
    }

    internal static bool IsSafeRelativePath(string path)
    {
        if (path.Length == 0 || path.StartsWith('/') || (path.Length > 1 && path[1] == ':'))
        {
            return false;
        }

        foreach (var segment in path.Split('/'))
        {
            if (segment == "..")
            {
                return false;
            }
        }

        return true;
    }

    private static string Shorten(string text) => text.Length <= 80 ? text : text[..80] + "...";
}
