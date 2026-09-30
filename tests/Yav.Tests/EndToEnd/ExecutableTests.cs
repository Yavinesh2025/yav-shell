using System.Text.Json;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.EndToEnd;

/// <summary>
/// yav.exe itself, started the way a script starts it: input and output are pipes. The agents are the
/// scripted fixture, so nothing here requests inference.
/// </summary>
public class ExecutableTests
{
    private const string Task = "Make the app say fixed.";

    [Fact]
    public async Task It_says_its_version()
    {
        using var yav = new YavProcess();

        var result = await yav.RunAsync(["--version"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("yav " + Fixtures.ProductVersion, result.Output.Trim());
        Assert.Equal(string.Empty, result.Error);
    }

    [Fact]
    public async Task It_explains_how_it_is_started()
    {
        using var yav = new YavProcess();

        var result = await yav.RunAsync(["--help"]);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("yav <path>", result.Output, StringComparison.Ordinal);
        Assert.Contains("yav run --project <path> --task <text>", result.Output, StringComparison.Ordinal);
        Assert.Contains("yav run --project <path> --prompt-file <file> [--json]", result.Output, StringComparison.Ordinal);
        Assert.Contains("yav doctor", result.Output, StringComparison.Ordinal);
        Assert.Contains("3  approval required", result.Output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("--bogus")]
    [InlineData("run")]
    [InlineData("run|--task")]
    [InlineData("run|--task|x|--prompt-file|y")]
    [InlineData("doctor|--apply")]
    [InlineData("one|two")]
    public async Task A_command_line_that_is_not_understood_does_nothing_and_says_why(string arguments)
    {
        using var yav = new YavProcess().WithPassingRun();

        var result = await yav.RunAsync(arguments.Split('|'));

        Assert.Equal(64, result.ExitCode);
        Assert.Equal(string.Empty, result.Output);
        Assert.StartsWith("yav: ", result.Error, StringComparison.Ordinal);
        Assert.Contains("See 'yav --help'.", result.Error, StringComparison.Ordinal);
        Assert.Empty(yav.Agents.CodexRequests("initialize"));
    }

    [Fact]
    public async Task With_json_a_command_line_that_is_not_understood_is_reported_as_json()
    {
        using var yav = new YavProcess();

        var result = await yav.RunAsync(["run", "--json"]);

        Assert.Equal(64, result.ExitCode);
        var report = Assert.Single(result.Json);
        Assert.Equal("invalid", report.GetProperty("outcome").GetString());
        Assert.Equal(64, report.GetProperty("exitCode").GetInt32());
    }

    [Fact]
    public async Task A_run_with_json_writes_nothing_but_json_lines_and_leaves_the_project_alone()
    {
        using var yav = new YavProcess().WithPassingRun();
        var before = yav.Project.Snapshot();

        var result = await yav.RunAsync(["run", "--project", yav.Project.Path, "--task", Task, "--json"], workingDirectory: Path.GetTempPath());

        Assert.Equal(0, result.ExitCode);
        Assert.Equal(string.Empty, result.Error);
        Assert.DoesNotContain('\u001b', result.Output);
        var lines = result.Json;
        Assert.All(lines, line => Assert.Equal(JsonValueKind.Object, line.ValueKind));
        Assert.Equal("result", lines[^1].GetProperty("type").GetString());
        Assert.Equal("ready_to_apply", lines[^1].GetProperty("outcome").GetString());
        Assert.Equal(RunState.ReadyToApply, yav.FindRun(lines[^1].GetProperty("runId").GetString()!)!.State);
        Assert.Equal("one\n", yav.Project.Read("src/app.txt"));
        Assert.Equal(before, yav.Project.Snapshot());
    }

    [Fact]
    public async Task A_run_with_apply_writes_the_accepted_candidate()
    {
        using var yav = new YavProcess().WithPassingRun();

        var result = await yav.RunAsync(["run", "-p", yav.Project.Path, "-t", Task, "--json", "--apply"]);

        Assert.Equal(0, result.ExitCode);
        Assert.True(result.Json[^1].GetProperty("apply").GetProperty("applied").GetBoolean());
        Assert.Equal("fixed\n", yav.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task The_request_is_taken_from_a_file()
    {
        using var yav = new YavProcess().WithPassingRun();
        var file = Path.Combine(yav.Paths.Home, "the request.md");
        File.WriteAllText(file, "Make the app say fixed.\nKeep \"quotes\" and %PATH% as they are.\n");

        var result = await yav.RunAsync(["run", "--project", yav.Project.Path, "--prompt-file", file, "--json"]);

        Assert.Equal(0, result.ExitCode);
        var sent = yav.Agents.CodexRequests("turn/start")[0]["params"]!["input"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("Keep \"quotes\" and %PATH% as they are.", sent, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_json_a_run_writes_plain_lines_without_control_sequences()
    {
        using var yav = new YavProcess().WithPassingRun();

        var result = await yav.RunAsync(["run", "--project", yav.Project.Path, "--task", Task]);

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain('\u001b', result.Output);
        Assert.Contains(result.Lines, line => line.StartsWith("[PREPARE]", StringComparison.Ordinal));
        Assert.Contains(result.Lines, line => line.StartsWith("[READY]", StringComparison.Ordinal));
        Assert.Contains("Outcome:  Ready to Apply", result.Lines);
        Assert.Contains(result.Lines, line => line.Contains("Changed app.txt to say fixed.", StringComparison.Ordinal) && line.StartsWith("  ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_request_for_approval_ends_the_run_as_approval_required_instead_of_waiting()
    {
        using var yav = new YavProcess();
        yav.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        yav.Agents.ImplementerTurn(Step.Approval(
            "npm install left-pad",
            onAccept: [Step.Write("src/app.txt", "installed\n"), Step.Message("Installed.")],
            onDecline: [Step.Message("Not installed.")]));

        var result = await yav.RunAsync(["run", "--project", yav.Project.Path, "--task", Task, "--json"], seconds: 90);

        Assert.Equal(3, result.ExitCode);
        Assert.Equal("approval_required", result.Json[^1].GetProperty("outcome").GetString());
        Assert.Equal("npm install left-pad", result.Json[^1].GetProperty("pendingApprovals")[0].GetProperty("command").GetString());
        Assert.DoesNotContain("Installed.", result.Output, StringComparison.Ordinal);
        Assert.Equal("one\n", yav.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task A_route_that_was_not_acknowledged_blocks_the_run()
    {
        using var yav = new YavProcess(acknowledgeRoutes: false).WithPassingRun();

        var result = await yav.RunAsync(["run", "--project", yav.Project.Path, "--task", Task, "--json"]);

        Assert.Equal(2, result.ExitCode);
        Assert.Contains(result.Json[^1].GetProperty("problems").EnumerateArray(), p => p.GetProperty("code").GetString() == "route-unacknowledged");
        Assert.Empty(yav.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Doctor_reports_as_json_and_asks_no_model_anything()
    {
        using var yav = new YavProcess().WithPassingRun();

        var result = await yav.RunAsync(["doctor", "--json"]);

        Assert.Equal(0, result.ExitCode);
        var report = Assert.Single(result.Json);
        Assert.Equal("doctor", report.GetProperty("type").GetString());
        var checks = report.GetProperty("checks").EnumerateArray().ToList();
        Assert.Contains(checks, c => c.GetProperty("name").GetString() == "Console" && c.GetProperty("detail").GetString()!.StartsWith("Input or output is redirected", StringComparison.Ordinal));
        Assert.Contains(checks, c => c.GetProperty("name").GetString() == "Data directory" && c.GetProperty("detail").GetString()!.StartsWith(yav.Paths.Home, StringComparison.Ordinal));
        Assert.Empty(yav.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task The_shell_reads_lines_from_a_pipe_one_after_the_other_and_waits_for_each_run()
    {
        using var yav = new YavProcess().WithPassingRun();

        var result = await yav.RunAsync([yav.Project.Path], input: $"{Task}\n/apply\n/exit\n");

        Assert.Equal(0, result.ExitCode);
        Assert.DoesNotContain('\u001b', result.Output);
        var lines = result.Lines.ToList();
        var ready = lines.FindIndex(l => l.StartsWith("[READY]", StringComparison.Ordinal));
        var done = lines.FindIndex(l => l.StartsWith("[DONE]", StringComparison.Ordinal));
        Assert.True(ready >= 0 && done > ready, result.Output);
        Assert.DoesNotContain(lines, l => l.Contains("waits until the active run has ended", StringComparison.Ordinal));
        Assert.Equal("fixed\n", yav.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task The_end_of_piped_input_lets_the_run_finish()
    {
        using var yav = new YavProcess().WithPassingRun();

        var result = await yav.RunAsync([yav.Project.Path], input: Task + "\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.StartsWith("[READY]", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Lines, l => l.StartsWith("[STOPPED]", StringComparison.Ordinal));
        Assert.Equal(2, yav.Agents.CodexRequests("turn/start").Count);
        Assert.Empty(yav.Agents.CodexRequests("turn/interrupt"));
        Assert.Equal("one\n", yav.Project.Read("src/app.txt"));
    }

    [Fact]
    public async Task Text_from_a_pipe_is_a_request_and_never_a_command_of_the_operating_system()
    {
        using var yav = new YavProcess().WithPassingRun();
        var marker = yav.Project.File("executed.txt");

        var result = await yav.RunAsync([yav.Project.Path], input: $"cmd /c echo executed> \"{marker}\"\n/exit\n");

        Assert.Equal(0, result.ExitCode);
        Assert.False(File.Exists(marker));
        Assert.Contains(yav.Agents.CodexRequests("turn/start"), t => t["params"]!["input"]![0]!["text"]!.GetValue<string>().Contains("cmd /c echo executed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Exec_gives_the_output_to_the_command_and_takes_it_back_afterwards()
    {
        using var yav = new YavProcess();
        File.WriteAllText(
            yav.Paths.SettingsFile,
            File.ReadAllText(yav.Paths.SettingsFile).Replace("\"shell\": \"pwsh\"", "\"shell\": \"cmd\"", StringComparison.Ordinal));

        var result = await yav.RunAsync([yav.Project.Path], input: "/exec echo written by the command\n/status\n/exit\n");

        Assert.Equal(0, result.ExitCode);
        var lines = result.Lines.Select(l => l.TrimEnd()).ToList();
        var announced = lines.FindIndex(l => l.StartsWith("[LOCAL]   cmd in ", StringComparison.Ordinal));
        var written = lines.IndexOf("written by the command");
        var ended = lines.FindIndex(l => l.StartsWith("[LOCAL]   exit 0 after", StringComparison.Ordinal));
        var status = lines.FindIndex(l => l.StartsWith("Status", StringComparison.Ordinal));
        Assert.True(announced >= 0 && written > announced && ended > written && status > ended, result.Output);
        Assert.Empty(yav.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task A_shell_is_not_started_when_there_is_no_console_to_give_it()
    {
        using var yav = new YavProcess();

        var result = await yav.RunAsync([yav.Project.Path], input: "/shell cmd\n/exit\n", seconds: 60);

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("A shell needs a console. Input or output is not a terminal here, so none was started.", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_question_that_nobody_can_answer_is_answered_with_no()
    {
        using var yav = new YavProcess().WithPassingRun();

        var result = await yav.RunAsync([yav.Project.Path], input: $"{Task}\n/discard\n/speed provider\n/apply\n/exit\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("Nobody can be asked in this mode, so this was not done.", StringComparison.Ordinal));
        Assert.Contains(result.Lines, l => l.StartsWith("[DONE]", StringComparison.Ordinal));
        Assert.Equal("fixed\n", yav.Project.Read("src/app.txt"));
        Assert.DoesNotContain("\"speed\": \"Provider\"", File.ReadAllText(yav.Paths.SettingsFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task It_starts_without_a_project_when_the_directory_does_not_exist()
    {
        using var yav = new YavProcess();

        var result = await yav.RunAsync([Path.Combine(yav.Paths.Home, "no such project")], input: "/exit\n");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains(result.Lines, l => l.Contains("does not exist. Select a project with /open <path>.", StringComparison.Ordinal));
        Assert.Contains("Project: none selected - use /open <path>", result.Lines);
    }
}
