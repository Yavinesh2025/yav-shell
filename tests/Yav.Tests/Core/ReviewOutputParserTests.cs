using System.Text.Json;
using Yav.Core.Runs;

namespace Yav.Tests.Core;

public class ReviewOutputParserTests
{
    private static readonly Func<string, bool> EveryPathExists = _ => true;

    private const string BlockingFinding = """
        {
          "severity": "major", "category": "defect", "optional": false,
          "file": "src/login.cs", "line": 42, "title": "Null user is dereferenced",
          "failure_scenario": "Login with an unknown user name throws instead of returning 401.",
          "evidence": "user.PasswordHash is read at line 42 before the null check at line 45.",
          "suggested_correction": "Move the null check before the first use.", "limitation": null
        }
        """;

    private const string Suggestion = """
        {
          "severity": "minor", "category": "style", "optional": true,
          "file": "src/login.cs", "line": null, "title": "Name could be clearer",
          "failure_scenario": "", "evidence": "", "suggested_correction": null, "limitation": null
        }
        """;

    private static string Output(string status, string findings = "", string limitations = "", string coverage = "Read login.cs and its tests.") => $$"""
        {
          "status": "{{status}}",
          "summary": "Reviewed the login change.",
          "coverage": "{{coverage}}",
          "limitations": [{{limitations}}],
          "findings": [{{findings}}]
        }
        """;

    private static ParsedReview Parse(string json, Func<string, bool>? exists = null)
    {
        using var document = JsonDocument.Parse(json);
        return ReviewOutputParser.Parse(document.RootElement.Clone(), null, exists ?? EveryPathExists);
    }

    [Fact]
    public void A_pass_without_findings_is_accepted()
    {
        var review = Parse(Output("pass"));

        Assert.True(review.OutputValid);
        Assert.Equal(ReviewVerdict.Pass, review.Verdict);
        Assert.Empty(review.Findings);
    }

    [Fact]
    public void A_pass_with_only_suggestions_is_accepted()
    {
        var review = Parse(Output("pass", Suggestion));

        Assert.True(review.OutputValid);
        Assert.Equal(ReviewVerdict.Pass, review.Verdict);
        Assert.True(Assert.Single(review.Findings).Optional);
    }

    [Fact]
    public void A_pass_that_also_reports_a_blocking_finding_is_not_a_pass()
    {
        var review = Parse(Output("pass", BlockingFinding));

        Assert.False(review.OutputValid);
        Assert.Equal(ReviewVerdict.UnableToVerify, review.Verdict);
        Assert.Contains(review.ValidationErrors, e => e.Contains("blocking finding"));
    }

    [Fact]
    public void Changes_required_carries_its_findings()
    {
        var review = Parse(Output("changes_required", BlockingFinding + "," + Suggestion));

        Assert.True(review.OutputValid);
        Assert.Equal(ReviewVerdict.ChangesRequired, review.Verdict);
        Assert.Equal(2, review.Findings.Count);
        var finding = review.Findings[0];
        Assert.Equal("src/login.cs:42", finding.Location);
        Assert.Equal(FindingSeverity.Major, finding.Severity);
        Assert.True(finding.Blocking);
        Assert.True(finding.LocationVerified);
    }

    [Fact]
    public void Changes_required_without_a_blocking_finding_is_invalid()
    {
        var review = Parse(Output("changes_required", Suggestion));

        Assert.False(review.OutputValid);
        Assert.Equal(ReviewVerdict.UnableToVerify, review.Verdict);
    }

    [Fact]
    public void Unable_to_verify_needs_a_stated_limitation()
    {
        var without = Parse(Output("unable_to_verify"));
        var with = Parse(Output("unable_to_verify", limitations: "\"The integration tests need a database.\""));

        Assert.False(without.OutputValid);
        Assert.True(with.OutputValid);
        Assert.Equal(ReviewVerdict.UnableToVerify, with.Verdict);
        Assert.Equal(["The integration tests need a database."], with.Limitations);
    }

    [Fact]
    public void A_pass_that_does_not_say_what_was_inspected_is_invalid()
    {
        var review = Parse(Output("pass", coverage: ""));

        Assert.False(review.OutputValid);
    }

    [Fact]
    public void An_unknown_status_is_invalid()
    {
        var review = Parse(Output("approved"));

        Assert.False(review.OutputValid);
        Assert.Equal(ReviewVerdict.UnableToVerify, review.Verdict);
    }

    [Fact]
    public void A_missing_required_field_is_invalid()
    {
        var review = Parse("""{ "status": "pass", "summary": "fine", "findings": [] }""");

        Assert.False(review.OutputValid);
        Assert.Contains(review.ValidationErrors, e => e.Contains("coverage"));
        Assert.Contains(review.ValidationErrors, e => e.Contains("limitations"));
    }

    [Fact]
    public void A_blocking_finding_without_a_failure_scenario_or_evidence_is_invalid()
    {
        var vague = """
            {
              "severity": "blocker", "category": "defect", "optional": false, "file": "a.cs", "line": 1,
              "title": "Looks wrong", "failure_scenario": "", "evidence": " ", "suggested_correction": null, "limitation": null
            }
            """;

        var review = Parse(Output("changes_required", vague));

        Assert.False(review.OutputValid);
        Assert.Contains(review.ValidationErrors, e => e.Contains("failure_scenario"));
        Assert.Contains(review.ValidationErrors, e => e.Contains("evidence"));
    }

    [Fact]
    public void A_style_finding_marked_blocking_is_treated_as_a_suggestion()
    {
        var style = """
            {
              "severity": "minor", "category": "style", "optional": false, "file": "a.cs", "line": 3,
              "title": "Prefer var", "failure_scenario": "none", "evidence": "line 3", "suggested_correction": null, "limitation": null
            }
            """;

        var review = Parse(Output("pass", style));

        Assert.True(review.OutputValid);
        Assert.Equal(ReviewVerdict.Pass, review.Verdict);
        Assert.True(Assert.Single(review.Findings).Optional);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("C:/Windows/system32/config")]
    [InlineData("/absolute/path.cs")]
    public void A_finding_outside_the_workspace_is_marked_unlocated(string file)
    {
        var finding = BlockingFinding.Replace("src/login.cs", file);

        var review = Parse(Output("changes_required", finding));

        Assert.True(review.OutputValid);
        Assert.False(Assert.Single(review.Findings).LocationVerified);
    }

    [Fact]
    public void A_finding_for_a_file_that_does_not_exist_is_marked_unlocated()
    {
        var review = Parse(Output("changes_required", BlockingFinding), exists: path => path != "src/login.cs");

        Assert.False(Assert.Single(review.Findings).LocationVerified);
        Assert.Contains(review.Notes, n => n.Contains("src/login.cs"));
    }

    [Fact]
    public void Backslashes_and_leading_dot_segments_in_a_path_are_normalized()
    {
        var finding = BlockingFinding.Replace("src/login.cs", ".\\\\src\\\\login.cs");

        var review = Parse(Output("changes_required", finding));

        Assert.Equal("src/login.cs", Assert.Single(review.Findings).File);
    }

    [Fact]
    public void A_final_message_that_is_json_is_parsed_when_no_structured_object_was_returned()
    {
        var review = ReviewOutputParser.Parse(null, "```json\n" + Output("pass") + "\n```", EveryPathExists);

        Assert.True(review.OutputValid);
        Assert.Equal(ReviewVerdict.Pass, review.Verdict);
    }

    [Theory]
    [InlineData("Looks good to me, ship it!")]
    [InlineData("")]
    [InlineData("{ \"status\": \"pass\", ")]
    [InlineData("The result is {\"status\":\"pass\"} as requested.")]
    public void Prose_or_broken_json_is_never_a_pass(string message)
    {
        var review = ReviewOutputParser.Parse(null, message, EveryPathExists);

        Assert.False(review.OutputValid);
        Assert.Equal(ReviewVerdict.UnableToVerify, review.Verdict);
        Assert.NotEmpty(review.ValidationErrors);
    }

    [Fact]
    public void The_schema_sent_to_the_reviewer_is_valid_json_with_every_field_required()
    {
        var schema = ReviewSchema.AsElement();

        var required = schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        var properties = schema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToArray();
        Assert.Equal(properties.Order(), required.Order());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
    }
}
