using System.Text;

namespace Yav.Platform.Processes;

/// <summary>Thrown when an argument cannot be passed to a batch launcher without risking command injection.</summary>
public sealed class UnsafeArgumentException(string argument, string reason)
    : ArgumentException($"The argument '{Shorten(argument)}' cannot be passed safely to a .cmd or .bat launcher: {reason}")
{
    private static string Shorten(string text) => text.Length <= 60 ? text : text[..60] + "...";
}

/// <summary>
/// Builds Windows command lines from structured argument lists. Callers never concatenate arguments
/// themselves, so an argument can never be reinterpreted as several arguments or as shell syntax.
/// </summary>
public static class CommandLine
{
    /// <summary>
    /// Quotes one argument using the rules the Microsoft C runtime uses to split a command line,
    /// which is what almost every native Windows program relies on.
    /// </summary>
    public static void AppendArgument(StringBuilder builder, string argument)
    {
        if (builder.Length > 0)
        {
            builder.Append(' ');
        }

        if (argument.Length > 0 && !NeedsQuoting(argument))
        {
            builder.Append(argument);
            return;
        }

        builder.Append('"');
        var i = 0;
        while (i < argument.Length)
        {
            var c = argument[i++];
            if (c == '\\')
            {
                var backslashes = 1;
                while (i < argument.Length && argument[i] == '\\')
                {
                    backslashes++;
                    i++;
                }

                if (i == argument.Length)
                {
                    // Backslashes before the closing quote must be doubled.
                    builder.Append('\\', backslashes * 2);
                }
                else if (argument[i] == '"')
                {
                    builder.Append('\\', (backslashes * 2) + 1);
                    builder.Append('"');
                    i++;
                }
                else
                {
                    builder.Append('\\', backslashes);
                }
            }
            else if (c == '"')
            {
                builder.Append('\\').Append('"');
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('"');
    }

    public static string Build(string executable, IReadOnlyList<string> arguments)
    {
        var builder = new StringBuilder();
        AppendArgument(builder, executable);
        foreach (var argument in arguments)
        {
            AppendArgument(builder, argument);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Builds the command line for <c>cmd.exe /d /s /c "..."</c> that runs a batch file.
    /// cmd.exe parses its command line before the batch file sees it, so characters that cmd.exe
    /// would expand or treat as operators are rejected rather than escaped on a best-effort basis.
    /// </summary>
    public static string BuildForBatch(string batchFile, IReadOnlyList<string> arguments)
    {
        ValidateBatchToken(batchFile, isPath: true);
        var inner = new StringBuilder();
        inner.Append('"').Append(batchFile).Append('"');
        foreach (var argument in arguments)
        {
            ValidateBatchToken(argument, isPath: false);
            inner.Append(' ');
            if (argument.Length == 0 || NeedsBatchQuoting(argument))
            {
                // Inside double quotes cmd.exe treats & | < > ^ ( ) as ordinary characters.
                inner.Append('"').Append(argument);

                // Trailing backslashes would escape the closing quote for the program that finally parses the
                // arguments, so each one is doubled.
                var trailing = 0;
                for (var i = argument.Length - 1; i >= 0 && argument[i] == '\\'; i--)
                {
                    trailing++;
                }

                inner.Append('\\', trailing);
                inner.Append('"');
            }
            else
            {
                inner.Append(argument);
            }
        }

        // With /s and a command that starts and ends with a quote, cmd.exe strips exactly that outer pair.
        return "/d /s /c \"" + inner + "\"";
    }

    private static void ValidateBatchToken(string token, bool isPath)
    {
        foreach (var c in token)
        {
            switch (c)
            {
                case '"':
                    throw new UnsafeArgumentException(token, "it contains a double quote");
                case '%':
                    throw new UnsafeArgumentException(token, "it contains '%', which cmd.exe expands as a variable");
                case '\r' or '\n':
                    throw new UnsafeArgumentException(token, "it contains a line break");
                case '\0':
                    throw new UnsafeArgumentException(token, "it contains a null character");
                case '!' when !isPath:
                    // Harmless unless delayed expansion is enabled, which a batch file may turn on itself.
                    throw new UnsafeArgumentException(token, "it contains '!', which a batch file may expand");
            }
        }
    }

    private static bool NeedsQuoting(string argument)
    {
        foreach (var c in argument)
        {
            if (c is ' ' or '\t' or '"' or '\n' or '\v')
            {
                return true;
            }
        }

        return false;
    }

    private static bool NeedsBatchQuoting(string argument)
    {
        foreach (var c in argument)
        {
            if (c is ' ' or '\t' or '&' or '|' or '<' or '>' or '^' or '(' or ')' or ',' or ';' or '=')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the block passed to CreateProcess: sorted, null-separated, double-null terminated.</summary>
    public static char[] BuildEnvironmentBlock(IReadOnlyDictionary<string, string?>? overrides)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            var key = (string)entry.Key;
            if (key.Length > 0)
            {
                variables[key] = (string?)entry.Value ?? string.Empty;
            }
        }

        if (overrides is not null)
        {
            foreach (var (key, value) in overrides)
            {
                if (key.Length == 0 || key.Contains('=') || key.Contains('\0'))
                {
                    throw new ArgumentException($"'{key}' is not a valid environment variable name.");
                }

                if (value is null)
                {
                    variables.Remove(key);
                }
                else if (value.Contains('\0'))
                {
                    throw new ArgumentException($"The value of environment variable '{key}' contains a null character.");
                }
                else
                {
                    variables[key] = value;
                }
            }
        }

        var builder = new StringBuilder();
        foreach (var (key, value) in variables)
        {
            builder.Append(key).Append('=').Append(value).Append('\0');
        }

        builder.Append('\0');
        var block = new char[builder.Length];
        builder.CopyTo(0, block, 0, builder.Length);
        return block;
    }
}
