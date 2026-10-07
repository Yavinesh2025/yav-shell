using Yav.Bench;
using Yav.Tests.Support;

namespace Yav.Tests.Bench;

/// <summary>The benchmark tasks and the tool that runs them. The agents are scripted; nothing is sent to a provider.</summary>
public class BenchToolTests
{
    private static string TasksDirectory
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "bench", "tasks");
        }
    }

    public static TheoryData<string> TaskIds()
    {
        var data = new TheoryData<string>();
        foreach (var task in BenchTask.LoadAll(TasksDirectory))
        {
            data.Add(task.Id);
        }

        return data;
    }

    private static BenchTask Task(string id) => BenchTask.LoadAll(TasksDirectory).Single(t => t.Id == id);

    [Fact]
    public async Task The_tool_takes_the_paths_it_is_given_from_where_it_was_started()
    {
        var bin = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".."));
        var configuration = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var tool = Path.Combine(bin, "Yav.Bench", configuration, "yav-bench.exe");
        // Next to the build output, so that the programs can be named relative to it.
        var work = Directory.CreateDirectory(Path.Combine(bin, "bench test " + Guid.NewGuid().ToString("N")[..8])).FullName;
        try
        {
            var result = await new Yav.Platform.Processes.ProcessRunner().RunAsync(
                new Yav.Core.Ports.ProcessSpec(
                    tool,
                    [
                        "run", "--only", "01", "--arms", "yav",
                        "--yav", Path.GetRelativePath(work, YavProcess.Executable),
                        "--fake-agent", Path.GetRelativePath(work, Fixtures.FakeAgent),
                        "--tasks", Path.GetRelativePath(work, TasksDirectory),
                        "--out", "results of it",
                    ],
                    work),
                new Yav.Core.Ports.CaptureOptions(Timeout: TimeSpan.FromSeconds(120)),
                CancellationToken.None);

            Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
            Assert.Contains("1 of 1 runs succeeded", result.StandardOutput, StringComparison.Ordinal);
            Assert.Contains("| yav | 1 | 1 | 100 % |", File.ReadAllText(Path.Combine(work, "results of it", "report.md")), StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(work, recursive: true);
        }
    }

    [Fact]
    public void There_are_at_least_ten_tasks_of_the_kinds_that_are_asked_for()
    {
        var tasks = BenchTask.LoadAll(TasksDirectory);

        Assert.True(tasks.Count >= 10, $"{tasks.Count} tasks");
        var kinds = tasks.Select(t => t.Category).Distinct().ToList();
        foreach (var kind in new[] { "small edit", "bug", "tests", "feature", "refactoring", "multi-file behavior", "related follow-up" })
        {
            Assert.Contains(kind, kinds);
        }

        Assert.Contains(tasks, t => t.Steps.Count == 2);
        Assert.All(tasks, t => Assert.NotEmpty(t.Steps[0].Checks));
        Assert.All(tasks, t => Assert.False(string.IsNullOrWhiteSpace(t.Steps[0].Request)));
        Assert.Equal(tasks.Count, tasks.Select(t => t.Id).Distinct().Count());
    }

    [Theory]
    [MemberData(nameof(TaskIds))]
    public async Task The_checks_of_a_task_fail_before_it_was_done_and_pass_for_its_solution(string id)
    {
        using var work = new TempDirectory("bench verify");

        var problems = await TaskVerifier.VerifyAsync(Task(id), work.Path, CancellationToken.None);

        Assert.Empty(problems);
    }

    [Theory]
    [InlineData(false, 1)]
    [InlineData(true, 0)]
    public async Task The_check_for_a_regression_test_leaves_no_copy_behind_whether_it_fails_or_passes(bool solved, int exitCode)
    {
        using var work = new TempDirectory("bench verify");
        // The check copies the project into the temporary directory of Node.js, which is this one.
        using var temp = new TempDirectory("node temp");
        var task = Task("12-regression-test");
        using var bench = await Workbench.CreateAsync(task, work.Path, CancellationToken.None);
        if (solved)
        {
            bench.Overlay(task.Steps[0].SolutionDirectory);
        }

        var check = task.Steps[0].Checks.Single(c => c.Id == "regression");
        var runner = new Yav.Platform.Processes.ProcessRunner();
        var result = await runner.RunAsync(
            new Yav.Core.Ports.ProcessSpec(
                runner.Resolve(check.Command)!,
                check.Arguments,
                bench.Project,
                new Dictionary<string, string?> { ["TEMP"] = temp.Path, ["TMP"] = temp.Path }),
            new Yav.Core.Ports.CaptureOptions(Timeout: TimeSpan.FromSeconds(check.TimeoutSeconds)),
            CancellationToken.None);

        Assert.True(result.ExitCode == exitCode, result.StandardOutput + result.StandardError);
        Assert.Empty(Directory.EnumerateFileSystemEntries(temp.Path));
    }

    [Theory]
    [MemberData(nameof(TaskIds))]
    public void The_solution_of_a_task_does_not_touch_what_decides_about_it(string id)
    {
        var task = Task(id);

        foreach (var step in task.Steps)
        {
            var changed = Directory.EnumerateFiles(step.SolutionDirectory, "*", SearchOption.AllDirectories)
                .Select(f => Path.GetRelativePath(step.SolutionDirectory, f).Replace('\\', '/'))
                .ToList();
            Assert.NotEmpty(changed);
            Assert.DoesNotContain(changed, path => path.StartsWith("acceptance/", StringComparison.Ordinal));
        }
    }

    [Theory]
    [InlineData("PASS", true)]
    [InlineData("pass - nothing to change", true)]
    [InlineData("  PASS\n", true)]
    [InlineData("{\"status\":\"pass\",\"findings\":[]}", true)]
    [InlineData("{\"status\":\"changes_required\",\"findings\":[]}", false)]
    [InlineData("{\"status\":", false)]
    [InlineData("This does not PASS: the empty case is missing.", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void A_review_by_hand_passes_only_when_it_says_so(string? verdict, bool passes)
    {
        Assert.Equal(passes, DirectArm.Passes(verdict));
    }

    [Fact]
    public void A_report_of_scripted_agents_says_that_it_measures_nothing_about_models()
    {
        var info = new BenchRunInfo
        {
            Mode = "fixture", StartedAt = Builders.Now, Seed = 1, Repetitions = 1, ModelA = "model-a", ModelB = "model-b",
            YavVersion = "0.1.0", Machine = "test", Arms = ["yav"], Tasks = ["01"],
        };
        StepResult[] results =
        [
            new() { Arm = "yav", TaskId = "01", Outcome = "ready_to_apply", Succeeded = true, ElapsedMs = 4200, TotalTokens = null, CostUsd = null },
        ];

        var report = BenchReport.Markdown(info, results);

        Assert.Contains("The agents were scripted.", report, StringComparison.Ordinal);
        Assert.Contains("They say nothing about what a model can do", report, StringComparison.Ordinal);
        Assert.Contains("| yav | 1 | 1 | 100", report, StringComparison.Ordinal);
        Assert.Contains("| yav | Unavailable | Unavailable | Unavailable | Unavailable | 1 | 1 |", report, StringComparison.Ordinal);
        Assert.DoesNotContain("$0", report, StringComparison.Ordinal);
    }

    [Fact]
    public void A_report_is_written_and_read_back_as_it_was()
    {
        using var directory = new TempDirectory("bench report");
        var info = new BenchRunInfo
        {
            Mode = "live", StartedAt = Builders.Now, Seed = 7, Repetitions = 2, ModelA = "a", ModelB = "b",
            YavVersion = "0.1.0", Machine = "test", Arms = ["yav", "two-models-by-hand"], Tasks = ["01", "02"],
        };
        StepResult[] results =
        [
            new() { Arm = "yav", TaskId = "01", Outcome = "ready_to_apply", Succeeded = true, ElapsedMs = 1000, TotalTokens = 10, CostUsd = 0.5m, Regressions = ["x"] },
            new() { Arm = "two-models-by-hand", TaskId = "01", Outcome = "blocked", Succeeded = false, ElapsedMs = 2000, Note = "a | b" },
        ];

        BenchReport.Save(directory.Path, info, results);
        var (readInfo, readResults) = BenchReport.Load(directory.Path);

        Assert.Equal(info.Seed, readInfo.Seed);
        Assert.Equal(info.Arms, readInfo.Arms);
        Assert.Equal(2, readResults.Count);
        Assert.Equal(results[0].CostUsd, readResults[0].CostUsd);
        Assert.Equal(["x"], readResults[0].Regressions);
        Assert.Null(readResults[1].TotalTokens);
        var report = File.ReadAllText(directory.File("report.md"));
        Assert.DoesNotContain("The agents were scripted.", report, StringComparison.Ordinal);
        Assert.Contains("a / b", report, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(Arms.Yav)]
    [InlineData(Arms.YavWithoutOptimization)]
    [InlineData(Arms.TwoModelsByHand)]
    [InlineData(Arms.ModelAAlone)]
    public async Task With_scripted_agents_an_arm_does_the_task_and_its_follow_up_and_is_judged_by_the_checks(string arm)
    {
        using var work = new TempDirectory("bench run");
        var task = Task("09-follow-up");
        using var bench = await Workbench.CreateAsync(task, work.Path, CancellationToken.None);
        var environment = BenchEnvironment.Fixture(YavProcess.Executable, Fixtures.FakeAgent);

        var results = await Arms.Find(arm).RunAsync(new ArmRun(environment, task, bench, Repetition: 1), CancellationToken.None);

        Assert.Equal(2, results.Count);
        Assert.All(results, r => Assert.True(r.Succeeded, $"{r.Outcome}: {r.Note} failed: {string.Join(",", r.FailedChecks)}"));
        Assert.All(results, r => Assert.Empty(r.Regressions));
        Assert.Equal([1, 2], results.Select(r => r.Step).ToArray());
        Assert.All(results, r => Assert.Equal(arm, r.Arm));
        Assert.All(results, r => Assert.InRange(r.ElapsedMs, 1, 120_000));
        Assert.True(results[1].Warm);
        Assert.Contains("parse_timeout", File.ReadAllText(Path.Combine(bench.Project, "settings.py")), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_task_that_was_not_done_is_reported_as_not_succeeded_whatever_the_run_says()
    {
        using var work = new TempDirectory("bench run");
        var task = Task("02-bug-off-by-one");
        var undone = task with
        {
            Steps = [task.Steps[0] with { SolutionDirectory = Path.Combine(task.ProjectDirectory) }],
        };
        using var bench = await Workbench.CreateAsync(undone, work.Path, CancellationToken.None);
        var environment = BenchEnvironment.Fixture(YavProcess.Executable, Fixtures.FakeAgent) with { MaxRepairCycles = 0 };

        var result = Assert.Single(await Arms.Find(Arms.ModelAAlone).RunAsync(new ArmRun(environment, undone, bench, 1), CancellationToken.None));

        Assert.False(result.Succeeded);
        Assert.Equal(["tests"], result.FailedChecks);
        Assert.Equal("blocked", result.Outcome);
    }
}
