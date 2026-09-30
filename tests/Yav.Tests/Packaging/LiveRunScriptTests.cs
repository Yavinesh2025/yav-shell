using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Packaging;

/// <summary>
/// scripts\live-run.ps1 without the parts that ask models: the script is parsed, and its functions, which decide
/// what the parts do with what they find, are run on sample files. The script needs PowerShell 7, so that is what
/// runs them here. The script itself is started only where it stops before anything is done.
/// </summary>
public class LiveRunScriptTests
{
    private static readonly ProcessRunner Runner = new();

    private static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
            {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }

    private static string Script => Path.Combine(Repository, "scripts", "live-run.ps1");

    private static string Quoted(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private sealed record Ran(int ExitCode, string StandardOutput, string StandardError);

    /// <summary>
    /// Runs PowerShell 7 as a person's shell starts a program. PowerShell 7 from the Microsoft Store refuses to be
    /// started the way the process runner of YAV starts the agents, which is why it is not used here.
    /// </summary>
    private static Task<Ran> PowerShellAsync(params string[] arguments) => PowerShellAsync(null, arguments);

    /// <param name="environment">Variables to set for PowerShell and what it starts; null removes one.</param>
    private static async Task<Ran> PowerShellAsync(IReadOnlyDictionary<string, string?>? environment, params string[] arguments)
    {
        var start = PowerShellStart(environment, arguments);

        using var process = Process.Start(start)!;
        process.StandardInput.Close();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            Assert.Fail($"pwsh {string.Join(' ', arguments)} did not end within 120 seconds.");
        }

        // A program the script started and left running would hold the output open for as long as it lives.
        try
        {
            await Task.WhenAll(output, error).WaitAsync(TimeSpan.FromSeconds(60));
        }
        catch (TimeoutException)
        {
            Assert.Fail($"pwsh {string.Join(' ', arguments)} ended, but a program it started still runs and holds its output open.");
        }

        return new Ran(process.ExitCode, await output, await error);
    }

    private static ProcessStartInfo PowerShellStart(IReadOnlyDictionary<string, string?>? environment, string[] arguments)
    {
        var start = new ProcessStartInfo(Runner.Resolve("pwsh") ?? throw new FileNotFoundException("PowerShell 7 (pwsh) is not installed on this machine."))
        {
            WorkingDirectory = Repository,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in (string[])["-NoProfile", "-NonInteractive", .. arguments])
        {
            start.ArgumentList.Add(argument);
        }

        foreach (var (name, value) in environment ?? new Dictionary<string, string?>())
        {
            if (value is null)
            {
                start.Environment.Remove(name);
            }
            else
            {
                start.Environment[name] = value;
            }
        }

        return start;
    }

    /// <summary>What the script says when it continues the run in the directory with the given part, and how it ends.</summary>
    private static Task<Ran> ContinueAsync(ScriptRun run, IReadOnlyDictionary<string, string?>? environment, params string[] arguments) =>
        PowerShellAsync(environment, ["-File", Script, "-Directory", run.Directory, .. arguments]);

    /// <summary>
    /// Runs a command with the functions of the script defined, under the strictness the script runs with. The rest
    /// of the script, which starts the parts, is not run.
    /// </summary>
    private static Task<string> CallAsync(string command) => CallAsync(Script, command);

    private static async Task<string> CallAsync(string script, string command)
    {
        var result = await PowerShellAsync(
            "-Command",
            "Set-StrictMode -Version Latest; $ErrorActionPreference = 'Stop'; [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false); "
            + "$tokens = $null; $errors = $null; "
            + $"$parsed = [System.Management.Automation.Language.Parser]::ParseFile({Quoted(script)}, [ref]$tokens, [ref]$errors); "
            + "foreach ($function in $parsed.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) { . ([ScriptBlock]::Create($function.Extent.Text)) }; "
            + command);

        // An error that does not end the command leaves the exit code at 0, so what was written as an error counts as well.
        Assert.True(result.ExitCode == 0 && result.StandardError.Length == 0, $"exit code {result.ExitCode}: {result.StandardError}{result.StandardOutput}");
        return result.StandardOutput.ReplaceLineEndings("\n").TrimEnd('\n');
    }

    /// <summary>What the function makes of a run-output.jsonl with these lines.</summary>
    private static async Task<JsonElement> RunOutputAsync(params string[] lines)
    {
        using var directory = new TempDirectory("live run");
        var file = directory.Write("run-output.jsonl", string.Join("\n", lines));
        return JsonDocument.Parse(await CallAsync($"Read-LiveRunOutput -Path {Quoted(file)} | ConvertTo-Json -Compress")).RootElement.Clone();
    }

    private static string? Text(JsonElement summary, string name) =>
        summary.GetProperty(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;

    [Fact]
    public async Task The_script_is_understood_by_powershell_7()
    {
        var result = await PowerShellAsync(
            "-Command",
            $"$errors = $null; $tokens = $null; [void][System.Management.Automation.Language.Parser]::ParseFile({Quoted(Script)}, [ref]$tokens, [ref]$errors); "
            + "$errors | ForEach-Object { 'line {0}: {1}' -f $_.Extent.StartLineNumber, $_.Message }");

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.StandardOutput.Trim() + result.StandardError.Trim());
    }

    [Fact]
    public async Task The_help_says_of_every_part_whether_it_asks_the_models()
    {
        var help = await CallAsync("$parsed.GetHelpContent().Description");

        foreach (var (part, asks) in new[] { ("setup", false), ("run", true), ("resume", true), ("inspect", false) })
        {
            Assert.Matches($@"(?m)^\s*{part}\s+{(asks ? "ASKS THE MODELS" : "Asks no model")}", help);
        }
    }

    [Fact]
    public async Task The_result_of_a_run_is_read_from_the_last_line_of_type_result()
    {
        var summary = await RunOutputAsync(
            """{"type":"event","runId":"r-1","event":"run.started"}""",
            """{"type":"result","outcome":"failed","exitCode":5,"runId":"r-1","state":"failed","reason":"an earlier result"}""",
            """{"type":"result","outcome":"approval_required","exitCode":3,"runId":"r-1","state":"blocked","reason":"Model A asked to run a command."}""",
            """{"type":"event","runId":"r-1","event":"late"}""");

        Assert.True(summary.GetProperty("resultWritten").GetBoolean());
        Assert.Equal("approval_required", Text(summary, "outcome"));
        Assert.Equal("r-1", Text(summary, "runId"));
        Assert.Equal("blocked", Text(summary, "state"));
        Assert.Equal("Model A asked to run a command.", Text(summary, "reason"));
    }

    [Fact]
    public async Task A_run_that_was_stopped_from_outside_has_no_result_and_keeps_its_number()
    {
        var summary = await RunOutputAsync(
            """{"type":"event","runId":"r-2","event":"run.started"}""",
            """{"type":"event","runId":"r-2","event":"state","from":"preparing","to":"implementing"}""",
            """{"type":"event","runId":"r-2","event":"agent.del""");

        Assert.False(summary.GetProperty("resultWritten").GetBoolean());
        Assert.Equal("no result was written", Text(summary, "outcome"));
        Assert.Equal("r-2", Text(summary, "runId"));
        Assert.Null(Text(summary, "reason"));
    }

    [Fact]
    public async Task A_failure_without_a_run_id_takes_the_number_from_the_lines_before_it()
    {
        var summary = await RunOutputAsync(
            """{"type":"event","runId":"r-3","event":"run.started"}""",
            """{"type":"result","outcome":"failed","exitCode":5,"reason":"The agent could not be used."}""");

        Assert.True(summary.GetProperty("resultWritten").GetBoolean());
        Assert.Equal("failed", Text(summary, "outcome"));
        Assert.Equal("r-3", Text(summary, "runId"));
        Assert.Equal("The agent could not be used.", Text(summary, "reason"));
    }

    [Fact]
    public async Task Output_without_anything_readable_is_no_result()
    {
        var failure = await RunOutputAsync("""{"type":"result","outcome":"invalid","exitCode":64,"reason":"--task is missing."}""");
        var empty = await RunOutputAsync();
        var garbage = await RunOutputAsync("not json", "42", "[1, 2]", "null");

        Assert.Equal("invalid", Text(failure, "outcome"));
        Assert.Null(Text(failure, "runId"));
        foreach (var nothing in new[] { empty, garbage })
        {
            Assert.False(nothing.GetProperty("resultWritten").GetBoolean());
            Assert.Equal("no result was written", Text(nothing, "outcome"));
            Assert.Null(Text(nothing, "runId"));
        }
    }

    [Theory]
    [InlineData("all", true, true, true, "ready_to_apply", 0, true, 0)]
    [InlineData("all", true, true, true, "approval_required", 3, false, 1)]
    [InlineData("all", false, true, true, "approval_required", 3, true, 0)]
    [InlineData("all", false, false, false, "no result was written", -1, false, 1)]
    [InlineData("all", false, true, false, "no result was written", 0, false, 1)]
    [InlineData("run", false, true, true, "blocked", 2, false, 0)]
    [InlineData("run", true, true, true, "approval_required", 3, false, 0)]
    [InlineData("run", false, false, true, "failed", -1, false, 1)]

    // What yav ended with decides as well: a run that failed, or whose command line was refused, did not go through.
    [InlineData("run", false, true, true, "failed", 5, false, 1)]
    [InlineData("all", false, true, true, "failed", 5, false, 1)]
    [InlineData("run", false, true, true, "invalid", 64, false, 1)]
    [InlineData("all", true, true, true, "invalid", 64, false, 1)]
    [InlineData("run", false, true, true, "ready_to_apply", 5, false, 1)]
    [InlineData("all", false, true, true, "blocked", null, false, 1)]
    public async Task What_follows_a_run_depends_on_how_it_ended(
        string part, bool apply, bool endedByItself, bool resultWritten, string outcome, int? yav, bool inspect, int exitCode)
    {
        var after = await CallAsync(
            $"$summary = [pscustomobject]@{{ endedByItself = ${endedByItself}; resultWritten = ${resultWritten}; outcome = {Quoted(outcome)}; exitCode = {(yav is null ? "$null" : yav)} }}; "
            + $"$after = Get-AfterRun -Summary $summary -Part {part} -Apply:${apply}; \"$($after.Inspect)|$($after.ExitCode)\"");

        Assert.Equal($"{inspect}|{exitCode}", after);
    }

    [Fact]
    public async Task The_exit_code_the_script_expects_for_each_outcome_is_the_one_yav_ends_with()
    {
        var kinds = Enum.GetValues<Yav.Core.Runs.RunOutcomeKind>();
        var outcomes = kinds.Select(kind => Yav.Console.Output.JsonOutput.Snake(kind.ToString())).ToList();

        var expected = await CallAsync($"foreach ($outcome in @({string.Join(", ", outcomes.Select(Quoted))})) {{ \"[$(Get-LiveRunExitCode $outcome)]\" }}");

        var said = expected.Split('\n');
        for (var i = 0; i < kinds.Length; i++)
        {
            var yav = Yav.Console.Output.ExitCodes.For(new Yav.Core.Runs.RunOutcome("r-1", kinds[i], default, null, null, null, null, []), applied: null);

            // A run that failed has no exit code in the table: it did not go through.
            Assert.Equal(kinds[i] == Yav.Core.Runs.RunOutcomeKind.Failed ? "[]" : $"[{yav}]", said[i]);
        }
    }

    [Fact]
    public async Task A_run_that_did_not_go_through_is_said_to_and_why()
    {
        var reasons = await CallAsync(
            "foreach ($summary in @{ endedByItself = $true; resultWritten = $true; outcome = 'failed'; exitCode = 5 }, @{ endedByItself = $true; resultWritten = $true; outcome = 'ready_to_apply'; exitCode = 5 }, @{ endedByItself = $true; resultWritten = $true; outcome = 'ready_to_apply'; exitCode = 0 }) { "
            + "$after = Get-AfterRun -Summary ([pscustomobject]$summary) -Part run; \"[$($after.Reason)]\" }");

        var said = reasons.Split('\n');
        Assert.Contains("'failed'", said[0], StringComparison.Ordinal);
        Assert.Contains("5", said[0], StringComparison.Ordinal);
        Assert.Contains("'ready_to_apply'", said[1], StringComparison.Ordinal);
        Assert.Equal("[]", said[2]);
    }

    [Theory]
    [InlineData("failed", 5)]
    [InlineData("invalid", 64)]
    public async Task A_run_that_yav_ended_as_failed_or_refused_did_not_go_through_and_the_script_ends_with_an_error(string outcome, int exitCode)
    {
        using var run = new ScriptRun();
        var standIn = run.StandIn(
            exitCode,
            lines: [$$"""{"type":"event","runId":"r-1","event":"run.started"}""", $$"""{"type":"result","outcome":"{{outcome}}","exitCode":{{exitCode}},"reason":"The stand-in says so."}"""]);

        var result = await ContinueAsync(run, standIn, "-Part", "run", "-IAuthorizeUsage", "-Minutes", "5");

        var said = Flatten(result.StandardOutput + result.StandardError);
        Assert.Single(run.Starts);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("did not go through", said, StringComparison.Ordinal);
        Assert.Contains($"'{outcome}'", said, StringComparison.Ordinal);
        Assert.Equal(exitCode, JsonDocument.Parse(run.Read("run-summary.json")).RootElement.GetProperty("exitCode").GetInt32());
    }

    [Theory]
    [InlineData("run-summary.json", "A run was made in")]
    [InlineData("run-begun.json", "A run was begun in")]
    [InlineData("run-output.jsonl", "A run was begun in")]
    public async Task A_second_run_in_a_directory_is_refused(string record, string refused)
    {
        using var directory = new TempDirectory("live run");
        var first = await CallAsync($"Assert-NoRunYet -Directory {Quoted(directory.Path)}; 'no run yet'");
        directory.Write(record, "{}");
        var second = await CallAsync($"try {{ Assert-NoRunYet -Directory {Quoted(directory.Path)}; 'accepted' }} catch {{ $_.Exception.Message }}");

        Assert.Equal("no run yet", first);
        Assert.Contains(refused, second, StringComparison.Ordinal);
    }

    /// <summary>A process as the functions of the script see it, which does what the test says and records what it was asked.</summary>
    private const string Pretend = """
        $calls = [System.Collections.Generic.List[string]]::new()
        function New-PretendProcess([bool[]]$Waits) {
            $pretend = [pscustomobject]@{ Id = 4242; HasExited = $false; Waits = [System.Collections.Generic.Queue[bool]]::new($Waits) }
            $pretend | Add-Member -MemberType ScriptMethod -Name Kill -Value { $calls.Add('kill') }
            $pretend | Add-Member -MemberType ScriptMethod -Name WaitForExit -Value { param($ms) $calls.Add("wait $ms"); if ($this.Waits.Count -gt 0) { $this.Waits.Dequeue() } else { $false } }
            $pretend
        }

        """;

    [Fact]
    public async Task A_program_that_runs_is_ended_by_its_own_process_object_and_waited_for()
    {
        var answer = await CallAsync(
            $"$process = Start-Process -FilePath {Quoted(Fixtures.FakeAgent)} -ArgumentList 'tool', 'sleep', '120' -PassThru -WindowStyle Hidden; $null = $process.Handle; "
            + "$stop = Stop-LiveProgram -Process $process -Seconds 60; \"$($stop.Ended)|$($process.HasExited)\"");

        Assert.Equal("True|True", answer);
    }

    [Fact]
    public async Task A_program_that_does_not_end_after_it_was_ended_is_reported_after_a_bounded_wait()
    {
        var answer = await CallAsync(
            Pretend + "$stop = Stop-LiveProgram -Process (New-PretendProcess @()) -Seconds 7; \"$($stop.Ended)|$($calls -join ',')|$($stop.Message)\"");

        var said = answer.Split('|');
        Assert.Equal("False", said[0]);
        Assert.Equal("kill,wait 7000", said[1]);
        Assert.Contains("4242", said[2], StringComparison.Ordinal);
        Assert.Contains("Stop-Process -Id 4242", said[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_wait_for_a_run_is_made_in_short_steps_and_ends_when_the_time_given_is_up()
    {
        var answer = await CallAsync(
            Pretend
            + "$ended = Wait-LiveProgram -Process (New-PretendProcess @($false, $false, $true)) -Milliseconds 3600000 -Step 250; \"$ended|$($calls -join ',')\"; $calls.Clear(); "
            + "$ended = Wait-LiveProgram -Process (New-PretendProcess @()) -Milliseconds 0 -Step 250; \"$ended|$($calls -join ',')\"");

        Assert.Equal("True|wait 250,wait 250,wait 250\nFalse|wait 250", answer);
    }

    /// <summary>Waits until a condition holds, and fails with the given text when it does not within a generous time.</summary>
    private static async Task UntilAsync(Func<bool> condition, string what, int seconds = 90)
    {
        var watch = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(watch.Elapsed < TimeSpan.FromSeconds(seconds), $"{what} did not happen within {seconds} seconds.");
            await Task.Delay(100);
        }
    }

    /// <summary>True while the stand-in with the given process id runs. Another process that got the id since does not count.</summary>
    private static bool StandInRuns(int id)
    {
        try
        {
            using var process = Process.GetProcessById(id);
            return !process.HasExited && string.Equals(process.MainModule?.FileName, StandInYav.Executable, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    /// <summary>True once the mark of a begun run says that its program was started and tied to the script.</summary>
    private static bool Tied(ScriptRun run)
    {
        try
        {
            return run.Exists("run-begun.json")
                && JsonDocument.Parse(run.Read("run-begun.json")).RootElement.TryGetProperty("tiedToScript", out var tied)
                && tied.ValueKind == JsonValueKind.True;
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Read while it was being written.
            return false;
        }
    }

    [Fact]
    public async Task The_program_of_a_run_ends_with_the_script_when_the_script_is_killed_and_the_directory_says_so()
    {
        using var run = new ScriptRun();
        var standIn = run.StandIn(hang: true);
        using var script = Process.Start(PowerShellStart(standIn, ["-File", Script, "-Directory", run.Directory, "-Part", "run", "-IAuthorizeUsage", "-Minutes", "30"]))!;
        try
        {
            // Read, but not waited for: a program the script started holds the output open for as long as it lives.
            script.StandardInput.Close();
            _ = script.StandardOutput.ReadToEndAsync();
            _ = script.StandardError.ReadToEndAsync();

            // Killed only once the script has tied the program to itself, which it does right after starting it.
            await UntilAsync(() => Tied(run) && run.Starts.Count > 0, "The program being started and tied to the script");
            var id = Assert.Single(run.Starts);
            using var program = Process.GetProcessById(id);
            script.Kill(entireProcessTree: false);
            await script.WaitForExitAsync();

            await UntilAsync(() => program.HasExited, "The program ending with the script");
        }
        finally
        {
            if (!script.HasExited)
            {
                script.Kill(entireProcessTree: true);
            }
        }

        Assert.False(run.Exists("run-summary.json"));

        // The next part says what happened, and nothing is started again.
        var again = await ContinueAsync(run, standIn, "-Part", "run", "-IAuthorizeUsage", "-Minutes", "5");
        var resumed = await ContinueAsync(run, standIn, "-Part", "resume", "-IAuthorizeUsage", "-Minutes", "5");

        Assert.NotEqual(0, again.ExitCode);
        Assert.Contains("A run was begun in", Flatten(again.StandardOutput + again.StandardError), StringComparison.Ordinal);
        Assert.NotEqual(0, resumed.ExitCode);
        Assert.Contains("A run was begun in", Flatten(resumed.StandardOutput + resumed.StandardError), StringComparison.Ordinal);
        Assert.Single(run.Starts);
        Assert.False(run.Exists("run-summary.json"));
    }

    [Fact]
    public async Task The_program_of_a_run_is_ended_when_the_script_is_stopped_while_it_waits_and_the_summary_says_so()
    {
        using var run = new ScriptRun();
        var standIn = run.StandIn(hang: true);

        // Stopped the way Control+C stops it: the pipeline is stopped, and its finally blocks run.
        var result = await PowerShellAsync(
            standIn,
            "-Command",
            "$ps = [powershell]::Create(); "
            + $"[void]$ps.AddCommand({Quoted(Script)}).AddParameter('Directory', {Quoted(run.Directory)}).AddParameter('Part', 'run').AddParameter('IAuthorizeUsage', $true).AddParameter('Minutes', 30); "
            + "$async = $ps.BeginInvoke(); $watch = [Diagnostics.Stopwatch]::StartNew(); "
            + $"while (-not ((Test-Path -LiteralPath {Quoted(run.File("run-begun.json"))}) -and (Get-Content -LiteralPath {Quoted(run.File("run-begun.json"))} -Raw) -match '\"tiedToScript\":\\s*true')) {{ "
            + "if ($watch.Elapsed.TotalSeconds -gt 90) { throw 'The program was not started.' }; Start-Sleep -Milliseconds 100 }; "
            + "$ps.Stop(); try { $ps.EndInvoke($async) } catch { }; 'stopped'");

        Assert.True(result.ExitCode == 0, result.StandardError + result.StandardOutput);
        var id = Assert.Single(run.Starts);
        await UntilAsync(() => !StandInRuns(id), "The program ending");
        var summary = JsonDocument.Parse(run.Read("run-summary.json")).RootElement;
        Assert.False(summary.GetProperty("endedByItself").GetBoolean());
        Assert.Contains("stopped", summary.GetProperty("stoppedBy").GetString(), StringComparison.Ordinal);
        Assert.False(summary.GetProperty("stillRunning").GetBoolean());

        // Ended by the script itself, not only by the job when the script let go of it.
        Assert.Equal($"yav.exe (process {id}) was ended.", summary.GetProperty("stop").GetString());
    }

    [Fact]
    public async Task A_record_of_a_part_that_is_repeated_gets_the_next_number()
    {
        using var directory = new TempDirectory("live run");
        var at = Quoted(directory.Path);
        var first = await CallAsync($"Get-LiveRecordName -Directory {at} -Part resume");
        foreach (var file in new[] { "resume-1-screens.txt", "resume-2-written.txt", "resume-test.log", "inspect-4-judgement.txt", "run-summary.json" })
        {
            directory.Write(file, string.Empty);
        }

        var next = await CallAsync($"Get-LiveRecordName -Directory {at} -Part resume; Get-LiveRecordName -Directory {at} -Part inspect");

        Assert.Equal("resume-1", first);
        Assert.Equal("resume-3\ninspect-5", next);
    }

    private const string Passed = """
        Test run for D:\YAV Shell\artifacts\bin\Yav.Tests\debug_win-x64\Yav.Tests.dll (.NETCoreApp,Version=v10.0)
        A total of 1 test files matched the specified pattern.
        [xUnit.net 00:00:00.40]   Starting:    Yav.Tests
        [xUnit.net 00:00:49.14]   Finished:    Yav.Tests
          Passed Yav.Tests.Live.LiveRunTests.The_shell_is_set_up_the_way_a_user_sets_it_up [48 s]

        Test Run Successful.
        Total tests: 1
             Passed: 1
         Total time: 49.5724 Seconds
        """;

    public static TheoryData<string, string, bool> TestLogs() => new()
    {
        { "passed", Passed, true },
        { "another test passed", Passed.Replace("The_shell_is_set_up_the_way", "Something_else_the_way", StringComparison.Ordinal), false },
        { "skipped", Passed.Replace("  Passed Yav", "  Skipped Yav", StringComparison.Ordinal).Replace("     Passed: 1", "    Skipped: 1", StringComparison.Ordinal), false },
        { "no test matched", "A total of 1 test files matched the specified pattern.\nNo test matches the given testcase filter `FullyQualifiedName~LiveRunTests.The_shell_is_set_up` in D:\\x\\Yav.Tests.dll", false },
        { "two tests", Passed.Replace("Total tests: 1", "Total tests: 2", StringComparison.Ordinal) + "\n  Passed Yav.Tests.Live.LiveRunTests.The_shell_is_set_up_twice [1 s]", false },
        { "failed", Passed.Replace("  Passed Yav", "  Failed Yav", StringComparison.Ordinal), false },
        { "empty", string.Empty, false },
    };

    [Theory]
    [MemberData(nameof(TestLogs))]
    public async Task A_part_counts_as_done_only_when_exactly_its_one_test_ran_and_passed(string name, string log, bool done)
    {
        _ = name;
        using var directory = new TempDirectory("live run");
        var file = directory.Write("setup-test.log", log.ReplaceLineEndings("\r\n"));

        Assert.Equal(done.ToString(), await CallAsync($"Test-LiveTestLog -Path {Quoted(file)} -Test The_shell_is_set_up"));
    }

    [Fact]
    public async Task Variables_of_a_claude_code_session_are_taken_out_and_put_back()
    {
        var inSession = await CallAsync(
            "$env:CLAUDECODE = '1'; $env:CLAUDE_PID = '42'; $env:GIT_EDITOR = 'true'; $env:COREPACK_ENABLE_AUTO_PIN = '0'; "
            + "$taken = Remove-HostingSessionVariables; "
            + "$during = @('CLAUDECODE', 'CLAUDE_PID', 'GIT_EDITOR', 'COREPACK_ENABLE_AUTO_PIN' | ForEach-Object { [Environment]::GetEnvironmentVariable($_) } | Where-Object { $_ }).Count; "
            + "Restore-HostingSessionVariables $taken; \"$during|$env:CLAUDECODE|$env:CLAUDE_PID|$env:GIT_EDITOR|$env:COREPACK_ENABLE_AUTO_PIN\"");

        // Outside such a session they may have been set on purpose, and they stay.
        var outside = await CallAsync(
            "Remove-Item Env:CLAUDECODE -ErrorAction SilentlyContinue; $env:GIT_EDITOR = 'true'; $env:NoDefaultCurrentDirectoryInExePath = '1'; "
            + "$taken = Remove-HostingSessionVariables; \"$env:GIT_EDITOR|$env:NoDefaultCurrentDirectoryInExePath\"; Restore-HostingSessionVariables $taken");

        Assert.Equal("0|1|42|true|0", inSession);
        Assert.Equal("true|1", outside);
    }

    /// <summary>A directory as the setup leaves it: live-run.json names what was set up.</summary>
    private static TempDirectory SetUp(bool setUp = true)
    {
        var directory = new TempDirectory("live run");
        directory.Write(
            "live-run.json",
            $$"""
            {
              "task": "02-bug-off-by-one",
              "program": "C:\\yav\\yav.exe",
              "modelA": "codex-app-server model-a",
              "modelB": "claude-cli opus",
              "effortA": "maximum",
              "effortB": "maximum",
              "acknowledged": ["codex", "claude"],
              "checksTrusted": true,
              "taskHash": "ABC123",
              "time": "2026-09-30T10:00:00.0000000Z",
              "setUp": {{(setUp ? "true" : "false")}}
            }
            """);
        return directory;
    }

    private static Task<string> ResolveAsync(TempDirectory directory, string named) => CallAsync(
        $"try {{ $setup = Resolve-LiveRunSetup -Directory {Quoted(directory.Path)} -Named @{{ {named} }}; \"$($setup.Task)|$($setup.Program)\" }} catch {{ 'refused: ' + $_.Exception.Message }}");

    [Fact]
    public async Task A_part_after_setup_goes_on_with_the_task_and_the_program_that_were_set_up()
    {
        using var directory = SetUp();

        Assert.Equal(@"02-bug-off-by-one|C:\yav\yav.exe", await ResolveAsync(directory, string.Empty));
        Assert.Equal(
            @"02-bug-off-by-one|C:\yav\yav.exe",
            await ResolveAsync(directory, @"Task = '02-bug-off-by-one'; Yav = 'c:\YAV\yav.exe'; ModelA = 'codex-app-server model-a'; Acknowledge = @('Claude', 'codex')"));
    }

    [Theory]
    [InlineData("Task = '01-small-edit'", "-Task '01-small-edit'")]
    [InlineData(@"Yav = 'C:\other\yav.exe'", @"-Yav 'C:\other\yav.exe'")]
    [InlineData("ModelB = 'claude-cli sonnet'", "-ModelB 'claude-cli sonnet'")]
    [InlineData("Acknowledge = @('codex')", "-Acknowledge 'codex'")]
    public async Task What_differs_from_the_setup_is_refused(string named, string refused)
    {
        using var directory = SetUp();

        var answer = await ResolveAsync(directory, named);

        Assert.StartsWith("refused: " + refused, answer, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_the_script_was_given_is_compared_with_the_setup_in_the_form_the_script_passes_it()
    {
        using var directory = SetUp();

        // The script passes $PSBoundParameters, a dictionary of another kind than the hashtables above.
        var answer = await CallAsync(
            $"$named = [System.Collections.Generic.Dictionary[string, object]]::new(); $named['Task'] = '02-bug-off-by-one'; $named['Part'] = 'inspect'; "
            + $"try {{ $setup = Resolve-LiveRunSetup -Directory {Quoted(directory.Path)} -Named $named; \"$($setup.Task)|$($setup.Program)\" }} catch {{ 'refused: ' + $_.Exception.Message }}; "
            + $"$named['Task'] = '01-small-edit'; try {{ Resolve-LiveRunSetup -Directory {Quoted(directory.Path)} -Named $named | Out-Null; 'accepted' }} catch {{ 'refused: ' + $_.Exception.Message }}");

        var said = answer.Split('\n');
        Assert.Equal(@"02-bug-off-by-one|C:\yav\yav.exe", said[0]);
        Assert.StartsWith("refused: -Task '01-small-edit'", said[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_part_after_setup_is_given_the_hash_of_the_task_file_that_was_approved()
    {
        using var directory = SetUp();

        Assert.Equal("ABC123", await CallAsync($"(Resolve-LiveRunSetup -Directory {Quoted(directory.Path)} -Named @{{}}).TaskHash"));
    }

    [Fact]
    public async Task A_directory_whose_setup_did_not_go_through_is_not_continued()
    {
        using var directory = SetUp(setUp: false);

        Assert.StartsWith("refused: The setup of", await ResolveAsync(directory, "Task = '02-bug-off-by-one'"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_directory_without_the_record_of_a_setup_is_not_continued_even_with_the_task_and_the_program_named()
    {
        using var directory = new TempDirectory("live run");
        directory.CreateDirectory("project");

        Assert.StartsWith("refused: ", await ResolveAsync(directory, "Task = '01-small-edit'"), StringComparison.Ordinal);
        Assert.StartsWith("refused: ", await ResolveAsync(directory, @"Task = '01-small-edit'; Yav = 'C:\yav\yav.exe'"), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("below, set up", true)]
    [InlineData("below, without the record", false)]
    [InlineData("the folder of the runs itself", false)]
    [InlineData("out through ..", false)]
    [InlineData("elsewhere", false)]
    public async Task A_part_after_setup_goes_on_only_in_a_directory_the_setup_made_below_the_folder_of_the_runs(string where, bool accepted)
    {
        using var root = new TempDirectory("runs");
        var runs = root.CreateDirectory(Path.Combine("artifacts", "live-run"));
        var directory = where switch
        {
            "below, set up" or "below, without the record" => root.CreateDirectory(Path.Combine("artifacts", "live-run", "20260930-101010")),
            "the folder of the runs itself" => runs,
            "out through .." => Path.Combine(runs, "..", "..", "elsewhere"),
            _ => root.CreateDirectory("elsewhere"),
        };
        foreach (var candidate in new[] { directory, root.CreateDirectory("elsewhere"), runs })
        {
            if (where != "below, without the record")
            {
                File.WriteAllText(Path.Combine(candidate, "live-run.json"), "{}");
            }
        }

        var answer = await CallAsync($"try {{ Assert-LiveRunDirectory -Directory {Quoted(directory)} -Runs {Quoted(runs)}; 'accepted' }} catch {{ 'refused: ' + $_.Exception.Message }}");

        Assert.Equal(accepted ? "accepted" : "refused", answer.Split(':')[0]);
    }

    [Fact]
    public async Task A_run_is_not_made_in_a_directory_that_is_not_below_the_folder_of_the_runs()
    {
        using var elsewhere = new TempDirectory("elsewhere");
        using var run = new ScriptRun(parent: elsewhere.Path);

        var result = await ContinueAsync(run, run.StandIn(), "-Part", "run", "-IAuthorizeUsage", "-Minutes", "5");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(@"artifacts\live-run", Flatten(result.StandardOutput + result.StandardError), StringComparison.Ordinal);
        Assert.Empty(run.Starts);
        Assert.False(run.Exists("request.md"));
    }

    [Fact]
    public async Task What_a_run_left_is_not_inspected_in_a_directory_that_is_not_below_the_folder_of_the_runs()
    {
        using var elsewhere = new TempDirectory("elsewhere");
        using var run = new ScriptRun(parent: elsewhere.Path);

        var result = await ContinueAsync(run, run.StandIn(), "-Part", "inspect", "-Apply");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(@"artifacts\live-run", Flatten(result.StandardOutput + result.StandardError), StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(run.Directory, "inspect-*"));
    }

    [Fact]
    public async Task What_the_setup_records_is_what_the_parts_after_it_read()
    {
        using var directory = new TempDirectory("live run");

        var answer = await CallAsync(
            $"Save-LiveRunSetup -Directory {Quoted(directory.Path)} -Setup ([ordered]@{{ task = '03-add-tests'; program = 'C:\\yav\\yav.exe'; acknowledged = @('codex'); setUp = $true }}); "
            + $"$setup = Resolve-LiveRunSetup -Directory {Quoted(directory.Path)} -Named @{{ Acknowledge = @('codex') }}; \"$($setup.Task)|$($setup.Program)\"");

        Assert.Equal(@"03-add-tests|C:\yav\yav.exe", answer);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("601")]
    public async Task A_time_outside_1_to_600_minutes_is_refused_before_anything_is_done(string minutes)
    {
        var result = await PowerShellAsync("-File", Script, "-Part", "inspect", "-Minutes", minutes);

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("Minutes", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run")]
    [InlineData("resume")]
    [InlineData("all")]
    public async Task A_part_that_asks_the_models_does_not_start_without_the_authorization(string part)
    {
        var result = await PowerShellAsync("-File", Script, "-Part", part, "-Directory", Path.Combine(Repository, "no such run"));

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("It does not start without -IAuthorizeUsage.", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Setup_without_trust_in_the_checks_stops_before_anything_is_done_and_names_their_commands()
    {
        var runs = Path.Combine(Repository, "artifacts", "live-run");
        var before = Directory.Exists(runs) ? Directory.GetDirectories(runs).Length : 0;

        // The program named does not exist either: were the checks not refused, the script would stop at that.
        var result = await PowerShellAsync(
            "-File", Script, "-Part", "setup", "-ModelA", "codex-app-server model-a", "-ModelB", "claude-cli opus",
            "-Yav", Path.Combine(Repository, "no such program", "yav.exe"));

        var said = result.StandardError + result.StandardOutput;
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("tests: python -m unittest discover -s tests -t .", Flatten(said), StringComparison.Ordinal);
        Assert.Contains("-TrustChecks", said, StringComparison.Ordinal);
        Assert.Equal(before, Directory.Exists(runs) ? Directory.GetDirectories(runs).Length : 0);
    }

    private static string Flatten(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static int RunsThere()
    {
        var runs = Path.Combine(Repository, "artifacts", "live-run");
        return Directory.Exists(runs) ? Directory.GetDirectories(runs).Length : 0;
    }

    [Theory]
    [InlineData("'codex', 'claude'", "codex:subscription,claude:subscription")]
    [InlineData("'codex,Claude:API-Key'", "codex:subscription,claude:api-key")]
    [InlineData("", "")]
    public async Task The_routes_agreed_to_are_passed_on_with_their_kind_and_a_bare_provider_means_its_subscription(string acknowledge, string routes)
    {
        Assert.Equal(routes, await CallAsync($"(ConvertTo-LiveRoutes -Acknowledge @({acknowledge})) -join ','"));
    }

    [Theory]
    [InlineData("openai", "'openai'")]
    [InlineData("anthropic", "'anthropic'")]
    [InlineData("claude:cloud", "'cloud'")]
    [InlineData("claude:", "'claude:'")]
    [InlineData("codex,claude:api-key,claude", "claude")]
    public async Task A_route_the_live_run_does_not_know_is_refused(string acknowledge, string named)
    {
        var answer = await CallAsync($"try {{ ConvertTo-LiveRoutes -Acknowledge {Quoted(acknowledge)} | Out-Null; 'accepted' }} catch {{ 'refused: ' + $_.Exception.Message }}");

        Assert.StartsWith("refused: -Acknowledge", answer, StringComparison.Ordinal);
        Assert.Contains(named, answer, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("openai", "'openai'")]
    [InlineData("codex", "claude")]
    public async Task Setup_is_refused_before_anything_is_done_when_the_routes_agreed_to_do_not_cover_the_models(string acknowledge, string named)
    {
        var before = RunsThere();

        // The program named does not exist either: were the routes not refused, the script would stop at that.
        var result = await PowerShellAsync(
            "-File", Script, "-Part", "setup", "-TrustChecks", "-ModelA", "codex-app-server model-a", "-ModelB", "claude-cli opus",
            "-Acknowledge", acknowledge, "-Yav", Path.Combine(Repository, "no such program", "yav.exe"));

        var said = Flatten(result.StandardError + result.StandardOutput);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("-Acknowledge", said, StringComparison.Ordinal);
        Assert.Contains(named, said, StringComparison.Ordinal);
        Assert.DoesNotContain("yav.exe was not found", said, StringComparison.Ordinal);
        Assert.Equal(before, RunsThere());
    }

    [Fact]
    public async Task The_help_says_that_setup_stops_when_a_route_the_models_need_is_not_agreed_to()
    {
        var help = Flatten(await CallAsync("$parsed.GetHelpContent().Parameters['ACKNOWLEDGE']"));

        Assert.Contains("Setup stops before anything is done when a route the models need is not named.", help, StringComparison.Ordinal);
        Assert.DoesNotContain("blocked", help, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_task_is_read_once_with_the_hash_of_its_file_as_the_parts_check_it()
    {
        var tasks = Path.Combine(Repository, "bench", "tasks");

        var answer = await CallAsync($"$task = Read-LiveTask -Task '01-small-edit' -Tasks {Quoted(tasks)}; \"$($task.Hash)|$($task.Definition.request)\"");

        Assert.Equal(
            $"{StandInRun.TaskHash("01-small-edit")}|{JsonDocument.Parse(File.ReadAllText(Path.Combine(tasks, "01-small-edit", "task.json"))).RootElement.GetProperty("request").GetString()}",
            answer);
    }

    [Fact]
    public async Task What_the_setup_approves_is_recorded_the_request_the_commands_of_the_checks_and_the_hash_of_the_task()
    {
        var tasks = Path.Combine(Repository, "bench", "tasks");

        var answer = await CallAsync(
            $"$task = Read-LiveTask -Task '01-small-edit' -Tasks {Quoted(tasks)}; "
            + "$record = New-LiveSetupRecord -Task '01-small-edit' -TaskFile $task -Program 'C:\\yav\\yav.exe' -ModelA 'codex-app-server a' -ModelB 'claude-cli b' -EffortA max -EffortB max -Routes @('codex:subscription', 'claude:subscription'); "
            + "$record | ConvertTo-Json -Depth 5 -Compress");

        var record = JsonDocument.Parse(answer).RootElement;
        Assert.Equal(StandInRun.TaskHash("01-small-edit"), record.GetProperty("taskHash").GetString());
        Assert.StartsWith("The greeting should end with a period", record.GetProperty("request").GetString(), StringComparison.Ordinal);
        Assert.Equal(["tests: python -m unittest discover -s tests -t ."], record.GetProperty("checks").EnumerateArray().Select(check => check.GetString()));
        Assert.False(record.GetProperty("setUp").GetBoolean());
    }

    [Theory]
    [InlineData("0000", "was changed after")]
    [InlineData(null, "does not record")]
    public async Task A_part_after_setup_does_nothing_when_the_task_file_is_not_what_was_approved(string? hash, string refused)
    {
        using var run = new ScriptRun();
        if (hash is null)
        {
            run.Setup.Remove("taskHash");
        }
        else
        {
            run.Setup["taskHash"] = hash;
        }

        run.SaveSetup();

        var result = await ContinueAsync(run, run.StandIn(), "-Part", "run", "-IAuthorizeUsage", "-Minutes", "5");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains(refused, Flatten(result.StandardOutput + result.StandardError), StringComparison.Ordinal);
        Assert.Empty(run.Starts);
        Assert.False(run.Exists("request.md"));
        Assert.False(run.Exists("run-begun.json"));
    }

    [Theory]
    [InlineData(true, "")]
    [InlineData(true, "FullyQualifiedName~Live")]
    [InlineData(false, "")]
    public async Task The_tests_of_the_product_never_include_the_parts_of_a_live_run(bool live, string filter)
    {
        var answer = await CallAsync(Path.Combine(Repository, "scripts", "test.ps1"), $"Get-TestFilter -Filter {Quoted(filter)} -Live:${live}");

        Assert.Contains("(FullyQualifiedName!~Yav.Tests.Live.LiveRunTests.)", answer, StringComparison.Ordinal);
        Assert.Equal(!live, answer.Contains("(Category!=Live)", StringComparison.Ordinal));
        Assert.Equal(filter.Length > 0, answer.StartsWith($"({filter})&", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("resume", true, "1")]
    [InlineData("resume", false, null)]
    [InlineData("setup", true, null)]
    [InlineData("inspect", true, null)]
    public async Task Only_a_part_that_asks_the_models_is_told_that_the_usage_was_authorized(string part, bool authorized, string? told)
    {
        var answer = await CallAsync(
            $"$variables = Get-LivePartVariables -Name {part} -Record '{part}-1' -Directory 'C:\\run' -Program 'C:\\yav\\yav.exe' -Task '01-small-edit' -TaskHash 'ABC' "
            + "-ModelA 'codex-app-server a' -ModelB 'claude-cli b' -EffortA max -EffortB max -Routes @('codex:subscription', 'claude:api-key') -TrustChecks:$true -Apply:$false "
            + $"-RunId 'r-1' -Minutes 30 -UsageAuthorized:${authorized}; "
            + "\"$($variables['YAV_LIVE_USAGE_AUTHORIZED'])|$($variables['YAV_LIVE_ACKNOWLEDGED'])|$($variables['YAV_LIVE_TASK_HASH'])|$($variables['YAV_LIVE_RUN'])\"");

        Assert.Equal($"{told}|codex:subscription,claude:api-key|ABC|{part}", answer);
    }

    [Fact]
    public async Task The_script_says_that_it_needs_powershell_7_2_or_later()
    {
        var required = await CallAsync("\"$($parsed.ScriptRequirements.RequiredPSVersion)\"");

        Assert.True(Version.TryParse(required, out var version) && version >= new Version(7, 2), $"The script requires '{required}'.");
    }

    [Fact]
    public async Task Windows_PowerShell_does_not_run_the_script_and_says_which_version_it_needs()
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            WorkingDirectory = Repository,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", Script, "-Part", "inspect" })
        {
            start.ArgumentList.Add(argument);
        }

        // Started below PowerShell 7, Windows PowerShell would look for its modules where PowerShell 7 keeps its own.
        start.Environment.Remove("PSModulePath");
        using var process = Process.Start(start)!;
        var said = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        Assert.NotEqual(0, process.ExitCode);
        Assert.Contains("7.2", Flatten(said), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Two_parts_that_start_at_once_in_one_directory_get_records_of_their_own()
    {
        using var directory = new TempDirectory("live run");
        var at = Quoted(directory.Path);

        var names = await CallAsync($"Get-LiveRecordName -Directory {at} -Part resume; Get-LiveRecordName -Directory {at} -Part resume; Get-LiveRecordName -Directory {at} -Part inspect");

        Assert.Equal("resume-1\nresume-2\ninspect-1", names);
    }

    /// <summary>
    /// Calls Invoke-Console with the stand-in in the place of dotnet: it ends with the given exit code and shows the
    /// variables it was given, so that what the part is told, and what the variables are afterwards, can be seen.
    /// </summary>
    private static async Task<(string Said, string Started)> ConsoleAsync(TempDirectory directory, string part, int exitCode, string before)
    {
        var started = directory.File("stand-in-started.txt");
        var said = await CallAsync(
            $"$env:YAV_STANDIN_RECORD = {Quoted(started)}; $env:YAV_STANDIN_EXIT = '{exitCode}'; $env:YAV_STANDIN_SHOW = 'YAV_LIVE_TASK,YAV_LIVE_RUN_ID'; "
            + before
            + $"try {{ Invoke-Console -Name {part} -Record '{part}-1' -Test 'The_part' -Directory {Quoted(directory.Path)} -DotNet {Quoted(StandInYav.Executable)} -Tests 'Yav.Tests.csproj' "
            + "-Variables @{ YAV_LIVE_TASK = 'during'; YAV_LIVE_RUN_ID = 'r-during' } | Out-Null; 'went through' } catch { 'failed: ' + $_.Exception.Message }; "
            + "\"after: [$env:YAV_LIVE_TASK] [$([Environment]::GetEnvironmentVariable('YAV_LIVE_RUN_ID'))]\"");
        return (said, System.IO.File.Exists(started) ? await System.IO.File.ReadAllTextAsync(started) : string.Empty);
    }

    [Theory]
    [InlineData("resume", "usage may have been consumed")]
    [InlineData("inspect", "No model was asked")]
    [InlineData("setup", "No model was asked")]
    public async Task A_part_that_did_not_go_through_says_what_it_may_have_done(string part, string said)
    {
        using var directory = new TempDirectory("live run");

        var (answer, _) = await ConsoleAsync(directory, part, exitCode: 1, before: string.Empty);

        var failure = answer.Split('\n').Single(line => line.StartsWith("failed: ", StringComparison.Ordinal));
        Assert.Contains($"'{part}'", failure, StringComparison.Ordinal);
        Assert.Contains(said, failure, StringComparison.Ordinal);
        Assert.DoesNotContain("Nothing else was done", failure, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    public async Task The_variables_a_part_is_given_are_set_back_to_what_they_were(int exitCode)
    {
        using var directory = new TempDirectory("live run");

        var (answer, started) = await ConsoleAsync(
            directory, "inspect", exitCode, before: "$env:YAV_LIVE_TASK = 'before'; Remove-Item Env:YAV_LIVE_RUN_ID -ErrorAction SilentlyContinue; ");

        Assert.Contains("env:YAV_LIVE_TASK=during", started, StringComparison.Ordinal);
        Assert.Contains("env:YAV_LIVE_RUN_ID=r-during", started, StringComparison.Ordinal);
        Assert.EndsWith("after: [before] []", answer, StringComparison.Ordinal);
    }

    private static readonly string[] HostingSession =
    [
        "CLAUDECODE", "CLAUDE_CODE_ENTRYPOINT", "CLAUDE_CODE_SESSION_ID", "CLAUDE_CODE_CHILD_SESSION", "CLAUDE_CODE_SESSION_ATTENDED",
        "CLAUDE_CODE_MESSAGING_SOCKET", "CLAUDE_CODE_MESSAGING_TOKEN", "CLAUDE_CODE_EXECPATH", "CLAUDE_PID", "CLAUDE_EFFORT",
        "GIT_EDITOR", "NoDefaultCurrentDirectoryInExePath", "COREPACK_ENABLE_AUTO_PIN",
    ];

    /// <summary>PowerShell that gives every variable of a hosting session a value of its own.</summary>
    private static string InHostingSession => $"$names = @({string.Join(", ", HostingSession.Select(Quoted))}); "
        + "foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, \"stand-in for $name\") }; ";

    /// <summary>PowerShell that says how many of the variables of a hosting session have their own value again.</summary>
    private const string PutBack = "@($names | Where-Object { [Environment]::GetEnvironmentVariable($_) -eq \"stand-in for $_\" }).Count";

    [Fact]
    public async Task Every_variable_of_a_hosting_session_is_taken_out_and_put_back()
    {
        var answer = await CallAsync(
            InHostingSession
            + "$taken = Remove-HostingSessionVariables; $during = @($names | Where-Object { $null -ne [Environment]::GetEnvironmentVariable($_) }).Count; "
            + $"Restore-HostingSessionVariables $taken; \"$during|$({PutBack})\"");

        Assert.Equal($"0|{HostingSession.Length}", answer);
    }

    [Fact]
    public async Task The_variables_of_a_hosting_session_are_not_passed_to_the_program_and_are_put_back_also_when_a_part_fails()
    {
        using var run = new ScriptRun();

        // Without the number of a run, inspect with -Apply fails before it starts anything.
        var standIn = run.StandIn(lines: [$$"""{"type":"result","outcome":"ready_to_apply","exitCode":0}"""]);
        standIn["YAV_STANDIN_SHOW"] = string.Join(',', HostingSession);

        // Run in this PowerShell, so that what the script leaves behind in it can be seen.
        var answer = await CallAsync(
            InHostingSession
            + string.Concat(standIn.Select(variable => $"$env:{variable.Key} = {Quoted(variable.Value!)}; "))
            + $"& {Quoted(Script)} -Part run -IAuthorizeUsage -Minutes 5 -Directory {Quoted(run.Directory)} *> $null; \"ran: $({PutBack})\"; "
            + $"try {{ & {Quoted(Script)} -Part inspect -Apply -Directory {Quoted(run.Directory)} *> $null; 'inspected' }} catch {{ \"failed: $({PutBack})\" }}");

        Assert.Equal($"ran: {HostingSession.Length}\nfailed: {HostingSession.Length}", answer);
        var shown = run.Read("stand-in-started.txt").ReplaceLineEndings("\n").Split('\n').Where(line => line.StartsWith("env:", StringComparison.Ordinal)).ToList();
        Assert.Equal(HostingSession.Select(name => "env:" + name), shown);
    }

    [Fact]
    public async Task A_run_that_went_through_is_summed_up_and_the_script_ends_without_an_error()
    {
        using var run = new ScriptRun();
        var standIn = run.StandIn(lines: [$$"""{"type":"event","runId":"r-1","event":"run.started"}""", $$"""{"type":"result","outcome":"ready_to_apply","exitCode":0,"runId":"r-1"}"""]);

        var result = await ContinueAsync(run, standIn, "-Part", "run", "-IAuthorizeUsage", "-Minutes", "5");

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        var summary = JsonDocument.Parse(run.Read("run-summary.json")).RootElement;
        Assert.True(summary.GetProperty("endedByItself").GetBoolean());
        Assert.Equal(JsonValueKind.Null, summary.GetProperty("stoppedBy").ValueKind);
        Assert.True(summary.GetProperty("tiedToScript").GetBoolean());
        Assert.Equal("r-1", summary.GetProperty("runId").GetString());

        // The program was started once, in the project, with the data directory of the run and the request of the task.
        var started = run.Read("stand-in-started.txt").ReplaceLineEndings("\n").Split('\n');
        Assert.Contains("home=" + run.File("home"), started);
        Assert.Contains("cwd=" + run.File("project"), started);
        Assert.Contains("arg=" + run.File("request.md"), started);
        Assert.Single(run.Starts);
    }

    [Theory]
    [InlineData("run-summary.json")]
    [InlineData("run-begun.json")]
    [InlineData("run-output.jsonl")]
    public async Task A_second_run_in_a_directory_is_refused_before_its_request_is_written_or_its_program_started(string left)
    {
        using var run = new ScriptRun();
        var standIn = run.StandIn(lines: [$$"""{"type":"result","outcome":"ready_to_apply","exitCode":0,"runId":"r-1"}"""]);
        var records = new Dictionary<string, string> { ["request.md"] = "The request of the first run." };
        if (left == "run-summary.json")
        {
            // A first run that went through, made by the script itself.
            Assert.Equal(0, (await ContinueAsync(run, standIn, "-Part", "run", "-IAuthorizeUsage", "-Minutes", "5")).ExitCode);
            records["run-summary.json"] = run.Read("run-summary.json");
            records["run-output.jsonl"] = run.Read("run-output.jsonl");
        }
        else
        {
            records[left] = "{}";
        }

        foreach (var (name, content) in records)
        {
            run.Write(name, content);
        }

        var starts = run.Starts.Count;

        var second = await ContinueAsync(run, standIn, "-Part", "run", "-IAuthorizeUsage", "-Minutes", "5");

        Assert.NotEqual(0, second.ExitCode);
        Assert.Contains("A run was", Flatten(second.StandardOutput + second.StandardError), StringComparison.Ordinal);
        Assert.Equal(starts, run.Starts.Count);
        foreach (var (name, content) in records)
        {
            Assert.Equal(content, run.Read(name));
        }
    }

    private const string SetUpRoutes = """
        [
          { "provider": "codex", "kind": "subscription", "label": "ChatGPT plan (pro)", "route": "codex-app-server:Subscription:openai" },
          { "provider": "claude", "kind": "api-key", "label": "Anthropic API key (configured in Claude Code)", "route": "claude-cli:ApiKey:firstParty" }
        ]
        """;

    [Fact]
    public async Task The_routes_the_setup_acknowledged_are_shown_with_their_labels_and_recorded()
    {
        using var directory = new TempDirectory("live run");
        directory.Write("setup-routes.json", SetUpRoutes);

        var shown = await CallAsync(
            $"Complete-LiveRunSetup -Directory {Quoted(directory.Path)} -Setup ([ordered]@{{ task = '01-small-edit'; acknowledged = @('codex:subscription', 'claude:api-key'); setUp = $false }})");

        Assert.Equal("codex: ChatGPT plan (pro) (subscription)\nclaude: Anthropic API key (configured in Claude Code) (api-key)", shown);
        var recorded = JsonDocument.Parse(directory.Read("live-run.json")).RootElement;
        Assert.True(recorded.GetProperty("setUp").GetBoolean());
        Assert.Equal(
            ["ChatGPT plan (pro)", "Anthropic API key (configured in Claude Code)"],
            recorded.GetProperty("routes").EnumerateArray().Select(route => route.GetProperty("label").GetString()));
    }

    [Fact]
    public async Task A_setup_that_recorded_no_routes_does_not_count_as_set_up()
    {
        using var directory = new TempDirectory("live run");

        var answer = await CallAsync(
            $"try {{ Complete-LiveRunSetup -Directory {Quoted(directory.Path)} -Setup ([ordered]@{{ setUp = $false }}) | Out-Null; 'set up' }} catch {{ 'refused: ' + $_.Exception.Message }}");

        Assert.StartsWith("refused: ", answer, StringComparison.Ordinal);
        Assert.False(directory.Exists("live-run.json"));
    }
}
