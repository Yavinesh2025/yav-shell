using System.Globalization;
using Yav.Console.Cli;
using Yav.Console.Commands;
using Yav.Console.Composition;
using Yav.Console.Doctor;
using Yav.Console.Rendering;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Settings;

namespace Yav.Console.Shell;

public sealed partial class InteractiveShell
{
    private const string Boundary =
        "This runs as you, with your rights, outside every agent sandbox and outside the isolated workspace.";

    /// <summary>
    /// Gives the real console to another program and takes it back afterwards. YAV reads no key and
    /// writes nothing while the other program runs.
    /// </summary>
    private async Task<int> ForegroundAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        using var keyboard = _input.ReleaseKeyboard();
        using var output = _screen.Suspend();
        _host?.RestoreOriginal();
        var treatedAsInput = false;
        try
        {
            if (_host is { Capabilities.InputIsTerminal: true })
            {
                treatedAsInput = System.Console.TreatControlCAsInput;

                // Control+C belongs to the program in the foreground while it runs.
                System.Console.TreatControlCAsInput = false;
            }

            return await _services.Runner.RunForegroundAsync(spec, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _host?.RestoreYav();
            if (_host is { Capabilities.InputIsTerminal: true })
            {
                System.Console.TreatControlCAsInput = treatedAsInput;
            }
        }
    }

    private (string Executable, string[] Prefix)? ResolveShell(string? wanted)
    {
        var name = (wanted ?? _services.Settings.Shell).ToLowerInvariant();
        var candidates = name switch
        {
            "cmd" => new[] { "cmd" },
            "powershell" => ["powershell"],
            _ => ["pwsh", "powershell"],
        };

        foreach (var candidate in candidates)
        {
            if (_services.Runner.Resolve(candidate) is { } path)
            {
                return (path, candidate == "cmd" ? ["/d", "/c"] : ["-NoLogo", "-NoProfile", "-Command"]);
            }
        }

        return null;
    }

    private async Task ExecAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var text = command.RawArguments.Trim();
        if (text.Length == 0)
        {
            _ui.Warn("Usage: /exec <command>");
            _ui.Muted(Boundary);
            return;
        }

        if (ResolveShell(null) is not { } shell)
        {
            _ui.Error("No shell was found on PATH (pwsh, powershell or cmd).");
            return;
        }

        var directory = _session.ProjectPath ?? Environment.CurrentDirectory;
        _ui.Note(Stages.Local, $"{Path.GetFileNameWithoutExtension(shell.Executable)} in {directory}. {Boundary}");

        // The text is handed to the shell as one argument. YAV neither interprets nor changes it, and no model sees it.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();
        var exitCode = await ForegroundAsync(
            new ProcessSpec(shell.Executable, [.. shell.Prefix, text], directory, Io: ProcessIo.InheritConsole, Label: "/exec"),
            cancellationToken).ConfigureAwait(false);
        _ui.Note(
            Stages.Local,
            $"exit {exitCode.ToString(CultureInfo.InvariantCulture)} after {Describe(System.Diagnostics.Stopwatch.GetElapsedTime(started))}",
            exitCode == 0 ? NoteLevel.Info : NoteLevel.Warning);
    }

    private async Task ShellAsync(IReadOnlyList<string> args, CancellationToken cancellationToken)
    {
        var wanted = args.FirstOrDefault()?.ToLowerInvariant();
        if (wanted is not (null or "pwsh" or "powershell" or "cmd"))
        {
            _ui.Warn("Usage: /shell [pwsh|powershell|cmd]");
            return;
        }

        // Somebody has to type into the shell, and in the console of this process: an input that can be asked
        // a question is not enough where it is not that console.
        if (!_input.CanAsk || _host is not { Capabilities.Interactive: true })
        {
            _ui.Warn("A shell needs a console. Input or output is not a terminal here, so none was started.");
            return;
        }

        if (ResolveShell(wanted) is not { } shell)
        {
            _ui.Error($"'{wanted ?? _services.Settings.Shell}' was not found on PATH.");
            return;
        }

        var directory = _session.ProjectPath ?? Environment.CurrentDirectory;
        _ui.Note(Stages.Local, $"Starting {Path.GetFileNameWithoutExtension(shell.Executable)} in {directory}. 'exit' returns to YAV.");
        _ui.Muted("           " + Boundary);
        _ui.Muted("           What you change there in the directory or the environment stays in that shell; it does not come back to YAV.");

        var interactive = Path.GetFileNameWithoutExtension(shell.Executable).Equals("cmd", StringComparison.OrdinalIgnoreCase)
            ? new[] { "/d" }
            : ["-NoLogo"];
        var exitCode = await ForegroundAsync(
            new ProcessSpec(shell.Executable, interactive, directory, Io: ProcessIo.InheritConsole, Label: "/shell"),
            cancellationToken).ConfigureAwait(false);
        _ui.Note(Stages.Local, $"Back in YAV. The shell ended with exit code {exitCode.ToString(CultureInfo.InvariantCulture)}.");
    }

    private void Settings(IReadOnlyList<string> args)
    {
        var settings = _services.Settings;
        if (args.Count == 0)
        {
            _ui.Heading("Settings");
            _ui.Pairs(
                ("File", _services.SettingsStore.Path),
                ("Data", _services.Paths.Home),
                ("Model A", Describe(settings.ModelA) + (settings.ModelA is null ? string.Empty : $", effort {settings.ModelA.EffortPreference}")),
                ("Model B", Describe(settings.ModelB) + (settings.ModelB is null ? string.Empty : $", effort {settings.ModelB.EffortPreference}")),
                ("shell", settings.Shell),
                ("plain", settings.PlainOutput ? "on: no colors and no cursor movement" : "off"),
                ("verbose", _session.Verbose ? "on: output of commands and checks is shown as it arrives" : "off"),
                ("telemetry", settings.Telemetry ? "on" : "off - YAV sends nothing anywhere"),
                ("keep-runs", $"{settings.Retention.KeepRunsDays} days"),
                ("keep-workspaces", $"{settings.Retention.KeepWorkspacesDays} days"),
                ("trust", _session.ProjectPath is null ? "no project selected" : _services.Database.IsProjectTrusted(_session.ProjectPath) ? "on for this project" : "off for this project"));
            foreach (var (id, adapter) in settings.Adapters.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                _ui.Pairs((id, $"{(adapter.Enabled ? "on" : "off")}{(adapter.ExecutablePath.Length > 0 ? ", " + adapter.ExecutablePath : ", found on PATH")}"));
            }

            _ui.Muted("Set with /settings <name> <value>: shell, plain, verbose, keep-runs, keep-workspaces, trust, workspace, adapter.");
            _ui.Muted("Models, effort, quality, speed and limits have commands of their own. Credentials are never kept in settings.");
            return;
        }

        var name = args[0].ToLowerInvariant();
        if (name == "path")
        {
            _ui.Say(_services.SettingsStore.Path);
            return;
        }

        if (args.Count < 2)
        {
            _ui.Warn("Usage: /settings <name> <value>");
            return;
        }

        var value = args[1];
        switch (name)
        {
            case "shell" when value.ToLowerInvariant() is "pwsh" or "powershell" or "cmd":
                Save(s => s with { Shell = value.ToLowerInvariant() });
                _ui.Say($"/shell and /exec use {value.ToLowerInvariant()}.");
                return;

            case "plain" when TryOnOff(value, out var plain):
                Save(s => s with { PlainOutput = plain });
                _ui.Say($"Plain output is {(plain ? "on" : "off")} from the next start.");
                return;

            case "verbose" when TryOnOff(value, out var verbose):
                _session.Verbose = verbose;
                _ui.Say($"Output of commands and checks is {(verbose ? "shown as it arrives" : "kept for /history and the evidence files")}, from the next run.");
                return;

            case "telemetry" when TryOnOff(value, out var telemetry):
                Save(s => s with { Telemetry = telemetry });
                _ui.Say(telemetry
                    ? "Telemetry is marked on. This version of YAV contains nothing that transmits data, so nothing is sent."
                    : "Telemetry is off. YAV sends nothing anywhere.");
                return;

            case "keep-runs" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650:
                Save(s => s with { Retention = s.Retention with { KeepRunsDays = days } });
                _ui.Say($"/history prune removes runs older than {days} days.");
                return;

            case "keep-workspaces" when int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var days) && days is >= 1 and <= 3650:
                Save(s => s with { Retention = s.Retention with { KeepWorkspacesDays = days } });
                _ui.Say($"Isolated workspaces are kept for {days} days.");
                return;

            case "trust" when TryOnOff(value, out var trusted):
                if (!RequireProject(out var trustedProject))
                {
                    return;
                }

                _services.Database.SetProjectTrusted(trustedProject, trusted);
                _ui.Say(trusted
                    ? "This project is trusted: configuration of agents that is kept in the repository is loaded. Text in the repository still is no instruction to YAV."
                    : "This project is not trusted: configuration of agents that is kept in the repository is not loaded.", trusted ? Tone.Warning : Tone.Normal);
                return;

            case "workspace" when value.ToLowerInvariant() is "in-place" or "isolated":
            {
                if (!RequireProject(out var project))
                {
                    return;
                }

                if (value.Equals("isolated", StringComparison.OrdinalIgnoreCase))
                {
                    _services.Database.Withdraw("in-place", Yav.Storage.YavDatabase.ProjectKey(project));
                    _ui.Say("Runs of this project use an isolated workspace.");
                    return;
                }

                _services.Database.AcknowledgeInPlace(project, "Acknowledged with /settings workspace in-place");
                _ui.Say("Recorded: you accept that an agent may work directly in this project, where its changes appear while it works.", Tone.Warning);
                _ui.Muted("YAV still uses an isolated workspace unless a run is asked to work in place. This version of the shell starts no run in place by itself.");
                return;
            }

            case "adapter" when args.Count >= 3:
            {
                var id = _services.Adapters.Keys.Concat(settings.Adapters.Keys).FirstOrDefault(k => k.Equals(value, StringComparison.OrdinalIgnoreCase)) ?? value;
                var current = settings.AdapterFor(id);
                var what = args[2].ToLowerInvariant();
                AdapterSettings? changed = what switch
                {
                    "on" => current with { Enabled = true },
                    "off" => current with { Enabled = false },
                    "path" when args.Count == 4 => File.Exists(args[3]) ? current with { ExecutablePath = Path.GetFullPath(args[3]) } : null,
                    "path" => current with { ExecutablePath = string.Empty },
                    _ => null,
                };
                if (changed is null)
                {
                    _ui.Warn("Usage: /settings adapter <id> on|off, or /settings adapter <id> path [<file>]. The file has to exist.");
                    return;
                }

                Save(s => s with { Adapters = new Dictionary<string, AdapterSettings>(s.Adapters, StringComparer.OrdinalIgnoreCase) { [id] = changed } });
                _ui.Say($"Adapter {id}: {(changed.Enabled ? "on" : "off")}{(changed.ExecutablePath.Length > 0 ? ", " + changed.ExecutablePath : ", found on PATH")}. This applies from the next start.");
                return;
            }
        }

        _ui.Warn($"'{name} {value}' is not a setting YAV has, or the value is outside what is allowed. /settings lists them.");
    }

    private async Task DoctorAsync(CancellationToken cancellationToken)
    {
        _ui.Muted("Asking the agents for their version, account and models. No inference is requested.");
        var report = await DoctorChecks.CollectAsync(_services, _host?.Capabilities, _session.ProjectPath, cancellationToken).ConfigureAwait(false);
        string? area = null;
        var lines = new List<Line>();
        foreach (var check in report.Checks)
        {
            if (!string.Equals(area, check.Area, StringComparison.Ordinal))
            {
                area = check.Area;
                lines.Add(Line.Empty);
                lines.Add(Line.Of(area, Tone.Accent, bold: true));
            }

            var tone = check.Status switch
            {
                CheckStatus.Ok => Tone.Success,
                CheckStatus.Warning => Tone.Warning,
                CheckStatus.Problem => Tone.Error,
                _ => Tone.Muted,
            };
            lines.Add(Line.Of(
                new Segment("  " + DoctorCommand.Mark(check.Status).PadRight(7), tone),
                new Segment(check.Name + ": ", Tone.Normal, Bold: true),
                new Segment(check.Detail, check.Status == CheckStatus.Info ? Tone.Muted : Tone.Normal)));
            if (check.Remedy is not null)
            {
                lines.Add(Line.Of(new Segment("         -> " + check.Remedy, Tone.Muted)));
            }
        }

        lines.Add(Line.Empty);
        lines.Add(report.HasProblems
            ? Line.Of($"{report.Count(CheckStatus.Problem)} problem(s), {report.Count(CheckStatus.Warning)} warning(s).", Tone.Error)
            : Line.Of($"No problems. {report.Count(CheckStatus.Warning)} warning(s).", Tone.Success));
        _ui.Lines(lines);
    }

    private void Help(IReadOnlyList<string> args)
    {
        if (args.Count > 0)
        {
            var info = CommandCatalog.Find(args[0].TrimStart('/'));
            if (info is null)
            {
                _ui.Warn($"/{args[0].TrimStart('/')} is not a command of YAV.");
                return;
            }

            _ui.Say(info.Usage, Tone.Accent);
            _ui.Say("  " + info.Summary);
            foreach (var detail in info.Details)
            {
                _ui.Muted("  " + detail);
            }

            if (!info.AllowedDuringRun)
            {
                _ui.Muted("  Waits until an active run has ended.");
            }

            return;
        }

        _ui.Say("Text without a slash is a request for Model A, or a follow-up to the task. Text with a slash is a command of YAV.", Tone.Normal);
        _ui.Say("YAV never runs what you type as a command of the operating system, except what you give to /exec or enter in /shell.", Tone.Normal);
        foreach (var group in CommandCatalog.All.GroupBy(c => c.Group))
        {
            _ui.Blank();
            _ui.Heading(group.Key switch
            {
                CommandGroup.Project => "Project",
                CommandGroup.Models => "Models and policy",
                CommandGroup.Task => "Task",
                CommandGroup.Inspect => "Inspect and check",
                CommandGroup.Deliver => "Deliver",
                CommandGroup.Records => "Records",
                CommandGroup.Local => "Local, without a model",
                _ => "Application",
            });
            _ui.Lines(group.Select(c => Line.Of(new Segment("  " + c.Usage.PadRight(46) + " ", Tone.Normal), new Segment(c.Summary, Tone.Muted))));
        }

        _ui.Blank();
        _ui.Heading("Keys");
        _ui.Pairs(
            ("Enter", "send the request or the command"),
            ("Shift+Enter, Ctrl+J", "start a new line"),
            ("Paste", "several lines are one input; a pasted line break never sends it"),
            ("Tab", "complete a command, its words, or a path"),
            ("Up, Down", "earlier input; with something typed, earlier input that begins with it"),
            ("Ctrl+Left, Ctrl+Right", "move by words"),
            ("Esc", "close the list of completions; while a run is active, empty the line"),
            ("Ctrl+C", "empty the line; on an empty line: stop the run, or leave"),
            ("//text", "a request that starts with a slash"),
            ("At a question of an agent", "a, s, d or c, or the word allow where it is asked for, then Enter; Esc declines; Ctrl+C declines and stops the turn"));
        _ui.Muted("/help <command> explains one command. docs\\user-guide.md explains the rest.");
    }

    private async Task ExitAsync(CancellationToken cancellationToken)
    {
        if (_session.Active is { StopRequested: false } && _input.CanAsk)
        {
            _ui.Warn("A run is active. Leaving stops it; its workspace and conversations are kept and /resume continues it after the next start.");
            if (!await ConfirmAsync("Stop the run and leave?", cancellationToken).ConfigureAwait(false))
            {
                return;
            }
        }

        if (_session.ProjectPath is { } project)
        {
            var waiting = _services.Database.GetQueue(project).Count;
            if (waiting > 0)
            {
                _ui.Muted($"{waiting} request(s) keep waiting. They do not run while YAV is closed.");
            }
        }

        await LeaveAsync(waitForRun: true).ConfigureAwait(false);
        _ui.Muted("State saved. Nothing continues to run after YAV has ended.");
    }
}
