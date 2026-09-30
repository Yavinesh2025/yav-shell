using System.Runtime.InteropServices;
using System.Text.Json;
using Yav.Core.Settings;
using Yav.Tests.Support;

namespace Yav.Tests.EndToEnd;

/// <summary>
/// Measures what a person waits for in a console: until the prompt is there, until a key that was typed
/// shows, until a command has answered. Times are taken when output arrives at the terminal, which here
/// is the pseudo console of Windows.
///
/// The numbers are written to a file. What is asserted is only that they are far from unusable, because
/// a test that runs next to others is no quiet place to measure. For numbers that are worth writing down,
/// run this test alone and name the program with YAV_MEASURE_EXE.
/// </summary>
[Collection(nameof(InteractiveConsoleTests))]
public class ConsoleMeasurements
{
    /// <summary>The program to measure. Without it, the one that was built with the tests.</summary>
    private const string ExecutableVariable = "YAV_MEASURE_EXE";

    /// <summary>The file the numbers are written to. Without it, artifacts\measurements\console.json.</summary>
    private const string OutputVariable = "YAV_MEASURE_OUT";

    private const int Rounds = 7;

    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true };

    private static string DefaultOutput
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "artifacts", "measurements", "console.json");
        }
    }

    private static string Executable =>
        Environment.GetEnvironmentVariable(ExecutableVariable) is { Length: > 0 } named ? named : YavProcess.Executable;

    private static string OutputNamed(string name)
    {
        var console = Environment.GetEnvironmentVariable(OutputVariable) is { Length: > 0 } file ? file : DefaultOutput;
        return Path.Combine(Path.GetDirectoryName(console)!, name);
    }

    private static string Described(string executable) =>
        Path.GetFileName(Path.GetDirectoryName(executable)) + "\\" + Path.GetFileName(executable);

    /// <summary>
    /// A run with agents that answer at once, so that what is measured is what YAV does around them.
    /// The intervals are the ones YAV measured itself and shows with /latency.
    /// </summary>
    [Fact]
    public async Task Where_the_time_of_a_run_goes_is_measured()
    {
        var executable = Executable;
        var process = new List<double>();
        var intervals = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        var timeline = new List<object>();

        for (var round = 0; round < Rounds; round++)
        {
            using var yav = new YavProcess().WithPassingRun();

            var started = DateTimeOffset.UtcNow;
            var result = await yav.RunAsync(
                ["run", "--project", yav.Project.Path, "--task", "Make the app say fixed.", "--json", "--apply"], executable: executable);

            Assert.True(result.ExitCode == 0, result.Output + result.Error);
            process.Add(result.Elapsed.TotalMilliseconds);
            var runId = result.Json[^1].GetProperty("runId").GetString()!;
            var spans = yav.SpansOf(runId).Where(span => span.Duration is not null).ToList();
            foreach (var kind in spans.GroupBy(span => span.Kind.ToString()))
            {
                if (!intervals.TryGetValue(kind.Key, out var values))
                {
                    intervals[kind.Key] = values = [];
                }

                // The time that passed, not the sum: an interval that lies within another is not counted twice.
                values.Add(Yav.Core.Timing.TimingMath.WallClock(kind).TotalMilliseconds);
            }

            if (round == Rounds - 1)
            {
                // The last run, moment by moment, counted from the start of the process.
                double Since(DateTimeOffset moment) => Math.Round((moment - started).TotalMilliseconds);
                timeline.AddRange(result.Json.SkipLast(1).Select(line => (object)new
                {
                    at = Since(line.GetProperty("at").GetDateTimeOffset()),
                    what = line.GetProperty("event").GetString()
                        + (line.TryGetProperty("message", out var message) ? ": " + message.GetString() : string.Empty)
                        + (line.TryGetProperty("to", out var to) ? ": " + to.GetString() : string.Empty),
                }));
                timeline.AddRange(spans.OrderBy(span => span.StartedAt).Select(span => (object)new
                {
                    at = Since(span.StartedAt),
                    what = $"interval {span.Kind} '{span.Label}' of {Math.Round(span.Duration!.Value.TotalMilliseconds)} ms",
                }));
                timeline.Add(new { at = Math.Round(result.Elapsed.TotalMilliseconds), what = "the process has ended" });
            }
        }

        var output = OutputNamed("run.json");
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(
            new
            {
                what = "Milliseconds of 'yav run --json --apply' for a change of one file in a project of two files, with scripted agents that answer at once. "
                    + "Looking at the agents is part of the preparation, and review and checks run at the same time, so the stages do not add up to the whole.",
                program = Described(executable),
                at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                machine = $"{RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors",
                rounds = Rounds,
                theWholeProcess = Summary(process),
                intervalsYavMeasured = intervals.OrderBy(pair => pair.Key, StringComparer.Ordinal).ToDictionary(pair => pair.Key, pair => Summary(pair.Value)),
                theLastRunMomentByMoment = timeline.OrderBy(entry => (double)entry.GetType().GetProperty("at")!.GetValue(entry)!).ToList(),
            },
            Indented));

        Assert.All(process, value => Assert.InRange(value, 1, 60_000));
        Assert.Contains(nameof(Yav.Core.Timing.SpanKind.Apply), intervals.Keys);
    }

    private static double Median(IReadOnlyList<double> values)
    {
        var sorted = values.Order().ToList();
        return sorted.Count % 2 == 1 ? sorted[sorted.Count / 2] : (sorted[(sorted.Count / 2) - 1] + sorted[sorted.Count / 2]) / 2;
    }

    private static object Summary(IReadOnlyList<double> values) => new
    {
        first = Math.Round(values[0]),
        medianOfTheOthers = Math.Round(Median([.. values.Skip(1)])),
        slowest = Math.Round(values.Max()),
        all = values.Select(value => Math.Round(value)).ToArray(),
    };

    [Fact]
    public async Task What_a_person_waits_for_in_a_console_is_measured()
    {
        var executable = Executable;
        var output = OutputNamed("console.json");
        var prompt = new List<double>();
        var echo = new List<double>();
        var reaction = new List<double>();
        var answer = new List<double>();

        for (var round = 0; round < Rounds; round++)
        {
            using var yav = new YavProcess();
            var waitsForInput = $"YAV {yav.Project.Path}>";
            using var console = PseudoConsole.Start(
                executable,
                [yav.Project.Path],
                yav.Project.Path,
                new Dictionary<string, string?> { [YavPaths.HomeVariable] = yav.Paths.Home, ["NO_COLOR"] = null, ["PROMPT"] = null },
                140,
                45);

            await console.WaitForCursorLineAsync(waitsForInput);
            await Task.Delay(300);
            prompt.Add(console.LastArrival!.Value.TotalMilliseconds);

            var pressed = console.Press("q");
            await console.WaitForCursorLineAsync(waitsForInput + " q");
            echo.Add((console.FirstArrivalAfter(pressed)!.Value - pressed).TotalMilliseconds);
            console.Press("\u007f");
            await console.WaitForCursorLineAsync(waitsForInput);

            await console.TypeAsync("/status");
            await Task.Delay(200);
            var entered = console.Press("\r");
            await console.WaitForAsync("Quality Lock");
            await console.WaitForCursorLineAsync(waitsForInput);
            await Task.Delay(300);
            reaction.Add((console.FirstArrivalAfter(entered)!.Value - entered).TotalMilliseconds);
            answer.Add((console.LastArrival!.Value - entered).TotalMilliseconds);

            await console.TypeAsync("/exit");
            await console.EnterAsync();
            Assert.Equal(0, await console.WaitForExitAsync());
        }

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, JsonSerializer.Serialize(
            new
            {
                what = "Milliseconds, measured at the pseudo console of Windows: from the start of the program or from a key until output arrived.",
                program = Described(executable),
                at = DateTimeOffset.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture),
                machine = $"{RuntimeInformation.OSDescription}, {Environment.ProcessorCount} logical processors",
                rounds = Rounds,
                startUntilThePromptWaits = Summary(prompt),
                keyUntilItShows = Summary(echo),
                enterUntilTheFirstReaction = Summary(reaction),
                enterUntilStatusHasAnswered = Summary(answer),
            },
            Indented));

        // Far from the targets (one second, 200 ms): these only notice that something is badly wrong.
        Assert.All(prompt, value => Assert.InRange(value, 1, 10_000));
        Assert.All(echo, value => Assert.InRange(value, 0, 2_000));
        Assert.All(answer, value => Assert.InRange(value, 0, 5_000));
    }
}
