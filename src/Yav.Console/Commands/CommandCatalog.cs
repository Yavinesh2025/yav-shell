namespace Yav.Console.Commands;

public enum CommandGroup
{
    Project,
    Models,
    Task,
    Inspect,
    Deliver,
    Records,
    Local,
    Application,
}

/// <param name="AllowedDuringRun">
/// False for commands that would change a run that is active, or that need the console for themselves.
/// They are refused while a run is active; nothing is changed silently.
/// </param>
public sealed record CommandInfo(
    string Name,
    CommandGroup Group,
    string Usage,
    string Summary,
    bool AllowedDuringRun,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<string> Details)
{
    /// <summary>Verbs of a command that waits for the end of a run which only look, and so are allowed while it is active.</summary>
    public IReadOnlyList<string> LookingVerbs { get; init; } = [];

    /// <summary>The command as it is used with these arguments: one that only looks is allowed while a run is active.</summary>
    public CommandInfo For(IReadOnlyList<string> arguments) =>
        !AllowedDuringRun && arguments.Count > 0 && LookingVerbs.Contains(arguments[0], StringComparer.OrdinalIgnoreCase)
            ? this with { AllowedDuringRun = true }
            : this;
}

/// <summary>The commands of the shell: what they are called, how they are written and when they may be used.</summary>
public static class CommandCatalog
{
    public static IReadOnlyList<CommandInfo> All { get; } =
    [
        Command("open", CommandGroup.Project, "/open <path> [--accept-gaps]", "Select a project", false,
            "--accept-gaps  accept that files ignored by Git are absent from the isolated workspace"),
        Command("cd", CommandGroup.Project, "/cd <path>", "Change to a directory of the project, or to another project, when no run is active", false),

        Command("models", CommandGroup.Models, "/models [a|b <adapter> <model> | swap | refresh]", "Show the models the providers list, and choose Model A and Model B", false,
            "/models                          what each agent offers for your account",
            "/models a codex-app-server <id>  choose the implementation model",
            "/models b claude-cli <id>        choose the review model",
            "/models swap                     exchange the two roles",
            "/models refresh                  ask the providers again"),
        Command("effort", CommandGroup.Models, "/effort [a|b <value>|maximum]", "Show and set the reasoning effort of each role", false,
            "maximum resolves to the highest value the provider lists for the model.",
            "When a model lists a value YAV cannot rank, the exact value has to be chosen."),
        Command("login", CommandGroup.Models, "/login [codex|claude] [--api-key | --forget-key | --acknowledge]", "Show the account route of a provider, acknowledge it, or start the provider's own login", false,
            "YAV never sees a password or a token. Signing in is done by the provider's own program."),
        Command("quality", CommandGroup.Models, "/quality [lock|strict on|off] [gates required|optional] [preexisting ask|repair]", "Show and change Quality Lock and what a candidate has to pass", false),
        Command("speed", CommandGroup.Models, "/speed [standard|provider]", "Show the serving tier, or ask for the provider's faster tier", false,
            "The faster tier is billed differently. It is shown and has to be authorized before it is used."),
        Command("adaptive", CommandGroup.Models, "/adaptive [on|off]", "Optional lower implementation effort for tasks you approve one by one. Off by default", false),
        Command("optimization", CommandGroup.Models, "/optimization [on|off]", "Turn YAV's supplemental workflow optimizations on or off", false),

        Command("new", CommandGroup.Task, "/new", "Start an unrelated task with a new workspace and new conversations", false),
        Command("resume", CommandGroup.Task, "/resume [run-id]", "Continue a run that stopped before it was ready", false),
        Command("attach", CommandGroup.Task, "/attach [<path> | clear]", "Attach a file to the next request", true),
        Command("replace", CommandGroup.Task, "/replace <file> <text> <replacement> [--count <n> | --all]", "Replace a text in one file without a model. The result is reviewed and checked like any change", false,
            "The text has to occur exactly once, unless --count <n> says how often it occurs or --all takes every occurrence.",
            "Text with blanks is written in double quotes; a double quote inside them is written twice.",
            "Upper and lower case count. A text of several lines, or a change that needs thought, is a request for Model A.",
            "The project is not changed before /apply; /diff shows what would be written."),
        Command("status", CommandGroup.Task, "/status", "Show the state of the run and the settings the providers confirmed", true),
        Command("stop", CommandGroup.Task, "/stop", "Interrupt the active run. Its workspace and conversations are kept", true),
        Command("queue", CommandGroup.Task, "/queue [add <text> | remove <n> | clear | steer <n>]", "Show and manage the requests that wait for the active run", true,
            "/queue steer <n>  add a waiting request to the running turn, when the agent supports it"),

        Command("diff", CommandGroup.Inspect, "/diff [--stat | --full | --export <file>]", "Show what the task changed, compared with the state it started from", true),
        Command("test", CommandGroup.Inspect, "/test [<gate> | list | detect | trust | waive <gate> <reason>]", "Run the required checks, or manage them", false,
            "/test detect   propose checks from what the project contains; nothing runs until you approve",
            "/test trust    review and approve yav.project.json",
            "/test waive    accept the current candidate although one check did not pass, with a reason"),
        Command("review", CommandGroup.Inspect, "/review [show | approve <path>]", "Run the review of Model B again, or show what it wrote", false,
            "/review approve <path>  approve a change to a protected path for the current candidate",
            "/review show            changes nothing and can be used while a run is active") with { LookingVerbs = ["show"] },

        Command("apply", CommandGroup.Deliver, "/apply [--merge]", "Write a candidate that passed into the project", false,
            "--merge  combine edits you made meanwhile with the candidate, in isolation, and check the result again"),
        Command("discard", CommandGroup.Deliver, "/discard [run-id]", "Remove the isolated changes of a task. The project is not touched", false),
        Command("undo", CommandGroup.Deliver, "/undo [--skip-edited]", "Reverse the most recent apply, file by file", false,
            "A file you edited after the apply is never overwritten."),

        Command("history", CommandGroup.Records, "/history [<run-id> | export <run-id> <file> | delete <run-id> | prune]", "Show saved tasks and runs", true),
        Command("usage", CommandGroup.Records, "/usage [run-id | all]", "Show usage per role, the billing route and where each number comes from", true),
        Command("latency", CommandGroup.Records, "/latency [run-id]", "Show measured times and what took longest", true),
        Command("limits", CommandGroup.Records, "/limits [minutes|repairs|tokens|ratelimit|queue <n>]", "Show and set the limits of a run", true),

        Command("exec", CommandGroup.Local, "/exec <command>", "Run a command in your shell. It runs with your rights, outside every agent sandbox", false),
        Command("shell", CommandGroup.Local, "/shell [powershell|pwsh|cmd]", "Hand the console to a real shell until you leave it with exit", false),

        Command("settings", CommandGroup.Application, "/settings [<name> <value> | path]", "Show and change YAV's own settings", false),
        Command("doctor", CommandGroup.Application, "/doctor", "Check prerequisites, agents, sandbox state and storage", false),
        Aliased(["?"], "help", CommandGroup.Application, "/help [command]", "Show the commands and the keys", true),
        Aliased(["quit", "q"], "exit", CommandGroup.Application, "/exit", "Save the state and leave", true),
    ];

    public static CommandInfo? Find(string name)
    {
        foreach (var command in All)
        {
            if (string.Equals(command.Name, name, StringComparison.OrdinalIgnoreCase)
                || command.Aliases.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                return command;
            }
        }

        return null;
    }

    /// <summary>Commands whose name is close to what was typed. Used for a hint only: a command is never run by guess.</summary>
    public static IReadOnlyList<string> Suggest(string typed)
    {
        var text = typed.ToLowerInvariant();
        return All
            .Select(c => (c.Name, Distance: Distance(text, c.Name)))
            .Where(c => c.Distance <= Math.Max(1, c.Name.Length / 3) || (text.Length >= 3 && c.Name.StartsWith(text, StringComparison.Ordinal)))
            .OrderBy(c => c.Distance)
            .ThenBy(c => c.Name, StringComparer.Ordinal)
            .Take(3)
            .Select(c => c.Name)
            .ToList();
    }

    public static IEnumerable<string> Complete(string typed)
    {
        if (!typed.StartsWith('/'))
        {
            return [];
        }

        var prefix = typed[1..];
        return All
            .Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .Select(c => "/" + c.Name)
            .Order(StringComparer.Ordinal);
    }

    private static CommandInfo Command(string name, CommandGroup group, string usage, string summary, bool duringRun, params string[] details) =>
        new(name, group, usage, summary, duringRun, [], details);

    private static CommandInfo Aliased(string[] aliases, string name, CommandGroup group, string usage, string summary, bool duringRun, params string[] details) =>
        new(name, group, usage, summary, duringRun, aliases, details);

    /// <summary>Edit distance in which exchanging two neighbouring letters counts as one change.</summary>
    private static int Distance(string a, string b)
    {
        var table = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++)
        {
            table[i, 0] = i;
        }

        for (var j = 0; j <= b.Length; j++)
        {
            table[0, j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                var value = Math.Min(Math.Min(table[i - 1, j] + 1, table[i, j - 1] + 1), table[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    value = Math.Min(value, table[i - 2, j - 2] + 1);
                }

                table[i, j] = value;
            }
        }

        return table[a.Length, b.Length];
    }
}
