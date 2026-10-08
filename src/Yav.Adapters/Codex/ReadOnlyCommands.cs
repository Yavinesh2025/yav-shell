namespace Yav.Adapters;

/// <summary>
/// Decides whether a command that Codex asks to run only reads. Such a command is allowed without asking the user
/// (decided by the user on 2026-10-08: "it asks too many questions"). The rule is strict on purpose: a command is
/// read-only only when every part of it is on the list below and nothing in it can chain, redirect or run other code.
/// Anything else is asked about as before.
/// </summary>
public static class ReadOnlyCommands
{
    /// <summary>Characters that chain commands, redirect output or embed code in a shell or in PowerShell.</summary>
    private static readonly char[] Forbidden = [';', '&', '>', '<', '`', '$', '{', '}', '(', ')', '\r', '\n'];

    private static readonly HashSet<string> Shells = new(StringComparer.OrdinalIgnoreCase) { "pwsh", "powershell", "cmd" };

    private static readonly HashSet<string> Readers = new(StringComparer.OrdinalIgnoreCase)
    {
        "get-childitem", "gci", "ls", "dir", "get-content", "gc", "cat", "type", "get-item", "gi", "test-path",
        "resolve-path", "get-location", "pwd", "select-object", "select", "select-string", "sls", "sort-object", "sort",
        "measure-object", "measure", "format-table", "ft", "format-list", "fl", "out-string", "findstr", "rg", "where",
        "get-command", "tree", "git",
    };

    private static readonly HashSet<string> GitReaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "status", "diff", "log", "show", "ls-files", "rev-parse", "branch", "blame", "grep",
    };

    public static bool IsReadOnly(string? command)
    {
        if (string.IsNullOrWhiteSpace(command) || command.IndexOfAny(Forbidden) >= 0)
        {
            return false;
        }

        var script = Unwrap(command.Trim());
        if (script is null)
        {
            return false;
        }

        foreach (var part in script.Split('|'))
        {
            var words = Words(part);
            if (words.Count == 0 || !Readers.Contains(Name(words[0])))
            {
                return false;
            }

            if (words.Any(w => w.StartsWith("--output", StringComparison.OrdinalIgnoreCase) || w.Equals("-Wait", StringComparison.OrdinalIgnoreCase)))
            {
                return false;
            }

            if (Name(words[0]) == "git" && (words.Count < 2 || !GitReaders.Contains(words[1])
                || (words[1].Equals("branch", StringComparison.OrdinalIgnoreCase) && words.Skip(2).Any(w => !w.StartsWith("--list", StringComparison.Ordinal) && w is not ("-a" or "-v" or "-r")))))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The script inside a shell wrapper such as <c>pwsh -NoProfile -Command '...'</c>, or the command itself.</summary>
    private static string? Unwrap(string command)
    {
        var words = Words(command);
        if (words.Count == 0 || !Shells.Contains(Name(words[0])))
        {
            return command;
        }

        // Only these switches may come before the script; anything else (-File, -EncodedCommand, ...) is not read.
        var index = 1;
        while (index < words.Count && words[index] is var w
            && (w.Equals("-NoProfile", StringComparison.OrdinalIgnoreCase) || w.Equals("-NonInteractive", StringComparison.OrdinalIgnoreCase)
                || w.Equals("-NoLogo", StringComparison.OrdinalIgnoreCase) || w.Equals("/d", StringComparison.OrdinalIgnoreCase)))
        {
            index++;
        }

        if (index >= words.Count || !(words[index].Equals("-Command", StringComparison.OrdinalIgnoreCase) || words[index].Equals("-c", StringComparison.OrdinalIgnoreCase) || words[index].Equals("/c", StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        var rest = string.Join(' ', words.Skip(index + 1));
        return rest.Length == 0 || Shells.Contains(Name(Words(rest).FirstOrDefault() ?? string.Empty)) ? null : rest;
    }

    /// <summary>The name of a program: without its folder, its quotes and .exe.</summary>
    private static string Name(string word)
    {
        var name = Path.GetFileName(word.Trim('"', '\''));
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }

    /// <summary>Splits at blanks, keeping what is in quotes together and dropping the quotes.</summary>
    private static List<string> Words(string text)
    {
        var words = new List<string>();
        var current = new System.Text.StringBuilder();
        char? quote = null;
        foreach (var c in text)
        {
            if (quote is null && (c == '"' || c == '\''))
            {
                quote = c;
            }
            else if (c == quote)
            {
                quote = null;
            }
            else if (quote is null && char.IsWhiteSpace(c))
            {
                if (current.Length > 0)
                {
                    words.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            words.Add(current.ToString());
        }

        return words;
    }
}
