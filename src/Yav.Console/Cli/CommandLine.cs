namespace Yav.Console.Cli;

public enum CliMode
{
    /// <summary>The inline shell.</summary>
    Interactive,

    /// <summary>One request, no prompt, a result and an exit code.</summary>
    Run,
    Doctor,
    Help,
    Version,
    Invalid,
}

public sealed record CliOptions(
    CliMode Mode,
    string? ProjectPath = null,
    string? Task = null,
    string? PromptFile = null,
    bool Json = false,
    bool Plain = false,
    bool Apply = false,
    string? TaskId = null,
    string? Error = null);

/// <summary>
/// Reads the arguments YAV was started with. Nothing is guessed: an argument that is not understood is an
/// error, and text is never taken for a task unless it was given as one.
/// </summary>
public static class CommandLine
{
    public const string Usage = """
        Usage:
          yav                                   Start the shell in the current directory
          yav <path>                            Start the shell with a project
          yav run --project <path> --task <text> [--json] [--apply] [--continue <task-id>]
          yav run --project <path> --prompt-file <file> [--json]
          yav doctor [--json]                   Check prerequisites and agents
          yav --version | --help

        Options:
          -p, --project <path>      The project directory (default: the current directory)
          -t, --task <text>         The request, exactly as it is sent to Model A
              --prompt-file <file>  Read the request from a UTF-8 text file
              --continue <task-id>  Continue an earlier task in its workspace and conversations
              --apply               Apply the candidate when it passed review and all required checks
              --json                Write only JSON lines to standard output
              --plain               No colors, no cursor movement

        Exit codes of 'yav run':
          0  ready to apply, applied, or answered without changes
          2  blocked: needs a decision of yours
          3  approval required: the agent asked for something nobody could answer
          4  rate limited
          5  failed
          6  interrupted
          7  needs reconciliation
          64 the command line was not understood
        """;

    private static readonly string[] Verbs = ["run", "doctor", "help", "version"];

    public static CliOptions Parse(IReadOnlyList<string> arguments)
    {
        if (arguments.Count == 0)
        {
            return new CliOptions(CliMode.Interactive);
        }

        var first = arguments[0];
        if (first is "--help" or "-h" or "/?" or "help")
        {
            return new CliOptions(CliMode.Help);
        }

        if (first is "--version" or "version")
        {
            return new CliOptions(CliMode.Version);
        }

        var mode = CliMode.Interactive;
        var index = 0;
        if (Verbs.Contains(first, StringComparer.Ordinal))
        {
            mode = first == "run" ? CliMode.Run : CliMode.Doctor;
            index = 1;
        }

        string? project = null;
        string? task = null;
        string? promptFile = null;
        string? taskId = null;
        var json = false;
        var plain = false;
        var apply = false;

        for (; index < arguments.Count; index++)
        {
            var argument = arguments[index];
            var (name, inline) = Split(argument);

            string? Value(out string? error)
            {
                error = null;
                if (inline is not null)
                {
                    return inline;
                }

                if (index + 1 >= arguments.Count)
                {
                    error = $"{name} needs a value.";
                    return null;
                }

                // The next argument is the value whatever it looks like: a task may well start with a dash.
                return arguments[++index];
            }

            switch (name)
            {
                case "--json":
                    json = true;
                    break;

                case "--plain":
                    plain = true;
                    break;

                case "--apply" when mode == CliMode.Run:
                    apply = true;
                    break;

                case "--project" or "-p" when mode == CliMode.Run:
                {
                    project = Value(out var error);
                    if (error is not null)
                    {
                        return Invalid(error);
                    }

                    break;
                }

                case "--task" or "-t" when mode == CliMode.Run:
                {
                    task = Value(out var error);
                    if (error is not null)
                    {
                        return Invalid(error);
                    }

                    break;
                }

                case "--prompt-file" when mode == CliMode.Run:
                {
                    promptFile = Value(out var error);
                    if (error is not null)
                    {
                        return Invalid(error);
                    }

                    break;
                }

                case "--continue" when mode == CliMode.Run:
                {
                    taskId = Value(out var error);
                    if (error is not null)
                    {
                        return Invalid(error);
                    }

                    break;
                }

                default:
                    if (argument.StartsWith('-') || mode != CliMode.Interactive)
                    {
                        return Invalid(mode == CliMode.Interactive
                            ? $"'{argument}' is not an option of yav."
                            : $"'{argument}' is not an option of 'yav {(mode == CliMode.Run ? "run" : "doctor")}'.");
                    }

                    if (project is not null)
                    {
                        return Invalid($"Only one project can be opened, but '{project}' and '{argument}' were given. A path with spaces needs quotes.");
                    }

                    project = argument;
                    break;
            }
        }

        if (mode == CliMode.Run)
        {
            if (task is not null && promptFile is not null)
            {
                return Invalid("Give the request with --task or with --prompt-file, not both.");
            }

            if (string.IsNullOrWhiteSpace(task) && promptFile is null)
            {
                return Invalid("'yav run' needs the request: --task <text> or --prompt-file <file>.");
            }
        }

        return new CliOptions(mode, project, task, promptFile, json, plain || json, apply, taskId);
    }

    private static (string Name, string? Inline) Split(string argument)
    {
        if (!argument.StartsWith("--", StringComparison.Ordinal))
        {
            return (argument, null);
        }

        var equals = argument.IndexOf('=');
        return equals > 2 ? (argument[..equals], argument[(equals + 1)..]) : (argument, null);
    }

    private static CliOptions Invalid(string error) => new(CliMode.Invalid, Error: error);
}
