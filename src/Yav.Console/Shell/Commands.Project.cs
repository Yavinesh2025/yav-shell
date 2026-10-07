using Yav.Console.Commands;
using Yav.Console.Doctor;
using Yav.Console.Rendering;
using Yav.Coordinator;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;

namespace Yav.Console.Shell;

public sealed partial class InteractiveShell
{
    private static (string Path, List<string> Flags) SplitPath(ParsedCommand command)
    {
        // A path may contain spaces, so everything that is not an option is the path.
        var flags = new List<string>();
        var rest = command.RawArguments;
        while (true)
        {
            var trimmed = rest.TrimEnd();
            var space = trimmed.LastIndexOf(' ');
            var last = space < 0 ? trimmed : trimmed[(space + 1)..];
            if (!last.StartsWith("--", StringComparison.Ordinal))
            {
                break;
            }

            flags.Add(last.ToLowerInvariant());
            rest = space < 0 ? string.Empty : trimmed[..space];
        }

        return (rest.Trim().Trim('"'), flags);
    }

    private async Task OpenAsync(ParsedCommand command, CancellationToken cancellationToken)
    {
        var (path, flags) = SplitPath(command);
        if (path.Length == 0)
        {
            _ui.Say(_session.ProjectPath is null ? "No project is selected." : $"Project: {_session.ProjectPath}");
            _ui.Muted($"Usage: /{command.Name} <path>");
            return;
        }

        var from = command.Name == "cd" && _session.ProjectPath is not null ? _session.ProjectPath : Environment.CurrentDirectory;
        await OpenProjectAsync(path, from, flags.Contains("--accept-gaps"), cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Selects a project as /open does: the directory has to exist. False when it was not selected; what was wrong was said.</summary>
    private async Task<bool> OpenProjectAsync(string path, string from, bool acceptGaps, CancellationToken cancellationToken)
    {
        string full;
        try
        {
            full = Path.GetFullPath(path, from);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ui.Error($"'{path}' is not a path: {ex.Message}");
            return false;
        }

        if (!Directory.Exists(full))
        {
            _ui.Error($"The directory '{full}' does not exist. The project was not changed.");
            return false;
        }

        var changed = !string.Equals(_session.ProjectPath, full, StringComparison.OrdinalIgnoreCase);
        _session.ProjectPath = full;
        if (changed)
        {
            // Another project means other tasks. Conversations of the earlier one are kept for /resume.
            _session.TaskId = null;
            _session.LastRunId = null;
            _session.TaskEffort = null;
            _session.Attachments.Clear();
        }

        _ui.Say($"Project: {full}", Tone.Accent);
        if (!IsSensibleProject(full))
        {
            _ui.Warn("This looks like a system or profile directory rather than a project. An isolated workspace would copy all of it.");
        }

        await DescribeProjectAsync(full, acceptGaps, cancellationToken).ConfigureAwait(false);
        if (changed)
        {
            RestoreLastRun();
        }

        Save(settings => settings with { LastProject = full });
        return true;
    }

    private async Task DescribeProjectAsync(string project, bool acceptGaps, CancellationToken cancellationToken)
    {
        var state = _services.Validation.LoadConfiguration(project);
        WorkspaceInspection inspection;
        try
        {
            inspection = await _services.Workspaces.InspectAsync(project, state.Effective, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            _ui.Warn("The project could not be inspected: " + ex.Message);
            return;
        }

        var pairs = new List<(string, string, Tone)>
        {
            ("Kind", inspection.IsGitRepository
                ? inspection.HeadCommit is null ? "Git repository without a commit: worked on in a protected copy" : $"Git repository, branch {inspection.Branch ?? "(detached)"}: worked on in an isolated worktree"
                : "not a Git repository: worked on in a protected copy", Tone.Normal),
        };
        if (inspection.IsDirty)
        {
            pairs.Add(("Your changes", $"{inspection.DirtyEntries.Count} uncommitted; they are carried into the workspace and are not attributed to a task", Tone.Normal));
        }

        pairs.Add(("Trust", _services.Database.IsProjectTrusted(project)
            ? "trusted: configuration of agents that is kept in the repository is loaded"
            : "not trusted: configuration kept in the repository is not loaded (/settings trust on)", Tone.Normal));
        pairs.Add(("Checks", state.Trust switch
        {
            ConfigurationTrust.Trusted => $"{state.Effective.RequiredGates.Count()} required: {string.Join(", ", state.Effective.RequiredGates.Select(g => g.Id))}",
            ConfigurationTrust.Untrusted => $"{_services.Validation.ConfigurationFileName} is there but not approved: nothing of it is used (/test trust)",
            ConfigurationTrust.Changed => $"{_services.Validation.ConfigurationFileName} differs from what you approved; the approved version applies (/test trust)",
            ConfigurationTrust.Invalid => $"{_services.Validation.ConfigurationFileName} cannot be used: {state.Errors.FirstOrDefault()}",
            _ => "none configured (/test detect proposes some)",
        }, state.Trust is ConfigurationTrust.Trusted ? Tone.Normal : Tone.Warning));
        _ui.Pairs(pairs);

        foreach (var reason in inspection.Unsupported)
        {
            _ui.Warn("  " + reason);
        }

        foreach (var warning in inspection.Warnings)
        {
            _ui.Muted("  " + warning);
        }

        if (inspection.EquivalenceGaps.Count == 0)
        {
            return;
        }

        if (acceptGaps)
        {
            AcceptGaps(project, inspection.EquivalenceGaps);
            return;
        }

        if (_services.Database.AreGapsAcknowledged(project, RunCoordinator.GapsFingerprint(inspection.EquivalenceGaps)))
        {
            _ui.Muted("  Accepted by you earlier: " + string.Join(" ", inspection.EquivalenceGaps));
            return;
        }

        _ui.Warn("  The isolated workspace would differ from the project: " + string.Join(" ", inspection.EquivalenceGaps));
        _ui.Muted($"  A run starts once you accepted that (/open \"{project}\" --accept-gaps) or listed what to copy under replicateIgnored in {_services.Validation.ConfigurationFileName}.");
    }

    /// <summary>Records that the user accepts exactly these differences between the project and its isolated workspace.</summary>
    private void AcceptGaps(string project, IReadOnlyList<string> gaps)
    {
        _services.Database.AcknowledgeGaps(project, RunCoordinator.GapsFingerprint(gaps), string.Join(" ", gaps));
        _ui.Say("  Accepted for this project: " + string.Join(" ", gaps));
    }

    private async Task NewTaskAsync()
    {
        if (_session.TaskId is { } task)
        {
            await Coordinator.CloseTaskAsync(task).ConfigureAwait(false);
        }

        var last = _session.LastRunId is null ? null : _services.Database.FindRun(_session.LastRunId);
        _session.TaskId = null;
        _session.LastRunId = null;
        _session.TaskEffort = null;
        _session.Attachments.Clear();
        _ui.Say("The next request starts a new task with its own workspace and its own conversations.");
        if (last is { State: RunState.ReadyToApply })
        {
            _ui.Warn($"Run {last.RunId} is ready but was not applied. /apply {last.RunId} is not possible later from a new task; use /resume or /history to come back to it, or /discard {last.RunId}.");
        }
    }

    private void Attach(ParsedCommand command)
    {
        var (path, _) = SplitPath(command);
        if (path.Length == 0)
        {
            if (_session.Attachments.Count == 0)
            {
                _ui.Say("Nothing is attached to the next request.");
            }
            else
            {
                _ui.Say("Attached to the next request:");
                foreach (var attachment in _session.Attachments)
                {
                    _ui.Say("  " + attachment);
                }
            }

            return;
        }

        if (path.Equals("clear", StringComparison.OrdinalIgnoreCase))
        {
            _session.Attachments.Clear();
            _ui.Say("Nothing is attached any more.");
            return;
        }

        string full;
        try
        {
            full = Path.GetFullPath(path, _session.ProjectPath ?? Environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            _ui.Error($"'{path}' is not a path: {ex.Message}");
            return;
        }

        if (!File.Exists(full))
        {
            _ui.Error($"The file '{full}' does not exist.");
            return;
        }

        if (Yav.Console.Composition.SettingsStore.LooksLikeSecret(Path.GetFileName(full)) || Path.GetFileName(full).StartsWith(".env", StringComparison.OrdinalIgnoreCase))
        {
            _ui.Warn($"'{Path.GetFileName(full)}' looks like it holds credentials. It was not attached: what is attached is named to a provider.");
            return;
        }

        if (!_session.Attachments.Contains(full, StringComparer.OrdinalIgnoreCase))
        {
            _session.Attachments.Add(full);
        }

        _ui.Say($"Attached to the next request: {full}");
        _ui.Muted("The file is named to Model A as reference data. Its content is read by the agent, under the agent's own permissions.");
    }

    private async Task StatusAsync(CancellationToken cancellationToken)
    {
        var settings = _services.Settings;
        _ui.Heading("Status");
        _ui.Pairs(
        [
            ("Project", _session.ProjectPath ?? "none selected", Tone.Normal),
            ("Quality Lock", settings.QualityLock ? $"ON ({settings.ToPolicy().Label})" : "OFF", settings.QualityLock ? Tone.Success : Tone.Warning),
            ("Required checks", settings.RequireGates ? "required" : "optional: a candidate can be accepted on review alone", settings.RequireGates ? Tone.Normal : Tone.Warning),
            ("Repair cycles", settings.Limits.MaxRepairCycles.ToString(System.Globalization.CultureInfo.InvariantCulture), Tone.Normal),
            ("Provider speed", settings.Speed == ProviderSpeedMode.Provider ? "provider tier requested" : "standard", Tone.Normal),
            ("Adaptive", settings.Adaptive ? "ON" : "OFF", settings.Adaptive ? Tone.Warning : Tone.Normal),
        ]);

        await RolesAsync(cancellationToken).ConfigureAwait(false);

        if (_session.Active is { } active)
        {
            var stored = active.RunId is null ? null : _services.Database.FindRun(active.RunId);
            var elapsed = _services.Clock.GetUtcNow() - active.StartedAt;
            _ui.Blank();
            _ui.Heading("Active run");
            _ui.Pairs(
            [
                ("Run", active.RunId ?? "not started yet", Tone.Normal),
                ("State", stored is null ? "Preparing" : RunStateMachine.Display(stored.State), Tone.Accent),
                ("Running for", Describe(elapsed), Tone.Normal),
                ("Request", Shorten(active.Request, 90), Tone.Normal),
            ]);
            if (active.RunId is not null)
            {
                Confirmations(active.RunId);
            }
        }
        else if (_session.LastRunId is { } last && _services.Database.FindRun(last) is { } run)
        {
            _ui.Blank();
            _ui.Heading("Last run");
            DescribeRun(run);
        }

        if (_session.ProjectPath is { } project)
        {
            var waiting = _services.Database.GetQueue(project).Count;
            if (waiting > 0)
            {
                _ui.Muted($"{waiting} request(s) waiting" + (_session.QueuePaused ? ", not started because the last run needs a decision" : string.Empty) + ". See /queue.");
            }
        }

        if (_session.Attachments.Count > 0)
        {
            _ui.Muted($"{_session.Attachments.Count} file(s) attached to the next request. See /attach.");
        }
    }

    private async Task RolesAsync(CancellationToken cancellationToken)
    {
        var settings = _services.Settings;
        var rows = new List<IReadOnlyList<string>>();
        foreach (var (name, selection) in new[] { ("A  implements", settings.ModelA), ("B  reviews", settings.ModelB) })
        {
            if (selection is null)
            {
                rows.Add([name, "not chosen", string.Empty, string.Empty, string.Empty]);
                continue;
            }

            // Says that it asks when it has to, and says what went wrong when something did.
            var snapshot = await SnapshotAsync(selection.AdapterId, refresh: false, cancellationToken).ConfigureAwait(false);

            var model = snapshot?.Models.FirstOrDefault(m => string.Equals(m.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase));
            var effort = selection.WantsMaximum
                ? model is null ? "maximum" : ProfileResolver.HighestEffort(model.SupportedEfforts) is { } highest ? $"maximum = {highest}" : "maximum (choose the exact value: /effort)"
                : selection.EffortPreference;
            rows.Add(
            [
                name,
                selection.ModelId + (model is null && snapshot is { Models.Count: > 0 } ? " (not listed)" : string.Empty),
                selection.AdapterId,
                effort,
                snapshot?.Auth is { Authenticated: true } auth ? $"{auth.RouteLabel}; {DoctorChecks.Describe(auth.Billing)}" : "not signed in",
            ]);
        }

        _ui.Table(["Role", "Model", "Adapter", "Effort requested", "Account route and billing"], rows);
    }

    private void DescribeRun(RunRecord run)
    {
        var candidate = run.CurrentCandidateId is null ? null : _services.Database.FindCandidate(run.CurrentCandidateId);
        var pairs = new List<(string, string, Tone)>
        {
            ("Run", run.RunId, Tone.Normal),
            ("State", RunStateMachine.Display(run.State) + (run.Disposition == RunDisposition.Pending ? string.Empty : $" ({run.Disposition})"), ToneOf(run.State)),
            ("Request", Shorten(run.RequestText, 90), Tone.Normal),
        };
        if (run.StateReason is not null)
        {
            pairs.Add(("Reason", Shorten(run.StateReason, 200), Tone.Normal));
        }

        if (candidate is not null)
        {
            pairs.Add(("Candidate", $"{candidate.ShortFingerprint}: {candidate.Changes.Summary}", Tone.Normal));
            var review = _services.Database.GetLatestReview(run.RunId, candidate.CandidateId);
            pairs.Add(("Review", review is null ? "none for this candidate" : RunPublisherText.Describe(review), review is { IsCleanPass: true } ? Tone.Success : Tone.Warning));
            var results = _services.Database.GetGateResults(run.RunId)
                .Where(r => !r.IsBaselineRun && r.Binding.CandidateFingerprint == candidate.Fingerprint)
                .GroupBy(r => r.GateId).Select(g => g.Last()).ToList();
            pairs.Add(("Checks", results.Count == 0
                ? "none ran for this candidate"
                : string.Join(", ", results.Select(r => $"{r.GateId}: {r.Status}")), results.All(r => r.Status == GateStatus.Passed) && results.Count > 0 ? Tone.Success : Tone.Warning));
        }

        if (run.RepairCyclesUsed > 0)
        {
            pairs.Add(("Repairs", run.RepairCyclesUsed.ToString(System.Globalization.CultureInfo.InvariantCulture), Tone.Normal));
        }

        _ui.Pairs(pairs);
        Confirmations(run.RunId);
    }

    /// <summary>What was asked of the providers and what they reported. Verified only where they reported it.</summary>
    private void Confirmations(string runId)
    {
        var confirmations = _services.Database.GetConfirmations(runId);
        if (confirmations.Count == 0)
        {
            _ui.Muted("  No provider has reported its settings for this run yet.");
            return;
        }

        var latest = confirmations
            .GroupBy(c => (c.Role, c.Setting))
            .Select(g => g.OrderByDescending(c => c.ObservedAt).First())
            .OrderBy(c => c.Role).ThenBy(c => c.Setting, StringComparer.Ordinal);
        _ui.Table(
            ["Role", "Setting", "Requested", "In effect", "Status", "Reported by"],
            latest.Select(c => (IReadOnlyList<string>)
            [
                c.Role == AgentRole.Implementer ? "A" : "B",
                c.Setting,
                string.IsNullOrEmpty(c.Requested) ? "-" : c.Requested,
                c.Effective ?? "not reported",
                c.Status switch
                {
                    VerificationStatus.Verified => "Verified",
                    VerificationStatus.RequestedUnverified => "Requested / Unverified",
                    VerificationStatus.Mismatch => "MISMATCH",
                    VerificationStatus.Unsupported => "Unsupported",
                    _ => "Unavailable",
                },
                c.Source,
            ]));
    }

    internal static string Describe(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m {time.Seconds}s"
        : time.TotalSeconds >= 1 ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{time.TotalSeconds:0.0}s")
        : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{time.TotalMilliseconds:0} ms");
}

/// <summary>Short descriptions of results, in the words the run itself uses.</summary>
internal static class RunPublisherText
{
    public static string Describe(ReviewResult review)
    {
        if (!review.OutputValid)
        {
            return "invalid output, which is not a pass";
        }

        var verdict = review.Verdict switch
        {
            ReviewVerdict.Pass => "Pass",
            ReviewVerdict.ChangesRequired => "Changes required",
            _ => "Unable to verify",
        };
        return $"{verdict}: {review.BlockingFindings.Count()} blocking finding(s), {review.Suggestions.Count()} suggestion(s)"
            + (review.SourceUnchangedDuringReview ? string.Empty : "; source changed during the review");
    }
}
