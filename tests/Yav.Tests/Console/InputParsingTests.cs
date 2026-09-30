using Yav.Console.Commands;

namespace Yav.Tests.Console;

public class InputClassifierTests
{
    [Theory]
    [InlineData("Fix the login bug and add regression tests.")]
    [InlineData("dir")]
    [InlineData("git status")]
    [InlineData("rm -rf build")]
    [InlineData("del /s /q *.*")]
    [InlineData("C:\\Windows\\System32\\cmd.exe /c echo hi")]
    [InlineData("  leading spaces are part of a request")]
    public void Text_without_a_slash_in_front_is_a_request_and_never_a_command_of_the_operating_system(string text)
    {
        var input = InputClassifier.Classify(text);

        Assert.Equal(InputKind.Request, input.Kind);
        Assert.Equal(text, input.Text);
        Assert.Null(input.Command);
    }

    [Theory]
    [InlineData("/help", "help", "")]
    [InlineData("/HELP", "help", "")]
    [InlineData("/open C:\\Projects\\MyApp", "open", "C:\\Projects\\MyApp")]
    [InlineData("  /status  ", "status", "")]
    [InlineData("/exec git status --short", "exec", "git status --short")]
    public void Text_with_a_slash_in_front_is_a_yav_command(string text, string name, string rest)
    {
        var input = InputClassifier.Classify(text);

        Assert.Equal(InputKind.Command, input.Kind);
        Assert.Equal(name, input.Command!.Name);
        Assert.Equal(rest, input.Command.RawArguments);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\r\n")]
    public void Nothing_is_neither(string text)
    {
        Assert.Equal(InputKind.Empty, InputClassifier.Classify(text).Kind);
    }

    [Fact]
    public void A_request_that_has_to_start_with_a_slash_is_written_with_two()
    {
        var input = InputClassifier.Classify("//src/login.cs throws when the user is unknown");

        Assert.Equal(InputKind.Request, input.Kind);
        Assert.Equal("/src/login.cs throws when the user is unknown", input.Text);
    }

    [Fact]
    public void A_pasted_text_of_several_lines_is_one_request()
    {
        const string Pasted = "Fix this:\n/open is broken\n/exit";

        var input = InputClassifier.Classify(Pasted);

        Assert.Equal(InputKind.Request, input.Kind);
        Assert.Equal(Pasted, input.Text);
    }

    [Fact]
    public void A_command_never_spans_lines_so_pasted_lines_cannot_smuggle_one_in()
    {
        var input = InputClassifier.Classify("/exec echo one\n/apply");

        Assert.Equal(InputKind.Invalid, input.Kind);
        Assert.Contains("one line", input.Problem, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/ open")]
    [InlineData("/123")]
    public void A_slash_that_is_not_followed_by_a_name_is_reported(string text)
    {
        var input = InputClassifier.Classify(text);

        Assert.Equal(InputKind.Invalid, input.Kind);
        Assert.NotNull(input.Problem);
    }
}

public class ArgumentSplittingTests
{
    private static string[] Split(string text) => CommandArguments.Split(text).ToArray();

    [Fact]
    public void Arguments_are_separated_by_spaces()
    {
        Assert.Equal(["a", "codex-app-server", "gpt-6-astra"], Split("a  codex-app-server\tgpt-6-astra"));
    }

    [Fact]
    public void Quotes_keep_a_path_with_spaces_together()
    {
        Assert.Equal([@"C:\Projects\My App", "--accept-gaps"], Split("\"C:\\Projects\\My App\" --accept-gaps"));
    }

    [Fact]
    public void Backslashes_are_ordinary_characters_as_they_are_in_windows_paths()
    {
        Assert.Equal([@"C:\new\table\", @"\\server\share"], Split(@"C:\new\table\ \\server\share"));
    }

    [Fact]
    public void A_quote_inside_quotes_is_written_twice()
    {
        Assert.Equal(["say \"hi\" now"], Split("\"say \"\"hi\"\" now\""));
    }

    [Fact]
    public void An_empty_argument_can_be_given()
    {
        Assert.Equal(["a", "", "b"], Split("a \"\" b"));
    }

    [Fact]
    public void A_quote_that_is_not_closed_takes_the_rest()
    {
        Assert.Equal(["C:\\My App"], Split("\"C:\\My App"));
    }

    [Fact]
    public void Nothing_gives_no_arguments()
    {
        Assert.Empty(Split("   "));
    }
}

public class CommandCatalogTests
{
    private static readonly string[] Specified =
    [
        "open", "cd", "models", "effort", "login", "quality", "speed", "adaptive", "optimization", "new", "resume", "attach", "status",
        "stop", "diff", "test", "review", "apply", "discard", "undo", "history", "usage", "latency", "limits", "queue", "exec", "shell",
        "settings", "doctor", "help", "exit",
    ];

    [Fact]
    public void Every_command_of_the_specification_exists()
    {
        var names = CommandCatalog.All.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);

        Assert.All(Specified, name => Assert.Contains(name, names));
    }

    [Fact]
    public void Every_command_says_what_it_does_and_how_it_is_written()
    {
        Assert.All(CommandCatalog.All, command =>
        {
            Assert.False(string.IsNullOrWhiteSpace(command.Summary), command.Name);
            Assert.StartsWith("/" + command.Name, command.Usage, StringComparison.Ordinal);
            Assert.Matches("^[a-z]+$", command.Name);
        });
    }

    [Fact]
    public void Names_are_unique()
    {
        Assert.Equal(CommandCatalog.All.Count, CommandCatalog.All.Select(c => c.Name).Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("status", "status")]
    [InlineData("STATUS", "status")]
    [InlineData("quit", "exit")]
    [InlineData("q", "exit")]
    [InlineData("?", "help")]
    public void A_command_is_found_by_its_name_or_alias(string typed, string expected)
    {
        Assert.Equal(expected, CommandCatalog.Find(typed)!.Name);
    }

    [Fact]
    public void An_unknown_command_is_not_found_and_not_guessed()
    {
        Assert.Null(CommandCatalog.Find("statsu"));
        Assert.Null(CommandCatalog.Find("sta"));
    }

    [Theory]
    [InlineData("statsu", "status")]
    [InlineData("aply", "apply")]
    [InlineData("modles", "models")]
    [InlineData("hlep", "help")]
    public void A_mistyped_command_gets_a_suggestion(string typed, string expected)
    {
        Assert.Contains(expected, CommandCatalog.Suggest(typed));
    }

    [Fact]
    public void Nothing_is_suggested_for_text_that_resembles_no_command()
    {
        Assert.Empty(CommandCatalog.Suggest("xylophone"));
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("status")]
    [InlineData("queue")]
    [InlineData("diff")]
    [InlineData("help")]
    [InlineData("usage")]
    public void Commands_that_only_look_or_stop_are_available_while_a_run_is_active(string name)
    {
        Assert.True(CommandCatalog.Find(name)!.AllowedDuringRun);
    }

    [Theory]
    [InlineData("open")]
    [InlineData("cd")]
    [InlineData("models")]
    [InlineData("effort")]
    [InlineData("apply")]
    [InlineData("discard")]
    [InlineData("undo")]
    [InlineData("shell")]
    [InlineData("new")]
    public void Commands_that_would_change_an_active_run_or_take_the_console_wait_until_it_ended(string name)
    {
        Assert.False(CommandCatalog.Find(name)!.AllowedDuringRun);
    }

    [Fact]
    public void Review_show_only_looks_and_is_allowed_while_a_run_is_active_and_the_rest_of_review_waits()
    {
        var review = CommandCatalog.Find("review")!;

        Assert.False(review.AllowedDuringRun);
        Assert.True(review.For(["show"]).AllowedDuringRun);
        Assert.True(review.For(["SHOW"]).AllowedDuringRun);
        Assert.False(review.For([]).AllowedDuringRun);
        Assert.False(review.For(["approve", "src/a.cs"]).AllowedDuringRun);
        Assert.False(CommandCatalog.Find("apply")!.For(["show"]).AllowedDuringRun);
    }

    [Fact]
    public void Completion_offers_the_commands_that_start_with_what_was_typed()
    {
        Assert.Equal(["/status", "/stop"], CommandCatalog.Complete("/st").ToArray());
        Assert.Contains("/help", CommandCatalog.Complete("/"));
        Assert.Empty(CommandCatalog.Complete("/zz"));
    }
}
