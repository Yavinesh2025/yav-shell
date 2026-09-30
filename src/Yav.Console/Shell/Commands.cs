using Yav.Console.Commands;
using Yav.Console.Rendering;
using Yav.Core.Runs;

namespace Yav.Console.Shell;

public sealed partial class InteractiveShell
{
    private async Task ExecuteAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var info = CommandCatalog.Find(command.Name);
        if (info is null)
        {
            var suggestions = CommandCatalog.Suggest(command.Name);
            _ui.Warn(
                $"/{command.Name} is not a command of YAV. Nothing was done and nothing was sent."
                + (suggestions.Count > 0 ? " Did you mean " + string.Join(" or ", suggestions.Select(s => "/" + s)) + "?" : " /help lists the commands."));
            return;
        }

        // What only looks, such as /review show, changes nothing and is not held back by a run.
        info = info.For(command.Arguments);
        if (_session.Active is not null && !info.AllowedDuringRun)
        {
            _ui.Warn(
                $"/{info.Name} waits until the active run has ended: it would change the run or take the console. "
                + "/stop interrupts the run; /status shows where it is.");
            return;
        }

        var args = command.Arguments;
        switch (info.Name)
        {
            case "open":
                await OpenAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case "cd":
                await OpenAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case "models":
                await ModelsAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case "effort":
                await EffortAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case "login":
                await LoginAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case "quality":
                Quality(args);
                break;
            case "speed":
                await SpeedAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case "adaptive":
                Toggle(args, "Adaptive mode", s => s.Adaptive, (s, v) => s with { Adaptive = v },
                    on: "ON. Runs are marked Adaptive, not Strict Max. Lower implementation effort is only used for a task when you approve it for that task; the review stays at maximum effort.",
                    off: "OFF. Every run uses the effort you chose for each role.");
                break;
            case "optimization":
                Toggle(args, "Workflow optimizations", s => s.Optimization, (s, v) => s with { Optimization = v },
                    on: "ON. Verified paths named in a request are offered to Model A as starting references, and valid evidence is reused.",
                    off: "OFF. Requests are sent as they are, for comparing with and without YAV's additions.");
                break;
            case "new":
                await NewTaskAsync().ConfigureAwait(false);
                break;
            case "resume":
                await ResumeAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case "attach":
                Attach(command);
                break;
            case "replace":
                await ReplaceAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case "status":
                await StatusAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "stop":
                await StopActiveAsync().ConfigureAwait(false);
                break;
            case "queue":
                await QueueAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case "diff":
                await DiffAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case "test":
                await TestAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case "review":
                await ReviewAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case "apply":
                await ApplyAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case "discard":
                await DiscardAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case "undo":
                await UndoAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case "history":
                await HistoryAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case "usage":
                Usage(args);
                break;
            case "latency":
                Latency(args);
                break;
            case "limits":
                Limits(args);
                break;
            case "exec":
                await ExecAsync(command, cancellationToken).ConfigureAwait(false);
                break;
            case "shell":
                await ShellAsync(args, cancellationToken).ConfigureAwait(false);
                break;
            case "settings":
                Settings(args);
                break;
            case "doctor":
                await DoctorAsync(cancellationToken).ConfigureAwait(false);
                break;
            case "help":
                Help(args);
                break;
            case "exit":
                await ExitAsync(cancellationToken).ConfigureAwait(false);
                break;
        }
    }

    private void Toggle(
        IReadOnlyList<string> args,
        string name,
        Func<Yav.Core.Settings.AppSettings, bool> read,
        Func<Yav.Core.Settings.AppSettings, bool, Yav.Core.Settings.AppSettings> write,
        string on,
        string off)
    {
        if (args.Count == 0)
        {
            _ui.Say($"{name}: {(read(_services.Settings) ? on : off)}");
            return;
        }

        if (!TryOnOff(args[0], out var value))
        {
            _ui.Warn($"'{args[0]}' is neither on nor off.");
            return;
        }

        Save(settings => write(settings, value));
        _ui.Say($"{name}: {(value ? on : off)}");
        _ui.Muted("This applies to the next run. A run that is active keeps the settings it started with.");
    }

    private static bool TryOnOff(string text, out bool value)
    {
        switch (text.ToLowerInvariant())
        {
            case "on" or "yes" or "true":
                value = true;
                return true;
            case "off" or "no" or "false":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    private void Save(Func<Yav.Core.Settings.AppSettings, Yav.Core.Settings.AppSettings> change)
    {
        if (!_services.Update(change))
        {
            _ui.Warn("The settings file was written by a newer version of YAV and is not changed. The change applies to this session only.");
        }
    }

    /// <summary>The run a command refers to: the one named, or the last one of this session.</summary>
    private RunRecord? FindRun(string? runId)
    {
        var id = runId ?? _session.LastRunId;
        if (id is null)
        {
            _ui.Warn("There is no run to refer to yet. /history lists earlier runs.");
            return null;
        }

        var run = _services.Database.FindRun(id);
        if (run is null)
        {
            // A beginning of a run id is enough when it is unambiguous.
            var matches = _services.Database.ListRuns(null, 500).Where(r => r.RunId.StartsWith(id, StringComparison.OrdinalIgnoreCase) || r.RunId.EndsWith(id, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 1)
            {
                return _services.Database.FindRun(matches[0].RunId);
            }

            _ui.Warn(matches.Count == 0 ? $"Run '{id}' does not exist. /history lists the runs." : $"'{id}' fits {matches.Count} runs. Write more of the run id.");
            return null;
        }

        return run;
    }

    private bool RequireProject(out string project)
    {
        project = _session.ProjectPath ?? string.Empty;
        if (_session.ProjectPath is null)
        {
            _ui.Warn("No project is selected. Select one with /open <path>.");
            return false;
        }

        return true;
    }

    private async Task<bool> ConfirmAsync(string question, CancellationToken cancellationToken)
    {
        if (!_input.CanAsk)
        {
            _ui.Warn("Nobody can be asked in this mode, so this was not done.");
            return false;
        }

        var answer = await _input.AskAsync(question + " Type yes to confirm: ", cancellationToken).ConfigureAwait(false);
        var confirmed = string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
        if (!confirmed)
        {
            _ui.Muted("Not confirmed. Nothing was changed.");
        }

        return confirmed;
    }

    private static Tone ToneOf(RunState state) => state switch
    {
        RunState.ReadyToApply or RunState.Completed => Tone.Success,
        RunState.Failed or RunState.NeedsReconciliation => Tone.Error,
        RunState.Blocked or RunState.RateLimited or RunState.Interrupted => Tone.Warning,
        _ => Tone.Normal,
    };
}
