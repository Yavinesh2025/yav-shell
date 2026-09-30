using Yav.Console.Cli;

namespace Yav.Tests.Console;

public class CommandLineTests
{
    private static CliOptions Parse(params string[] arguments) => CommandLine.Parse(arguments);

    [Fact]
    public void Without_arguments_the_shell_starts_interactively()
    {
        var options = Parse();

        Assert.Equal(CliMode.Interactive, options.Mode);
        Assert.Null(options.ProjectPath);
        Assert.Null(options.Error);
    }

    [Fact]
    public void A_single_path_opens_that_project()
    {
        var options = Parse(@"C:\Projects\My App");

        Assert.Equal(CliMode.Interactive, options.Mode);
        Assert.Equal(@"C:\Projects\My App", options.ProjectPath);
    }

    [Fact]
    public void Run_takes_a_project_and_a_task()
    {
        var options = Parse("run", "--project", @"C:\Projects\MyApp", "--task", "Fix the login bug");

        Assert.Equal(CliMode.Run, options.Mode);
        Assert.Equal(@"C:\Projects\MyApp", options.ProjectPath);
        Assert.Equal("Fix the login bug", options.Task);
        Assert.False(options.Json);
    }

    [Fact]
    public void Run_takes_the_task_from_a_file_and_can_answer_in_json()
    {
        var options = Parse("run", "--project", @"C:\Projects\MyApp", "--prompt-file", "task.md", "--json");

        Assert.Equal(CliMode.Run, options.Mode);
        Assert.Equal("task.md", options.PromptFile);
        Assert.Null(options.Task);
        Assert.True(options.Json);
        Assert.Null(options.Error);
    }

    [Theory]
    [InlineData("--project=C:\\Projects\\MyApp", "--task=Fix it")]
    [InlineData("-p", "C:\\Projects\\MyApp", "-t", "Fix it")]
    public void Options_can_be_written_with_an_equals_sign_or_in_short_form(params string[] arguments)
    {
        var options = Parse(["run", .. arguments]);

        Assert.Null(options.Error);
        Assert.Equal(@"C:\Projects\MyApp", options.ProjectPath);
        Assert.Equal("Fix it", options.Task);
    }

    [Fact]
    public void A_task_that_looks_like_an_option_is_still_the_task()
    {
        var options = Parse("run", "--project", ".", "--task", "--json should be documented");

        Assert.Null(options.Error);
        Assert.Equal("--json should be documented", options.Task);
        Assert.False(options.Json);
    }

    [Fact]
    public void Run_without_a_task_is_an_error_and_never_an_empty_request()
    {
        var options = Parse("run", "--project", ".");

        Assert.Equal(CliMode.Invalid, options.Mode);
        Assert.Contains("--task", options.Error, StringComparison.Ordinal);
        Assert.Contains("--prompt-file", options.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_task_and_a_prompt_file_together_are_refused()
    {
        var options = Parse("run", "--task", "a", "--prompt-file", "b.md");

        Assert.Equal(CliMode.Invalid, options.Mode);
        Assert.Contains("not both", options.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run", "--task")]
    [InlineData("run", "--project")]
    [InlineData("run", "--task", "x", "--prompt-file")]
    public void An_option_without_its_value_is_an_error(params string[] arguments)
    {
        var options = Parse(arguments);

        Assert.Equal(CliMode.Invalid, options.Mode);
        Assert.Contains("needs a value", options.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("run", "--task", "x", "--frobnicate")]
    [InlineData("doctor", "--task", "x")]
    [InlineData("--unknown")]
    public void An_unknown_option_is_an_error_and_is_not_guessed_at(params string[] arguments)
    {
        var options = Parse(arguments);

        Assert.Equal(CliMode.Invalid, options.Mode);
        Assert.NotNull(options.Error);
    }

    [Fact]
    public void Doctor_is_a_mode_of_its_own()
    {
        Assert.Equal(CliMode.Doctor, Parse("doctor").Mode);
        Assert.True(Parse("doctor", "--json").Json);
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("/?")]
    [InlineData("help")]
    public void Help_is_recognized_in_the_usual_spellings(string argument)
    {
        Assert.Equal(CliMode.Help, Parse(argument).Mode);
    }

    [Theory]
    [InlineData("--version")]
    [InlineData("version")]
    public void The_version_can_be_asked_for(string argument)
    {
        Assert.Equal(CliMode.Version, Parse(argument).Mode);
    }

    [Fact]
    public void Plain_output_can_be_forced()
    {
        Assert.True(Parse("--plain").Plain);
        Assert.True(Parse(@"C:\Projects\MyApp", "--plain").Plain);
        Assert.True(Parse("run", "--task", "x", "--plain").Plain);
    }

    [Fact]
    public void Json_implies_that_nothing_but_json_is_written()
    {
        var options = Parse("run", "--task", "x", "--json");

        Assert.True(options.Json);
        Assert.True(options.Plain);
    }

    [Fact]
    public void A_run_can_continue_a_task_and_can_apply_what_passed()
    {
        var options = Parse("run", "--task", "Also log it", "--continue", "t-abc", "--apply");

        Assert.Null(options.Error);
        Assert.Equal("t-abc", options.TaskId);
        Assert.True(options.Apply);
    }

    [Fact]
    public void Two_paths_are_an_error()
    {
        var options = Parse(@"C:\a", @"C:\b");

        Assert.Equal(CliMode.Invalid, options.Mode);
    }

    [Fact]
    public void A_directory_named_like_a_verb_can_be_opened_by_writing_it_as_a_path()
    {
        var options = Parse(@".\run");

        Assert.Equal(CliMode.Interactive, options.Mode);
        Assert.Equal(@".\run", options.ProjectPath);
    }
}
