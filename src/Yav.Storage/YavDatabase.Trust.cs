using Yav.Core.Ports;

namespace Yav.Storage;

public sealed partial class YavDatabase : IProjectTrustStore, IJournalStore
{
    private const string RouteKind = "route";
    private const string SpeedKind = "paid-speed";
    private const string InPlaceKind = "in-place";
    private const string GapsKind = "workspace-gaps";

    public bool IsProjectTrusted(string projectPath) => Read(() => Query(
        "SELECT trusted FROM project_trust WHERE project_key = $key;",
        reader => reader.GetInt64(0) != 0,
        null,
        ("$key", ProjectKey(projectPath))).FirstOrDefault());

    public void SetProjectTrusted(string projectPath, bool trusted) => InTransaction(transaction => Write(
        """
        INSERT INTO project_trust (project_key, project_path, trusted, trusted_at)
        VALUES ($key, $path, $trusted, $at)
        ON CONFLICT (project_key) DO UPDATE SET trusted = excluded.trusted, trusted_at = excluded.trusted_at;
        """,
        transaction,
        ("$key", ProjectKey(projectPath)),
        ("$path", Path.GetFullPath(projectPath)),
        ("$trusted", trusted ? 1 : 0),
        ("$at", trusted ? Stamp(_clock.GetUtcNow()) : null)));

    public (string Hash, string Json)? GetTrustedConfiguration(string projectPath) => Read(() =>
    {
        var rows = Query(
            "SELECT config_hash, config_json FROM project_trust WHERE project_key = $key AND config_hash IS NOT NULL AND config_json IS NOT NULL;",
            reader => (Hash: reader.GetString(0), Json: reader.GetString(1)),
            null,
            ("$key", ProjectKey(projectPath)));
        return rows.Count == 0 ? ((string Hash, string Json)?)null : rows[0];
    });

    public void SaveTrustedConfiguration(string projectPath, string hash, string json) => InTransaction(transaction => Write(
        """
        INSERT INTO project_trust (project_key, project_path, trusted, config_hash, config_json, config_trusted_at)
        VALUES ($key, $path, 0, $hash, $json, $at)
        ON CONFLICT (project_key) DO UPDATE SET
            config_hash = excluded.config_hash, config_json = excluded.config_json, config_trusted_at = excluded.config_trusted_at;
        """,
        transaction,
        ("$key", ProjectKey(projectPath)),
        ("$path", Path.GetFullPath(projectPath)),
        ("$hash", hash),
        ("$json", json),
        ("$at", Stamp(_clock.GetUtcNow()))));

    public bool IsRouteAcknowledged(string routeKey) => IsAcknowledged(RouteKind, routeKey);

    public void AcknowledgeRoute(string routeKey, string statement) => Acknowledge(RouteKind, routeKey, statement);

    public bool IsPaidSpeedAuthorized(string routeKey) => IsAcknowledged(SpeedKind, routeKey);

    public void AuthorizePaidSpeed(string routeKey, string statement) => Acknowledge(SpeedKind, routeKey, statement);

    public bool IsInPlaceAcknowledged(string projectPath) => IsAcknowledged(InPlaceKind, ProjectKey(projectPath));

    public void AcknowledgeInPlace(string projectPath, string statement) => Acknowledge(InPlaceKind, ProjectKey(projectPath), statement);

    public bool AreGapsAcknowledged(string projectPath, string gapsFingerprint) =>
        IsAcknowledged(GapsKind, ProjectKey(projectPath) + "|" + gapsFingerprint);

    public void AcknowledgeGaps(string projectPath, string gapsFingerprint, string statement) =>
        Acknowledge(GapsKind, ProjectKey(projectPath) + "|" + gapsFingerprint, statement);

    /// <summary>Withdraws an acknowledgement, so the next run asks again.</summary>
    public bool Withdraw(string kind, string subject) => InTransaction(transaction =>
        Write("DELETE FROM acknowledgements WHERE kind = $kind AND subject = $subject;", transaction, ("$kind", kind), ("$subject", subject)) > 0);

    public IReadOnlyList<(string Kind, string Subject, string Statement, DateTimeOffset At)> ListAcknowledgements() => Read(() => Query(
        "SELECT kind, subject, statement, acknowledged_at FROM acknowledgements ORDER BY acknowledged_at;",
        reader => (reader.GetString(0), reader.GetString(1), reader.GetString(2), Moment(reader.GetString(3))),
        null));

    public void Save(ApplyJournal journal) => InTransaction(transaction => Write(
        """
        INSERT INTO journals (journal_id, run_id, candidate_id, candidate_fingerprint, project_path, project_key, state, entries_json,
            created_at, updated_at)
        VALUES ($id, $run, $candidate, $fingerprint, $path, $key, $state, $entries, $created, $updated)
        ON CONFLICT (journal_id) DO UPDATE SET state = excluded.state, entries_json = excluded.entries_json, updated_at = excluded.updated_at;
        """,
        transaction,
        ("$id", journal.JournalId),
        ("$run", journal.RunId),
        ("$candidate", journal.CandidateId),
        ("$fingerprint", journal.CandidateFingerprint),
        ("$path", journal.ProjectPath),
        ("$key", ProjectKey(journal.ProjectPath)),
        ("$state", journal.State.ToString()),
        ("$entries", StorageJson.Write(journal.Entries.ToList(), StorageJson.Default.ListJournalEntry)),
        ("$created", Stamp(journal.CreatedAt)),
        ("$updated", Stamp(_clock.GetUtcNow()))));

    public ApplyJournal? Find(string journalId) => Read(() =>
        Query("SELECT * FROM journals WHERE journal_id = $id;", MapJournal, null, ("$id", journalId)).FirstOrDefault());

    public ApplyJournal? FindLatestForProject(string projectPath) => Read(() => Query(
        "SELECT * FROM journals WHERE project_key = $key AND state = 'Committed' ORDER BY updated_at DESC, rowid DESC LIMIT 1;",
        MapJournal,
        null,
        ("$key", ProjectKey(projectPath))).FirstOrDefault());

    public ApplyJournal? FindForRun(string runId) => Read(() => Query(
        "SELECT * FROM journals WHERE run_id = $run ORDER BY updated_at DESC, rowid DESC LIMIT 1;",
        MapJournal,
        null,
        ("$run", runId)).FirstOrDefault());

    public IReadOnlyList<ApplyJournal> ListForProject(string projectPath, int limit) => Read(() => Query(
        "SELECT * FROM journals WHERE project_key = $key ORDER BY updated_at DESC, rowid DESC LIMIT $limit;",
        MapJournal,
        null,
        ("$key", ProjectKey(projectPath)),
        ("$limit", Math.Max(0, limit))));

    public IReadOnlyList<ApplyJournal> FindUnfinished() => Read(() => Query(
        "SELECT * FROM journals WHERE state IN ('Prepared', 'InProgress', 'Interrupted') ORDER BY created_at;",
        MapJournal,
        null));

    private bool IsAcknowledged(string kind, string subject) => Read(() => Query(
        "SELECT 1 FROM acknowledgements WHERE kind = $kind AND subject = $subject;",
        _ => true,
        null,
        ("$kind", kind),
        ("$subject", subject)).Count > 0);

    private void Acknowledge(string kind, string subject, string statement) => InTransaction(transaction => Write(
        """
        INSERT INTO acknowledgements (kind, subject, statement, acknowledged_at) VALUES ($kind, $subject, $statement, $at)
        ON CONFLICT (kind, subject) DO UPDATE SET statement = excluded.statement, acknowledged_at = excluded.acknowledged_at;
        """,
        transaction,
        ("$kind", kind),
        ("$subject", subject),
        ("$statement", statement),
        ("$at", Stamp(_clock.GetUtcNow()))));

    private static ApplyJournal MapJournal(Microsoft.Data.Sqlite.SqliteDataReader reader) => new(
        Required(reader, "journal_id"),
        Required(reader, "run_id"),
        Required(reader, "candidate_id"),
        Required(reader, "candidate_fingerprint"),
        Required(reader, "project_path"),
        Enumeration<JournalState>(reader, "state"),
        StorageJson.Read(Required(reader, "entries_json"), StorageJson.Default.ListJournalEntry),
        Moment(Required(reader, "created_at")),
        Moment(Required(reader, "updated_at")));
}
