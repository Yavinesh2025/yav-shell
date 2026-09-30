using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Core.Timing;
using Yav.Platform.Processes;
using Yav.Storage;
using Yav.Validation;

namespace Yav.Bench;

/// <summary>What a run of a task is given: the task, a project of its own, and a place for what the run needs.</summary>
public sealed record ArmRun(BenchEnvironment Environment, BenchTask Task, Workbench Bench, int Repetition);

/// <summary>One way of getting a task done. The arms of a comparison differ in this and in nothing else.</summary>
public interface IArm
{
    string Name { get; }

    string Description { get; }

    /// <summary>Performs the steps of the task one after the other in the same project, and reports each.</summary>
    Task<IReadOnlyList<StepResult>> RunAsync(ArmRun run, CancellationToken cancellationToken);
}

public static class Arms
{
    public const string Yav = "yav";
    public const string YavWithoutOptimization = "yav-optimization-off";
    public const string TwoModelsByHand = "two-models-by-hand";
    public const string ModelAAlone = "model-a-alone";

    public static IReadOnlyList<IArm> All { get; } =
    [
        new YavArm(Yav, optimization: true),
        new YavArm(YavWithoutOptimization, optimization: false),
        new DirectArm(TwoModelsByHand, review: true),
        new DirectArm(ModelAAlone, review: false),
    ];

    public static IArm Find(string name) =>
        All.FirstOrDefault(a => a.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ArgumentException($"'{name}' is not an arm. The arms are: {string.Join(", ", All.Select(a => a.Name))}.");

    /// <summary>What every arm reports the same way: whether the checks of the step pass in the project now, and which passed before.</summary>
    internal static async Task<(bool Succeeded, List<string> Failed, List<string> Regressions)> JudgeAsync(
        Workbench bench,
        BenchStep step,
        IReadOnlyList<CheckOutcome> before,
        CancellationToken cancellationToken)
    {
        var after = await bench.RunChecksAsync(step.Checks, cancellationToken).ConfigureAwait(false);
        var failed = after.Where(c => !c.Passed).Select(c => c.Id).ToList();
        var regressions = after
            .Where(c => !c.Passed && before.Any(b => b.Id == c.Id && b.Passed))
            .Select(c => c.Id)
            .ToList();
        return (failed.Count == 0, failed, regressions);
    }
}

/// <summary>The task is given to yav.exe, as a script would give it: 'yav run --json --apply'.</summary>
public sealed class YavArm(string name, bool optimization) : IArm
{
    public string Name => name;

    public string Description => optimization
        ? "YAV Shell as it is: isolated workspace, Model A, review by Model B and required checks side by side, acceptance, apply."
        : "YAV Shell with /optimization off: the same without YAV's own additions to a request and without reuse of evidence.";

    public async Task<IReadOnlyList<StepResult>> RunAsync(ArmRun run, CancellationToken cancellationToken)
    {
        var home = new YavPaths(Path.Combine(run.Bench.Directory, "yav-home"));
        home.EnsureCreated();
        var scenario = run.Environment.Mode == BenchMode.Fixture ? BenchEnvironment.WriteScenario(run.Task, Path.Combine(run.Bench.Directory, "agents")) : null;

        var settings = new AppSettings
        {
            ModelA = run.Environment.ModelA,
            ModelB = run.Environment.ModelB,
            Optimization = optimization,
            Limits = new LimitSettings { MaxRepairCycles = run.Environment.MaxRepairCycles },
        };
        foreach (var (id, flavor) in new[] { ("codex-app-server", "codex"), ("codex-exec", "codex"), ("claude-cli", "claude") })
        {
            settings.Adapters[id] = run.Environment.AdapterSettings(flavor, scenario);
        }

        File.WriteAllText(home.SettingsFile, settings.ToJson(), new UTF8Encoding(false));

        var results = new List<StepResult>();
        string? taskId = null;
        for (var index = 0; index < run.Task.Steps.Count; index++)
        {
            var step = run.Task.Steps[index];
            var before = await run.Bench.RunChecksAsync(step.Checks, cancellationToken).ConfigureAwait(false);
            Approve(run, home, step);

            var arguments = new List<string> { "run", "--project", run.Bench.Project, "--task", step.Request, "--json", "--apply" };
            if (taskId is not null)
            {
                arguments.AddRange(["--continue", taskId]);
            }

            var started = DateTimeOffset.UtcNow;
            var watch = Stopwatch.StartNew();
            var (exitCode, output, error) = await StartAsync(run.Environment.Yav, arguments, run.Bench.Project, home.Home, cancellationToken).ConfigureAwait(false);
            watch.Stop();

            var result = LastObject(output);
            var outcome = result?.TryGetProperty("outcome", out var o) == true ? o.GetString() ?? "unknown" : "unknown";
            var runId = result?.TryGetProperty("runId", out var r) == true ? r.GetString() : null;
            taskId = result?.TryGetProperty("taskId", out var t) == true ? t.GetString() ?? taskId : taskId;
            var (succeeded, failed, regressions) = await Arms.JudgeAsync(run.Bench, step, before, cancellationToken).ConfigureAwait(false);

            var measured = Measured(home, runId);
            results.Add(new StepResult
            {
                Arm = Name,
                TaskId = run.Task.Id,
                Step = index + 1,
                Repetition = run.Repetition,
                Mode = run.Environment.Mode.ToString().ToLowerInvariant(),
                Outcome = outcome,
                Succeeded = succeeded,
                FailedChecks = failed,
                Regressions = regressions,
                ElapsedMs = watch.ElapsedMilliseconds,
                ActiveMs = measured.ApprovalWaitingMs is null ? null : watch.ElapsedMilliseconds - measured.ApprovalWaitingMs,
                ApprovalWaitingMs = measured.ApprovalWaitingMs,
                FirstActionMs = measured.FirstActionMs,
                RepairCycles = measured.RepairCycles,
                TotalTokens = measured.Implementer is null || measured.Reviewer is null ? null : measured.Implementer + measured.Reviewer,
                ImplementerTokens = measured.Implementer,
                ReviewerTokens = measured.Reviewer,
                CostUsd = measured.Cost,
                Turns = measured.Turns,
                Retries = measured.Retries,
                Warm = index > 0,
                RunId = runId,
                Note = exitCode == 0 ? null : $"exit code {exitCode}" + (error.Trim().Length > 0 ? ": " + error.Trim() : string.Empty),
                StartedAt = started,
            });
        }

        return results;
    }

    /// <summary>
    /// What the user does once for a project in YAV: trust it, approve its checks, acknowledge the account
    /// route. In live mode a route is acknowledged here only when the user acknowledged it in YAV.
    /// </summary>
    private static void Approve(ArmRun run, YavPaths home, BenchStep step)
    {
        var database = YavDatabase.Open(home.Database, TimeProvider.System);
        try
        {
            database.SetProjectTrusted(run.Bench.Project, true);
            var validation = new ValidationService(new ProcessRunner(), database, TimeProvider.System, "bench");
            var configuration = run.Task.Configuration(step);
            validation.TrustConfiguration(run.Bench.Project, configuration, validation.Serialize(configuration));

            if (run.Environment.Mode == BenchMode.Fixture)
            {
                database.AcknowledgeRoute("codex-app-server:Subscription:openai", "benchmark with scripted agents");
                return;
            }

            var user = YavDatabase.Open(new YavPaths(run.Environment.UserHome!).Database, TimeProvider.System);
            try
            {
                foreach (var acknowledged in user.ListAcknowledgements().Where(a => a.Kind == "route"))
                {
                    database.AcknowledgeRoute(acknowledged.Subject, "acknowledged by the user in YAV; taken over for the benchmark");
                }
            }
            finally
            {
                user.Dispose();
            }
        }
        finally
        {
            database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private sealed record Measurements(long? Implementer, long? Reviewer, decimal? Cost, int Turns, int Retries, long? ApprovalWaitingMs, long? FirstActionMs, int RepairCycles);

    private static Measurements Measured(YavPaths home, string? runId)
    {
        if (string.IsNullOrEmpty(runId))
        {
            return new Measurements(null, null, null, 0, 0, null, null, 0);
        }

        var database = YavDatabase.Open(home.Database, TimeProvider.System);
        try
        {
            var run = database.FindRun(runId);
            var usage = database.GetUsage(runId, null);
            long? Tokens(AgentRole role)
            {
                var records = usage.Where(u => u.Role == role).ToList();
                return records.Count == 0 || records.Any(u => u.Tokens.Total is null || !u.RunShareKnown) ? null : records.Sum(u => u.Tokens.Total!.Value);
            }

            var costs = usage.Where(u => u.ProviderCostUsd is not null).ToList();
            var spans = database.GetSpans(runId);
            var events = database.GetEvents(runId, 100_000);
            var first = events.FirstOrDefault(e => e.Type.StartsWith("agent.", StringComparison.Ordinal) && e.Type != "agent.configured");
            return new Measurements(
                Tokens(AgentRole.Implementer),
                Tokens(AgentRole.Reviewer),
                costs.Count == 0 || costs.Count < usage.Count ? null : costs.Sum(u => u.ProviderCostUsd!.Value),
                usage.Sum(u => u.Turns),
                usage.Sum(u => u.Retries),
                (long)spans.Where(s => s.Kind == SpanKind.ApprovalWaiting && s.Duration is not null).Sum(s => s.Duration!.Value.TotalMilliseconds),
                first is null || run is null ? null : (long)(first.At - run.CreatedAt).TotalMilliseconds,
                run?.RepairCyclesUsed ?? 0);
        }
        finally
        {
            database.Dispose();
            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        }
    }

    private static JsonElement? LastObject(string output)
    {
        foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Reverse())
        {
            try
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.ValueKind == JsonValueKind.Object
                    && document.RootElement.TryGetProperty("type", out var type) && type.GetString() == "result")
                {
                    return document.RootElement.Clone();
                }
            }
            catch (JsonException)
            {
                // Not a line of the result.
            }
        }

        return null;
    }

    private static async Task<(int ExitCode, string Output, string Error)> StartAsync(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        string home,
        CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        start.Environment[YavPaths.HomeVariable] = home;
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"'{executable}' could not be started.");
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var error = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw;
        }

        return (process.ExitCode, await output.ConfigureAwait(false), await error.ConfigureAwait(false));
    }
}

/// <summary>
/// The same models, asked the way a person asks them in two terminals: Model A is given the request in
/// the project itself, the checks are run, Model B is shown the diff, and what is wrong goes back to
/// Model A. No isolated workspace, no frozen candidate, no evidence, none of YAV's additions.
/// With review off it is Model A alone, which is no quality-controlled workflow and is reported apart.
/// </summary>
public sealed class DirectArm(string name, bool review) : IArm
{
    public string Name => name;

    public string Description => review
        ? "The same two models without YAV: Model A works in the project itself, the checks are run, Model B is shown the diff, problems go back to Model A."
        : "Model A alone with the checks, without any review. Not an equivalent workflow; reported for orientation only.";

    public async Task<IReadOnlyList<StepResult>> RunAsync(ArmRun run, CancellationToken cancellationToken)
    {
        var runner = new ProcessRunner();
        var scenario = run.Environment.Mode == BenchMode.Fixture ? BenchEnvironment.WriteScenario(run.Task, Path.Combine(run.Bench.Directory, "agents")) : null;
        var adapters = new Dictionary<string, IAgentAdapter>(StringComparer.OrdinalIgnoreCase);
        IAgentAdapter Adapter(string id)
        {
            if (!adapters.TryGetValue(id, out var adapter))
            {
                adapter = run.Environment.CreateAdapter(id, scenario, runner);
                adapters[id] = adapter;
            }

            return adapter;
        }

        var results = new List<StepResult>();
        IAgentSession? implementer = null;
        IAgentSession? reviewer = null;
        var implementerUsage = new SessionUsageTracker(null, null, sessionIsNew: true);
        var reviewerUsage = new SessionUsageTracker(null, null, sessionIsNew: true);
        try
        {
            for (var index = 0; index < run.Task.Steps.Count; index++)
            {
                var step = run.Task.Steps[index];
                var before = await run.Bench.RunChecksAsync(step.Checks, cancellationToken).ConfigureAwait(false);
                var (tokensA, tokensB) = (implementerUsage.RunTokens.Total ?? 0, reviewerUsage.RunTokens.Total ?? 0);
                var started = DateTimeOffset.UtcNow;
                var watch = Stopwatch.StartNew();
                long? firstAction = null;
                var turns = 0;
                var retries = 0;
                var cycles = 0;
                var outcome = "completed";
                string? note = null;

                async Task<string?> TurnAsync(IAgentSession session, SessionUsageTracker usage, string prompt)
                {
                    turns++;
                    await session.StartTurnAsync(new TurnRequest(prompt, null, []), cancellationToken).ConfigureAwait(false);
                    await foreach (var agentEvent in session.Events.ReadAllAsync(cancellationToken).ConfigureAwait(false))
                    {
                        switch (agentEvent)
                        {
                            case ApprovalRequested approval:
                                // Nobody is there to ask, as in 'yav run'.
                                await session.RespondToApprovalAsync(approval.Request.ApprovalId, ApprovalDecision.Cancel, cancellationToken).ConfigureAwait(false);
                                outcome = "approval_required";
                                break;
                            case UsageUpdated updated:
                                usage.Observe(updated.Usage);
                                break;
                            case ProviderRetry or AgentError { WillRetry: true }:
                                retries++;
                                break;
                            case AssistantMessage or CommandStarted or FilesChanged or ToolActivity:
                                firstAction ??= watch.ElapsedMilliseconds;
                                break;
                            case TurnCompleted completed:
                                if (completed.Outcome != TurnOutcome.Completed && outcome == "completed")
                                {
                                    outcome = completed.Outcome switch
                                    {
                                        TurnOutcome.RateLimited or TurnOutcome.UsageLimitReached => "rate_limited",
                                        TurnOutcome.ApprovalRequired => "approval_required",
                                        TurnOutcome.Interrupted => "interrupted",
                                        _ => "failed",
                                    };
                                    note = completed.ErrorMessage;
                                }

                                return completed.FinalMessage;
                        }
                    }

                    outcome = "failed";
                    note = "The agent ended before the turn did.";
                    return null;
                }

                implementer ??= await OpenAsync(Adapter(run.Environment.ModelA.AdapterId), run.Environment.ModelA, AgentRole.Implementer, run.Bench.Project, cancellationToken).ConfigureAwait(false);
                await TurnAsync(implementer, implementerUsage, step.Request).ConfigureAwait(false);

                while (outcome == "completed")
                {
                    var checks = await run.Bench.RunChecksAsync(step.Checks, cancellationToken).ConfigureAwait(false);
                    var problems = new StringBuilder();
                    foreach (var check in checks.Where(c => !c.Passed))
                    {
                        problems.Append("The check '").Append(check.Id).Append("' failed (exit ").Append(check.ExitCode).Append("):\n").Append(check.Output).Append("\n\n");
                    }

                    if (review)
                    {
                        reviewer ??= await OpenAsync(Adapter(run.Environment.ModelB.AdapterId), run.Environment.ModelB, AgentRole.Reviewer, run.Bench.Project, cancellationToken).ConfigureAwait(false);
                        var diff = await run.Bench.DiffAsync(cancellationToken).ConfigureAwait(false);
                        var verdict = await TurnAsync(
                            reviewer, reviewerUsage,
                            $"Review this change. It was made for this request:\n{step.Request}\n\nThe diff:\n{diff}\n\nAnswer with PASS when nothing has to change. Otherwise say what has to change.")
                            .ConfigureAwait(false);
                        if (outcome != "completed")
                        {
                            break;
                        }

                        if (!Passes(verdict))
                        {
                            problems.Append("The reviewer said:\n").Append(verdict).Append('\n');
                        }
                    }

                    if (problems.Length == 0)
                    {
                        break;
                    }

                    if (cycles >= run.Environment.MaxRepairCycles)
                    {
                        outcome = "blocked";
                        note = "Problems were left when the repair cycles were used up.";
                        break;
                    }

                    cycles++;
                    await TurnAsync(implementer, implementerUsage, "This is not right yet.\n\n" + problems + "\nCorrect it.").ConfigureAwait(false);
                }

                watch.Stop();
                var (succeeded, failed, regressions) = await Arms.JudgeAsync(run.Bench, step, before, cancellationToken).ConfigureAwait(false);
                long? usedA = implementerUsage.RunTokens.Total is { } totalA ? totalA - tokensA : null;
                long? usedB = !review ? 0 : reviewerUsage.RunTokens.Total is { } totalB ? totalB - tokensB : null;
                results.Add(new StepResult
                {
                    Arm = Name,
                    TaskId = run.Task.Id,
                    Step = index + 1,
                    Repetition = run.Repetition,
                    Mode = run.Environment.Mode.ToString().ToLowerInvariant(),
                    Outcome = outcome,
                    Succeeded = succeeded,
                    FailedChecks = failed,
                    Regressions = regressions,
                    ElapsedMs = watch.ElapsedMilliseconds,
                    ActiveMs = watch.ElapsedMilliseconds,
                    ApprovalWaitingMs = 0,
                    FirstActionMs = firstAction,
                    RepairCycles = cycles,
                    TotalTokens = usedA is null || usedB is null ? null : usedA + usedB,
                    ImplementerTokens = usedA,
                    ReviewerTokens = review ? usedB : null,
                    CostUsd = implementerUsage.RunCostUsd is { } costA && (!review || reviewerUsage.RunCostUsd is not null)
                        ? costA + (review ? reviewerUsage.RunCostUsd!.Value : 0)
                        : null,
                    Turns = turns,
                    Retries = retries,
                    Warm = index > 0,
                    Note = note,
                    StartedAt = started,
                });

                if (outcome is not ("completed" or "blocked"))
                {
                    break;
                }
            }
        }
        finally
        {
            foreach (var session in new[] { implementer, reviewer })
            {
                if (session is not null)
                {
                    await session.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
                    await session.DisposeAsync().ConfigureAwait(false);
                }
            }

            foreach (var adapter in adapters.Values)
            {
                await adapter.DisposeAsync().ConfigureAwait(false);
            }
        }

        return results;
    }

    /// <summary>A verdict of the reviewer that asks for nothing: the word PASS, or a structured result that says so.</summary>
    internal static bool Passes(string? verdict)
    {
        if (string.IsNullOrWhiteSpace(verdict))
        {
            return false;
        }

        var text = verdict.Trim();
        if (text.StartsWith('{'))
        {
            try
            {
                using var document = JsonDocument.Parse(text);
                return document.RootElement.TryGetProperty("status", out var status)
                    && string.Equals(status.GetString(), "pass", StringComparison.OrdinalIgnoreCase);
            }
            catch (JsonException)
            {
                return false;
            }
        }

        return text.StartsWith("PASS", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<IAgentSession> OpenAsync(IAgentAdapter adapter, RoleSelection selection, AgentRole role, string directory, CancellationToken cancellationToken)
    {
        var effort = selection.EffortPreference;
        if (selection.WantsMaximum)
        {
            var models = await adapter.ListModelsAsync(cancellationToken).ConfigureAwait(false);
            var model = models.FirstOrDefault(m => string.Equals(m.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"{adapter.Id} does not list the model '{selection.ModelId}'.");
            effort = model.SupportedEfforts.Count == 0
                ? string.Empty
                : ProfileResolver.HighestEffort(model.SupportedEfforts)
                  ?? throw new InvalidOperationException($"'{selection.ModelId}' lists an effort that cannot be ranked. Choose the exact value in YAV (/effort).");
        }

        return await adapter.StartSessionAsync(
            new SessionRequest(
                role,
                selection.ModelId,
                effort,
                directory,
                role == AgentRole.Reviewer ? SandboxLevel.ReadOnly : SandboxLevel.WorkspaceWrite,
                role == AgentRole.Reviewer ? ApprovalMode.NeverAsk : ApprovalMode.AskUser,
                string.Empty,
                ProjectTrusted: true,
                ServiceTier: null,
                AdditionalReadableDirectories: []),
            cancellationToken).ConfigureAwait(false);
    }
}
