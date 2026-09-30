using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Yav.Bench;

/// <summary>What is known about a benchmark run as a whole.</summary>
public sealed record BenchRunInfo
{
    public required string Mode { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required int Seed { get; init; }

    public required int Repetitions { get; init; }

    public required string ModelA { get; init; }

    public required string ModelB { get; init; }

    public required string YavVersion { get; init; }

    public required string Machine { get; init; }

    public IReadOnlyList<string> Arms { get; init; } = [];

    public IReadOnlyList<string> Tasks { get; init; } = [];
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    WriteIndented = false)]
[JsonSerializable(typeof(StepResult))]
[JsonSerializable(typeof(BenchRunInfo))]
internal sealed partial class BenchJson : JsonSerializerContext;

/// <summary>Writes and reads the results of a benchmark run, and turns them into a report.</summary>
public static class BenchReport
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    public static void Save(string directory, BenchRunInfo info, IReadOnlyList<StepResult> results)
    {
        Directory.CreateDirectory(directory);
        var indented = new JsonSerializerOptions(BenchJson.Default.Options) { WriteIndented = true };
        File.WriteAllText(Path.Combine(directory, "run.json"), JsonSerializer.Serialize(info, typeof(BenchRunInfo), new BenchJson(indented)), new UTF8Encoding(false));
        File.WriteAllLines(
            Path.Combine(directory, "results.jsonl"),
            results.Select(r => JsonSerializer.Serialize(r, BenchJson.Default.StepResult)),
            new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(directory, "report.md"), Markdown(info, results), new UTF8Encoding(false));
    }

    public static (BenchRunInfo Info, IReadOnlyList<StepResult> Results) Load(string directory)
    {
        var info = JsonSerializer.Deserialize(File.ReadAllText(Path.Combine(directory, "run.json")), BenchJson.Default.BenchRunInfo)
            ?? throw new InvalidDataException("run.json is empty.");
        var results = File.ReadLines(Path.Combine(directory, "results.jsonl"))
            .Where(line => line.Trim().Length > 0)
            .Select(line => JsonSerializer.Deserialize(line, BenchJson.Default.StepResult)!)
            .ToList();
        return (info, results);
    }

    public static string Markdown(BenchRunInfo info, IReadOnlyList<StepResult> results)
    {
        var text = new StringBuilder();
        var fixture = info.Mode.Equals("fixture", StringComparison.OrdinalIgnoreCase);
        text.AppendLine("# Benchmark results").AppendLine();
        text.AppendLine(Invariant, $"Run on {info.StartedAt:yyyy-MM-dd HH:mm} UTC, mode **{info.Mode}**, YAV Shell {info.YavVersion}, {info.Machine}.");
        text.AppendLine(Invariant, $"{info.Tasks.Count} tasks, {info.Repetitions} repetition(s), order shuffled with seed {info.Seed}. Model A: {info.ModelA}. Model B: {info.ModelB}.");
        text.AppendLine();

        if (fixture)
        {
            text.AppendLine("> **The agents were scripted.** In fixture mode a stand-in plays Model A and Model B: it writes the");
            text.AppendLine("> solution that is known to pass and reviews nothing. Nothing was sent to a provider. These numbers");
            text.AppendLine("> measure what YAV Shell does around the agents on this machine: preparing the workspace, freezing the");
            text.AppendLine("> candidate, running the checks, applying. **They say nothing about what a model can do, about");
            text.AppendLine("> provider time, about usage or about cost**, and a success rate of 100% is what the script produces.");
            text.AppendLine();
        }
        else
        {
            text.AppendLine("> A finite number of runs of a finite number of tasks. The numbers describe these runs. They are no");
            text.AppendLine("> promise for other tasks, other models or another day.");
            text.AppendLine();
        }

        var arms = results.Select(r => r.Arm).Distinct().ToList();
        var summaries = arms.Select(arm => BenchStatistics.Summarize(arm, results.Where(r => r.Arm == arm).ToList())).ToList();

        text.AppendLine("## By arm").AppendLine();
        text.AppendLine("| Arm | Runs | Succeeded | Success rate | With regressions | Repair cycles | Median | 90th percentile | Median active | Median until first action |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|---:|---:|");
        foreach (var s in summaries)
        {
            text.AppendLine(Invariant, $"| {s.Arm} | {s.Runs} | {s.Successes} | {s.SuccessRate:P0} | {s.RunsWithRegressions} | {s.RepairCycles} | {Seconds(s.MedianSeconds)} | {Seconds(s.TailSeconds)} | {Seconds(s.MedianActiveSeconds)} | {Seconds(s.MedianFirstActionSeconds)} |");
        }

        text.AppendLine();
        text.AppendLine("A run succeeded when every acceptance check of its task passes in the project afterwards. The benchmark decides that");
        text.AppendLine("itself, the same way for every arm. Elapsed time is everything from the start of a run to its end.");
        text.AppendLine();

        text.AppendLine("## Usage and cost").AppendLine();
        text.AppendLine("| Arm | Tokens, all runs | Tokens per successful task | Charge, all runs | Charge per successful task | Runs without usage | Runs without charge |");
        text.AppendLine("|---|---:|---:|---:|---:|---:|---:|");
        foreach (var s in summaries)
        {
            text.AppendLine(Invariant, $"| {s.Arm} | {Number(s.TotalTokens)} | {Number(s.TokensPerSuccess)} | {Money(s.TotalCostUsd)} | {Money(s.CostPerSuccessUsd)} | {s.RunsWithoutUsage} | {s.RunsWithoutCost} |");
        }

        text.AppendLine();
        text.AppendLine("What failed runs consumed is part of the cost of a successful task. A sum is given only when every run reported");
        text.AppendLine("the value; otherwise it is Unavailable, because a sum of what happened to be reported would look like the whole.");
        text.AppendLine("A charge is what an agent itself reported. Usage of a subscription is not turned into an amount of money.");
        text.AppendLine();

        var reference = arms.FirstOrDefault(a => a == Arms.Yav);
        if (reference is not null && arms.Count > 1)
        {
            text.AppendLine("## Times where both succeeded").AppendLine();
            text.AppendLine($"Compared with `{reference}`, for the runs in which both arms succeeded at the same task, step and repetition.");
            text.AppendLine();
            text.AppendLine("| Compared with | Pairs | Median of " + reference + " | Median of the other | Median difference |");
            text.AppendLine("|---|---:|---:|---:|---:|");
            foreach (var other in arms.Where(a => a != reference))
            {
                var matched = BenchStatistics.Matched(results.Where(r => r.Arm == reference).ToList(), results.Where(r => r.Arm == other).ToList());
                text.AppendLine(Invariant, $"| {other} | {matched.Pairs} | {Seconds(matched.MedianSecondsFirst)} | {Seconds(matched.MedianSecondsSecond)} | {Signed(matched.MedianDifferenceSeconds)} |");
            }

            text.AppendLine();
            if (arms.Contains(Arms.ModelAAlone))
            {
                text.AppendLine($"`{Arms.ModelAAlone}` has no review. It is no quality-controlled workflow and is listed for orientation, not as an equal.");
                text.AppendLine();
            }
        }

        text.AppendLine("## By task").AppendLine();
        text.AppendLine("| Task | Step | Arm | Repetition | Succeeded | Outcome | Elapsed | Repairs | Failed checks | Regressions | Note |");
        text.AppendLine("|---|---:|---|---:|---|---|---:|---:|---|---|---|");
        foreach (var r in results.OrderBy(r => r.TaskId, StringComparer.Ordinal).ThenBy(r => r.Step).ThenBy(r => r.Arm, StringComparer.Ordinal).ThenBy(r => r.Repetition))
        {
            text.AppendLine(Invariant, $"| {r.TaskId} | {r.Step} | {r.Arm} | {r.Repetition} | {(r.Succeeded ? "yes" : "**no**")} | {r.Outcome} | {Seconds(r.ElapsedMs / 1000.0)} | {r.RepairCycles} | {string.Join(", ", r.FailedChecks)} | {string.Join(", ", r.Regressions)} | {Cell(r.Note)} |");
        }

        return text.ToString();
    }

    private static string Seconds(double? value) => value is null ? "Unavailable" : value.Value.ToString("0.00", Invariant) + " s";

    private static string Signed(double? value) => value is null ? "Unavailable" : value.Value.ToString("+0.00;-0.00;0.00", Invariant) + " s";

    private static string Number(long? value) => value is null ? "Unavailable" : value.Value.ToString("N0", Invariant);

    private static string Money(decimal? value) => value is null ? "Unavailable" : "$" + value.Value.ToString("0.0000", Invariant);

    private static string Cell(string? text) =>
        string.IsNullOrEmpty(text) ? string.Empty : text.ReplaceLineEndings(" ").Replace("|", "/", StringComparison.Ordinal);
}
