using Microsoft.Data.Sqlite;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Timing;

namespace Yav.Storage;

public sealed partial class YavDatabase : IRunStore
{
    private const string RunColumns =
        "run_id, task_id, sequence, kind, project_path, request_text, acceptance_version, profile_hash, state, disposition, "
        + "state_reason, repair_cycles_used, current_candidate_id, created_at, updated_at, owner_pid, yav_version";

    public void SaveTask(TaskContext task) => InTransaction(transaction => Write(
        """
        INSERT INTO tasks (task_id, project_path, project_key, created_at, workspace_id, implementer_session_id, implementer_adapter_id, reviewer_session_id, reviewer_adapter_id)
        VALUES ($id, $path, $key, $created, $workspace, $implementerSession, $implementerAdapter, $reviewerSession, $reviewerAdapter)
        ON CONFLICT (task_id) DO UPDATE SET
            workspace_id = excluded.workspace_id,
            implementer_session_id = excluded.implementer_session_id,
            implementer_adapter_id = excluded.implementer_adapter_id,
            reviewer_session_id = excluded.reviewer_session_id,
            reviewer_adapter_id = excluded.reviewer_adapter_id;
        """,
        transaction,
        ("$id", task.TaskId),
        ("$path", task.ProjectPath),
        ("$key", ProjectKey(task.ProjectPath)),
        ("$created", Stamp(task.CreatedAt)),
        ("$workspace", task.WorkspaceId),
        ("$implementerSession", task.ImplementerSessionId),
        ("$implementerAdapter", task.ImplementerAdapterId),
        ("$reviewerSession", task.ReviewerSessionId),
        ("$reviewerAdapter", task.ReviewerAdapterId)));

    public TaskContext? FindTask(string taskId) => Read(() => Query(
        "SELECT * FROM tasks WHERE task_id = $id;",
        reader => new TaskContext(
            Required(reader, "task_id"),
            Required(reader, "project_path"),
            Moment(Required(reader, "created_at")),
            Text(reader, "workspace_id"),
            Text(reader, "implementer_session_id"),
            Text(reader, "implementer_adapter_id"),
            Text(reader, "reviewer_session_id"),
            Text(reader, "reviewer_adapter_id")),
        null,
        ("$id", taskId)).FirstOrDefault());

    public void SaveRequirement(string taskId, Requirement requirement) => InTransaction(transaction => Write(
        """
        INSERT INTO requirements (task_id, version, text, added_at, attachments_json)
        VALUES ($task, $version, $text, $at, $attachments)
        ON CONFLICT (task_id, version) DO UPDATE SET text = excluded.text, attachments_json = excluded.attachments_json;
        """,
        transaction,
        ("$task", taskId),
        ("$version", requirement.Version),
        ("$text", requirement.Text),
        ("$at", Stamp(requirement.AddedAt)),
        ("$attachments", StorageJson.WriteList(requirement.Attachments))));

    public IReadOnlyList<Requirement> GetRequirements(string taskId) => Read(() => Query(
        "SELECT * FROM requirements WHERE task_id = $task ORDER BY version;",
        reader => new Requirement(
            Integer(reader, "version"),
            Required(reader, "text"),
            Moment(Required(reader, "added_at")),
            StorageJson.ReadList(Required(reader, "attachments_json"))),
        null,
        ("$task", taskId)));

    public void CreateRun(RunRecord run, RunProfile profile) => InTransaction(transaction => Write(
        """
        INSERT INTO runs (run_id, task_id, sequence, kind, project_path, project_key, request_text, acceptance_version, profile_hash,
            profile_json, model_a, model_b, state, disposition, state_reason, repair_cycles_used, current_candidate_id, created_at,
            updated_at, owner_pid, yav_version)
        VALUES ($id, $task, $sequence, $kind, $path, $key, $request, $acceptance, $hash, $profile, $modelA, $modelB, $state, $disposition,
            $reason, $repairs, $candidate, $created, $updated, $pid, $version);
        """,
        transaction,
        ("$id", run.RunId),
        ("$task", run.TaskId),
        ("$sequence", run.Sequence),
        ("$kind", run.Kind.ToString()),
        ("$path", run.ProjectPath),
        ("$key", ProjectKey(run.ProjectPath)),
        ("$request", run.RequestText),
        ("$acceptance", run.AcceptanceVersion),
        ("$hash", run.ProfileHash),
        ("$profile", profile.ToCanonicalJson()),
        ("$modelA", profile.Implementer.ModelId),
        ("$modelB", profile.Reviewer.ModelId),
        ("$state", run.State.ToString()),
        ("$disposition", run.Disposition.ToString()),
        ("$reason", run.StateReason),
        ("$repairs", run.RepairCyclesUsed),
        ("$candidate", run.CurrentCandidateId),
        ("$created", Stamp(run.CreatedAt)),
        ("$updated", Stamp(run.UpdatedAt)),
        ("$pid", run.OwnerProcessId),
        ("$version", run.YavVersion)));

    public RunRecord? FindRun(string runId) => Read(() => FindRun(runId, null));

    public RunProfile? GetProfile(string runId) => Read(() =>
    {
        var json = Query("SELECT profile_json FROM runs WHERE run_id = $id;", reader => reader.GetString(0), null, ("$id", runId)).FirstOrDefault();
        return json is null ? null : RunProfile.FromJson(json);
    });

    public RunRecord Transition(string runId, RunState from, RunState to, string? reason) => InTransaction(transaction =>
    {
        var current = FindRun(runId, transaction) ?? throw new KeyNotFoundException($"Run {runId} does not exist.");
        if (current.State != from || !RunStateMachine.CanTransition(from, to))
        {
            throw new InvalidRunTransitionException(runId, current.State, to);
        }

        var now = _clock.GetUtcNow();
        var changed = Write(
            "UPDATE runs SET state = $to, state_reason = $reason, updated_at = $now WHERE run_id = $id AND state = $from;",
            transaction,
            ("$to", to.ToString()),
            ("$reason", reason),
            ("$now", Stamp(now)),
            ("$id", runId),
            ("$from", from.ToString()));
        if (changed != 1)
        {
            throw new InvalidRunTransitionException(runId, current.State, to);
        }

        return current with { State = to, StateReason = reason, UpdatedAt = now };
    });

    public void UpdateRun(string runId, Func<RunRecord, RunRecord> change) => InTransaction(transaction =>
    {
        var current = FindRun(runId, transaction) ?? throw new KeyNotFoundException($"Run {runId} does not exist.");
        var updated = change(current);
        if (updated.State != current.State)
        {
            throw new InvalidOperationException("A run's state is changed with Transition, which validates the change.");
        }

        Write(
            """
            UPDATE runs SET disposition = $disposition, state_reason = $reason, repair_cycles_used = $repairs,
                current_candidate_id = $candidate, acceptance_version = $acceptance, updated_at = $now, owner_pid = $pid
            WHERE run_id = $id;
            """,
            transaction,
            ("$disposition", updated.Disposition.ToString()),
            ("$reason", updated.StateReason),
            ("$repairs", updated.RepairCyclesUsed),
            ("$candidate", updated.CurrentCandidateId),
            ("$acceptance", updated.AcceptanceVersion),
            ("$now", Stamp(_clock.GetUtcNow())),
            ("$pid", updated.OwnerProcessId),
            ("$id", runId));
    });

    public void AppendEvent(RunEventRecord record) => InTransaction(transaction =>
    {
        Write(
            "INSERT INTO run_events (run_id, at, type, stage, summary, detail) VALUES ($run, $at, $type, $stage, $summary, $detail);",
            transaction,
            ("$run", record.RunId),
            ("$at", Stamp(record.At)),
            ("$type", record.Type),
            ("$stage", record.Stage),
            ("$summary", record.Summary),
            ("$detail", record.Detail));

        if (!_eventCounts.TryGetValue(record.RunId, out var count))
        {
            using var counter = Command("SELECT COUNT(*) FROM run_events WHERE run_id = $run;", transaction, ("$run", record.RunId));
            count = Convert.ToInt32(counter.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture) - 1;
        }

        count++;
        if (count > MaxEventsPerRun)
        {
            Write(
                """
                DELETE FROM run_events WHERE run_id = $run AND sequence <= (
                    SELECT sequence FROM run_events WHERE run_id = $run ORDER BY sequence DESC LIMIT 1 OFFSET $keep);
                """,
                transaction,
                ("$run", record.RunId),
                ("$keep", MaxEventsPerRun));
            count = MaxEventsPerRun;
        }

        _eventCounts[record.RunId] = count;
    });

    public IReadOnlyList<RunEventRecord> GetEvents(string runId, int limit) => Read(() =>
    {
        var newestFirst = Query(
            "SELECT * FROM run_events WHERE run_id = $run ORDER BY sequence DESC LIMIT $limit;",
            reader => new RunEventRecord(
                Long(reader, "sequence"),
                Required(reader, "run_id"),
                Moment(Required(reader, "at")),
                Required(reader, "type"),
                Required(reader, "stage"),
                Required(reader, "summary"),
                Text(reader, "detail")),
            null,
            ("$run", runId),
            ("$limit", Math.Max(0, limit)));
        newestFirst.Reverse();
        return (IReadOnlyList<RunEventRecord>)newestFirst;
    });

    public void SaveCandidate(Candidate candidate, string manifestId) => InTransaction(transaction =>
    {
        Write(
            """
            INSERT INTO candidates (candidate_id, run_id, sequence, fingerprint, baseline_fingerprint, acceptance_version, profile_hash,
                manifest_id, changes_json, protected_json, tests_touched_json, frozen_at)
            VALUES ($id, $run, $sequence, $fingerprint, $baseline, $acceptance, $profile, $manifest, $changes, $protected, $tests, $frozen)
            ON CONFLICT (candidate_id) DO UPDATE SET
                fingerprint = excluded.fingerprint, manifest_id = excluded.manifest_id, changes_json = excluded.changes_json,
                protected_json = excluded.protected_json, tests_touched_json = excluded.tests_touched_json, frozen_at = excluded.frozen_at;
            """,
            transaction,
            ("$id", candidate.CandidateId),
            ("$run", candidate.RunId),
            ("$sequence", candidate.Sequence),
            ("$fingerprint", candidate.Fingerprint),
            ("$baseline", candidate.BaselineFingerprint),
            ("$acceptance", candidate.AcceptanceVersion),
            ("$profile", candidate.ProfileHash),
            ("$manifest", manifestId),
            ("$changes", StorageJson.Write(candidate.Changes, StorageJson.Default.ChangeSet)),
            ("$protected", StorageJson.WriteList(candidate.ProtectedPathsTouched)),
            ("$tests", StorageJson.WriteList(candidate.ExistingTestsTouched)),
            ("$frozen", Stamp(candidate.FrozenAt)));
        Write(
            "UPDATE runs SET current_candidate_id = $candidate, updated_at = $now WHERE run_id = $run;",
            transaction,
            ("$candidate", candidate.CandidateId),
            ("$now", Stamp(_clock.GetUtcNow())),
            ("$run", candidate.RunId));
    });

    public Candidate? FindCandidate(string candidateId) => Read(() =>
        Query("SELECT * FROM candidates WHERE candidate_id = $id;", MapCandidate, null, ("$id", candidateId)).FirstOrDefault());

    public IReadOnlyList<Candidate> GetCandidates(string runId) => Read(() =>
        Query("SELECT * FROM candidates WHERE run_id = $run ORDER BY sequence;", MapCandidate, null, ("$run", runId)));

    public string? GetManifestId(string candidateId) => Read(() =>
        Query("SELECT manifest_id FROM candidates WHERE candidate_id = $id;", reader => reader.GetString(0), null, ("$id", candidateId)).FirstOrDefault());

    public void SaveReview(ReviewResult review) => InTransaction(transaction => Write(
        """
        INSERT OR REPLACE INTO reviews (review_id, run_id, candidate_id, binding_json, verdict, summary, coverage, findings_json,
            limitations_json, output_valid, validation_errors_json, reviewer_adapter_id, reviewer_model, reviewer_session_id,
            source_unchanged, started_at, completed_at)
        VALUES ($id, $run, $candidate, $binding, $verdict, $summary, $coverage, $findings, $limitations, $valid, $errors, $adapter,
            $model, $session, $unchanged, $started, $completed);
        """,
        transaction,
        ("$id", review.ReviewId),
        ("$run", review.RunId),
        ("$candidate", review.CandidateId),
        ("$binding", StorageJson.Write(review.Binding, StorageJson.Default.EvidenceBinding)),
        ("$verdict", review.Verdict.ToString()),
        ("$summary", review.Summary),
        ("$coverage", review.Coverage),
        ("$findings", StorageJson.Write(review.Findings.ToList(), StorageJson.Default.ListFinding)),
        ("$limitations", StorageJson.WriteList(review.Limitations)),
        ("$valid", review.OutputValid ? 1 : 0),
        ("$errors", StorageJson.WriteList(review.ValidationErrors)),
        ("$adapter", review.ReviewerAdapterId),
        ("$model", review.ReviewerModel),
        ("$session", review.ReviewerSessionId),
        ("$unchanged", review.SourceUnchangedDuringReview ? 1 : 0),
        ("$started", Stamp(review.StartedAt)),
        ("$completed", Stamp(review.CompletedAt))));

    public ReviewResult? GetLatestReview(string runId, string candidateId) => Read(() => Query(
        "SELECT * FROM reviews WHERE run_id = $run AND candidate_id = $candidate ORDER BY completed_at DESC, rowid DESC LIMIT 1;",
        MapReview,
        null,
        ("$run", runId),
        ("$candidate", candidateId)).FirstOrDefault());

    public IReadOnlyList<ReviewResult> GetReviews(string runId) => Read(() =>
        Query("SELECT * FROM reviews WHERE run_id = $run ORDER BY completed_at, rowid;", MapReview, null, ("$run", runId)));

    public void SaveGateResult(GateResult result) => InTransaction(transaction => Write(
        """
        INSERT OR REPLACE INTO gate_results (result_id, run_id, gate_id, gate_title, kind, required, binding_json, status, exit_code,
            command_line, working_directory, started_at, duration_ms, output_path, output_tail, output_bytes, limitation,
            fails_on_baseline, is_baseline_run)
        VALUES ($id, $run, $gate, $title, $kind, $required, $binding, $status, $exit, $command, $directory, $started, $duration,
            $outputPath, $tail, $bytes, $limitation, $baselineFailure, $baselineRun);
        """,
        transaction,
        ("$id", result.ResultId),
        ("$run", result.RunId),
        ("$gate", result.GateId),
        ("$title", result.GateTitle),
        ("$kind", result.Kind.ToString()),
        ("$required", result.Required ? 1 : 0),
        ("$binding", StorageJson.Write(result.Binding, StorageJson.Default.EvidenceBinding)),
        ("$status", result.Status.ToString()),
        ("$exit", result.ExitCode),
        ("$command", result.CommandLine),
        ("$directory", result.WorkingDirectory),
        ("$started", Stamp(result.StartedAt)),
        ("$duration", result.DurationMs),
        ("$outputPath", result.OutputPath),
        ("$tail", result.OutputTail),
        ("$bytes", result.OutputBytes),
        ("$limitation", result.Limitation),
        ("$baselineFailure", result.FailsOnBaseline is null ? null : result.FailsOnBaseline.Value ? 1 : 0),
        ("$baselineRun", result.IsBaselineRun ? 1 : 0)));

    public IReadOnlyList<GateResult> GetGateResults(string runId) => Read(() => Query(
        "SELECT * FROM gate_results WHERE run_id = $run ORDER BY started_at, rowid;",
        reader => new GateResult(
            Required(reader, "result_id"),
            Required(reader, "run_id"),
            Required(reader, "gate_id"),
            Required(reader, "gate_title"),
            Enumeration<GateKind>(reader, "kind"),
            Flag(reader, "required"),
            StorageJson.Read(Required(reader, "binding_json"), StorageJson.Default.EvidenceBinding),
            Enumeration<GateStatus>(reader, "status"),
            NullableInteger(reader, "exit_code"),
            Required(reader, "command_line"),
            Required(reader, "working_directory"),
            Moment(Required(reader, "started_at")),
            Long(reader, "duration_ms"),
            Text(reader, "output_path"),
            Required(reader, "output_tail"),
            Long(reader, "output_bytes"),
            Text(reader, "limitation"),
            NullableFlag(reader, "fails_on_baseline"),
            Flag(reader, "is_baseline_run")),
        null,
        ("$run", runId)));

    public void SaveWaiver(GateWaiver waiver) => InTransaction(transaction => Write(
        "INSERT OR REPLACE INTO waivers (run_id, gate_id, candidate_fingerprint, reason, granted_at) VALUES ($run, $gate, $fingerprint, $reason, $at);",
        transaction,
        ("$run", waiver.RunId),
        ("$gate", waiver.GateId),
        ("$fingerprint", waiver.CandidateFingerprint),
        ("$reason", waiver.Reason),
        ("$at", Stamp(waiver.GrantedAt))));

    public IReadOnlyList<GateWaiver> GetWaivers(string runId) => Read(() => Query(
        "SELECT * FROM waivers WHERE run_id = $run ORDER BY granted_at;",
        reader => new GateWaiver(
            Required(reader, "run_id"),
            Required(reader, "gate_id"),
            Required(reader, "candidate_fingerprint"),
            Required(reader, "reason"),
            Moment(Required(reader, "granted_at"))),
        null,
        ("$run", runId)));

    public void ApproveProtectedPath(string runId, string candidateFingerprint, string path) => InTransaction(transaction => Write(
        "INSERT OR REPLACE INTO protected_approvals (run_id, candidate_fingerprint, path, approved_at) VALUES ($run, $fingerprint, $path, $at);",
        transaction,
        ("$run", runId),
        ("$fingerprint", candidateFingerprint),
        ("$path", path),
        ("$at", Stamp(_clock.GetUtcNow()))));

    public IReadOnlyList<string> GetApprovedProtectedPaths(string runId, string candidateFingerprint) => Read(() => Query(
        "SELECT path FROM protected_approvals WHERE run_id = $run AND candidate_fingerprint = $fingerprint ORDER BY path;",
        reader => reader.GetString(0),
        null,
        ("$run", runId),
        ("$fingerprint", candidateFingerprint)));

    public void SaveConfirmation(string runId, ProfileConfirmation confirmation) => InTransaction(transaction => Write(
        """
        INSERT INTO confirmations (run_id, role, setting, requested, effective, status, source, observed_at)
        VALUES ($run, $role, $setting, $requested, $effective, $status, $source, $at);
        """,
        transaction,
        ("$run", runId),
        ("$role", confirmation.Role.ToString()),
        ("$setting", confirmation.Setting),
        ("$requested", confirmation.Requested),
        ("$effective", confirmation.Effective),
        ("$status", confirmation.Status.ToString()),
        ("$source", confirmation.Source),
        ("$at", Stamp(confirmation.ObservedAt))));

    public IReadOnlyList<ProfileConfirmation> GetConfirmations(string runId) => Read(() => Query(
        "SELECT * FROM confirmations WHERE run_id = $run ORDER BY id;",
        reader => new ProfileConfirmation(
            Enumeration<AgentRole>(reader, "role"),
            Required(reader, "setting"),
            Text(reader, "requested"),
            Text(reader, "effective"),
            Enumeration<Yav.Core.VerificationStatus>(reader, "status"),
            Required(reader, "source"),
            Moment(Required(reader, "observed_at"))),
        null,
        ("$run", runId)));

    public void SaveSession(SessionRecord session) => InTransaction(transaction => Write(
        """
        INSERT INTO sessions (task_id, role, adapter_id, session_id, model, tokens_json, cost_usd, updated_at)
        VALUES ($task, $role, $adapter, $session, $model, $tokens, $cost, $at)
        ON CONFLICT (task_id, role) DO UPDATE SET
            adapter_id = excluded.adapter_id, session_id = excluded.session_id, model = excluded.model,
            tokens_json = excluded.tokens_json, cost_usd = excluded.cost_usd, updated_at = excluded.updated_at;
        """,
        transaction,
        ("$task", session.TaskId),
        ("$role", session.Role.ToString()),
        ("$adapter", session.AdapterId),
        ("$session", session.SessionId),
        ("$model", session.Model),
        ("$tokens", session.CumulativeTokens is null ? null : StorageJson.Write(session.CumulativeTokens, StorageJson.Default.TokenCounts)),
        ("$cost", Money(session.CumulativeCostUsd)),
        ("$at", Stamp(session.UpdatedAt))));

    public SessionRecord? FindSession(string taskId, AgentRole role) => Read(() => Query(
        "SELECT * FROM sessions WHERE task_id = $task AND role = $role;",
        reader =>
        {
            var tokens = Text(reader, "tokens_json");
            return new SessionRecord(
                Required(reader, "task_id"),
                Enumeration<AgentRole>(reader, "role"),
                Required(reader, "adapter_id"),
                Required(reader, "session_id"),
                Text(reader, "model"),
                tokens is null ? null : StorageJson.Read(tokens, StorageJson.Default.TokenCounts),
                Money(reader, "cost_usd"),
                Moment(Required(reader, "updated_at")));
        },
        null,
        ("$task", taskId),
        ("$role", role.ToString())).FirstOrDefault());

    public void SaveUsage(UsageRecord usage) => InTransaction(transaction => Write(
        """
        INSERT INTO usage (run_id, role, adapter_id, session_id, model, tokens_json, run_share_known, cost_usd, cost_provenance, turns,
            retries, billing_route, source, observed_at)
        VALUES ($run, $role, $adapter, $session, $model, $tokens, $known, $cost, $provenance, $turns, $retries, $route, $source, $at);
        """,
        transaction,
        ("$run", usage.RunId),
        ("$role", usage.Role.ToString()),
        ("$adapter", usage.AdapterId),
        ("$session", usage.SessionId),
        ("$model", usage.Model),
        ("$tokens", StorageJson.Write(usage.Tokens, StorageJson.Default.TokenCounts)),
        ("$known", usage.RunShareKnown ? 1 : 0),
        ("$cost", Money(usage.ProviderCostUsd)),
        ("$provenance", usage.CostProvenance.ToString()),
        ("$turns", usage.Turns),
        ("$retries", usage.Retries),
        ("$route", usage.BillingRoute),
        ("$source", usage.Source),
        ("$at", Stamp(usage.ObservedAt))));

    public IReadOnlyList<UsageRecord> GetUsage(string? runId, DateTimeOffset? since) => Read(() => Query(
        "SELECT * FROM usage WHERE ($run IS NULL OR run_id = $run) AND ($since IS NULL OR observed_at >= $since) ORDER BY id;",
        reader => new UsageRecord(
            Required(reader, "run_id"),
            Enumeration<AgentRole>(reader, "role"),
            Required(reader, "adapter_id"),
            Text(reader, "session_id"),
            Text(reader, "model"),
            StorageJson.Read(Required(reader, "tokens_json"), StorageJson.Default.TokenCounts),
            Flag(reader, "run_share_known"),
            Money(reader, "cost_usd"),
            Enumeration<Yav.Core.ValueProvenance>(reader, "cost_provenance"),
            Integer(reader, "turns"),
            Integer(reader, "retries"),
            Required(reader, "billing_route"),
            Required(reader, "source"),
            Moment(Required(reader, "observed_at"))),
        null,
        ("$run", runId),
        ("$since", since is null ? null : Stamp(since.Value))));

    public void SaveSpan(TimingSpan span) => InTransaction(transaction => Write(
        """
        INSERT OR REPLACE INTO spans (span_id, run_id, kind, label, started_at, duration_ticks, parallel_group)
        VALUES ($id, $run, $kind, $label, $started, $duration, $group);
        """,
        transaction,
        ("$id", span.SpanId),
        ("$run", span.RunId),
        ("$kind", span.Kind.ToString()),
        ("$label", span.Label),
        ("$started", Stamp(span.StartedAt)),
        ("$duration", span.Duration?.Ticks),
        ("$group", span.ParallelGroup)));

    public IReadOnlyList<TimingSpan> GetSpans(string? runId) => Read(() => Query(
        "SELECT * FROM spans WHERE ($run IS NULL AND run_id IS NULL) OR run_id = $run ORDER BY started_at, rowid;",
        reader =>
        {
            var ticks = NullableLong(reader, "duration_ticks");
            return new TimingSpan(
                Required(reader, "span_id"),
                Text(reader, "run_id"),
                Enumeration<SpanKind>(reader, "kind"),
                Required(reader, "label"),
                Moment(Required(reader, "started_at")),
                ticks is null ? null : TimeSpan.FromTicks(ticks.Value),
                Text(reader, "parallel_group"));
        },
        null,
        ("$run", runId)));

    public void SaveApproval(ApprovalRecord approval) => InTransaction(transaction => Write(
        """
        INSERT INTO approvals (run_id, approval_id, role, kind, title, command, decision, decided_by, requested_at, decided_at)
        VALUES ($run, $approval, $role, $kind, $title, $command, $decision, $by, $requested, $decided);
        """,
        transaction,
        ("$run", approval.RunId),
        ("$approval", approval.ApprovalId),
        ("$role", approval.Role.ToString()),
        ("$kind", approval.Kind.ToString()),
        ("$title", approval.Title),
        ("$command", approval.Command),
        ("$decision", approval.Decision),
        ("$by", approval.DecidedBy),
        ("$requested", Stamp(approval.RequestedAt)),
        ("$decided", Stamp(approval.DecidedAt))));

    /// <summary>What was asked for in a run and how it was answered, in the order in which it was recorded.</summary>
    public IReadOnlyList<ApprovalRecord> GetApprovals(string runId) => Read(() => Query(
        "SELECT * FROM approvals WHERE run_id = $run ORDER BY rowid;",
        reader => new ApprovalRecord(
            Required(reader, "run_id"),
            Required(reader, "approval_id"),
            Enumeration<AgentRole>(reader, "role"),
            Enumeration<ApprovalKind>(reader, "kind"),
            Required(reader, "title"),
            Text(reader, "command"),
            Required(reader, "decision"),
            Required(reader, "decided_by"),
            Moment(Required(reader, "requested_at")),
            Moment(Required(reader, "decided_at"))),
        null,
        ("$run", runId)));

    public IReadOnlyList<RunSummary> ListRuns(string? projectPath, int limit) => Read(() => Query(
        """
        SELECT run_id, task_id, sequence, project_path, request_text, state, disposition, state_reason, created_at, updated_at, model_a, model_b
        FROM runs
        WHERE $key IS NULL OR project_key = $key
        ORDER BY created_at DESC, run_id DESC
        LIMIT $limit;
        """,
        reader => new RunSummary(
            Required(reader, "run_id"),
            Required(reader, "task_id"),
            Integer(reader, "sequence"),
            Required(reader, "project_path"),
            Required(reader, "request_text"),
            Enumeration<RunState>(reader, "state"),
            Enumeration<RunDisposition>(reader, "disposition"),
            Text(reader, "state_reason"),
            Moment(Required(reader, "created_at")),
            Moment(Required(reader, "updated_at")),
            Text(reader, "model_a"),
            Text(reader, "model_b")),
        null,
        ("$key", projectPath is null ? null : ProjectKey(projectPath)),
        ("$limit", Math.Max(0, limit))));

    public IReadOnlyList<RunRecord> MarkOrphanedRuns(Func<int, bool> processIsAlive) => InTransaction(transaction =>
    {
        var active = Enum.GetValues<RunState>().Where(RunStateMachine.IsActive).Select(s => "'" + s + "'");
        var candidates = Query($"SELECT {RunColumns} FROM runs WHERE state IN ({string.Join(", ", active)});", MapRun, transaction);
        var orphaned = new List<RunRecord>();
        var now = _clock.GetUtcNow();
        foreach (var run in candidates)
        {
            if (processIsAlive(run.OwnerProcessId))
            {
                continue;
            }

            var reason = $"YAV stopped while this run was {RunStateMachine.Display(run.State)} (process {run.OwnerProcessId} is gone). "
                + "Nothing is resubmitted until the provider and workspace state have been checked.";
            Write(
                "UPDATE runs SET state = $state, state_reason = $reason, updated_at = $now WHERE run_id = $id AND state = $from;",
                transaction,
                ("$state", RunState.NeedsReconciliation.ToString()),
                ("$reason", reason),
                ("$now", Stamp(now)),
                ("$id", run.RunId),
                ("$from", run.State.ToString()));
            orphaned.Add(run with { State = RunState.NeedsReconciliation, StateReason = reason, UpdatedAt = now });
        }

        return (IReadOnlyList<RunRecord>)orphaned;
    });

    public void Enqueue(QueuedRequest request) => InTransaction(transaction =>
    {
        var key = ProjectKey(request.ProjectPath);
        using var next = Command("SELECT COALESCE(MAX(position), 0) + 1 FROM queue WHERE project_key = $key;", transaction, ("$key", key));
        var position = Convert.ToInt64(next.ExecuteScalar(), System.Globalization.CultureInfo.InvariantCulture);
        Write(
            """
            INSERT INTO queue (queue_id, project_path, project_key, text, is_follow_up, attachments_json, queued_at, position)
            VALUES ($id, $path, $key, $text, $followUp, $attachments, $at, $position);
            """,
            transaction,
            ("$id", request.QueueId),
            ("$path", request.ProjectPath),
            ("$key", key),
            ("$text", request.Text),
            ("$followUp", request.IsFollowUp ? 1 : 0),
            ("$attachments", StorageJson.WriteList(request.Attachments)),
            ("$at", Stamp(request.QueuedAt)),
            ("$position", position));
    });

    public IReadOnlyList<QueuedRequest> GetQueue(string projectPath) => Read(() => Query(
        "SELECT * FROM queue WHERE project_key = $key ORDER BY position;",
        reader => new QueuedRequest(
            Required(reader, "queue_id"),
            Required(reader, "project_path"),
            Required(reader, "text"),
            Flag(reader, "is_follow_up"),
            StorageJson.ReadList(Required(reader, "attachments_json")),
            Moment(Required(reader, "queued_at"))),
        null,
        ("$key", ProjectKey(projectPath))));

    public bool RemoveFromQueue(string queueId) =>
        InTransaction(transaction => Write("DELETE FROM queue WHERE queue_id = $id;", transaction, ("$id", queueId)) > 0);

    public int ClearQueue(string projectPath) =>
        InTransaction(transaction => Write("DELETE FROM queue WHERE project_key = $key;", transaction, ("$key", ProjectKey(projectPath))));

    public RetentionReport ApplyRetention(TimeSpan keepFor, DateTimeOffset now) => InTransaction(transaction =>
    {
        var cutoff = Stamp(now - keepFor);
        var expired = Query(
            """
            SELECT run_id FROM runs
            WHERE updated_at < $cutoff
              AND (state IN ('Completed', 'Failed') OR disposition IN ('Discarded', 'Undone', 'Applied', 'NoChanges'));
            """,
            reader => reader.GetString(0),
            transaction,
            ("$cutoff", cutoff));

        var events = 0;
        foreach (var runId in expired)
        {
            events += DeleteRunRows(runId, transaction);
            _eventCounts.Remove(runId);
        }

        return new RetentionReport(expired.Count, events, 0);
    });

    public bool DeleteRun(string runId) => InTransaction(transaction =>
    {
        var exists = FindRun(runId, transaction) is not null;
        if (exists)
        {
            DeleteRunRows(runId, transaction);
            _eventCounts.Remove(runId);
        }

        return exists;
    });

    private int DeleteRunRows(string runId, SqliteTransaction transaction)
    {
        var events = Write("DELETE FROM run_events WHERE run_id = $run;", transaction, ("$run", runId));
        foreach (var table in new[] { "candidates", "reviews", "gate_results", "waivers", "protected_approvals", "confirmations", "usage", "spans", "approvals" })
        {
            Write($"DELETE FROM {table} WHERE run_id = $run;", transaction, ("$run", runId));
        }

        Write("DELETE FROM runs WHERE run_id = $run;", transaction, ("$run", runId));
        return events;
    }

    private RunRecord? FindRun(string runId, SqliteTransaction? transaction) =>
        Query($"SELECT {RunColumns} FROM runs WHERE run_id = $id;", MapRun, transaction, ("$id", runId)).FirstOrDefault();

    private static RunRecord MapRun(SqliteDataReader reader) => new(
        Required(reader, "run_id"),
        Required(reader, "task_id"),
        Integer(reader, "sequence"),
        Enumeration<RunKind>(reader, "kind"),
        Required(reader, "project_path"),
        Required(reader, "request_text"),
        Integer(reader, "acceptance_version"),
        Required(reader, "profile_hash"),
        Enumeration<RunState>(reader, "state"),
        Enumeration<RunDisposition>(reader, "disposition"),
        Text(reader, "state_reason"),
        Integer(reader, "repair_cycles_used"),
        Text(reader, "current_candidate_id"),
        Moment(Required(reader, "created_at")),
        Moment(Required(reader, "updated_at")),
        Integer(reader, "owner_pid"),
        Required(reader, "yav_version"));

    private static Candidate MapCandidate(SqliteDataReader reader) => new(
        Required(reader, "candidate_id"),
        Required(reader, "run_id"),
        Integer(reader, "sequence"),
        Required(reader, "fingerprint"),
        Required(reader, "baseline_fingerprint"),
        Integer(reader, "acceptance_version"),
        Required(reader, "profile_hash"),
        StorageJson.Read(Required(reader, "changes_json"), StorageJson.Default.ChangeSet),
        StorageJson.ReadList(Required(reader, "protected_json")),
        StorageJson.ReadList(Required(reader, "tests_touched_json")),
        Moment(Required(reader, "frozen_at")));

    private static ReviewResult MapReview(SqliteDataReader reader) => new(
        Required(reader, "review_id"),
        Required(reader, "run_id"),
        Required(reader, "candidate_id"),
        StorageJson.Read(Required(reader, "binding_json"), StorageJson.Default.EvidenceBinding),
        Enumeration<ReviewVerdict>(reader, "verdict"),
        Required(reader, "summary"),
        Required(reader, "coverage"),
        StorageJson.Read(Required(reader, "findings_json"), StorageJson.Default.ListFinding),
        StorageJson.ReadList(Required(reader, "limitations_json")),
        Flag(reader, "output_valid"),
        StorageJson.ReadList(Required(reader, "validation_errors_json")),
        Required(reader, "reviewer_adapter_id"),
        Required(reader, "reviewer_model"),
        Text(reader, "reviewer_session_id"),
        Flag(reader, "source_unchanged"),
        Moment(Required(reader, "started_at")),
        Moment(Required(reader, "completed_at")));
}
