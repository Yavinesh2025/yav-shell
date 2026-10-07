using System.Diagnostics;
using Yav.Platform.Install;
using Yav.Tests.Support;

namespace Yav.Tests.Packaging;

/// <summary>
/// What the installation asks Windows itself. Only what needs no change to the user's registry is tested here:
/// which processes run a program file, and how two spellings of a path are taken for the same one.
/// </summary>
public class WindowsInstallSystemTests
{
    /// <summary>Starts the scripted stand-in from wherever it is, waiting thirty seconds, with nothing of the test run's console.</summary>
    private static Process StartWaiting(string executable)
    {
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("tool");
        start.ArgumentList.Add("sleep");
        start.ArgumentList.Add("30");
        return Process.Start(start)!;
    }

    private static void End(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(10_000);
        }
        catch (InvalidOperationException)
        {
            // It has ended already.
        }

        process.Dispose();
    }

    /// <summary>A copy of the scripted stand-in, with what it needs beside it, in a directory with blanks, brackets and other letters.</summary>
    private static string CopyOfTheStandIn(TempDirectory directory)
    {
        var from = Path.GetDirectoryName(Fixtures.FakeAgent)!;
        var to = directory.CreateDirectory("copy [1] 日本");
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        return Path.Combine(to, Path.GetFileName(Fixtures.FakeAgent));
    }

    [Fact]
    public void The_processes_of_a_program_file_are_found_and_those_of_a_copy_elsewhere_are_not()
    {
        using var directory = new TempDirectory("processes");
        var copy = CopyOfTheStandIn(directory);
        var system = new WindowsInstallSystem();
        var ofTheCopy = StartWaiting(copy);
        var ofTheOriginal = StartWaiting(Fixtures.FakeAgent);
        var copyId = ofTheCopy.Id;
        var originalId = ofTheOriginal.Id;
        try
        {
            var runningTheCopy = system.ProcessesRunning(copy);
            var runningTheOriginal = system.ProcessesRunning(Fixtures.FakeAgent);

            Assert.Contains(copyId, runningTheCopy);
            Assert.DoesNotContain(originalId, runningTheCopy);
            Assert.Contains(originalId, runningTheOriginal);
            Assert.DoesNotContain(copyId, runningTheOriginal);
        }
        finally
        {
            End(ofTheCopy);
            End(ofTheOriginal);
        }

        Assert.DoesNotContain(copyId, system.ProcessesRunning(copy));
    }

    [ShortNamesFact]
    public void A_program_file_named_by_its_short_name_is_the_same_program()
    {
        using var directory = new TempDirectory("short processes");
        var copy = CopyOfTheStandIn(directory);
        var process = StartWaiting(WindowsNames.Short(copy));
        try
        {
            Assert.Contains(process.Id, new WindowsInstallSystem().ProcessesRunning(copy));
            Assert.Contains(process.Id, new WindowsInstallSystem().ProcessesRunning(WindowsNames.Long(copy)));
        }
        finally
        {
            End(process);
        }
    }

    [Fact]
    public void A_program_in_a_directory_reached_through_a_junction_is_found_by_either_path()
    {
        // Windows names the image of a process by the file it opened, after the junction: AppData moved to another
        // drive and left behind as a junction is such a case.
        using var directory = new TempDirectory("junction processes");
        var copy = CopyOfTheStandIn(directory);
        directory.Junction("linked", Path.GetDirectoryName(copy)!);
        var throughTheJunction = directory.File("linked/" + Path.GetFileName(copy));
        var process = StartWaiting(throughTheJunction);
        try
        {
            Assert.Contains(process.Id, new WindowsInstallSystem().ProcessesRunning(throughTheJunction));
            Assert.Contains(process.Id, new WindowsInstallSystem().ProcessesRunning(copy));
        }
        finally
        {
            End(process);
        }
    }

    [Fact]
    public void Nothing_runs_a_program_file_that_does_not_exist()
    {
        using var directory = new TempDirectory("no program");

        Assert.Empty(new WindowsInstallSystem().ProcessesRunning(directory.File("yav.exe")));
    }

    [ShortNamesFact]
    public void The_long_form_of_a_path_names_each_existing_part_by_its_long_name_and_keeps_the_rest()
    {
        using var directory = new TempDirectory("long [1] 日本");
        var existing = directory.CreateDirectory("Programs ünï");
        var shortName = WindowsNames.Short(existing);

        // The path of the directory for temporary files may be a short one itself: it is the long form that differs.
        Assert.NotEqual(WindowsNames.Long(existing), shortName, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(WindowsNames.Long(existing), LongPath.Of(shortName));
        Assert.Equal(Path.Combine(WindowsNames.Long(existing), "Not There Yet", "YavShell"), LongPath.Of(Path.Combine(shortName, "Not There Yet", "YavShell")));
        Assert.Equal(WindowsNames.Long(existing), LongPath.Of(shortName + Path.DirectorySeparatorChar));
        Assert.True(LongPath.Same(shortName, WindowsNames.Long(existing).ToUpperInvariant()));
    }

    [Theory]
    [InlineData(@"C:\a\b", @"C:\a", true)]
    [InlineData(@"C:\a", @"C:\a\", true)]
    [InlineData(@"c:\A\b\c", @"C:\a", true)]
    [InlineData(@"C:\ab", @"C:\a", false)]
    [InlineData(@"C:\a", @"C:\a\b", false)]
    [InlineData(@"C:\a\..\b", @"C:\a", false)]
    [InlineData(@"C:\anything", @"C:\", true)]
    public void A_path_lies_in_a_directory_only_below_its_separator(string path, string directory, bool expected)
    {
        Assert.Equal(expected, LongPath.IsSameOrInside(path, directory));
    }

    [Fact]
    public void The_root_of_a_volume_keeps_its_separator()
    {
        Assert.Equal(@"C:\", LongPath.Of(@"C:\"));
    }
}
