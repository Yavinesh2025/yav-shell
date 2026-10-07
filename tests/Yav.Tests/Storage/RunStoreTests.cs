using Microsoft.Data.Sqlite;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Core.Timing;
using Yav.Storage;
using Yav.Tests.Support;

namespace Yav.Tests.Storage;

public sealed class RunStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new("db");
    private readonly ManualClock _clock = new();
    private readonly YavDatabase _database;

    public RunStoreTests()
    {
        _database = YavDatabase.Open(_directory.File("yav.db"), _clock);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }

    private RunRecord NewRun(string runId = "20260929-100000-aaaaa", RunState state = RunState.Preparing, int ownerPid = 4242, string project = @"C:\Projects\MyApp") => new(
        RunId: runId,
        TaskId: "task-1",
        Sequence: 1,
        Kind: RunKind.CodingTask,
        ProjectPath: project,
        RequestText: "Fix the login bug and add regression tests.",
        AcceptanceVersion: 1,
        ProfileHash: Builders.ProfileHash,
        State: state,
        Disposition: RunDisposition.Pending,
        StateReason: null,
        RepairCyclesUsed: 0,
        CurrentCandidateId: null,
        CreatedAt: _clock.GetUtcNow(),
        UpdatedAt: _clock.GetUtcNow(),
        OwnerProcessId: ownerPid,
        YavVersion: "0.1.0");

    private RunRecord Create(string runId = "20260929-100000-aaaaa", RunState state = RunState.Preparing, int ownerPid = 4242, string project = @"C:\Projects\MyApp")
    {
        var run = NewRun(runId, state, ownerPid, project);
        _database.CreateRun(run, Builders.Profile());
        return run;
    }

    [Fact]
    public void A_run_and_its_profile_are_stored_together_and_read_back()
    {
        Create();

        var run = _database.FindRun("20260929-100000-aaaaa");
        var profile = _database.GetProfile("20260929-100000-aaaaa");

        Assert.NotNull(run);
        Assert.Equal("Fix the login bug and add regression tests.", run.RequestText);
        Assert.Equal(RunState.Preparing, run.State);
        Assert.Equal(Builders.Profile().ComputeHash(), profile!.ComputeHash());
    }

    [Fact]
    public void The_request_text_is_kept_exactly_including_line_breaks_and_unicode()
    {
        var text = "Fix this:\r\n  1. ünï 日本 🙂\r\n  2. path C:\\a b\\c \"quoted\" 'single'\r\n";
        _database.CreateRun(NewRun() with { RequestText = text }, Builders.Profile());

        Assert.Equal(text, _database.FindRun("20260929-100000-aaaaa")!.RequestText);
    }

    [Fact]
    public void An_allowed_transition_is_stored_with_its_reason()
    {
        Create();
        _clock.Advance(TimeSpan.FromSeconds(5));

        var updated = _database.Transition("20260929-100000-aaaaa", RunState.Preparing, RunState.Implementing, "workspace ready");

        Assert.Equal(RunState.Implementing, updated.State);
        Assert.Equal("workspace ready", updated.StateReason);
        Assert.Equal(RunState.Implementing, _database.FindRun("20260929-100000-aaaaa")!.State);
        Assert.Equal(_clock.GetUtcNow(), _database.FindRun("20260929-100000-aaaaa")!.UpdatedAt);
    }

    [Fact]
    public void A_transition_that_skips_checking_is_refused_and_the_state_is_unchanged()
    {
        Create(state: RunState.Implementing);

        var error = Assert.Throws<InvalidRunTransitionException>(() =>
            _database.Transition("20260929-100000-aaaaa", RunState.Implementing, RunState.ReadyToApply, "skip"));

        Assert.Equal(RunState.ReadyToApply, error.To);
        Assert.Equal(RunState.Implementing, _database.FindRun("20260929-100000-aaaaa")!.State);
    }

    [Fact]
    public void A_transition_from_a_state_the_run_is_no_longer_in_is_refused()
    {
        Create(state: RunState.Checking);

        Assert.Throws<InvalidRunTransitionException>(() =>
            _database.Transition("20260929-100000-aaaaa", RunState.Implementing, RunState.Checking, "late"));

        Assert.Equal(RunState.Checking, _database.FindRun("20260929-100000-aaaaa")!.State);
    }

    [Fact]
    public void A_run_whose_process_is_gone_needs_reconciliation()
    {
        Create("20260929-100000-aaaaa", RunState.Implementing, ownerPid: 111);
        Create("20260929-100001-bbbbb", RunState.Checking, ownerPid: 222);
        Create("20260929-100002-ccccc", RunState.ReadyToApply, ownerPid: 111);

        var orphaned = _database.MarkOrphanedRuns(pid => pid == 222);

        Assert.Equal(["20260929-100000-aaaaa"], orphaned.Select(r => r.RunId));
        Assert.Equal(RunState.NeedsReconciliation, _database.FindRun("20260929-100000-aaaaa")!.State);
        Assert.Equal(RunState.Checking, _database.FindRun("20260929-100001-bbbbb")!.State);
        Assert.Equal(RunState.ReadyToApply, _database.FindRun("20260929-100002-ccccc")!.State);
    }

    [Fact]
    public void A_candidate_with_its_changes_is_read_back()
    {
        Create();
        var candidate = Builders.Candidate(protectedPaths: ["yav.project.json"]) with { RunId = "20260929-100000-aaaaa" };

        _database.SaveCandidate(candidate, "manifest-7");
        var stored = _database.FindCandidate("c-1");

        Assert.NotNull(stored);
        Assert.Equal(Builders.Fingerprint, stored.Fingerprint);
        Assert.Equal(["yav.project.json"], stored.ProtectedPathsTouched);
        var file = Assert.Single(stored.Changes.Files);
        Assert.Equal("src/login.cs", file.Path);
        Assert.Equal(ChangeKind.Modified, file.Kind);
        Assert.Equal(candidate.FrozenAt, stored.FrozenAt);
    }

    [Fact]
    public void A_review_with_findings_is_read_back()
    {
        Create();
        var review = Builders.Review(ReviewVerdict.ChangesRequired, findings: [Builders.Finding(), Builders.Finding(optional: true, title: "Rename")])
            with { RunId = "20260929-100000-aaaaa" };

        _database.SaveReview(review);
        var stored = _database.GetLatestReview("20260929-100000-aaaaa", "c-1");

        Assert.NotNull(stored);
        Assert.Equal(ReviewVerdict.ChangesRequired, stored.Verdict);
        Assert.Equal(2, stored.Findings.Count);
        Assert.Equal("Null user is dereferenced", stored.Findings[0].Title);
        Assert.Equal(42, stored.Findings[0].Line);
        Assert.True(stored.Findings[1].Optional);
        Assert.Equal(Builders.Binding(), stored.Binding);
    }

    [Fact]
    public void Gate_results_waivers_and_confirmations_are_read_back()
    {
        Create();
        var result = Builders.GateResult(status: GateStatus.Failed, exitCode: 3, failsOnBaseline: true) with { RunId = "20260929-100000-aaaaa" };
        var waiver = new GateWaiver("20260929-100000-aaaaa", "test", Builders.Fingerprint, "Known flaky", _clock.GetUtcNow());
        var confirmation = Builders.Confirmed(AgentRole.Reviewer, ProfileSettings.Effort, "max");

        _database.SaveGateResult(result);
        _database.SaveWaiver(waiver);
        _database.SaveConfirmation("20260929-100000-aaaaa", confirmation);
        _database.ApproveProtectedPath("20260929-100000-aaaaa", Builders.Fingerprint, "tests/acceptance/a.spec.ts");

        var storedResult = Assert.Single(_database.GetGateResults("20260929-100000-aaaaa"));
        Assert.Equal(GateStatus.Failed, storedResult.Status);
        Assert.Equal(3, storedResult.ExitCode);
        Assert.True(storedResult.FailsOnBaseline);
        Assert.Equal(waiver, Assert.Single(_database.GetWaivers("20260929-100000-aaaaa")));
        Assert.Equal(confirmation, Assert.Single(_database.GetConfirmations("20260929-100000-aaaaa")));
        Assert.Equal(["tests/acceptance/a.spec.ts"], _database.GetApprovedProtectedPaths("20260929-100000-aaaaa", Builders.Fingerprint));
        Assert.Empty(_database.GetApprovedProtectedPaths("20260929-100000-aaaaa", Builders.OtherFingerprint));
    }

    [Fact]
    public void Events_come_back_in_order_and_the_oldest_are_dropped_beyond_the_limit()
    {
        _database.MaxEventsPerRun = 5;
        Create();
        for (var i = 1; i <= 8; i++)
        {
            _database.AppendEvent(new RunEventRecord(0, "20260929-100000-aaaaa", _clock.GetUtcNow(), "note", Stages.CodeA, $"event {i}", null));
        }

        var events = _database.GetEvents("20260929-100000-aaaaa", 100);

        Assert.Equal(["event 4", "event 5", "event 6", "event 7", "event 8"], events.Select(e => e.Summary));
    }

    [Fact]
    public void Requirements_keep_their_order_and_exact_text()
    {
        _database.SaveTask(new TaskContext("task-1", @"C:\Projects\MyApp", _clock.GetUtcNow(), null, null, null, null, null));
        _database.SaveRequirement("task-1", new Requirement(1, "Fix the login bug.", _clock.GetUtcNow(), []));
        _database.SaveRequirement("task-1", new Requirement(2, "Also cover the\nempty password case.", _clock.GetUtcNow(), [@"C:\notes\spec.md"]));

        var requirements = _database.GetRequirements("task-1");

        Assert.Equal([1, 2], requirements.Select(r => r.Version));
        Assert.Equal("Also cover the\nempty password case.", requirements[1].Text);
        Assert.Equal([@"C:\notes\spec.md"], requirements[1].Attachments);
    }

    [Fact]
    public void A_task_keeps_its_session_identifiers_for_later_follow_ups()
    {
        var task = new TaskContext("task-1", @"C:\Projects\MyApp", _clock.GetUtcNow(), "ws-1", "thread-a", "codex-app-server", "thread-b", "claude-cli");
        _database.SaveTask(task);
        _database.SaveSession(new SessionRecord("task-1", AgentRole.Implementer, "codex-app-server", "thread-a", "model-a", new TokenCounts(900, 100, null, 50, 20), 1.25m, _clock.GetUtcNow()));

        var stored = _database.FindTask("task-1");
        var session = _database.FindSession("task-1", AgentRole.Implementer);

        Assert.Equal(task, stored);
        Assert.Equal("thread-a", session!.SessionId);
        Assert.Equal(900, session.CumulativeTokens!.UncachedInput);
        Assert.Null(session.CumulativeTokens.CacheWrite);
        Assert.Equal(1.25m, session.CumulativeCostUsd);
        Assert.Null(_database.FindSession("task-1", AgentRole.Reviewer));
    }

    [Fact]
    public void Usage_that_was_not_reported_stays_unavailable_after_storage()
    {
        Create();
        _database.SaveUsage(new UsageRecord(
            "20260929-100000-aaaaa", AgentRole.Reviewer, "claude-cli", "s-1", "model-b", TokenCounts.Unavailable, false, null,
            ValueProvenance.Unavailable, 1, 0, "Anthropic API key", "result message", _clock.GetUtcNow()));

        var usage = Assert.Single(_database.GetUsage("20260929-100000-aaaaa", null));

        Assert.Null(usage.Tokens.Total);
        Assert.Null(usage.Tokens.Output);
        Assert.Null(usage.ProviderCostUsd);
        Assert.False(usage.RunShareKnown);
    }

    [Fact]
    public void Spans_are_read_back_with_their_duration_and_group()
    {
        Create();
        var span = new TimingSpan("s-1", "20260929-100000-aaaaa", SpanKind.Review, "Model B review", _clock.GetUtcNow(), TimeSpan.FromMilliseconds(61_500), "check-1");

        _database.SaveSpan(span);

        Assert.Equal(span, Assert.Single(_database.GetSpans("20260929-100000-aaaaa")));
    }

    [Fact]
    public void The_queue_is_per_project_and_keeps_its_order()
    {
        _database.Enqueue(new QueuedRequest("q-1", @"C:\Projects\MyApp", "first", false, [], _clock.GetUtcNow()));
        _clock.Advance(TimeSpan.FromSeconds(1));
        _database.Enqueue(new QueuedRequest("q-2", @"C:\Projects\MyApp", "second", true, ["a.md"], _clock.GetUtcNow()));
        _database.Enqueue(new QueuedRequest("q-3", @"C:\Projects\Other", "elsewhere", false, [], _clock.GetUtcNow()));

        Assert.Equal(["first", "second"], _database.GetQueue(@"c:\projects\myapp\").Select(q => q.Text));
        Assert.True(_database.RemoveFromQueue("q-1"));
        Assert.False(_database.RemoveFromQueue("q-1"));
        Assert.Equal(1, _database.ClearQueue(@"C:\Projects\MyApp"));
        Assert.Single(_database.GetQueue(@"C:\Projects\Other"));
    }

    [Fact]
    public void History_lists_the_newest_run_first_and_can_be_limited_to_a_project()
    {
        Create("20260929-100000-aaaaa");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Create("20260929-100100-bbbbb", project: @"C:\Projects\Other");
        _clock.Advance(TimeSpan.FromMinutes(1));
        Create("20260929-100200-ccccc");

        var all = _database.ListRuns(null, 10);
        var mine = _database.ListRuns(@"C:\Projects\MyApp", 10);

        Assert.Equal(["20260929-100200-ccccc", "20260929-100100-bbbbb", "20260929-100000-aaaaa"], all.Select(r => r.RunId));
        Assert.Equal(["20260929-100200-ccccc", "20260929-100000-aaaaa"], mine.Select(r => r.RunId));
        Assert.Equal("model-a", all[0].ModelA);
        Assert.Equal("model-b", all[0].ModelB);
    }

    [Fact]
    public void Retention_removes_old_finished_runs_and_keeps_unfinished_ones()
    {
        Create("20260801-100000-aaaaa", RunState.Completed);
        Create("20260801-100001-bbbbb", RunState.ReadyToApply);
        _database.AppendEvent(new RunEventRecord(0, "20260801-100000-aaaaa", _clock.GetUtcNow(), "note", Stages.Done, "old", null));
        _clock.Advance(TimeSpan.FromDays(40));
        Create("20260910-100000-ccccc", RunState.Completed);

        var report = _database.ApplyRetention(TimeSpan.FromDays(30), _clock.GetUtcNow());

        Assert.Equal(1, report.RunsRemoved);
        Assert.Null(_database.FindRun("20260801-100000-aaaaa"));
        Assert.Empty(_database.GetEvents("20260801-100000-aaaaa", 10));
        Assert.NotNull(_database.FindRun("20260801-100001-bbbbb"));
        Assert.NotNull(_database.FindRun("20260910-100000-ccccc"));
    }

    [Fact]
    public void Deleting_a_run_removes_everything_stored_for_it()
    {
        Create();
        _database.SaveCandidate(Builders.Candidate() with { RunId = "20260929-100000-aaaaa" }, "m");
        _database.SaveReview(Builders.Review() with { RunId = "20260929-100000-aaaaa" });
        _database.SaveGateResult(Builders.GateResult() with { RunId = "20260929-100000-aaaaa" });

        Assert.True(_database.DeleteRun("20260929-100000-aaaaa"));

        Assert.Null(_database.FindRun("20260929-100000-aaaaa"));
        Assert.Null(_database.FindCandidate("c-1"));
        Assert.Empty(_database.GetReviews("20260929-100000-aaaaa"));
        Assert.Empty(_database.GetGateResults("20260929-100000-aaaaa"));
        Assert.False(_database.DeleteRun("20260929-100000-aaaaa"));
    }
}

public sealed class DatabaseLifecycleTests : IDisposable
{
    private readonly TempDirectory _directory = new("dbl");
    private readonly ManualClock _clock = new();

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }

    [Fact]
    public void Data_survives_closing_and_reopening()
    {
        var path = _directory.File("yav.db");
        using (var first = YavDatabase.Open(path, _clock))
        {
            first.SetProjectTrusted(@"C:\Projects\MyApp", true);
        }

        using var second = YavDatabase.Open(path, _clock);

        Assert.True(second.IsProjectTrusted(@"c:\projects\myapp"));
        Assert.Equal(YavDatabase.LatestSchemaVersion, second.SchemaVersion);
    }

    [Fact]
    public void A_database_from_a_newer_version_is_not_opened()
    {
        var path = _directory.File("yav.db");
        YavDatabase.Open(path, _clock).Dispose();
        using (var raw = new SqliteConnection($"Data Source={path};Pooling=False"))
        {
            raw.Open();
            using var command = raw.CreateCommand();
            command.CommandText = $"INSERT INTO schema_migrations (version, name, applied_at) VALUES ({YavDatabase.LatestSchemaVersion + 1}, 'future', '2030-01-01T00:00:00Z')";
            command.ExecuteNonQuery();
        }

        var error = Assert.Throws<DatabaseVersionException>(() => YavDatabase.Open(path, _clock));

        Assert.Equal(YavDatabase.LatestSchemaVersion + 1, error.FoundVersion);
    }

    [Fact]
    public void Two_connections_to_the_same_file_see_each_others_writes()
    {
        var path = _directory.File("yav.db");
        using var first = YavDatabase.Open(path, _clock);
        using var second = YavDatabase.Open(path, _clock);

        first.AcknowledgeRoute("codex-app-server:Subscription:default", "I understand this uses my ChatGPT plan.");

        Assert.True(second.IsRouteAcknowledged("codex-app-server:Subscription:default"));
    }
}

public sealed class TrustStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new("trust");
    private readonly YavDatabase _database;

    public TrustStoreTests()
    {
        _database = YavDatabase.Open(_directory.File("yav.db"), new ManualClock());
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }

    [Fact]
    public void A_project_is_untrusted_until_the_user_trusts_it()
    {
        Assert.False(_database.IsProjectTrusted(@"C:\Projects\New"));

        _database.SetProjectTrusted(@"C:\Projects\New\", true);

        Assert.True(_database.IsProjectTrusted(@"C:\PROJECTS\new"));
        Assert.False(_database.IsProjectTrusted(@"C:\Projects\New\sub"));
    }

    [Fact]
    public void Trust_can_be_withdrawn()
    {
        _database.SetProjectTrusted(@"C:\Projects\New", true);

        _database.SetProjectTrusted(@"C:\Projects\New", false);

        Assert.False(_database.IsProjectTrusted(@"C:\Projects\New"));
    }

    [Fact]
    public void The_approved_gate_configuration_is_kept_by_content()
    {
        Assert.Null(_database.GetTrustedConfiguration(@"C:\Projects\MyApp"));

        _database.SaveTrustedConfiguration(@"C:\Projects\MyApp", "hash-1", "{\"gates\":[]}");

        var stored = _database.GetTrustedConfiguration(@"C:\Projects\MyApp");
        Assert.Equal("hash-1", stored!.Value.Hash);
        Assert.Equal("{\"gates\":[]}", stored.Value.Json);
    }

    [Fact]
    public void Acknowledgements_are_per_route_and_per_kind()
    {
        _database.AcknowledgeRoute("claude-cli:ApiKey:firstParty", "Billed per token to my API account.");

        Assert.True(_database.IsRouteAcknowledged("claude-cli:ApiKey:firstParty"));
        Assert.False(_database.IsRouteAcknowledged("claude-cli:Subscription:firstParty"));
        Assert.False(_database.IsPaidSpeedAuthorized("claude-cli:ApiKey:firstParty"));
        Assert.False(_database.IsInPlaceAcknowledged(@"C:\Projects\MyApp"));
    }

    [Fact]
    public void Paid_speed_and_in_place_execution_need_their_own_authorization()
    {
        _database.AuthorizePaidSpeed("codex-app-server:Subscription:default", "Fast mode consumes credits at a higher rate.");
        _database.AcknowledgeInPlace(@"C:\Projects\MyApp", "Weaker protection accepted.");

        Assert.True(_database.IsPaidSpeedAuthorized("codex-app-server:Subscription:default"));
        Assert.False(_database.IsRouteAcknowledged("codex-app-server:Subscription:default"));
        Assert.True(_database.IsInPlaceAcknowledged(@"c:\projects\myapp\"));
    }

    [Fact]
    public void Accepting_what_is_absent_from_the_isolated_copy_covers_exactly_those_gaps_of_that_project()
    {
        _database.AcknowledgeGaps(@"C:\Projects\MyApp", "gaps-1", "node_modules is absent from the isolated workspace.");

        Assert.True(_database.AreGapsAcknowledged(@"c:\projects\myapp\", "gaps-1"));
        Assert.False(_database.AreGapsAcknowledged(@"C:\Projects\MyApp", "gaps-2"));
        Assert.False(_database.AreGapsAcknowledged(@"C:\Projects\Other", "gaps-1"));
        Assert.False(_database.IsInPlaceAcknowledged(@"C:\Projects\MyApp"));
    }

    [Fact]
    public void Accepting_the_review_alone_holds_for_that_project_until_it_is_withdrawn()
    {
        _database.AcknowledgeInPlace(@"C:\Projects\MyApp", "Weaker protection accepted.");
        _database.AcknowledgeGaps(@"C:\Projects\MyApp", "gaps-1", "node_modules is absent from the isolated workspace.");
        Assert.False(_database.IsReviewOnlyAccepted(@"C:\Projects\MyApp"));
        Assert.False(_database.WithdrawReviewOnly(@"C:\Projects\MyApp"));

        _database.AcceptReviewOnly(@"C:\Projects\MyApp", "Accepted on the review of Model B alone: the project has no approved check.");

        Assert.True(_database.IsReviewOnlyAccepted(@"c:\projects\myapp\"));
        Assert.False(_database.IsReviewOnlyAccepted(@"C:\Projects\Other"));
        Assert.Contains(_database.ListAcknowledgements(), a => a.Kind == "review-only" && a.Statement.StartsWith("Accepted on the review of Model B alone", StringComparison.Ordinal));

        Assert.True(_database.WithdrawReviewOnly(@"c:\projects\myapp\"));
        Assert.False(_database.IsReviewOnlyAccepted(@"C:\Projects\MyApp"));
        Assert.False(_database.WithdrawReviewOnly(@"C:\Projects\MyApp"));

        // Withdrawing it withdraws nothing else.
        Assert.True(_database.IsInPlaceAcknowledged(@"C:\Projects\MyApp"));
        Assert.True(_database.AreGapsAcknowledged(@"C:\Projects\MyApp", "gaps-1"));
    }

    [Fact]
    public void When_the_acceptance_of_the_review_alone_was_withdrawn_is_kept_for_that_project()
    {
        var clock = new ManualClock();
        using var directory = new TempDirectory("withdrawn");
        using var database = YavDatabase.Open(directory.File("yav.db"), clock);

        // Nothing to withdraw: nothing is recorded.
        Assert.False(database.WithdrawReviewOnly(@"C:\Projects\MyApp"));
        Assert.Null(database.ReviewOnlyWithdrawnAt(@"C:\Projects\MyApp"));

        database.AcceptReviewOnly(@"C:\Projects\MyApp", "Accepted on the review of Model B alone.");
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(database.WithdrawReviewOnly(@"c:\projects\myapp\"));
        Assert.Equal(clock.GetUtcNow(), database.ReviewOnlyWithdrawnAt(@"C:\Projects\MyApp"));
        Assert.Null(database.ReviewOnlyWithdrawnAt(@"C:\Projects\Other"));

        // Accepted once more and withdrawn again: the later withdrawal counts.
        database.AcceptReviewOnly(@"C:\Projects\MyApp", "Accepted on the review of Model B alone.");
        clock.Advance(TimeSpan.FromMinutes(5));
        Assert.True(database.WithdrawReviewOnly(@"C:\Projects\MyApp"));
        Assert.Equal(clock.GetUtcNow(), database.ReviewOnlyWithdrawnAt(@"C:\Projects\MyApp"));

        // A withdrawal is not an acknowledgement, and does not make the acceptance count.
        Assert.False(database.IsReviewOnlyAccepted(@"C:\Projects\MyApp"));
        Assert.DoesNotContain(database.ListAcknowledgements(), a => a.Kind.StartsWith("review-only", StringComparison.Ordinal));
        database.Dispose();
        SqliteConnection.ClearAllPools();
    }
}

public sealed class JournalStoreTests : IDisposable
{
    private readonly TempDirectory _directory = new("journal");
    private readonly ManualClock _clock = new();
    private readonly YavDatabase _database;

    public JournalStoreTests()
    {
        _database = YavDatabase.Open(_directory.File("yav.db"), _clock);
    }

    public void Dispose()
    {
        _database.Dispose();
        SqliteConnection.ClearAllPools();
        _directory.Dispose();
    }

    private ApplyJournal Journal(string id, JournalState state, string run = "run-1", string project = @"C:\Projects\MyApp") => new(
        id, run, "c-1", Builders.Fingerprint, project, state,
        [
            new JournalEntry(0, "src/login.cs", ChangeKind.Modified, "pre-hash", "post-hash", true),
            new JournalEntry(1, "src/new file.cs", ChangeKind.Added, null, "post-2", false),
            new JournalEntry(2, "old.txt", ChangeKind.Deleted, "pre-3", null, false),
        ],
        _clock.GetUtcNow(), _clock.GetUtcNow());

    [Fact]
    public void A_journal_is_read_back_with_every_entry()
    {
        var journal = Journal("j-1", JournalState.InProgress);

        _database.Save(journal);
        var stored = _database.Find("j-1");

        Assert.NotNull(stored);
        Assert.Equal(JournalState.InProgress, stored.State);
        Assert.Equal(journal.Entries, stored.Entries);
    }

    [Fact]
    public void Saving_again_replaces_the_stored_state()
    {
        _database.Save(Journal("j-1", JournalState.InProgress));

        _database.Save(Journal("j-1", JournalState.Committed));

        Assert.Equal(JournalState.Committed, _database.Find("j-1")!.State);
    }

    [Fact]
    public void Only_journals_that_did_not_finish_are_listed_for_reconciliation()
    {
        _database.Save(Journal("j-1", JournalState.Committed));
        _database.Save(Journal("j-2", JournalState.InProgress));
        _database.Save(Journal("j-3", JournalState.Prepared));
        _database.Save(Journal("j-4", JournalState.RolledBack));
        _database.Save(Journal("j-5", JournalState.Interrupted));

        var unfinished = _database.FindUnfinished();

        Assert.Equal(["j-2", "j-3", "j-5"], unfinished.Select(j => j.JournalId).Order());
    }

    [Fact]
    public void The_latest_committed_journal_of_a_project_is_the_one_undo_uses()
    {
        _database.Save(Journal("j-1", JournalState.Committed, run: "run-1"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _database.Save(Journal("j-2", JournalState.Committed, run: "run-2"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _database.Save(Journal("j-3", JournalState.Undone, run: "run-3"));
        _database.Save(Journal("j-4", JournalState.Committed, run: "run-4", project: @"C:\Projects\Other"));

        Assert.Equal("j-2", _database.FindLatestForProject(@"c:\projects\myapp")!.JournalId);
        Assert.Equal("j-1", _database.FindForRun("run-1")!.JournalId);
        Assert.Null(_database.FindLatestForProject(@"C:\Projects\None"));
    }

    [Fact]
    public void The_applies_of_a_project_are_listed_newest_first_whatever_became_of_them()
    {
        _database.Save(Journal("j-1", JournalState.Committed, run: "run-1"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _database.Save(Journal("j-2", JournalState.Undone, run: "run-2"));
        _clock.Advance(TimeSpan.FromMinutes(1));
        _database.Save(Journal("j-3", JournalState.Interrupted, run: "run-3"));
        _database.Save(Journal("j-4", JournalState.Committed, run: "run-4", project: @"C:\Projects\Other"));

        var journals = _database.ListForProject(@"c:\projects\myapp\", 10);

        Assert.Equal(["j-3", "j-2", "j-1"], journals.Select(j => j.JournalId));
        Assert.Equal(JournalState.Interrupted, journals[0].State);
        Assert.Equal(["j-3", "j-2"], _database.ListForProject(@"C:\Projects\MyApp", 2).Select(j => j.JournalId));
        Assert.Empty(_database.ListForProject(@"C:\Projects\None", 10));
    }
}
