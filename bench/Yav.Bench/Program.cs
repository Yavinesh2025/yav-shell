
using System.Runtime.InteropServices;
using Yav.Bench;
using Yav.Core.Settings;

const string Usage = """
    yav-bench verify [--tasks <directory>]
        Checks the tasks themselves: for every task the acceptance checks must fail for the project as
        it is and pass for the solution that comes with the task. No agent is involved.

    yav-bench run [--mode fixture] [--arms <a,b,...>] [--tasks <directory>] [--only <task,...>]
                  [--repeat <n>] [--seed <n>] [--out <directory>] [--yav <yav.exe>] [--fake-agent <exe>]
        Runs the tasks with scripted agents. Nothing is sent to a provider.

    yav-bench run --mode live --i-authorize-usage [the same options]
        Runs the tasks with the agents that are installed, the models you chose in YAV and the account
        routes you acknowledged there. EVERY RUN CONSUMES USAGE OF YOUR ACCOUNTS. It does not start
        without --i-authorize-usage and without your "yes" to what it shows first.

    yav-bench report <directory>
        Writes report.md again from the results in that directory.

    Arms: yav, yav-optimization-off, two-models-by-hand, model-a-alone
    """;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    Console.Out.WriteLine(Usage);
    return 0;
}

var options = new Dictionary<string, string>(StringComparer.Ordinal);
var flags = new HashSet<string>(StringComparer.Ordinal);
var positional = new List<string>();
for (var i = 1; i < args.Length; i++)
{
    if (args[i] is "--i-authorize-usage" or "--yes")
    {
        flags.Add(args[i]);
    }
    else if (args[i].StartsWith("--", StringComparison.Ordinal))
    {
        if (i + 1 >= args.Length)
        {
            Console.Error.WriteLine($"yav-bench: {args[i]} needs a value.");
            return 64;
        }

        options[args[i]] = args[++i];
    }
    else
    {
        positional.Add(args[i]);
    }
}

// Runs work in directories of their own. A path that was given relative to the directory the tool was
// started in has to be resolved before that, or it would name something else, or nothing, later.
foreach (var name in new[] { "--yav", "--fake-agent", "--tasks", "--out" })
{
    if (options.TryGetValue(name, out var given))
    {
        options[name] = Path.GetFullPath(given);
    }
}

for (var i = 0; i < positional.Count; i++)
{
    positional[i] = Path.GetFullPath(positional[i]);
}

var repository = FindRepository();
var tasksDirectory = options.GetValueOrDefault("--tasks") ?? Path.Combine(repository, "bench", "tasks");
using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};

try
{
    switch (args[0])
    {
        case "verify":
            return await VerifyAsync(tasksDirectory, stop.Token);

        case "run":
            return await RunAsync(stop.Token);

        case "report":
        {
            if (positional.Count != 1)
            {
                Console.Error.WriteLine("yav-bench: report needs the directory of a run.");
                return 64;
            }

            var (info, results) = BenchReport.Load(positional[0]);
            BenchReport.Save(positional[0], info, results);
            Console.Out.WriteLine(Path.Combine(positional[0], "report.md"));
            return 0;
        }

        default:
            Console.Error.WriteLine($"yav-bench: '{args[0]}' is not something yav-bench does.");
            Console.Error.WriteLine(Usage);
            return 64;
    }
}
catch (OperationCanceledException)
{
    Console.Error.WriteLine("yav-bench: stopped.");
    return 6;
}
catch (Exception ex) when (ex is InvalidOperationException or IOException or ArgumentException or InvalidDataException)
{
    Console.Error.WriteLine("yav-bench: " + ex.Message);
    return 5;
}

async Task<int> RunAsync(CancellationToken cancellationToken)
{
    var live = string.Equals(options.GetValueOrDefault("--mode"), "live", StringComparison.OrdinalIgnoreCase);
    if (options.TryGetValue("--mode", out var mode) && !live && !mode.Equals("fixture", StringComparison.OrdinalIgnoreCase))
    {
        Console.Error.WriteLine($"yav-bench: '{mode}' is not a mode. The modes are fixture and live.");
        return 64;
    }

    var configuration = Path.GetFileName(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
    var yav = options.GetValueOrDefault("--yav") ?? Path.Combine(repository, "artifacts", "bin", "Yav.Console", configuration, "yav.exe");
    if (!File.Exists(yav))
    {
        Console.Error.WriteLine($"yav-bench: yav.exe was not found at {yav}. Build it (scripts\\build.ps1) or name it with --yav.");
        return 5;
    }

    var tasks = BenchTask.LoadAll(tasksDirectory);
    if (options.TryGetValue("--only", out var only))
    {
        var wanted = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        tasks = tasks.Where(t => wanted.Any(w => t.Id.StartsWith(w, StringComparison.OrdinalIgnoreCase))).ToList();
    }

    var arms = (options.GetValueOrDefault("--arms") ?? string.Join(',', Arms.All.Select(a => a.Name)))
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(Arms.Find)
        .ToList();
    var repeat = int.TryParse(options.GetValueOrDefault("--repeat"), out var r) && r > 0 ? r : 1;
    var seed = int.TryParse(options.GetValueOrDefault("--seed"), out var s) ? s : Environment.TickCount;
    if (tasks.Count == 0 || arms.Count == 0)
    {
        Console.Error.WriteLine("yav-bench: there is nothing to run.");
        return 64;
    }

    BenchEnvironment environment;
    if (live)
    {
        if (!flags.Contains("--i-authorize-usage"))
        {
            Console.Error.WriteLine("yav-bench: live mode consumes usage of your accounts and does not start without --i-authorize-usage.");
            return 64;
        }

        environment = BenchEnvironment.Live(yav, YavPaths.Resolve());
        var runs = tasks.Sum(t => t.Steps.Count) * arms.Count * repeat;
        Console.Out.WriteLine($"Live mode. {runs} runs: {tasks.Count} tasks, {arms.Count} arm(s), {repeat} repetition(s).");
        Console.Out.WriteLine($"  Model A: {environment.ModelA.ModelId} through {environment.ModelA.AdapterId}, effort {environment.ModelA.EffortPreference}");
        Console.Out.WriteLine($"  Model B: {environment.ModelB.ModelId} through {environment.ModelB.AdapterId}, effort {environment.ModelB.EffortPreference}");
        Console.Out.WriteLine("  Every run sends requests to the providers of these models, through the accounts their agents are signed in to,");
        Console.Out.WriteLine("  and counts against the limits or the balance of those accounts. How much that is, is not known beforehand.");
        Console.Out.WriteLine("  'yav doctor' and /login in YAV show the account routes. Runs that fail consume usage as well.");
        if (!flags.Contains("--yes"))
        {
            Console.Out.Write("Type yes to start: ");
            if (!string.Equals(Console.In.ReadLine()?.Trim(), "yes", StringComparison.OrdinalIgnoreCase))
            {
                Console.Out.WriteLine("Not started. Nothing was sent.");
                return 0;
            }
        }
    }
    else
    {
        var fake = options.GetValueOrDefault("--fake-agent") ?? Path.Combine(repository, "artifacts", "bin", "Yav.FakeAgent", configuration, "yav-fake-agent.exe");
        if (!File.Exists(fake))
        {
            Console.Error.WriteLine($"yav-bench: the scripted agent was not found at {fake}. Build the tests (scripts\\build.ps1) or name it with --fake-agent.");
            return 5;
        }

        environment = BenchEnvironment.Fixture(yav, fake);
    }

    var started = DateTimeOffset.UtcNow;
    var output = options.GetValueOrDefault("--out")
        ?? Path.Combine(repository, "bench", "results", $"{started:yyyyMMdd-HHmmss}-{(live ? "live" : "fixture")}");
    var work = Path.Combine(Path.GetTempPath(), "yav-bench", started.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture));
    Directory.CreateDirectory(work);

    var plan = BenchStatistics.Shuffle(
        (from task in tasks from arm in arms from repetition in Enumerable.Range(1, repeat) select (task, arm, repetition)).ToList(),
        seed);
    var results = new List<StepResult>();
    var number = 0;
    foreach (var (task, arm, repetition) in plan)
    {
        cancellationToken.ThrowIfCancellationRequested();
        number++;
        Console.Out.Write($"[{number}/{plan.Count}] {task.Id}  {arm.Name}  #{repetition} ... ");
        using var bench = await Workbench.CreateAsync(task, work, cancellationToken);
        try
        {
            var steps = await arm.RunAsync(new ArmRun(environment, task, bench, repetition), cancellationToken);
            results.AddRange(steps);
            Console.Out.WriteLine(string.Join("; ", steps.Select(step =>
                $"{(step.Succeeded ? "ok" : "NOT OK")} {step.Outcome} {step.ElapsedMs / 1000.0:0.0}s")));
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not OutOfMemoryException)
        {
            // A run that could not be made is a run that did not succeed. It is reported, not left out.
            Console.Out.WriteLine("FAILED TO RUN: " + ex.Message);
            results.Add(new StepResult
            {
                Arm = arm.Name,
                TaskId = task.Id,
                Repetition = repetition,
                Mode = live ? "live" : "fixture",
                Outcome = "could_not_run",
                Succeeded = false,
                ElapsedMs = 0,
                Note = ex.GetType().Name + ": " + ex.Message,
                StartedAt = DateTimeOffset.UtcNow,
            });
        }
    }

    var info = new BenchRunInfo
    {
        Mode = live ? "live" : "fixture",
        StartedAt = started,
        Seed = seed,
        Repetitions = repeat,
        ModelA = $"{environment.ModelA.ModelId} ({environment.ModelA.AdapterId}, effort {environment.ModelA.EffortPreference})",
        ModelB = $"{environment.ModelB.ModelId} ({environment.ModelB.AdapterId}, effort {environment.ModelB.EffortPreference})",
        YavVersion = System.Diagnostics.FileVersionInfo.GetVersionInfo(yav).ProductVersion?.Split('+')[0] ?? "unknown",
        Machine = $"{RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors",
        Arms = arms.Select(a => a.Name).ToList(),
        Tasks = tasks.Select(t => t.Id).ToList(),
    };
    BenchReport.Save(output, info, results);
    try
    {
        Directory.Delete(work, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
        _ = ex;
    }

    Console.Out.WriteLine();
    Console.Out.WriteLine($"{results.Count(x => x.Succeeded)} of {results.Count} runs succeeded. Report: {Path.Combine(output, "report.md")}");
    return results.All(x => x.Succeeded) ? 0 : 1;
}

static async Task<int> VerifyAsync(string tasksDirectory, CancellationToken cancellationToken)
{
    var work = Path.Combine(Path.GetTempPath(), "yav-bench", "verify-" + Guid.NewGuid().ToString("N")[..8]);
    var problems = 0;
    var tasks = BenchTask.LoadAll(tasksDirectory);
    foreach (var task in tasks)
    {
        foreach (var problem in await TaskVerifier.VerifyAsync(task, work, cancellationToken))
        {
            problems++;
            Console.Out.WriteLine($"{task.Id}: {problem}");
        }
    }

    Console.Out.WriteLine(problems == 0
        ? $"{tasks.Count} tasks: every one fails before and passes with its solution."
        : $"{problems} problem(s) in {tasks.Count} tasks.");
    return problems == 0 ? 0 : 1;
}

static string FindRepository()
{
    var directory = new DirectoryInfo(AppContext.BaseDirectory);
    while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
    {
        directory = directory.Parent;
    }

    return directory?.FullName ?? Environment.CurrentDirectory;
}

namespace Yav.Bench
{
    /// <summary>Checks a task itself, without any agent.</summary>
    public static class TaskVerifier
    {
        /// <summary>
        /// What is wrong with a task; nothing when it is sound. A task is sound when, for each of its
        /// steps, at least one check fails before the step was done and every check passes after the
        /// solution of the step was put into the project.
        /// </summary>
        public static async Task<IReadOnlyList<string>> VerifyAsync(BenchTask task, string workDirectory, CancellationToken cancellationToken)
        {
            var problems = new List<string>();
            using var bench = await Workbench.CreateAsync(task, workDirectory, cancellationToken).ConfigureAwait(false);
            for (var index = 0; index < task.Steps.Count; index++)
            {
                var step = task.Steps[index];
                var before = await bench.RunChecksAsync(step.Checks, cancellationToken).ConfigureAwait(false);
                if (before.All(c => c.Passed))
                {
                    problems.Add($"step {index + 1}: every check passes before anything was done, so the checks do not show whether the task was done.");
                }

                bench.Overlay(step.SolutionDirectory);
                var after = await bench.RunChecksAsync(step.Checks, cancellationToken).ConfigureAwait(false);
                foreach (var failed in after.Where(c => !c.Passed))
                {
                    problems.Add($"step {index + 1}: the check '{failed.Id}' fails for the solution (exit {failed.ExitCode}): {failed.Output}");
                }
            }

            return problems;
        }
    }
}
