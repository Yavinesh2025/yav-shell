using Microsoft.Data.Sqlite;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Platform.Processes;
using Yav.Storage;
using Yav.Tests.Support;
using Yav.Validation;

namespace Yav.Tests.Validation;

public sealed class ValidationHarness : IDisposable
{
    private readonly TempDirectory _home = new("vhome");

    public ValidationHarness()
    {
        Database = YavDatabase.Open(_home.File("yav.db"), Clock);
        Service = new ValidationService(new ProcessRunner(), Database, Clock, "0.1.0");
        Evidence = _home.CreateDirectory("evidence");
    }

    public ManualClock Clock { get; } = new();

    public YavDatabase Database { get; }

    public ValidationService Service { get; }

    public string Evidence { get; }

    public GateRunContext Context(string root, bool baseline = false) =>
        new("run-1", root, Builders.Binding(), Evidence, baseline);

    /// <summary>A gate that runs the test fixture's tool mode, so its behavior is exactly known.</summary>
    public static GateDefinition Tool(string id, string[] arguments, int timeout = 60, string cwd = "", string[]? requires = null, Dictionary<string, string>? environment = null) => new(
        Id: id,
        Kind: GateKind.Test,
        Title: "Gate " + id,
        Command: Fixtures.FakeAgent,
        Arguments: ["tool", .. arguments],
        WorkingDirectory: cwd,
        TimeoutSeconds: timeout,
        Required: true,
        Environment: environment ?? [],
        SuccessExitCodes: [0],
        Requires: requires ?? []);

    public void Dispose()
    {
        Database.Dispose();
        SqliteConnection.ClearAllPools();
        _home.Dispose();
    }
}

public class ConfigurationParsingTests
{
    private const string Valid = """
        {
          // Comments and trailing commas are accepted.
          "schemaVersion": 1,
          "gates": [
            { "id": "build", "kind": "build", "title": "Build", "command": "dotnet", "args": ["build", "--nologo"], "timeoutSeconds": 900 },
            { "id": "unit", "kind": "test", "command": "dotnet", "args": ["test"], "cwd": "tests/unit", "required": false,
              "env": { "DOTNET_NOLOGO": "1" }, "successExitCodes": [0, 3], "requires": ["env:DATABASE_URL", "tool:docker"] },
          ],
          "prepare": [ { "id": "restore", "command": "dotnet", "args": ["restore"] } ],
          "protectedPaths": ["tests/acceptance/**"],
          "replicateIgnored": ["local.settings.json"],
          "allowSecrets": [],
          "validation": { "execution": "candidate" }
        }
        """;

    private static ValidationService Service(ValidationHarness harness) => harness.Service;

    [Fact]
    public void A_valid_configuration_is_read_completely()
    {
        using var harness = new ValidationHarness();

        var configuration = Service(harness).Parse(Valid, out var errors);

        Assert.Empty(errors);
        Assert.Equal(["build", "unit"], configuration.Gates.Select(g => g.Id));
        var build = configuration.Gates[0];
        Assert.Equal(GateKind.Build, build.Kind);
        Assert.Equal(["build", "--nologo"], build.Arguments);
        Assert.Equal(900, build.TimeoutSeconds);
        Assert.True(build.Required);
        Assert.Equal(string.Empty, build.WorkingDirectory);
        var unit = configuration.Gates[1];
        Assert.False(unit.Required);
        Assert.Equal("tests/unit", unit.WorkingDirectory);
        Assert.Equal("1", unit.Environment["DOTNET_NOLOGO"]);
        Assert.Equal([0, 3], unit.SuccessExitCodes);
        Assert.Equal(["env:DATABASE_URL", "tool:docker"], unit.Requires);
        Assert.Equal("restore", Assert.Single(configuration.Prepare).Id);
        Assert.Equal(["tests/acceptance/**"], configuration.ProtectedPaths);
        Assert.Equal(ProjectConfiguration.ExecutionCandidate, configuration.ValidationExecution);
        Assert.Equal(["build"], configuration.RequiredGates.Select(g => g.Id));
    }

    [Fact]
    public void Defaults_apply_to_what_is_left_out()
    {
        using var harness = new ValidationHarness();

        var configuration = Service(harness).Parse("""{ "gates": [ { "id": "t", "command": "npm", "args": ["test"] } ] }""", out var errors);

        Assert.Empty(errors);
        var gate = Assert.Single(configuration.Gates);
        Assert.True(gate.Required);
        Assert.Equal(GateKind.Custom, gate.Kind);
        Assert.Equal([0], gate.SuccessExitCodes);
        Assert.Equal(ProjectConfiguration.ExecutionCopy, configuration.ValidationExecution);
        Assert.Equal(ProjectConfiguration.DefaultTestPaths, configuration.TestPaths);
    }

    [Theory]
    [InlineData("""{ "gates": [ { "id": "a", "command": "x" }, { "id": "a", "command": "y" } ] }""", "more than once")]
    [InlineData("""{ "gates": [ { "id": "a" } ] }""", "command")]
    [InlineData("""{ "gates": [ { "command": "x" } ] }""", "id")]
    [InlineData("""{ "gates": [ { "id": "bad id!", "command": "x" } ] }""", "id")]
    [InlineData("""{ "gates": [ { "id": "a", "command": "x", "cwd": "../outside" } ] }""", "cwd")]
    [InlineData("""{ "gates": [ { "id": "a", "command": "x", "cwd": "C:/Windows" } ] }""", "cwd")]
    [InlineData("""{ "gates": [ { "id": "a", "command": "x", "timeoutSeconds": 0 } ] }""", "timeoutSeconds")]
    [InlineData("""{ "gates": [ { "id": "a", "command": "x", "kind": "sorcery" } ] }""", "kind")]
    [InlineData("""{ "gates": [ { "id": "a", "command": "x", "requires": ["telepathy:now"] } ] }""", "requires")]
    [InlineData("""{ "gates": [ { "id": "a", "command": "x", "args": "not an array" } ] }""", "args")]
    [InlineData("""{ "gates": [ { "id": "a", "command": "x", "successExitCodes": [] } ] }""", "successExitCodes")]
    [InlineData("""{ "schemaVersion": 99, "gates": [] }""", "schemaVersion")]
    [InlineData("""{ "validation": { "execution": "anywhere" } }""", "execution")]
    [InlineData("""{ "gates": [ { "id": "a", "command": "x", "surprise": true } ] }""", "surprise")]
    [InlineData("""{ "gates": """, "JSON")]
    [InlineData("""[1, 2, 3]""", "object")]
    public void An_invalid_configuration_says_what_is_wrong(string json, string expectedInMessage)
    {
        using var harness = new ValidationHarness();

        Service(harness).Parse(json, out var errors);

        Assert.NotEmpty(errors);
        Assert.Contains(errors, e => e.Contains(expectedInMessage, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Writing_and_reading_a_configuration_gives_the_same_configuration()
    {
        using var harness = new ValidationHarness();
        var original = Service(harness).Parse(Valid, out _);

        var restored = Service(harness).Parse(Service(harness).Serialize(original), out var errors);

        Assert.Empty(errors);
        Assert.Equal(Service(harness).Serialize(original), Service(harness).Serialize(restored));
        Assert.Equal(original.Gates[1].Requires, restored.Gates[1].Requires);
        Assert.Equal(original.Gates[1].Environment, restored.Gates[1].Environment);
    }
}

public class ConfigurationTrustTests
{
    private const string OneGate = """{ "gates": [ { "id": "t", "command": "npm", "args": ["test"] } ] }""";
    private const string WeakenedGate = """{ "gates": [ { "id": "t", "command": "cmd", "args": ["/c", "exit 0"] } ] }""";

    [Fact]
    public void A_project_without_a_configuration_has_no_gates()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("project");

        var state = harness.Service.LoadConfiguration(project.Path);

        Assert.Equal(ConfigurationTrust.None, state.Trust);
        Assert.Empty(state.Effective.Gates);
    }

    [Fact]
    public void A_configuration_that_was_never_approved_is_not_in_effect()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("project");
        project.Write("yav.project.json", OneGate);

        var state = harness.Service.LoadConfiguration(project.Path);

        Assert.Equal(ConfigurationTrust.Untrusted, state.Trust);
        Assert.Empty(state.Effective.Gates);
        Assert.Equal("t", Assert.Single(state.Pending!.Gates).Id);
    }

    [Fact]
    public void An_approved_configuration_is_in_effect()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("project");
        project.Write("yav.project.json", OneGate);
        var pending = harness.Service.LoadConfiguration(project.Path).Pending!;

        harness.Service.TrustConfiguration(project.Path, pending, harness.Service.Serialize(pending));
        var state = harness.Service.LoadConfiguration(project.Path);

        Assert.Equal(ConfigurationTrust.Trusted, state.Trust);
        Assert.Equal("npm", Assert.Single(state.Effective.Gates).Command);
        Assert.Null(state.Pending);
        Assert.Equal(state.TrustedHash, state.FileHash);
    }

    [Fact]
    public void A_change_to_the_file_does_not_take_effect_until_it_is_approved()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("project");
        project.Write("yav.project.json", OneGate);
        var pending = harness.Service.LoadConfiguration(project.Path).Pending!;
        harness.Service.TrustConfiguration(project.Path, pending, harness.Service.Serialize(pending));

        // For example an agent that makes a failing gate pass by replacing its command.
        project.Write("yav.project.json", WeakenedGate);
        var state = harness.Service.LoadConfiguration(project.Path);

        Assert.Equal(ConfigurationTrust.Changed, state.Trust);
        Assert.Equal("npm", Assert.Single(state.Effective.Gates).Command);
        Assert.Equal("cmd", Assert.Single(state.Pending!.Gates).Command);
        Assert.NotEqual(state.TrustedHash, state.FileHash);
    }

    [Fact]
    public void Reformatting_the_file_is_not_a_change()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("project");
        project.Write("yav.project.json", OneGate);
        var pending = harness.Service.LoadConfiguration(project.Path).Pending!;
        harness.Service.TrustConfiguration(project.Path, pending, harness.Service.Serialize(pending));

        project.Write("yav.project.json", "{\r\n  // reformatted\r\n  \"gates\": [\r\n    { \"command\": \"npm\", \"args\": [\"test\"], \"id\": \"t\" }\r\n  ]\r\n}\r\n");
        var state = harness.Service.LoadConfiguration(project.Path);

        Assert.Equal(ConfigurationTrust.Trusted, state.Trust);
    }

    [Fact]
    public void A_file_that_cannot_be_read_keeps_the_approved_configuration_in_effect()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("project");
        project.Write("yav.project.json", OneGate);
        var pending = harness.Service.LoadConfiguration(project.Path).Pending!;
        harness.Service.TrustConfiguration(project.Path, pending, harness.Service.Serialize(pending));

        project.Write("yav.project.json", "{ broken");
        var state = harness.Service.LoadConfiguration(project.Path);

        Assert.Equal(ConfigurationTrust.Invalid, state.Trust);
        Assert.Equal("npm", Assert.Single(state.Effective.Gates).Command);
        Assert.NotEmpty(state.Errors);
    }

    [Fact]
    public void Gates_approved_inside_yav_need_no_file_in_the_project()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("project");
        var configuration = harness.Service.Parse(OneGate, out _);

        harness.Service.TrustConfiguration(project.Path, configuration, harness.Service.Serialize(configuration));
        var state = harness.Service.LoadConfiguration(project.Path);

        Assert.Equal(ConfigurationTrust.Trusted, state.Trust);
        Assert.Single(state.Effective.Gates);
        Assert.False(project.Exists("yav.project.json"));
    }
}

public class GateRunTests
{
    [Fact]
    public async Task A_command_that_exits_with_zero_passes()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("ok", ["lines", "3"]);

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Passed, result.Status);
        Assert.Equal(0, result.ExitCode);
        Assert.Equal(Builders.Binding(), result.Binding);
        Assert.Equal("line 1\nline 2\nline 3\n", result.OutputTail);
        Assert.Equal("line 1\nline 2\nline 3\n", (await File.ReadAllTextAsync(result.OutputPath!)).ReplaceLineEndings("\n"));
        Assert.StartsWith(harness.Evidence, result.OutputPath!, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.IsBaselineRun);
    }

    [Fact]
    public async Task A_command_that_exits_with_an_error_fails_and_keeps_its_output()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("bad", ["stderr", "2 tests failed", "1"]);

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Failed, result.Status);
        Assert.Equal(1, result.ExitCode);
        Assert.Contains("2 tests failed", result.OutputTail);
    }

    [Fact]
    public async Task An_exit_code_the_gate_lists_as_success_passes()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("three", ["exit", "3"]) with { SuccessExitCodes = [0, 3] };

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Passed, result.Status);
        Assert.Equal(3, result.ExitCode);
    }

    [Fact]
    public async Task A_command_that_runs_too_long_times_out()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("slow", ["sleep", "60"], timeout: 2);

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.TimedOut, result.Status);
        Assert.True(result.DurationMs < 30_000);
    }

    [Fact]
    public async Task A_cancelled_gate_is_reported_as_cancelled()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("slow", ["sleep", "60"]);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, cancellation.Token);

        Assert.Equal(GateStatus.Cancelled, result.Status);
    }

    [Fact]
    public async Task A_command_that_does_not_exist_is_an_error_not_a_pass()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("missing", []) with { Command = "yav-no-such-tool-xyz", Arguments = [] };

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Error, result.Status);
        Assert.Null(result.ExitCode);
        Assert.Contains("yav-no-such-tool-xyz", result.Limitation);
    }

    [Fact]
    public async Task A_program_planted_in_the_project_is_not_run_in_place_of_a_tool()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        root.Write("yav-planted-tool.cmd", "@echo off\r\necho planted> planted-marker.txt\r\n");
        var gate = ValidationHarness.Tool("planted", []) with { Command = "yav-planted-tool", Arguments = [] };

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Error, result.Status);
        Assert.False(root.Exists("planted-marker.txt"), "A file in the project was executed because its name matched the command.");
    }

    [Fact]
    public async Task A_script_named_by_an_explicit_path_is_run_from_the_checked_copy()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        root.Write("scripts/check it.cmd", "@echo off\r\necho checked %1\r\nexit /b 0\r\n");
        var gate = ValidationHarness.Tool("script", []) with { Command = @".\scripts\check it.cmd", Arguments = ["with space"] };

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Passed, result.Status);
        Assert.Contains("checked \"with space\"", result.OutputTail);
    }

    [Fact]
    public async Task The_gate_runs_in_its_own_working_directory()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var sub = root.CreateDirectory("packages/ünï app");
        var gate = ValidationHarness.Tool("cwd", ["cwd"], cwd: "packages/ünï app");

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Passed, result.Status);
        Assert.Equal(sub, Fixtures.DecodeArguments(result.OutputTail).Single(), ignoreCase: true);
        Assert.Equal(sub, result.WorkingDirectory, ignoreCase: true);
    }

    [Fact]
    public async Task A_working_directory_that_does_not_exist_is_an_error()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("cwd", ["cwd"], cwd: "no/such/dir");

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Error, result.Status);
        Assert.Contains("no/such/dir", result.Limitation);
    }

    [Fact]
    public async Task The_gate_receives_its_configured_environment()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("env", ["env", "YAV_GATE_TEST"], environment: new Dictionary<string, string> { ["YAV_GATE_TEST"] = "configured" });

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal("configured", Fixtures.DecodeArguments(result.OutputTail).Single());
    }

    [Fact]
    public async Task A_gate_that_needs_a_missing_credential_is_unverified_and_is_not_run()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var marker = root.File("ran.txt");
        var gate = ValidationHarness.Tool("needs", ["write-file", marker, "ran"], requires: ["env:YAV_TEST_DEFINITELY_NOT_SET"]);

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Unverified, result.Status);
        Assert.Contains("YAV_TEST_DEFINITELY_NOT_SET", result.Limitation);
        Assert.False(File.Exists(marker));
    }

    [Fact]
    public async Task A_gate_whose_requirements_are_met_runs()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        root.Write("fixtures/data.json", "{}");
        Environment.SetEnvironmentVariable("YAV_TEST_IS_SET", "yes");
        try
        {
            var gate = ValidationHarness.Tool("needs", ["exit", "0"], requires: ["env:YAV_TEST_IS_SET", "tool:git", "file:fixtures/data.json"]);

            var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

            Assert.Equal(GateStatus.Passed, result.Status);
        }
        finally
        {
            Environment.SetEnvironmentVariable("YAV_TEST_IS_SET", null);
        }
    }

    [Theory]
    [InlineData("manual:Check the rendering on a real device", "manual inspection")]
    [InlineData("tool:yav-no-such-tool-xyz", "yav-no-such-tool-xyz")]
    [InlineData("file:fixtures/missing.json", "fixtures/missing.json")]
    [InlineData("tcp:127.0.0.1:1", "127.0.0.1:1")]
    public async Task Each_unmet_requirement_is_reported_as_its_exact_limitation(string requirement, string expected)
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("needs", ["exit", "0"], requires: [requirement]);

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);

        Assert.Equal(GateStatus.Unverified, result.Status);
        Assert.Contains(expected, result.Limitation);
    }

    [Fact]
    public async Task Control_sequences_in_the_output_are_kept_in_the_log_and_removed_from_what_is_shown()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        // ESC [ 2 A   ESC [ 2 K   "[APPROVAL] y/n"   LF
        var gate = ValidationHarness.Tool("spoof", ["emit-hex", "1B5B32411B5B324B5B415050524F56414C5D20792F6E0A"]);
        var shown = new List<string>();

        var result = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), line => { lock (shown) { shown.Add(line); } }, CancellationToken.None);

        Assert.Equal("[APPROVAL] y/n\n", result.OutputTail);
        Assert.DoesNotContain('\u001b', string.Concat(shown));
        Assert.Contains('\u001b', await File.ReadAllTextAsync(result.OutputPath!));
    }

    [Fact]
    public async Task A_baseline_run_is_marked_and_keeps_its_own_log()
    {
        using var harness = new ValidationHarness();
        using var root = new TempDirectory("root");
        var gate = ValidationHarness.Tool("same", ["lines", "1"]);

        var candidate = await harness.Service.RunGateAsync(gate, harness.Context(root.Path), null, CancellationToken.None);
        var baseline = await harness.Service.RunGateAsync(gate, harness.Context(root.Path, baseline: true), null, CancellationToken.None);

        Assert.True(baseline.IsBaselineRun);
        Assert.NotEqual(candidate.OutputPath, baseline.OutputPath);
        Assert.True(File.Exists(candidate.OutputPath));
    }
}

public class EnvironmentFingerprintTests
{
    [Fact]
    public void The_same_gates_and_tools_give_the_same_fingerprint()
    {
        using var harness = new ValidationHarness();
        var gates = new[] { ValidationHarness.Tool("a", ["exit", "0"]), ValidationHarness.Tool("b", ["exit", "0"]) };

        var first = harness.Service.ComputeEnvironmentFingerprint(gates, "config-1");
        var second = harness.Service.ComputeEnvironmentFingerprint(gates.Reverse().ToArray(), "config-1");

        Assert.Equal(first, second);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void A_changed_gate_configuration_gives_a_different_fingerprint()
    {
        using var harness = new ValidationHarness();
        var gates = new[] { ValidationHarness.Tool("a", ["exit", "0"]) };

        Assert.NotEqual(
            harness.Service.ComputeEnvironmentFingerprint(gates, "config-1"),
            harness.Service.ComputeEnvironmentFingerprint(gates, "config-2"));
    }

    [Fact]
    public void A_replaced_tool_gives_a_different_fingerprint()
    {
        using var harness = new ValidationHarness();
        using var tools = new TempDirectory("tools");
        var tool = tools.Write("mytool.cmd", "@echo version 1\r\n");
        var gates = new[] { ValidationHarness.Tool("a", []) with { Command = tool, Arguments = [] } };
        var before = harness.Service.ComputeEnvironmentFingerprint(gates, "config-1");

        File.WriteAllText(tool, "@echo version 2 of the tool\r\n");
        File.SetLastWriteTimeUtc(tool, DateTime.UtcNow.AddMinutes(5));

        Assert.NotEqual(before, harness.Service.ComputeEnvironmentFingerprint(gates, "config-1"));
    }

    [Fact]
    public void A_tool_that_disappeared_gives_a_different_fingerprint()
    {
        using var harness = new ValidationHarness();
        using var tools = new TempDirectory("tools");
        var tool = tools.Write("mytool.cmd", "@echo version 1\r\n");
        var gates = new[] { ValidationHarness.Tool("a", []) with { Command = tool, Arguments = [] } };
        var before = harness.Service.ComputeEnvironmentFingerprint(gates, "config-1");

        File.Delete(tool);

        Assert.NotEqual(before, harness.Service.ComputeEnvironmentFingerprint(gates, "config-1"));
    }
}

public class GateDetectionTests
{
    [Fact]
    public void Node_scripts_are_proposed_with_the_package_manager_the_project_uses()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("node");
        project.Write("package.json", """{ "scripts": { "build": "tsc -p .", "lint": "eslint .", "test": "vitest run", "typecheck": "tsc --noEmit", "start": "node ." } }""");
        project.Write("pnpm-lock.yaml", "lockfileVersion: 9\n");

        var proposals = harness.Service.Detect(project.Path);

        Assert.Equal(["build", "lint", "typecheck", "test"], proposals.Select(p => p.Gate.Id));
        Assert.All(proposals, p => Assert.Equal("pnpm", p.Gate.Command));
        Assert.Equal(["run", "build"], proposals[0].Gate.Arguments);
        Assert.Equal(GateKind.Test, proposals[3].Gate.Kind);
    }

    [Fact]
    public void The_placeholder_test_script_that_always_fails_is_not_proposed()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("node");
        project.Write("package.json", """{ "scripts": { "test": "echo \"Error: no test specified\" && exit 1" } }""");

        Assert.Empty(harness.Service.Detect(project.Path));
    }

    [Fact]
    public void A_dotnet_solution_gets_build_and_test()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("dotnet");
        project.Write("App.slnx", "<Solution />");

        var proposals = harness.Service.Detect(project.Path);

        Assert.Equal(["build", "test"], proposals.Select(p => p.Gate.Id));
        Assert.All(proposals, p => Assert.Equal("dotnet", p.Gate.Command));
    }

    [Theory]
    [InlineData("Cargo.toml", "[package]\nname = \"x\"\n", "cargo")]
    [InlineData("go.mod", "module example.invalid/x\n", "go")]
    [InlineData("pyproject.toml", "[tool.pytest.ini_options]\n", "python")]
    public void Other_project_types_are_recognized(string file, string content, string command)
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("other");
        project.Write(file, content);

        var proposals = harness.Service.Detect(project.Path);

        Assert.NotEmpty(proposals);
        Assert.All(proposals, p => Assert.Equal(command, p.Gate.Command));
        Assert.Contains(proposals, p => p.Gate.Kind == GateKind.Test);
    }

    [Fact]
    public void A_directory_that_is_not_recognized_gets_no_proposal()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("unknown");
        project.Write("notes.txt", "nothing to build");

        Assert.Empty(harness.Service.Detect(project.Path));
    }

    [Fact]
    public void A_proposal_is_never_in_effect_by_itself()
    {
        using var harness = new ValidationHarness();
        using var project = new TempDirectory("dotnet");
        project.Write("App.slnx", "<Solution />");

        harness.Service.Detect(project.Path);

        Assert.Empty(harness.Service.LoadConfiguration(project.Path).Effective.Gates);
    }
}
