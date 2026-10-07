using Yav.Console.Install;

namespace Yav.Tests.Packaging;

/// <summary>
/// The PATH of the user account is changed as text: the directory of YAV Shell is added or taken out, and every
/// other part stays as it was, in its order and its spelling, empty parts and %VARIABLES% included.
/// </summary>
public class PathListTests
{
    [Theory]
    [InlineData("", @"C:\Users\me\AppData\Local\Programs\YavShell", @"C:\Users\me\AppData\Local\Programs\YavShell")]
    [InlineData(@"C:\a;C:\b", @"C:\Programs\Yav Shell", @"C:\a;C:\b;C:\Programs\Yav Shell")]
    [InlineData(@"C:\a;C:\b;", @"C:\yav", @"C:\a;C:\b;C:\yav")]
    [InlineData(@"%USERPROFILE%\bin;C:\b", @"C:\yav", @"%USERPROFILE%\bin;C:\b;C:\yav")]
    [InlineData(@"C:\a;;C:\b", @"C:\yav", @"C:\a;;C:\b;C:\yav")]
    public void The_directory_is_added_at_the_end_and_nothing_else_is_changed(string path, string directory, string expected)
    {
        Assert.Equal(expected, PathList.WithEntry(path, directory));
    }

    [Theory]
    [InlineData(@"C:\a;C:\yav;C:\b", @"C:\yav")]
    [InlineData(@"C:\a;c:\YAV\;C:\b", @"C:\yav")]
    [InlineData(@"C:\a;""C:\yav"";C:\b", @"C:\yav")]
    [InlineData(@"C:\a; C:\yav ;C:\b", @"C:\yav\")]
    public void A_directory_that_is_there_already_is_not_added_again(string path, string directory)
    {
        Assert.True(PathList.Contains(path, directory));
        Assert.Equal(path, PathList.WithEntry(path, directory));
    }

    [Theory]
    [InlineData(@"C:\a;C:\yav;C:\b", @"C:\yav", @"C:\a;C:\b")]
    [InlineData(@"C:\yav", @"C:\yav", "")]
    [InlineData(@"C:\a;c:\YAV\;C:\b;C:\yav", @"C:\yav", @"C:\a;C:\b")]
    [InlineData(@"%USERPROFILE%\bin;C:\yav", @"C:\yav", @"%USERPROFILE%\bin")]
    [InlineData(@"C:\yavx;C:\yav\sub;C:\yav", @"C:\yav", @"C:\yavx;C:\yav\sub")]
    public void The_directory_is_removed_and_nothing_else_is_changed(string path, string directory, string expected)
    {
        Assert.Equal(expected, PathList.WithoutEntry(path, directory));
    }

    [Theory]
    [InlineData(@"C:\a;;C:\b;", @"C:\yav")]
    [InlineData("", @"C:\yav")]
    [InlineData(@"C:\yavx;%YAV%", @"C:\yav")]
    public void Removing_a_directory_that_is_not_there_changes_nothing_at_all(string path, string directory)
    {
        Assert.False(PathList.Contains(path, directory));
        Assert.Equal(path, PathList.WithoutEntry(path, directory));
    }

    [Fact]
    public void A_part_that_names_the_directory_through_a_variable_is_the_directory()
    {
        // Windows expands the variable when it looks for a program, so the part does find yav there.
        var variable = "YAV_PATHLIST_TEST_" + Guid.NewGuid().ToString("N")[..8];
        Environment.SetEnvironmentVariable(variable, @"C:\Programs\Yav Shell");
        try
        {
            var path = $@"C:\a;%{variable}%\;C:\b";

            Assert.True(PathList.Contains(path, @"C:\Programs\Yav Shell"));
            Assert.Equal(path, PathList.WithEntry(path, @"C:\Programs\Yav Shell"));
            Assert.Equal(@"C:\a;C:\b", PathList.WithoutEntry(path, @"C:\Programs\Yav Shell"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(variable, null);
        }
    }
}
