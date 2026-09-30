namespace Yav.Tests.Support;

/// <summary>
/// The temporary directories of the tests hold junctions, among other things. What a junction leads to is
/// not the directory's to change, and a junction can lead back into the directory that holds it.
/// </summary>
public sealed class TempDirectoryTests
{
    [Fact]
    public void A_directory_that_holds_a_junction_back_into_itself_is_removed()
    {
        var directory = new TempDirectory("junction back");
        directory.Write("inside/a.txt", "x");
        directory.Junction("back", directory.Path);

        directory.Dispose();

        Assert.False(Directory.Exists(directory.Path));
    }

    [Fact]
    public void Where_a_junction_leads_out_of_the_directory_nothing_is_changed()
    {
        using var outside = new TempDirectory("outside");
        var kept = outside.Write("kept.txt", "kept");
        File.SetAttributes(kept, FileAttributes.ReadOnly);
        var directory = new TempDirectory("junction out");
        directory.Junction("out", outside.Path);

        directory.Dispose();

        Assert.False(Directory.Exists(directory.Path));
        Assert.Equal("kept", File.ReadAllText(kept));
        Assert.True(File.GetAttributes(kept).HasFlag(FileAttributes.ReadOnly));
    }
}
