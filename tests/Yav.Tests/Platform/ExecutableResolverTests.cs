using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Platform;

public class ExecutableResolverTests
{
    [Theory]
    [InlineData(@"C:\Users\someone\AppData\Local\Microsoft\WindowsApps\pwsh.exe", true)]
    [InlineData(@"C:\Program Files\WindowsApps\Microsoft.PowerShell_7.6.6.0_x64__8wekyb3d8bbwe\pwsh.EXE", true)]
    [InlineData(@"c:\program files\windowsapps\PythonSoftwareFoundation.Python.3.14_qbz5n2kfra8p0\python.exe", true)]
    [InlineData(@"C:\Program Files\PowerShell\7\pwsh.exe", false)]
    [InlineData(@"C:\WindowsAppsOfMine\tool.exe", false)]
    [InlineData(@"C:\tools\WindowsApps.exe", false)]
    [InlineData(@"C:\tools\WindowsApps", false)]
    public void A_program_of_a_package_is_known_by_the_folder_it_is_in(string path, bool packaged)
    {
        Assert.Equal(packaged, ExecutableResolver.IsPackaged(path));
    }

    [Fact]
    public void A_tool_in_the_working_directory_is_not_found_by_bare_name()
    {
        using var project = new TempDirectory("project");
        using var pathDirectory = new TempDirectory("path");
        project.Write("git.exe", "planted");

        var resolved = ExecutableResolver.Resolve("git", project.Path, pathVariable: pathDirectory.Path, pathExtVariable: ".EXE;.CMD");

        Assert.Null(resolved);
    }

    [Fact]
    public void A_tool_on_path_is_found_by_bare_name()
    {
        using var project = new TempDirectory("project");
        using var pathDirectory = new TempDirectory("path");
        var expected = pathDirectory.Write("mytool.exe", "real");

        var resolved = ExecutableResolver.Resolve("mytool", project.Path, pathVariable: pathDirectory.Path, pathExtVariable: ".EXE;.CMD");

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    [Fact]
    public void An_explicit_relative_path_is_resolved_against_the_working_directory()
    {
        using var project = new TempDirectory("project");
        var expected = project.Write("scripts/build.cmd", "@echo off");

        var resolved = ExecutableResolver.Resolve(@".\scripts\build.cmd", project.Path, pathVariable: string.Empty);

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    [Fact]
    public void A_relative_path_entry_is_ignored()
    {
        using var project = new TempDirectory("project");
        project.Write("tools/mytool.exe", "planted");

        // "." and "tools" would both resolve against the current directory.
        var resolved = ExecutableResolver.Resolve("mytool", project.Path, pathVariable: ".;tools", pathExtVariable: ".EXE");

        Assert.Null(resolved);
    }

    [Fact]
    public void The_first_path_directory_wins_and_extension_order_is_honored_within_it()
    {
        using var first = new TempDirectory("first");
        using var second = new TempDirectory("second");
        var expected = first.Write("tool.cmd", "@echo off");
        second.Write("tool.exe", "native");

        var resolved = ExecutableResolver.Resolve("tool", null, pathVariable: first.Path + ";" + second.Path, pathExtVariable: ".EXE;.CMD");

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    [Fact]
    public void Within_one_directory_a_native_executable_is_preferred_over_a_batch_file()
    {
        using var directory = new TempDirectory("both");
        directory.Write("tool.cmd", "@echo off");
        var expected = directory.Write("tool.exe", "native");

        var resolved = ExecutableResolver.Resolve("tool", null, pathVariable: directory.Path, pathExtVariable: ".EXE;.CMD");

        Assert.Equal(expected, resolved, ignoreCase: true);
    }

    [Theory]
    [InlineData("tool.cmd", true)]
    [InlineData("TOOL.BAT", true)]
    [InlineData("tool.exe", false)]
    [InlineData("tool.ps1", false)]
    public void Batch_files_are_recognized_by_extension(string name, bool expected)
    {
        Assert.Equal(expected, ExecutableResolver.IsBatchFile(name));
    }
}
