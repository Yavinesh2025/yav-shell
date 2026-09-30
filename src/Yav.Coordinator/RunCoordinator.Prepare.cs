using System.Security.Cryptography;
using Yav.Core;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Timing;

namespace Yav.Coordinator;

public sealed partial class RunCoordinator
{
    private static readonly string[] InstructionFileNames = ["AGENTS.md", "AGENTS.override.md", "CLAUDE.md", ".claude/CLAUDE.md"];

    /// <summary>
    /// Finds out whether a request could run and with which exact settings. Reads the project, the approved
    /// configuration and the agents' accounts and models; sends no inference request and changes nothing.
    /// </summary>
    public async Task<Preflight> PreflightAsync(
        RunRequest request,
        RunConfiguration configuration,
        CancellationToken cancellationToken,
        bool refreshAgents = false,
        TimingRecorder? timing = null)
    {
        timing ??= new TimingRecorder(Clock, null);
        var project = Path.GetFullPath(request.ProjectPath);
        var problems = new List<ProfileProblem>();
        var validation = _services.Validation;

        if (!Directory.Exists(project))
        {
            problems.Add(new ProfileProblem(
                null, ProblemSeverity.Blocking, "project-missing", $"The project directory '{project}' does not exist.", "Select a project with /open."));
            return new Preflight(
                project, new ProfileResolution(null, problems),
                null, new ProjectConfigurationState(ConfigurationTrust.None, ProjectConfiguration.Empty, null, null, null, null, []),
                WorkspaceMode.Snapshot, problems);
        }

        var state = validation.LoadConfiguration(project);
        var file = validation.ConfigurationFileName;
        switch (state.Trust)
        {
            case ConfigurationTrust.Untrusted:
                problems.Add(new ProfileProblem(
                    null, ProblemSeverity.Warning, "gates-untrusted",
                    $"{file} has not been approved by you, so nothing from it is used.", "Review and approve it with /test trust."));
                break;

            case ConfigurationTrust.Changed:
                problems.Add(new ProfileProblem(
                    null, ProblemSeverity.Warning, "gates-changed",
                    $"{file} differs from the version you approved. The approved version stays in effect.", "Review the change with /test trust."));
                break;

            case ConfigurationTrust.Invalid:
                problems.Add(new ProfileProblem(
                    null, ProblemSeverity.Warning, "gates-invalid",
                    $"{file} cannot be used: {state.Errors.FirstOrDefault() ?? "it could not be read"}", "Correct the file, then approve it with /test trust."));
                break;

            default:
                foreach (var error in state.Errors)
                {
                    problems.Add(new ProfileProblem(null, ProblemSeverity.Warning, "gates-invalid", error, null));
                }

                break;
        }

        // The project and the agents do not depend on each other, and both are only read: they are looked
        // at side by side. Neither sends an inference request.
        var together = Ids.NewId("pg");
        var adapterIds = new[] { configuration.ModelA?.AdapterId, configuration.ModelB?.AdapterId }
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
        var inspecting = MeasuredAsync(
            timing, SpanKind.LocalPreparation, "project", together,
            () => _services.Workspaces.InspectAsync(project, state.Effective, cancellationToken));
        var asking = MeasuredAsync(
            timing, SpanKind.AgentInitialization, "agents", together,
            () => Catalog.GetManyAsync(adapterIds, refreshAgents, cancellationToken));
        try
        {
            await Task.WhenAll(inspecting, asking).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Both have ended, one way or the other. What went wrong first is what is reported.
            _ = ex;
        }

        var inspection = await inspecting.ConfigureAwait(false);
        var snapshots = await asking.ConfigureAwait(false);

        var mode = request.Mode
            ?? (inspection.IsGitRepository && inspection.HeadCommit is not null ? WorkspaceMode.GitWorktree : WorkspaceMode.Snapshot);

        var continues = request.TaskId is not null && _services.Store.FindTask(request.TaskId)?.WorkspaceId is { } workspaceId
            && await _services.Workspaces.FindAsync(workspaceId, cancellationToken).ConfigureAwait(false) is not null;

        if (mode == WorkspaceMode.InPlace)
        {
            if (!_services.Trust.IsInPlaceAcknowledged(project))
            {
                problems.Add(new ProfileProblem(
                    null, ProblemSeverity.Blocking, "workspace-in-place",
                    "Working directly in the project gives weaker protection: changes are not isolated and appear while the agent works.",
                    "Acknowledge this for the project with /settings workspace in-place, or use an isolated workspace."));
            }
        }
        else if (!continues)
        {
            foreach (var reason in inspection.Unsupported)
            {
                problems.Add(new ProfileProblem(null, ProblemSeverity.Blocking, "workspace-unsupported", reason, null));
            }

            if (inspection.EquivalenceGaps.Count > 0 && !request.EquivalenceGapsAcknowledged
                && !_services.Trust.AreGapsAcknowledged(project, GapsFingerprint(inspection.EquivalenceGaps)))
            {
                problems.Add(new ProfileProblem(
                    null, ProblemSeverity.Blocking, "workspace-gaps",
                    "The isolated workspace would not be an equivalent reproduction of the project. " + string.Join(" ", inspection.EquivalenceGaps),
                    $"Accept this for the project with /open --accept-gaps, or list what to copy under replicateIgnored in {file}."));
            }
        }

        foreach (var warning in inspection.Warnings)
        {
            problems.Add(new ProfileProblem(null, ProblemSeverity.Info, "workspace-note", warning, null));
        }

        var resolution = ProfileResolver.Resolve(new ProfileRequest(
            ProjectPath: project,
            ProjectTrusted: _services.Trust.IsProjectTrusted(project),
            Policy: configuration.Policy,
            Implementer: configuration.ModelA,
            Reviewer: configuration.ModelB,
            Speed: configuration.Speed,
            Adapters: snapshots,
            ProjectInstructionFiles: FindInstructionFiles(project, inspection.RepositoryRoot),
            RequiredGateIds: state.Effective.RequiredGates.Select(g => g.Id).ToList(),
            GateConfigurationHash: state.TrustedHash,
            YavVersion: _services.YavVersion,
            Now: Clock.GetUtcNow())
        {
            ApprovedImplementerEffort = request.ImplementerEffort,
        });

        problems.InsertRange(0, resolution.Problems);
        return new Preflight(project, resolution, inspection, state, mode, problems);
    }

    private static async Task<T> MeasuredAsync<T>(TimingRecorder timing, SpanKind kind, string label, string group, Func<Task<T>> work)
    {
        using (timing.Start(kind, label, group))
        {
            return await work().ConfigureAwait(false);
        }
    }

    /// <summary>Identifies an exact set of gaps, so that an acknowledgement does not cover gaps that appear later.</summary>
    public static string GapsFingerprint(IReadOnlyList<string> gaps) =>
        Convert.ToHexStringLower(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('\n', gaps.OrderBy(g => g, StringComparer.Ordinal)))))[..24];

    private static List<string> FindInstructionFiles(string project, string? repositoryRoot)
    {
        var found = new List<string>();
        var directories = new List<string> { project };
        if (repositoryRoot is not null && !string.Equals(Path.GetFullPath(repositoryRoot), project, StringComparison.OrdinalIgnoreCase))
        {
            directories.Add(Path.GetFullPath(repositoryRoot));
        }

        foreach (var directory in directories)
        {
            foreach (var name in InstructionFileNames)
            {
                var path = Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
                try
                {
                    if (File.Exists(path))
                    {
                        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                        var hash = Convert.ToHexStringLower(SHA256.HashData(stream))[..12];
                        found.Add($"{path} (sha256:{hash})");
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    found.Add($"{path} (unreadable)");
                }
            }
        }

        return found;
    }

    private sealed class PublishingProgress(RunPublisher publisher) : IProgress<string>
    {
        public void Report(string value) => publisher.Note(Stages.Prepare, value);
    }

    private async Task<Halt?> PrepareWorkspaceAsync(RunContext context, Preflight preflight, CancellationToken stop)
    {
        var task = _services.Store.FindTask(context.TaskId)!;
        IsolatedWorkspace workspace;
        using (context.Timing.Start(SpanKind.LocalPreparation, "workspace"))
        {
            workspace = await _services.Workspaces.PrepareAsync(
                new WorkspaceRequest(
                    context.TaskId, preflight.ProjectPath, preflight.Mode, context.Project, task.WorkspaceId,
                    // Preflight already established that the gaps, if any, were accepted.
                    EquivalenceGapsAcknowledged: true),
                preflight.Inspection!,
                new PublishingProgress(context.Publisher),
                stop).ConfigureAwait(false);
        }

        context.Workspace = workspace;
        if (!string.Equals(task.WorkspaceId, workspace.WorkspaceId, StringComparison.Ordinal))
        {
            _services.Store.SaveTask(task with { WorkspaceId = workspace.WorkspaceId });
        }

        foreach (var note in workspace.Notes)
        {
            context.Publisher.Note(Stages.Prepare, note, NoteLevel.Warning);
        }

        if (!workspace.Reused && context.Project.Prepare.Count > 0)
        {
            if (await RunPrepareCommandsAsync(context, workspace, stop).ConfigureAwait(false) is { } halt)
            {
                return halt;
            }
        }

        var kind = workspace.Mode switch
        {
            WorkspaceMode.GitWorktree => "isolated worktree",
            WorkspaceMode.Snapshot => "protected copy",
            _ => "in place, not isolated",
        };
        var dirty = preflight.Inspection!.IsDirty && !workspace.Reused
            ? $"; {preflight.Inspection.DirtyEntries.Count} uncommitted change(s) of yours are part of the baseline"
            : string.Empty;
        context.Publisher.Note(
            Stages.Prepare,
            $"Project rules and workspace verified ({kind}, {workspace.FileCount} files{dirty})",
            NoteLevel.Success);
        return null;
    }

    private async Task<Halt?> RunPrepareCommandsAsync(RunContext context, IsolatedWorkspace workspace, CancellationToken stop)
    {
        var binding = new EvidenceBinding(workspace.BaselineFingerprint, context.AcceptanceVersion, context.ProfileHash, "prepare");
        foreach (var command in context.Project.Prepare)
        {
            context.Publisher.Note(Stages.Prepare, $"Running {command.Title}: {command.DisplayCommand}");
            GateResult result;
            using (context.Timing.Start(SpanKind.LocalPreparation, command.Title))
            {
                result = await _services.Validation.RunGateAsync(
                    command,
                    new GateRunContext(context.RunId, ProjectDirectoryIn(workspace, workspace.RootPath), binding, workspace.EvidencePath, IsBaselineRun: true),
                    line => context.Publisher.Publish(new GateOutput(context.RunId, Clock.GetUtcNow(), command.Id, line)),
                    stop).ConfigureAwait(false);
            }

            stop.ThrowIfCancellationRequested();
            if (result.Status != GateStatus.Passed)
            {
                return new Halt(
                    RunState.Blocked, RunOutcomeKind.Blocked,
                    $"Preparation did not succeed: {RunPublisher.Describe(result)}. The complete output is in {result.OutputPath ?? "(none)"}. Model A was not called.",
                    Level: NoteLevel.Error);
            }
        }

        var after = await _services.Workspaces.FingerprintAsync(workspace, stop).ConfigureAwait(false);
        if (!string.Equals(after, workspace.BaselineFingerprint, StringComparison.Ordinal))
        {
            context.Publisher.Note(
                Stages.Prepare,
                "The preparation commands changed files that belong to the source. Those changes will appear in the task's diff.",
                NoteLevel.Warning);
        }

        return null;
    }

    /// <summary>
    /// The directory inside a copy of the workspace that corresponds to the directory the user opened, which
    /// is where the project's configuration and its gates are relative to.
    /// </summary>
    private static string ProjectDirectoryIn(IsolatedWorkspace workspace, string copyRoot)
    {
        var relative = Path.GetRelativePath(workspace.RootPath, workspace.AgentDirectory);
        return relative is "." or "" ? copyRoot : Path.Combine(copyRoot, relative);
    }
}
