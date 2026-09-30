using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Yav.Workspace;

/// <summary>
/// Matches workspace-relative paths against patterns. <c>*</c> and <c>?</c> stay inside one path segment,
/// <c>**</c> spans segments. Matching ignores letter case, as the file system does.
/// </summary>
public static class Glob
{
    private static readonly ConcurrentDictionary<string, Regex> Cache = new(StringComparer.Ordinal);

    public static bool IsMatch(string pattern, string path)
    {
        var regex = Cache.GetOrAdd(pattern, Compile);
        return regex.IsMatch(path.Replace('\\', '/'));
    }

    public static bool MatchesAny(IEnumerable<string> patterns, string path)
    {
        foreach (var pattern in patterns)
        {
            if (IsMatch(pattern, path))
            {
                return true;
            }
        }

        return false;
    }

    private static Regex Compile(string pattern)
    {
        var text = pattern.Trim().Replace('\\', '/');
        while (text.StartsWith("./", StringComparison.Ordinal))
        {
            text = text[2..];
        }

        var builder = new StringBuilder("^");
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (c == '*' && i + 1 < text.Length && text[i + 1] == '*')
            {
                var atSegmentStart = i == 0 || text[i - 1] == '/';
                var followedBySlash = i + 2 < text.Length && text[i + 2] == '/';
                var atEnd = i + 2 == text.Length;
                if (atSegmentStart && followedBySlash)
                {
                    // "**/" matches any number of directories, including none.
                    builder.Append("(?:.*/)?");
                    i += 3;
                }
                else if (atEnd)
                {
                    builder.Append(".*");
                    i += 2;
                }
                else
                {
                    builder.Append(".*");
                    i += 2;
                }
            }
            else if (c == '*')
            {
                builder.Append("[^/]*");
                i++;
            }
            else if (c == '?')
            {
                builder.Append("[^/]");
                i++;
            }
            else
            {
                builder.Append(Regex.Escape(c.ToString()));
                i++;
            }
        }

        builder.Append('$');
        return new Regex(builder.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromSeconds(2));
    }
}

/// <summary>Recognizes files that usually hold credentials, so they are never copied without explicit permission.</summary>
public static class SecretPaths
{
    private static readonly string[] ExactNames =
    [
        ".env", ".npmrc", ".pypirc", ".netrc", "id_rsa", "id_dsa", "id_ecdsa", "id_ed25519", "auth.json", ".git-credentials",
        "credentials", "secrets", ".htpasswd",
    ];

    private static readonly string[] Extensions = [".pem", ".key", ".pfx", ".p12", ".jks", ".keystore", ".tfvars", ".ppk"];

    private static readonly string[] HarmlessEnvSuffixes = [".example", ".sample", ".template", ".dist", ".defaults"];

    public static bool LooksLikeSecret(string path)
    {
        var name = Path.GetFileName(path.Replace('\\', '/'));
        if (name.Length == 0)
        {
            return false;
        }

        if (ExactNames.Contains(name, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.StartsWith(".env.", StringComparison.OrdinalIgnoreCase))
        {
            return !HarmlessEnvSuffixes.Any(suffix => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        }

        var extension = Path.GetExtension(name);
        if (Extensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        return stem.Equals("secrets", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("secret", StringComparison.OrdinalIgnoreCase)
            || stem.Equals("credentials", StringComparison.OrdinalIgnoreCase);
    }
}
