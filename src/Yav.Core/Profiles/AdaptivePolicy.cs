using System.Text.RegularExpressions;

namespace Yav.Core.Profiles;

/// <param name="MayLower">True when a lower effort may be offered to the user for the task.</param>
/// <param name="Reason">Why it may not, in words for the user. Empty when it may.</param>
public sealed record AdaptiveDecision(bool MayLower, string Reason);

/// <summary>
/// Adaptive mode: which tasks may be offered a lower implementation effort at all. Whatever is decided
/// here, a lower effort is used only after the user approved it for the task; this only keeps the
/// question from being asked where the answer should be no.
/// </summary>
public static partial class AdaptivePolicy
{
    /// <summary>A request that is longer than this is not a small task.</summary>
    public const int LongestRequest = 600;

    /// <summary>A request of more paragraphs than this asks for several things.</summary>
    public const int MostParagraphs = 2;

    // ---------------------------------------------------------------------------------------------
    // YOURS TO SHAPE. What counts as risky is a judgement about your work, not a fact YAV can know.
    // The words below are a cautious start. A word here keeps every task that mentions it at the
    // effort you chose for Model A; a word that is missing lets the question be asked.
    // Words are matched as whole words, also with the endings -s, -es, -d, -ed, -ing.
    // ---------------------------------------------------------------------------------------------
    private static readonly string[] RiskyWords =
    [
        "password", "credential", "secret", "token",
        "auth", "authentication", "authorization", "login", "permission", "privilege",
        "encryption", "encrypt", "decrypt", "certificate", "signature",
        "payment", "billing", "invoice", "refund",
        "migration", "migrate", "schema", "delete", "drop", "truncate",
        "production", "deploy", "release", "rollback",
        "security", "vulnerability", "sandbox",
    ];

    public static AdaptiveDecision Decide(string request, IReadOnlyList<string> protectedPaths)
    {
        var text = request.Trim();
        if (text.Length == 0)
        {
            return new AdaptiveDecision(false, "the request is empty");
        }

        if (text.Length > LongestRequest)
        {
            return new AdaptiveDecision(false, $"the request is long ({text.Length} characters), which is not a small task");
        }

        var paragraphs = Paragraphs().Split(text).Count(p => p.Trim().Length > 0);
        if (paragraphs > MostParagraphs)
        {
            return new AdaptiveDecision(false, $"the request has {paragraphs} parts, which is not one small task");
        }

        // A path at the end of a sentence is followed by its full stop, which is not part of the path.
        foreach (var token in PathLike().Matches(text).Select(m => m.Value.TrimEnd('.', ',', ';', ':').Replace('\\', '/')))
        {
            var named = protectedPaths.FirstOrDefault(pattern => Matches(pattern, token));
            if (named is not null)
            {
                return new AdaptiveDecision(false, $"the request names '{token}', which is a protected path ({named})");
            }
        }

        foreach (var word in RiskyWords)
        {
            if (Regex.IsMatch(text, $@"\b{Regex.Escape(word)}(s|es|d|ed|ing)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
            {
                return new AdaptiveDecision(false, $"the request mentions '{word}'");
            }
        }

        return new AdaptiveDecision(true, string.Empty);
    }

    /// <summary>True when the path is what the pattern describes. "**" stands for any directories, "*" for anything inside one.</summary>
    private static bool Matches(string pattern, string path)
    {
        var expression = "^" + Regex.Escape(pattern.Trim().Replace('\\', '/'))
            .Replace(@"\*\*/", "(.*/)?", StringComparison.Ordinal)
            .Replace(@"\*\*", ".*", StringComparison.Ordinal)
            .Replace(@"\*", "[^/]*", StringComparison.Ordinal)
            .Replace(@"\?", "[^/]", StringComparison.Ordinal) + "$";
        return Regex.IsMatch(path, expression, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    [GeneratedRegex(@"(\r?\n\s*){2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Paragraphs();

    // Something with a separator or an extension in it: src/app.cs, deploy\release.yml, yav.project.json.
    [GeneratedRegex(@"[\w.\-]+(?:[\\/][\w.\-]+)+|[\w\-]+(?:\.[\w\-]+)+", RegexOptions.CultureInvariant)]
    private static partial Regex PathLike();
}
