using System.Text;

namespace Yav.Console.Commands;

public enum InputKind
{
    Empty,

    /// <summary>Text for Model A. Never run as a command of the operating system.</summary>
    Request,

    /// <summary>A command of YAV itself.</summary>
    Command,

    /// <summary>Starts like a command but is not one. It is reported, never sent anywhere.</summary>
    Invalid,
}

public sealed record ParsedCommand(string Name, IReadOnlyList<string> Arguments, string RawArguments);

public sealed record ClassifiedInput(InputKind Kind, string Text, ParsedCommand? Command, string? Problem);

/// <summary>
/// Decides what a line of input is. Text without a slash in front is a request, whatever it looks like:
/// YAV never guesses that natural-language input should be executed by the operating system.
/// </summary>
public static class InputClassifier
{
    public static ClassifiedInput Classify(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return new ClassifiedInput(InputKind.Empty, string.Empty, null, null);
        }

        var trimmed = text.TrimStart();
        if (!trimmed.StartsWith('/'))
        {
            return new ClassifiedInput(InputKind.Request, text, null, null);
        }

        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            // The way to begin a request with a slash, for example with a path.
            return new ClassifiedInput(InputKind.Request, trimmed[1..], null, null);
        }

        var line = trimmed.TrimEnd();
        if (line.Contains('\n') || line.Contains('\r'))
        {
            var firstLine = line.Split(['\r', '\n'], 2)[0];
            if (IsCommandShaped(firstLine))
            {
                return new ClassifiedInput(
                    InputKind.Invalid, text, null,
                    "A command is written on one line. The text has several lines, so nothing was done. "
                    + "To send it as a request, put a second slash in front.");
            }

            return new ClassifiedInput(InputKind.Request, text, null, null);
        }

        if (!IsCommandShaped(line))
        {
            return new ClassifiedInput(
                InputKind.Invalid, text, null,
                "A command is a slash followed by its name, for example /help. To send text that starts with a slash as a request, put a second slash in front.");
        }

        var end = 1;
        while (end < line.Length && !char.IsWhiteSpace(line[end]))
        {
            end++;
        }

        var name = line[1..end].ToLowerInvariant();
        var rest = line[end..].Trim();
        return new ClassifiedInput(InputKind.Command, text, new ParsedCommand(name, CommandArguments.Split(rest), rest), null);
    }

    private static bool IsCommandShaped(string line) =>
        line.Length > 1 && (char.IsAsciiLetter(line[1]) || line[1] == '?');
}

public static class CommandArguments
{
    /// <summary>
    /// Splits at white space. Double quotes keep text together and a quote inside quotes is written twice.
    /// A backslash is an ordinary character, because that is what it is in a Windows path.
    /// </summary>
    public static IReadOnlyList<string> Split(string text)
    {
        var arguments = new List<string>();
        var current = new StringBuilder();
        var quoted = false;
        var started = false;

        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        current.Append('"');
                        i++;
                    }
                    else
                    {
                        quoted = false;
                    }
                }
                else
                {
                    current.Append(c);
                }

                continue;
            }

            if (c == '"')
            {
                quoted = true;
                started = true;
            }
            else if (char.IsWhiteSpace(c))
            {
                if (started)
                {
                    arguments.Add(current.ToString());
                    current.Clear();
                    started = false;
                }
            }
            else
            {
                current.Append(c);
                started = true;
            }
        }

        if (started)
        {
            arguments.Add(current.ToString());
        }

        return arguments;
    }
}
