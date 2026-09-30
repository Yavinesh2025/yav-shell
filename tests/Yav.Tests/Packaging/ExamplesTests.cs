using System.Text.Json;
using Yav.Core.Runs;
using Yav.Tests.Support;
using Yav.Validation;

namespace Yav.Tests.Packaging;

/// <summary>
/// The files in examples\ are part of the documentation, so what they show has to be what YAV does.
/// The agents are the scripted stand-in; the program is the real one.
/// </summary>
public class ExamplesTests
{
    /// <summary>Set to 1 to write examples\run-output.jsonl anew from the run that the test makes.</summary>
    private const string WriteVariable = "YAV_WRITE_EXAMPLES";

    private static string Examples
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
            {
                directory = directory.Parent;
            }

            return Path.Combine(directory!.FullName, "examples");
        }
    }

    private static string[] ConfigurationFiles() => Directory
        .EnumerateFiles(Examples, "yav.project.json", SearchOption.AllDirectories)
        .Select(file => Path.GetRelativePath(Examples, file))
        .Order(StringComparer.Ordinal)
        .ToArray();

    public static TheoryData<string> Configurations() => new(ConfigurationFiles());

    [Fact]
    public void There_is_an_example_of_a_configuration_for_each_kind_of_project()
    {
        Assert.Equal([@"dotnet\yav.project.json", @"node\yav.project.json", @"python\yav.project.json"], ConfigurationFiles());
    }

    [Theory]
    [MemberData(nameof(Configurations))]
    public void A_configuration_that_is_given_as_an_example_is_one_yav_accepts(string file)
    {
        var configuration = ConfigurationParser.Parse(File.ReadAllText(Path.Combine(Examples, file)), out var errors);

        Assert.Empty(errors);
        Assert.NotEmpty(configuration.Gates);
        Assert.Contains(configuration.Gates, gate => gate.Required && gate.Kind == GateKind.Test);
    }

    /// <summary>A project with a misspelling, a check that notices it, and agents that correct and review it.</summary>
    private static YavProcess ProjectWithMisspelling()
    {
        var yav = new YavProcess(true, ("src/greeting.txt", "Helo, world!\n"), ("README.md", "# Greeting\n"));
        yav.TrustGates(new GateDefinition(
            Id: "spelling",
            Kind: GateKind.Test,
            Title: "The misspelling is gone",
            Command: "findstr",
            Arguments: ["/C:Helo", @"src\greeting.txt"],
            WorkingDirectory: string.Empty,
            TimeoutSeconds: 60,
            Required: true,
            Environment: new Dictionary<string, string>(),
            // findstr ends with 1 when it did not find the text.
            SuccessExitCodes: [1],
            Requires: []));
        yav.Agents
            .ImplementerTurn(Step.Write("src/greeting.txt", "Hello, world!\n"), Step.Message("Corrected \"Helo\" to \"Hello\" in src/greeting.txt."))
            .ReviewerTurn(Step.Review("pass"));
        return yav;
    }

    /// <summary>The output with the directories of this machine replaced by the ones the guide uses.</summary>
    private static string WithoutThisMachine(string output, YavProcess yav)
    {
        static string InJson(string path) => path.Replace(@"\", @"\\", StringComparison.Ordinal);

        var replacements = new (string From, string To)[]
        {
            (yav.Project.Path, @"C:\Projects\Greeting"),
            (yav.Paths.Home, @"C:\Users\you\AppData\Local\YavShell"),
            (yav.Agents.Workspace, @"C:\Users\you\AppData\Local\Temp\agents"),
            (Path.GetDirectoryName(Fixtures.FakeAgent)!, @"C:\Programs\agents"),
        };
        foreach (var (from, to) in replacements.OrderByDescending(r => r.From.Length))
        {
            output = output
                .Replace(InJson(from), InJson(to), StringComparison.OrdinalIgnoreCase)
                .Replace(InJson(from).Replace('\\', '/'), InJson(to), StringComparison.OrdinalIgnoreCase)
                .Replace(from.Replace('\\', '/'), to.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);
        }

        return output.ReplaceLineEndings("\n");
    }

    /// <summary>What kinds of lines there are: the event and the names of its fields.</summary>
    private static string[] ShapesOf(string output) => output
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => JsonDocument.Parse(line).RootElement)
        .Select(line =>
            (line.TryGetProperty("event", out var name) ? name.GetString() : line.GetProperty("type").GetString())
            + ": " + string.Join(' ', line.EnumerateObject().Select(property => property.Name)))
        .Distinct(StringComparer.Ordinal)
        .Order(StringComparer.Ordinal)
        .ToArray();

    [Fact]
    public async Task The_output_that_is_given_as_an_example_is_what_a_run_writes()
    {
        using var yav = ProjectWithMisspelling();
        var file = Path.Combine(Examples, "run-output.jsonl");

        var result = await yav.RunAsync(["run", "--project", yav.Project.Path, "--prompt-file", Path.Combine(Examples, "task.md"), "--json"]);

        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        var written = WithoutThisMachine(result.Output, yav);
        if (Environment.GetEnvironmentVariable(WriteVariable) == "1")
        {
            File.WriteAllText(file, written);
        }

        var example = File.ReadAllText(file).ReplaceLineEndings("\n");
        Assert.Equal(ShapesOf(written), ShapesOf(example));
        Assert.Equal("ready_to_apply", JsonDocument.Parse(example.TrimEnd('\n').Split('\n')[^1]).RootElement.GetProperty("outcome").GetString());
    }

    [Fact]
    public void The_output_that_is_given_as_an_example_names_nothing_of_the_machine_it_was_made_on()
    {
        var example = File.ReadAllText(Path.Combine(Examples, "run-output.jsonl"));

        Assert.DoesNotContain(Environment.UserName, example, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("yav-tests", example, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fake", example, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Dropbox", example, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain('\u001b', example);
    }

    [Fact]
    public async Task The_commands_that_are_given_as_an_example_for_a_pipe_do_what_the_guide_says()
    {
        using var yav = ProjectWithMisspelling();

        var result = await yav.RunAsync([yav.Project.Path], input: File.ReadAllText(Path.Combine(Examples, "commands.txt")));

        Assert.True(result.ExitCode == 0, result.Output + result.Error);
        Assert.Contains("[READY]", result.Output, StringComparison.Ordinal);
        Assert.Contains("[APPLY]", result.Output, StringComparison.Ordinal);
        Assert.Equal("Hello, world!\n", File.ReadAllText(Path.Combine(yav.Project.Path, "src", "greeting.txt")));
        Assert.DoesNotContain("is not a command", result.Output, StringComparison.OrdinalIgnoreCase);
    }
}
