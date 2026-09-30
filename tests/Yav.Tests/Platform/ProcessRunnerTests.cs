using System.Diagnostics;
using System.Text;
using Yav.Core.Ports;
using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Platform;

public class ProcessRunnerTests
{
    private static readonly string[] HostileArguments =
    [
        "plain",
        "with space",
        "",
        "quote\"inside",
        @"trailing\",
        @"trailing space\ ",
        @"C:\Program Files\x\",
        "semi;colon & ampersand | pipe > redirect",
        "%PATH%",
        "caret^ and !bang!",
        "ünïcödé 日本語 🙂",
        "tab\tinside",
        "$(subshell) `backtick`",
        "--flag=value with space",
    ];

    [Fact]
    public async Task Arguments_arrive_exactly_as_given()
    {
        using var directory = new TempDirectory("args");

        var result = await Fixtures.RunToolAsync(directory.Path, ["echo-args", .. HostileArguments]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(HostileArguments, Fixtures.DecodeArguments(result.StandardOutput));
    }

    [Fact]
    public async Task A_cmd_launcher_receives_arguments_with_spaces_and_operators_intact()
    {
        using var directory = new TempDirectory("cmd");
        var launcher = directory.Write("launch tool.cmd", $"@echo off\r\n\"{Fixtures.FakeAgent}\" tool echo-args %*\r\n");
        string[] arguments = ["with space", "a&b", "plain", @"C:\dir with space\", "pipe|char"];
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            new ProcessSpec(launcher, arguments, directory.Path),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(arguments, Fixtures.DecodeArguments(result.StandardOutput));
    }

    [Fact]
    public async Task A_cmd_launcher_refuses_an_argument_cmd_would_expand()
    {
        using var directory = new TempDirectory("cmd");
        var launcher = directory.Write("t.cmd", $"@echo off\r\n\"{Fixtures.FakeAgent}\" tool echo-args %*\r\n");
        var runner = new ProcessRunner();

        await Assert.ThrowsAsync<UnsafeArgumentException>(() => runner.RunAsync(
            new ProcessSpec(launcher, ["%USERPROFILE%"], directory.Path),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
            CancellationToken.None));
    }

    [Fact]
    public async Task The_child_runs_in_the_requested_working_directory()
    {
        using var directory = new TempDirectory("cwd");

        var result = await Fixtures.RunToolAsync(directory.Path, "cwd");

        var reported = Fixtures.DecodeArguments(result.StandardOutput).Single();
        Assert.Equal(directory.Path, reported, ignoreCase: true);
    }

    [Fact]
    public async Task Environment_overrides_reach_the_child_and_removed_variables_do_not()
    {
        using var directory = new TempDirectory("env");
        Environment.SetEnvironmentVariable("YAV_TEST_SECRET", "parent-value");
        var runner = new ProcessRunner();
        try
        {
            var added = await runner.RunAsync(
                Fixtures.Tool(directory.Path, "env", "YAV_TEST_ADDED") with { Environment = new Dictionary<string, string?> { ["YAV_TEST_ADDED"] = "ünï value" } },
                new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
                CancellationToken.None);
            var removed = await runner.RunAsync(
                Fixtures.Tool(directory.Path, "env", "YAV_TEST_SECRET") with { Environment = new Dictionary<string, string?> { ["YAV_TEST_SECRET"] = null } },
                new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
                CancellationToken.None);

            Assert.Equal("ünï value", Fixtures.DecodeArguments(added.StandardOutput).Single());
            Assert.Equal("null", removed.StandardOutput.Trim());
        }
        finally
        {
            Environment.SetEnvironmentVariable("YAV_TEST_SECRET", null);
        }
    }

    [Fact]
    public async Task A_large_prompt_with_crlf_travels_over_standard_input_unchanged()
    {
        using var directory = new TempDirectory("stdin");
        var builder = new StringBuilder();
        for (var i = 0; i < 20_000; i++)
        {
            builder.Append("line ").Append(i).Append(" ünï 日本 \"quoted\" %PATH%\r\n");
        }

        var prompt = builder.ToString();
        var log = directory.File("out.log");
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            Fixtures.Tool(directory.Path, "stdin-echo"),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(60), StandardInput: prompt, LogPath: log, MaxCapturedCharacters: 8 * 1024 * 1024),
            CancellationToken.None);

        Assert.Equal(0, result.ExitCode);
        Assert.False(result.OutputTruncated);
        var lines = result.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(20_000, lines.Length);
        Assert.Equal("line 0 ünï 日本 \"quoted\" %PATH%", lines[0]);
        Assert.Equal("line 19999 ünï 日本 \"quoted\" %PATH%", lines[^1]);
    }

    [Fact]
    public async Task The_exit_code_and_standard_error_are_reported()
    {
        using var directory = new TempDirectory("exit");

        var result = await Fixtures.RunToolAsync(directory.Path, "stderr", "it went wrong", "7");

        Assert.Equal(7, result.ExitCode);
        Assert.False(result.Succeeded);
        Assert.Equal("it went wrong\n", result.StandardError);
        Assert.Equal(string.Empty, result.StandardOutput);
    }

    [Fact]
    public async Task A_character_split_across_two_reads_is_decoded_once()
    {
        using var directory = new TempDirectory("utf8");

        // "h", then the first byte of "é"; then its second byte, "日" and a line feed.
        var result = await Fixtures.RunToolAsync(directory.Path, "emit-hex", "68C3", "A9E697A50A");

        Assert.Equal("hé日\n", result.StandardOutput);
    }

    [Fact]
    public async Task Output_that_is_not_utf8_is_still_delivered_without_replacement_characters()
    {
        using var directory = new TempDirectory("oem");

        // 0x82 is not valid UTF-8 on its own. In the OEM code pages it is a letter.
        var result = await Fixtures.RunToolAsync(directory.Path, "emit-hex", "61826262 0A".Replace(" ", string.Empty));

        var line = result.StandardOutput.TrimEnd('\n');
        Assert.Equal(4, line.Length);
        Assert.StartsWith("a", line);
        Assert.EndsWith("bb", line);
        Assert.DoesNotContain('\uFFFD', line);
    }

    [Fact]
    public async Task Long_output_is_bounded_in_memory_but_complete_in_the_log()
    {
        using var directory = new TempDirectory("bounded");
        var log = directory.File("full.log");
        var runner = new ProcessRunner();

        var result = await runner.RunAsync(
            Fixtures.Tool(directory.Path, "lines", "5000"),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(30), MaxCapturedCharacters: 2048, LogPath: log),
            CancellationToken.None);

        Assert.True(result.OutputTruncated);
        Assert.True(result.StandardOutput.Length < 4096);
        Assert.StartsWith("line 1\n", result.StandardOutput);
        Assert.EndsWith("line 5000\n", result.StandardOutput);
        Assert.Contains("omitted", result.StandardOutput);
        Assert.Equal(5000, File.ReadAllLines(log).Length);
    }

    [Fact]
    public async Task A_timeout_ends_the_process_and_everything_it_started()
    {
        using var directory = new TempDirectory("timeout");
        var runner = new ProcessRunner();
        var childId = 0;

        var result = await runner.RunAsync(
            Fixtures.Tool(directory.Path, "spawn-tree", "120"),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(3), OnOutputLine: line => int.TryParse(line, out childId)),
            CancellationToken.None);

        Assert.True(result.TimedOut);
        Assert.NotEqual(0, childId);
        Assert.True(await WaitUntilDeadAsync(childId), $"Grandchild {childId} survived the timeout.");
    }

    [Fact]
    public async Task Cancelling_ends_owned_processes_and_leaves_unrelated_processes_alive()
    {
        using var directory = new TempDirectory("cancel");
        var runner = new ProcessRunner();

        // Started without YAV's process runner: this stands for the user's own editor, shell or agent.
        var unrelatedStart = new ProcessStartInfo(Fixtures.FakeAgent) { UseShellExecute = false, CreateNoWindow = true };
        unrelatedStart.ArgumentList.Add("tool");
        unrelatedStart.ArgumentList.Add("sleep");
        unrelatedStart.ArgumentList.Add("60");
        using var unrelated = Process.Start(unrelatedStart)!;
        try
        {
            var ownedChild = 0;
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));

            var result = await runner.RunAsync(
                Fixtures.Tool(directory.Path, "spawn-tree", "120"),
                new CaptureOptions(OnOutputLine: line => int.TryParse(line, out ownedChild)),
                cancellation.Token);

            Assert.True(result.Cancelled);
            Assert.NotEqual(0, ownedChild);
            Assert.True(await WaitUntilDeadAsync(ownedChild), "The owned grandchild survived cancellation.");
            Assert.False(unrelated.HasExited, "An unrelated process was terminated.");
        }
        finally
        {
            if (!unrelated.HasExited)
            {
                unrelated.Kill();
            }
        }
    }

    [Fact]
    public async Task Disposing_ends_a_background_child_that_outlived_its_parent()
    {
        using var directory = new TempDirectory("orphan");
        var runner = new ProcessRunner();
        int childId;

        var process = runner.Start(Fixtures.Tool(directory.Path, "spawn-tree", "120", "exit-now"));
        await using (process)
        {
            using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8, false, 1024, leaveOpen: true);
            childId = int.Parse((await reader.ReadLineAsync())!);
            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(ProcessRunner.IsProcessAlive(childId, null), "The background child should still be running before dispose.");
        }

        Assert.True(await WaitUntilDeadAsync(childId), $"Background child {childId} survived dispose.");
    }

    [Fact]
    public async Task Shutdown_lets_a_process_finish_when_its_input_closes()
    {
        using var directory = new TempDirectory("graceful");
        var runner = new ProcessRunner();

        var process = runner.Start(Fixtures.Tool(directory.Path, "stdin-lines"));
        await using (process)
        {
            await process.StandardInput.WriteAsync("hello\n"u8.ToArray());
            await process.StandardInput.FlushAsync();
            using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8, false, 1024, leaveOpen: true);
            var reply = await reader.ReadLineAsync();

            var exitedOnItsOwn = await process.ShutdownAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("got:hello", reply);
            Assert.True(exitedOnItsOwn);
            Assert.Equal(0, process.ExitCode);
        }
    }

    [Fact]
    public async Task Shutdown_terminates_a_process_that_ignores_the_request()
    {
        using var directory = new TempDirectory("forced");
        var runner = new ProcessRunner();

        var process = runner.Start(Fixtures.Tool(directory.Path, "sleep", "120"));
        await using (process)
        {
            var exitedOnItsOwn = await process.ShutdownAsync(TimeSpan.FromSeconds(1));

            Assert.False(exitedOnItsOwn);
            Assert.True(process.HasExited);
        }
    }

    [Fact]
    public void A_missing_executable_is_reported_by_name()
    {
        using var directory = new TempDirectory("missing");
        var runner = new ProcessRunner();

        var error = Assert.Throws<ExecutableNotFoundException>(() =>
            runner.Start(new ProcessSpec("yav-definitely-not-a-real-tool", [], directory.Path)));

        Assert.Equal("yav-definitely-not-a-real-tool", error.Command);
    }

    [Fact]
    public void A_missing_working_directory_is_reported()
    {
        var runner = new ProcessRunner();

        Assert.Throws<DirectoryNotFoundException>(() =>
            runner.Start(new ProcessSpec(Fixtures.FakeAgent, ["tool", "exit", "0"], @"C:\yav-no-such-directory-xyz")));
    }

    private static async Task<bool> WaitUntilDeadAsync(int processId)
    {
        for (var i = 0; i < 100; i++)
        {
            if (!ProcessRunner.IsProcessAlive(processId, null))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
