using Yav.Console.Input;
using Yav.Tests.Support;

namespace Yav.Tests.Console;

public class InputCompletionTests
{
    private static TempDirectory Project()
    {
        var directory = new TempDirectory("complete");
        directory.CreateDirectory("src");
        directory.CreateDirectory("src/services");
        directory.CreateDirectory("docs and notes");
        directory.Write("README.md", "x");
        directory.Write("src/login.cs", "x");
        directory.Write("src/logout.cs", "x");
        return directory;
    }

    [Theory]
    [InlineData("/settings shell cmd")]
    [InlineData("/settings ")]
    [InlineData("/nonsense and more")]
    [InlineData("/speed provider now")]
    [InlineData("/open \"C:\\some where\" --accept-gaps")]
    [InlineData("/attach docs and notes/x")]
    [InlineData("/")]
    [InlineData("")]
    [InlineData("a request, not a command")]
    [InlineData("/first line\n/second line")]
    [InlineData("/test waive tests because of 日本語")]
    public void What_is_replaced_by_a_completion_always_contains_the_caret(string text)
    {
        using var project = Project();

        for (var caret = 0; caret <= text.Length; caret++)
        {
            var completion = InputCompletion.For(text, caret, project.Path);

            Assert.True(
                completion.Start <= caret && caret <= completion.Start + completion.Length,
                $"'{text}' with the caret at {caret}: the part from {completion.Start} of length {completion.Length} does not contain it");
            Assert.True(completion.Start + completion.Length <= text.Length);
        }
    }

    [Fact]
    public void The_name_of_a_command_is_completed()
    {
        var completion = InputCompletion.For("/sta", 4, null);

        Assert.Equal(0, completion.Start);
        Assert.Equal(4, completion.Length);
        Assert.Equal(["/status"], completion.Candidates.Select(c => c.Text).ToArray());
    }

    [Fact]
    public void Every_command_is_offered_after_the_slash_with_what_it_does()
    {
        var completion = InputCompletion.For("/", 1, null);

        Assert.Contains(completion.Candidates, c => c.Text == "/apply" && c.Description.Contains("candidate", StringComparison.Ordinal));
        Assert.True(completion.Candidates.Count > 25);
    }

    [Fact]
    public void A_request_is_never_completed()
    {
        Assert.Empty(InputCompletion.For("fix the login", 13, null).Candidates);
        Assert.Empty(InputCompletion.For("see src/lo", 10, @"C:\").Candidates);
    }

    [Fact]
    public void Directories_are_offered_for_a_project_to_open()
    {
        using var project = Project();

        var completion = InputCompletion.For("/cd s", 5, project.Path);

        Assert.Equal(4, completion.Start);
        Assert.Equal(1, completion.Length);
        Assert.Equal([@"src\"], completion.Candidates.Select(c => c.Text).ToArray());
    }

    [Fact]
    public void Files_are_offered_only_where_a_file_is_meant()
    {
        using var project = Project();

        var attach = InputCompletion.For(@"/attach src\log", 15, project.Path);
        var open = InputCompletion.For(@"/open src\log", 13, project.Path);

        Assert.Equal([@"src\login.cs", @"src\logout.cs"], attach.Candidates.Select(c => c.Text).ToArray());
        Assert.Empty(open.Candidates);
    }

    [Fact]
    public void A_path_with_a_space_is_completed_in_quotes()
    {
        using var project = Project();

        var completion = InputCompletion.For("/cd do", 6, project.Path);

        Assert.Equal(["\"docs and notes\\\""], completion.Candidates.Select(c => c.Text).ToArray());
    }

    [Fact]
    public void A_path_that_was_begun_in_quotes_is_replaced_as_a_whole()
    {
        using var project = Project();

        var completion = InputCompletion.For("/cd \"docs a", 11, project.Path);

        Assert.Equal(4, completion.Start);
        Assert.Equal(7, completion.Length);
        Assert.Equal(["\"docs and notes\\\""], completion.Candidates.Select(c => c.Text).ToArray());
    }

    [Fact]
    public void An_absolute_path_is_completed_where_it_points()
    {
        using var project = Project();
        var typed = "/open " + Path.Combine(project.Path, "sr");

        var completion = InputCompletion.For(typed, typed.Length, null);

        var candidate = Assert.Single(completion.Candidates);
        Assert.EndsWith(@"\src\", candidate.Text.Trim('"'), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("/open \u0000bad")]
    [InlineData("/open C:\\does\\not\\exist\\x")]
    [InlineData("/open ::::")]
    [InlineData("/open \\\\?\\")]
    public void A_path_that_cannot_be_read_gives_nothing_and_no_error(string text)
    {
        Assert.Empty(InputCompletion.For(text, text.Length, @"C:\").Candidates);
    }

    [Fact]
    public void The_number_of_candidates_is_bounded()
    {
        using var directory = new TempDirectory("many");
        for (var i = 0; i < 300; i++)
        {
            directory.CreateDirectory($"dir{i:000}");
        }

        var completion = InputCompletion.For("/cd d", 5, directory.Path);

        Assert.Equal(InputCompletion.MaxCandidates, completion.Candidates.Count);
    }

    [Fact]
    public void Arguments_of_commands_that_take_words_are_offered()
    {
        Assert.Equal(["standard", "provider"], InputCompletion.For("/speed ", 7, null).Candidates.Select(c => c.Text).ToArray());
        Assert.Equal(["on", "off"], InputCompletion.For("/adaptive ", 10, null).Candidates.Select(c => c.Text).ToArray());
        Assert.Equal(["provider"], InputCompletion.For("/speed p", 8, null).Candidates.Select(c => c.Text).ToArray());
    }
}
