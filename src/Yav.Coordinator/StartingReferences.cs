using Yav.Core.Templates;

namespace Yav.Coordinator;

/// <summary>
/// Finds paths the user named in a request and verifies that they exist. They are offered to the
/// implementer as a starting point. Nothing is summarized and nothing is withheld: the agent is free to read
/// whatever else it needs.
/// </summary>
internal static class StartingReferences
{
    private const int Limit = 12;

    /// <summary>Written with periods in ordinary text, which makes them look like a file name with an extension.</summary>
    private static readonly string[] Abbreviations = ["e.g", "i.e", "etc", "vs", "a.m", "p.m"];

    private static readonly char[] Separators = [' ', '\t', '\r', '\n', '"', '\'', '`', '(', ')', '[', ']', '<', '>', ',', ';'];

    public static IReadOnlyList<StartingReference> Find(string requestText, string directory, DateTimeOffset now)
    {
        var references = new List<StartingReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar);

        foreach (var raw in requestText.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            if (references.Count >= Limit)
            {
                break;
            }

            var token = raw.Trim().TrimEnd('.', ':', '!', '?');
            if (token.Length is < 3 or > 260 || !LooksLikePath(token) || Abbreviations.Contains(token, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            string full;
            try
            {
                full = Path.GetFullPath(Path.Combine(root, token.Replace('/', Path.DirectorySeparatorChar)));
            }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                continue;
            }

            // Only paths inside the workspace are offered; a request cannot point the agent somewhere else this way.
            if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isFile = File.Exists(full);
            if (!isFile && !Directory.Exists(full))
            {
                continue;
            }

            var relative = Path.GetRelativePath(root, full).Replace('\\', '/').TrimEnd('/');
            if (seen.Add(relative))
            {
                references.Add(new StartingReference(relative, isFile ? "named in the request" : "directory named in the request", now));
            }
        }

        return references;
    }

    private static bool LooksLikePath(string token)
    {
        if (token.Contains("://", StringComparison.Ordinal) || token.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || token.Contains('*') || token.Contains('?'))
        {
            return false;
        }

        if (token.Contains('/') || token.Contains('\\'))
        {
            return true;
        }

        // A bare name with an extension, such as "README.md".
        var dot = token.LastIndexOf('.');
        return dot > 0 && dot < token.Length - 1 && token.Length - dot <= 8 && token[(dot + 1)..].All(char.IsLetterOrDigit);
    }
}
