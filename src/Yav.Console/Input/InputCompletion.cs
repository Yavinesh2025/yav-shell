using Yav.Console.Commands;

namespace Yav.Console.Input;

public sealed record CompletionCandidate(string Text, string Description);

/// <param name="Start">Where in the text the part begins that a candidate replaces.</param>
/// <param name="Length">How much is replaced. The part that is replaced always contains the caret.</param>
public sealed record CompletionResult(int Start, int Length, IReadOnlyList<CompletionCandidate> Candidates)
{
    /// <summary>Nothing to offer at that place.</summary>
    public static CompletionResult NothingAt(int caret) => new(caret, 0, []);
}

/// <summary>
/// What can be completed at the caret: names of commands, their words, and paths. A request is never
/// completed: what the user writes to a model is theirs alone.
/// </summary>
public static class InputCompletion
{
    public const int MaxCandidates = 50;

    private static readonly Dictionary<string, string[]> Words = new(StringComparer.Ordinal)
    {
        ["speed"] = ["standard", "provider"],
        ["adaptive"] = ["on", "off"],
        ["optimization"] = ["on", "off"],
        ["models"] = ["a", "b", "swap", "refresh"],
        ["effort"] = ["a", "b"],
        ["login"] = ["codex", "claude"],
        ["quality"] = ["lock", "strict", "gates", "preexisting"],
        ["test"] = ["list", "detect", "trust", "waive"],
        ["review"] = ["show", "approve"],
        ["queue"] = ["add", "remove", "clear", "steer"],
        ["history"] = ["export", "delete", "prune"],
        ["limits"] = ["minutes", "repairs", "tokens", "ratelimit", "queue"],
        ["shell"] = ["pwsh", "powershell", "cmd"],
        ["apply"] = ["--merge"],
        ["undo"] = ["--skip-edited"],
        ["diff"] = ["--stat", "--full", "--export"],
    };

    private static readonly string[] DirectoryCommands = ["open", "cd"];
    private static readonly string[] FileCommands = ["attach"];

    public static CompletionResult For(string text, int caret, string? projectPath)
    {
        caret = Math.Clamp(caret, 0, text.Length);
        if (!text.StartsWith('/') || text.Contains('\n'))
        {
            return CompletionResult.NothingAt(caret);
        }

        var nameEnd = 1;
        while (nameEnd < text.Length && !char.IsWhiteSpace(text[nameEnd]))
        {
            nameEnd++;
        }

        if (caret <= nameEnd)
        {
            var typed = text[..caret];
            var candidates = CommandCatalog.All
                .Where(c => ("/" + c.Name).StartsWith(typed, StringComparison.OrdinalIgnoreCase))
                .OrderBy(c => c.Name, StringComparer.Ordinal)
                .Select(c => new CompletionCandidate("/" + c.Name, c.Summary))
                .ToList();
            return new CompletionResult(0, nameEnd, candidates);
        }

        var command = CommandCatalog.Find(text[1..nameEnd]);
        if (command is null)
        {
            return CompletionResult.NothingAt(caret);
        }

        // The argument the caret is in: from the last separator that is not inside quotes.
        var start = nameEnd;
        var quoted = false;
        for (var i = nameEnd; i < caret; i++)
        {
            if (text[i] == '"')
            {
                quoted = !quoted;
                if (quoted)
                {
                    start = i;
                }
            }
            else if (char.IsWhiteSpace(text[i]) && !quoted)
            {
                start = i + 1;
            }
        }

        if (DirectoryCommands.Contains(command.Name, StringComparer.Ordinal) || FileCommands.Contains(command.Name, StringComparer.Ordinal))
        {
            // These commands take one path, and a path may contain spaces: everything after the name is the path.
            var pathStart = nameEnd;
            while (pathStart < caret && char.IsWhiteSpace(text[pathStart]))
            {
                pathStart++;
            }

            var files = FileCommands.Contains(command.Name, StringComparer.Ordinal);
            return new CompletionResult(pathStart, caret - pathStart, Paths(text[pathStart..caret].Trim('"'), projectPath, files));
        }

        var fragment = text[start..caret];
        var length = caret - start;
        var unquoted = fragment.Trim('"');

        if (Words.TryGetValue(command.Name, out var words) && IsFirstArgument(text, nameEnd, start))
        {
            var matches = words
                .Where(w => w.StartsWith(unquoted, StringComparison.OrdinalIgnoreCase))
                .Select(w => new CompletionCandidate(w, string.Empty))
                .ToList();
            return new CompletionResult(start, length, matches);
        }

        return CompletionResult.NothingAt(caret);
    }

    private static bool IsFirstArgument(string text, int nameEnd, int start) => string.IsNullOrWhiteSpace(text[nameEnd..start]);

    private static List<CompletionCandidate> Paths(string typed, string? projectPath, bool includeFiles)
    {
        var candidates = new List<CompletionCandidate>();
        try
        {
            if (typed.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            {
                return candidates;
            }

            var separator = typed.LastIndexOfAny(['\\', '/']);
            var directoryPart = separator >= 0 ? typed[..(separator + 1)] : string.Empty;
            var namePart = separator >= 0 ? typed[(separator + 1)..] : typed;

            string directory;
            if (Path.IsPathFullyQualified(directoryPart))
            {
                directory = directoryPart;
            }
            else if (projectPath is not null && !Path.IsPathRooted(directoryPart))
            {
                directory = Path.Combine(projectPath, directoryPart);
            }
            else
            {
                return candidates;
            }

            if (!Directory.Exists(directory))
            {
                return candidates;
            }

            var options = new EnumerationOptions
            {
                IgnoreInaccessible = true,
                RecurseSubdirectories = false,
                MatchCasing = MatchCasing.CaseInsensitive,
                AttributesToSkip = FileAttributes.System,
            };

            foreach (var entry in Directory.EnumerateDirectories(directory, namePart + "*", options).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (candidates.Count >= MaxCandidates)
                {
                    return candidates;
                }

                candidates.Add(new CompletionCandidate(Quote(directoryPart.Replace('/', '\\') + Path.GetFileName(entry) + "\\"), "directory"));
            }

            if (includeFiles)
            {
                foreach (var entry in Directory.EnumerateFiles(directory, namePart + "*", options).Order(StringComparer.OrdinalIgnoreCase))
                {
                    if (candidates.Count >= MaxCandidates)
                    {
                        return candidates;
                    }

                    candidates.Add(new CompletionCandidate(Quote(directoryPart.Replace('/', '\\') + Path.GetFileName(entry)), "file"));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException)
        {
            // Completion is a convenience. A path that cannot be read simply has no candidates.
            candidates.Clear();
        }

        return candidates;
    }

    private static string Quote(string path) => path.Any(char.IsWhiteSpace) ? "\"" + path + "\"" : path;
}
