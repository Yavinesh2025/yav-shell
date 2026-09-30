using Yav.Coordinator;
using Yav.Core.Agents;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

public class WorkspacePathsTests
{
    private const string Root = @"C:\data\workspaces\w1\w";

    private static ToolActivity Shown(string? path, string summary = "", string? root = Root) =>
        Assert.IsType<ToolActivity>(WorkspacePaths.ForDisplay(new ToolActivity(Builders.Now, "t1", "Read", summary, "started", path), root));

    [Theory]
    [InlineData(@"C:\data\workspaces\w1\w\src\app.txt", "src/app.txt")]
    [InlineData(@"C:/data/workspaces/w1/w/src/app.txt", "src/app.txt")]
    [InlineData(@"c:\DATA\workspaces\w1\w\README.md", "README.md")]
    [InlineData(@"C:\data\workspaces\w1\w", ".")]
    [InlineData(@"C:\data\workspaces\w1\w\", ".")]
    public void What_a_tool_was_pointed_at_inside_the_workspace_is_shown_from_there(string given, string shown)
    {
        Assert.Equal(shown, Shown(given).Path);
    }

    [Theory]
    [InlineData(@"C:\data\workspaces\w1\evidence\gate-tests.log")]
    [InlineData(@"C:\data\workspaces\w1\w-of-somebody-else\src\app.txt")]
    [InlineData(@"C:\data\workspaces\w1")]
    [InlineData(@"C:\Users\me\.ssh\id_rsa")]
    [InlineData(@"\\server\share\file.txt")]
    public void What_is_outside_the_workspace_keeps_its_whole_path_so_that_it_is_not_taken_for_a_file_of_the_project(string given)
    {
        Assert.Equal(given, Shown(given).Path);
    }

    [Fact]
    public void A_path_the_agent_gave_without_saying_where_it_begins_is_shown_as_it_was_given()
    {
        Assert.Equal("src/app.txt", Shown(@"src\app.txt").Path);
    }

    [Fact]
    public void What_a_tool_looks_for_is_not_touched()
    {
        var shown = Shown(@"C:\data\workspaces\w1\w\src", @"C:\data\workspaces\w1\w\\d+");

        Assert.Equal(@"C:\data\workspaces\w1\w\\d+", shown.Summary);
        Assert.Equal("src", shown.Path);
        Assert.Equal(@"C:\data\workspaces\w1\w\\d+ in src", shown.Given);
    }

    [ShortNamesFact]
    public void A_directory_is_the_same_by_each_of_its_names_and_no_other_is()
    {
        using var directory = new TempDirectory("names");
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "the workspace of a run")).FullName;
        var beside = Directory.CreateDirectory(Path.Combine(directory.Path, "the workspace of another run")).FullName;

        Assert.NotEqual(WindowsNames.Long(workspace), WindowsNames.Short(workspace), StringComparer.OrdinalIgnoreCase);
        Assert.True(WorkspacePaths.SameDirectory(workspace, WindowsNames.Short(workspace)));
        Assert.True(WorkspacePaths.SameDirectory(WindowsNames.Short(workspace), WindowsNames.Long(workspace)));
        Assert.True(WorkspacePaths.SameDirectory(workspace, workspace.ToUpperInvariant() + @"\"));
        Assert.False(WorkspacePaths.SameDirectory(workspace, beside));
        Assert.False(WorkspacePaths.SameDirectory(workspace, WindowsNames.Short(beside)));
        Assert.False(WorkspacePaths.SameDirectory(workspace, directory.Path));
    }

    [Fact]
    public void A_directory_that_is_reached_through_a_junction_is_the_directory_the_junction_leads_to()
    {
        // Claude Code 2.1.284 reports the directory it works in with every junction resolved; Codex 0.158.0 as it
        // was given. Either is the workspace.
        using var directory = new TempDirectory("links");
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "the workspace of a run")).FullName;
        var beside = Directory.CreateDirectory(Path.Combine(directory.Path, "the workspace of another run")).FullName;
        var link = directory.Junction("a junction", workspace);
        var leading = directory.Junction("a junction to the folder", directory.Path);

        Assert.True(WorkspacePaths.SameDirectory(workspace, link));
        Assert.True(WorkspacePaths.SameDirectory(link, workspace));
        Assert.True(WorkspacePaths.SameDirectory(workspace, Path.Combine(leading, "the workspace of a run")));
        Assert.True(WorkspacePaths.SameDirectory(Path.Combine(leading, "a junction"), workspace + @"\"));
        Assert.False(WorkspacePaths.SameDirectory(beside, link));
        Assert.False(WorkspacePaths.SameDirectory(Path.Combine(leading, "the workspace of another run"), workspace));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("the workspace*")]
    [InlineData("the workspace of a ru?")]
    [InlineData("????????????????????????")]
    public void A_name_with_a_wildcard_names_no_directory_that_is_there(string written)
    {
        // What an agent reports is a text. Looked up as a pattern, it would be answered with the directory that is there.
        using var directory = new TempDirectory("names");
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "the workspace of a run")).FullName;

        Assert.False(WorkspacePaths.SameDirectory(workspace, Path.Combine(directory.Path, written)));
    }

    [Fact]
    public void Without_a_workspace_or_without_a_path_nothing_is_changed()
    {
        Assert.Equal(@"C:\data\workspaces\w1\w\src\app.txt", Shown(@"C:\data\workspaces\w1\w\src\app.txt", root: null).Path);
        Assert.Null(Shown(null).Path);
        Assert.Equal(string.Empty, Shown(null).Given);
        Assert.Equal("query", Shown(null, "query").Given);
    }
}
