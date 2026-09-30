namespace Yav.Storage;

internal sealed record Migration(int Version, string Name, string Sql);

/// <summary>
/// Schema history. A migration is never edited after it shipped; a change is always a new migration.
/// </summary>
internal static class Migrations
{
    public static readonly IReadOnlyList<Migration> All =
    [
        new Migration(1, "initial", """
            CREATE TABLE tasks (
                task_id TEXT PRIMARY KEY,
                project_path TEXT NOT NULL,
                project_key TEXT NOT NULL,
                created_at TEXT NOT NULL,
                workspace_id TEXT,
                implementer_session_id TEXT,
                implementer_adapter_id TEXT,
                reviewer_session_id TEXT,
                reviewer_adapter_id TEXT
            );

            CREATE TABLE requirements (
                task_id TEXT NOT NULL,
                version INTEGER NOT NULL,
                text TEXT NOT NULL,
                added_at TEXT NOT NULL,
                attachments_json TEXT NOT NULL,
                PRIMARY KEY (task_id, version)
            );

            CREATE TABLE runs (
                run_id TEXT PRIMARY KEY,
                task_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                kind TEXT NOT NULL,
                project_path TEXT NOT NULL,
                project_key TEXT NOT NULL,
                request_text TEXT NOT NULL,
                acceptance_version INTEGER NOT NULL,
                profile_hash TEXT NOT NULL,
                profile_json TEXT NOT NULL,
                model_a TEXT,
                model_b TEXT,
                state TEXT NOT NULL,
                disposition TEXT NOT NULL,
                state_reason TEXT,
                repair_cycles_used INTEGER NOT NULL,
                current_candidate_id TEXT,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL,
                owner_pid INTEGER NOT NULL,
                yav_version TEXT NOT NULL
            );
            CREATE INDEX ix_runs_project ON runs (project_key, created_at);
            CREATE INDEX ix_runs_state ON runs (state);

            CREATE TABLE run_events (
                sequence INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                at TEXT NOT NULL,
                type TEXT NOT NULL,
                stage TEXT NOT NULL,
                summary TEXT NOT NULL,
                detail TEXT
            );
            CREATE INDEX ix_run_events_run ON run_events (run_id, sequence);

            CREATE TABLE candidates (
                candidate_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                sequence INTEGER NOT NULL,
                fingerprint TEXT NOT NULL,
                baseline_fingerprint TEXT NOT NULL,
                acceptance_version INTEGER NOT NULL,
                profile_hash TEXT NOT NULL,
                manifest_id TEXT NOT NULL,
                changes_json TEXT NOT NULL,
                protected_json TEXT NOT NULL,
                tests_touched_json TEXT NOT NULL,
                frozen_at TEXT NOT NULL
            );
            CREATE INDEX ix_candidates_run ON candidates (run_id, sequence);

            CREATE TABLE reviews (
                review_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                candidate_id TEXT NOT NULL,
                binding_json TEXT NOT NULL,
                verdict TEXT NOT NULL,
                summary TEXT NOT NULL,
                coverage TEXT NOT NULL,
                findings_json TEXT NOT NULL,
                limitations_json TEXT NOT NULL,
                output_valid INTEGER NOT NULL,
                validation_errors_json TEXT NOT NULL,
                reviewer_adapter_id TEXT NOT NULL,
                reviewer_model TEXT NOT NULL,
                reviewer_session_id TEXT,
                source_unchanged INTEGER NOT NULL,
                started_at TEXT NOT NULL,
                completed_at TEXT NOT NULL
            );
            CREATE INDEX ix_reviews_run ON reviews (run_id, completed_at);

            CREATE TABLE gate_results (
                result_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                gate_id TEXT NOT NULL,
                gate_title TEXT NOT NULL,
                kind TEXT NOT NULL,
                required INTEGER NOT NULL,
                binding_json TEXT NOT NULL,
                status TEXT NOT NULL,
                exit_code INTEGER,
                command_line TEXT NOT NULL,
                working_directory TEXT NOT NULL,
                started_at TEXT NOT NULL,
                duration_ms INTEGER NOT NULL,
                output_path TEXT,
                output_tail TEXT NOT NULL,
                output_bytes INTEGER NOT NULL,
                limitation TEXT,
                fails_on_baseline INTEGER,
                is_baseline_run INTEGER NOT NULL
            );
            CREATE INDEX ix_gate_results_run ON gate_results (run_id, started_at);

            CREATE TABLE waivers (
                run_id TEXT NOT NULL,
                gate_id TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                reason TEXT NOT NULL,
                granted_at TEXT NOT NULL,
                PRIMARY KEY (run_id, gate_id, candidate_fingerprint)
            );

            CREATE TABLE protected_approvals (
                run_id TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                path TEXT NOT NULL,
                approved_at TEXT NOT NULL,
                PRIMARY KEY (run_id, candidate_fingerprint, path)
            );

            CREATE TABLE confirmations (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                role TEXT NOT NULL,
                setting TEXT NOT NULL,
                requested TEXT,
                effective TEXT,
                status TEXT NOT NULL,
                source TEXT NOT NULL,
                observed_at TEXT NOT NULL
            );
            CREATE INDEX ix_confirmations_run ON confirmations (run_id);

            CREATE TABLE sessions (
                task_id TEXT NOT NULL,
                role TEXT NOT NULL,
                adapter_id TEXT NOT NULL,
                session_id TEXT NOT NULL,
                model TEXT,
                tokens_json TEXT,
                cost_usd TEXT,
                updated_at TEXT NOT NULL,
                PRIMARY KEY (task_id, role)
            );

            CREATE TABLE usage (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                role TEXT NOT NULL,
                adapter_id TEXT NOT NULL,
                session_id TEXT,
                model TEXT,
                tokens_json TEXT NOT NULL,
                run_share_known INTEGER NOT NULL,
                cost_usd TEXT,
                cost_provenance TEXT NOT NULL,
                turns INTEGER NOT NULL,
                retries INTEGER NOT NULL,
                billing_route TEXT NOT NULL,
                source TEXT NOT NULL,
                observed_at TEXT NOT NULL
            );
            CREATE INDEX ix_usage_run ON usage (run_id);
            CREATE INDEX ix_usage_time ON usage (observed_at);

            CREATE TABLE spans (
                span_id TEXT PRIMARY KEY,
                run_id TEXT,
                kind TEXT NOT NULL,
                label TEXT NOT NULL,
                started_at TEXT NOT NULL,
                duration_ticks INTEGER,
                parallel_group TEXT
            );
            CREATE INDEX ix_spans_run ON spans (run_id, started_at);

            CREATE TABLE approvals (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                run_id TEXT NOT NULL,
                approval_id TEXT NOT NULL,
                role TEXT NOT NULL,
                kind TEXT NOT NULL,
                title TEXT NOT NULL,
                command TEXT,
                decision TEXT NOT NULL,
                decided_by TEXT NOT NULL,
                requested_at TEXT NOT NULL,
                decided_at TEXT NOT NULL
            );
            CREATE INDEX ix_approvals_run ON approvals (run_id);

            CREATE TABLE queue (
                queue_id TEXT PRIMARY KEY,
                project_path TEXT NOT NULL,
                project_key TEXT NOT NULL,
                text TEXT NOT NULL,
                is_follow_up INTEGER NOT NULL,
                attachments_json TEXT NOT NULL,
                queued_at TEXT NOT NULL,
                position INTEGER NOT NULL
            );
            CREATE INDEX ix_queue_project ON queue (project_key, position);

            CREATE TABLE project_trust (
                project_key TEXT PRIMARY KEY,
                project_path TEXT NOT NULL,
                trusted INTEGER NOT NULL,
                trusted_at TEXT,
                config_hash TEXT,
                config_json TEXT,
                config_trusted_at TEXT
            );

            CREATE TABLE acknowledgements (
                kind TEXT NOT NULL,
                subject TEXT NOT NULL,
                statement TEXT NOT NULL,
                acknowledged_at TEXT NOT NULL,
                PRIMARY KEY (kind, subject)
            );

            CREATE TABLE journals (
                journal_id TEXT PRIMARY KEY,
                run_id TEXT NOT NULL,
                candidate_id TEXT NOT NULL,
                candidate_fingerprint TEXT NOT NULL,
                project_path TEXT NOT NULL,
                project_key TEXT NOT NULL,
                state TEXT NOT NULL,
                entries_json TEXT NOT NULL,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            CREATE INDEX ix_journals_project ON journals (project_key, updated_at);
            CREATE INDEX ix_journals_run ON journals (run_id);
            """),
    ];
}
