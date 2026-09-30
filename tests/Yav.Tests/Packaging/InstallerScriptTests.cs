using Yav.Core.Ports;
using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Packaging;

/// <summary>What started Windows PowerShell, which decides where it looks for its modules.</summary>
public enum StartedFrom
{
    Windows,

    /// <summary>
    /// A program that was started from PowerShell 7 hands on where PowerShell 7 keeps its modules. Windows
    /// PowerShell then no longer finds its commands that are written as scripts, such as Get-FileHash.
    /// </summary>
    BelowPowerShell7,
}

/// <summary>
/// The scripts that install and remove YAV Shell. They run in Windows PowerShell 5.1, because that is
/// what a new Windows has, so that is what runs them here.
/// </summary>
public class InstallerScriptTests
{
    private static readonly ProcessRunner Runner = new();

    private static readonly string WindowsModules =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "Modules");

    /// <summary>
    /// The commands that the module Microsoft.PowerShell.Utility of PowerShell 7 says it has. Windows PowerShell
    /// believes it, and that is the problem: a stand-in that names only a few of them does not show it.
    /// </summary>
    private static readonly string[] CommandsOfPowerShell7 =
    [
        "Export-Alias", "Get-Alias", "Import-Alias", "New-Alias", "Remove-Alias", "Set-Alias", "Export-Clixml", "Import-Clixml",
        "Measure-Command", "Trace-Command", "ConvertFrom-Csv", "ConvertTo-Csv", "Export-Csv", "Import-Csv", "Get-Culture",
        "Format-Custom", "Get-Date", "Set-Date", "Write-Debug", "Wait-Debugger", "Register-EngineEvent", "Write-Error",
        "Get-Event", "New-Event", "Remove-Event", "Unregister-Event", "Wait-Event", "Get-EventSubscriber", "Invoke-Expression",
        "Out-File", "Unblock-File", "Get-FileHash", "Export-FormatData", "Get-FormatData", "Update-FormatData", "New-Guid",
        "Format-Hex", "Get-Host", "Read-Host", "Write-Host", "ConvertTo-Html", "Write-Information", "ConvertFrom-Json",
        "ConvertTo-Json", "Test-Json", "Format-List", "Import-LocalizedData", "Send-MailMessage", "ConvertFrom-Markdown",
        "Show-Markdown", "Get-MarkdownOption", "Set-MarkdownOption", "Add-Member", "Get-Member", "Compare-Object", "Group-Object",
        "Measure-Object", "New-Object", "Select-Object", "Sort-Object", "Tee-Object", "Register-ObjectEvent", "Write-Output",
        "Import-PowerShellDataFile", "Write-Progress", "Disable-PSBreakpoint", "Enable-PSBreakpoint", "Get-PSBreakpoint",
        "Remove-PSBreakpoint", "Set-PSBreakpoint", "Get-PSCallStack", "Export-PSSession", "Import-PSSession", "Get-Random",
        "Get-SecureRandom", "Invoke-RestMethod", "Debug-Runspace", "Get-Runspace", "Disable-RunspaceDebug", "Enable-RunspaceDebug",
        "Get-RunspaceDebug", "ConvertFrom-SddlString", "Start-Sleep", "Join-String", "Out-String", "Select-String",
        "ConvertFrom-StringData", "Format-Table", "New-TemporaryFile", "New-TimeSpan", "Get-TraceSource", "Set-TraceSource",
        "Add-Type", "Get-TypeData", "Remove-TypeData", "Update-TypeData", "Get-UICulture", "Get-Unique", "Get-Uptime",
        "Clear-Variable", "Get-Variable", "New-Variable", "Remove-Variable", "Set-Variable", "Get-Verb", "Write-Verbose",
        "Write-Warning", "Invoke-WebRequest", "Format-Wide", "ConvertTo-Xml", "Select-Xml", "Get-Error", "Update-List",
        "Out-GridView", "Show-Command", "Out-Printer", "ConvertTo-CliXml", "ConvertFrom-CliXml",
    ];

    /// <summary>Stands in for the modules of PowerShell 7, so that the tests do not need it to be installed.</summary>
    private static readonly Lazy<string> ModulesOfPowerShell7 = new(() =>
    {
        var modules = new TempDirectory("modules of powershell 7");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => modules.Dispose();
        modules.Write(
            "Microsoft.PowerShell.Utility/Microsoft.PowerShell.Utility.psd1",
            $$"""
            @{
            GUID = "1DA87E53-152B-403E-98DC-74D7B4D63D59"
            Author = "PowerShell"
            ModuleVersion = "7.0.0.0"
            CompatiblePSEditions = @("Core")
            PowerShellVersion = "3.0"
            CmdletsToExport = @({{string.Join(", ", CommandsOfPowerShell7.Select(name => "'" + name + "'"))}})
            FunctionsToExport = @()
            AliasesToExport = @('fhx')
            NestedModules = @("Microsoft.PowerShell.Commands.Utility.dll")
            }
            """);
        return modules.Path;
    });

    private static Dictionary<string, string?> EnvironmentOf(StartedFrom startedFrom) => new()
    {
        // Without the variable Windows PowerShell puts the directories together itself, as it does when Windows starts it.
        ["PSModulePath"] = startedFrom == StartedFrom.BelowPowerShell7 ? ModulesOfPowerShell7.Value + ";" + WindowsModules : null,
    };

    private static string Repository
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "YavShell.slnx")))
            {
                directory = directory.Parent;
            }

            return directory!.FullName;
        }
    }

    private static string WindowsPowerShell =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string Quoted(string text) => "'" + text.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<string> RunAsync(string command, StartedFrom startedFrom = StartedFrom.Windows)
    {
        var result = await Runner.RunAsync(
            new ProcessSpec(WindowsPowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-Command", command], Repository, EnvironmentOf(startedFrom)),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(90)),
            CancellationToken.None);
        // An error that does not end the command leaves the exit code at 0, so what was written as an error counts as well.
        Assert.True(result.ExitCode == 0 && result.StandardError.Length == 0, $"exit code {result.ExitCode}: {result.StandardError}{result.StandardOutput}");
        return result.StandardOutput.ReplaceLineEndings("\n").TrimEnd('\n');
    }

    private static Task<string> CallAsync(string function, string path, string entry) =>
        RunAsync($". {Quoted(Path.Combine(Repository, "installer", "YavInstall.ps1"))}; [Console]::Out.Write(({function} -Path {Quoted(path)} -Entry {Quoted(entry)}))");

    public static TheoryData<string> Scripts()
    {
        var data = new TheoryData<string>();
        foreach (var directory in new[] { "installer", "scripts" })
        {
            foreach (var file in Directory.EnumerateFiles(Path.Combine(Repository, directory), "*.ps1").Order(StringComparer.Ordinal))
            {
                data.Add(Path.GetRelativePath(Repository, file));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task A_script_is_understood_by_the_powershell_a_new_windows_has(string script)
    {
        var output = await RunAsync(
            "$errors = $null; $tokens = $null; "
            + $"[void][System.Management.Automation.Language.Parser]::ParseFile({Quoted(Path.Combine(Repository, script))}, [ref]$tokens, [ref]$errors); "
            + "$errors | ForEach-Object { 'line {0}: {1}' -f $_.Extent.StartLineNumber, $_.Message }");

        Assert.Equal(string.Empty, output);
    }

    /// <summary>The commands of Windows PowerShell that are written as scripts. Below PowerShell 7 it does not find them.</summary>
    private static readonly string[] CommandsThatAreScripts =
        ["Get-FileHash", "New-Guid", "New-TemporaryFile", "Format-Hex", "Import-PowerShellDataFile", "ConvertFrom-SddlString"];

    [Theory]
    [MemberData(nameof(Scripts))]
    public async Task A_script_uses_no_command_that_windows_powershell_does_not_find_below_powershell_7(string script)
    {
        var output = await RunAsync(
            "$errors = $null; $tokens = $null; "
            + $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quoted(Path.Combine(Repository, script))}, [ref]$tokens, [ref]$errors); "
            + "$ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.CommandAst] }, $true) | "
            + "ForEach-Object { $_.GetCommandName() } | Where-Object { $_ } | Sort-Object -Unique");

        var used = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.NotEmpty(used);
        Assert.Empty(used.Intersect(CommandsThatAreScripts, StringComparer.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("", @"C:\Users\me\AppData\Local\Programs\YavShell", @"C:\Users\me\AppData\Local\Programs\YavShell")]
    [InlineData(@"C:\a;C:\b", @"C:\Programs\Yav Shell", @"C:\a;C:\b;C:\Programs\Yav Shell")]
    [InlineData(@"C:\a;C:\b;", @"C:\yav", @"C:\a;C:\b;C:\yav")]
    [InlineData(@"%USERPROFILE%\bin;C:\b", @"C:\yav", @"%USERPROFILE%\bin;C:\b;C:\yav")]
    [InlineData(@"C:\a;;C:\b", @"C:\yav", @"C:\a;;C:\b;C:\yav")]
    public async Task The_directory_is_added_at_the_end_and_nothing_else_is_changed(string path, string entry, string expected)
    {
        Assert.Equal(expected, await CallAsync("Add-YavPathEntry", path, entry));
    }

    [Theory]
    [InlineData(@"C:\a;C:\yav;C:\b", @"C:\yav")]
    [InlineData(@"C:\a;c:\YAV\;C:\b", @"C:\yav")]
    [InlineData(@"C:\a;""C:\yav"";C:\b", @"C:\yav")]
    [InlineData(@"C:\a; C:\yav ;C:\b", @"C:\yav\")]
    public async Task A_directory_that_is_there_already_is_not_added_again(string path, string entry)
    {
        Assert.Equal(path, await CallAsync("Add-YavPathEntry", path, entry));
    }

    [Theory]
    [InlineData(@"C:\a;C:\yav;C:\b", @"C:\yav", @"C:\a;C:\b")]
    [InlineData(@"C:\yav", @"C:\yav", "")]
    [InlineData(@"C:\a;c:\YAV\;C:\b;C:\yav", @"C:\yav", @"C:\a;C:\b")]
    [InlineData(@"%USERPROFILE%\bin;C:\yav", @"C:\yav", @"%USERPROFILE%\bin")]
    [InlineData(@"C:\yavx;C:\yav\sub;C:\yav", @"C:\yav", @"C:\yavx;C:\yav\sub")]
    public async Task The_directory_is_removed_and_nothing_else_is_changed(string path, string entry, string expected)
    {
        Assert.Equal(expected, await CallAsync("Remove-YavPathEntry", path, entry));
    }

    [Theory]
    [InlineData(@"C:\a;;C:\b;", @"C:\yav")]
    [InlineData("", @"C:\yav")]
    [InlineData(@"C:\yavx;%YAV%", @"C:\yav")]
    public async Task Removing_a_directory_that_is_not_there_changes_nothing_at_all(string path, string entry)
    {
        Assert.Equal(path, await CallAsync("Remove-YavPathEntry", path, entry));
    }

    [Fact]
    public async Task The_stand_in_for_powershell_7_hides_the_commands_of_windows_powershell_that_are_scripts()
    {
        // Counted after other commands of the same module were used, as the scripts do it. Used first, they
        // make Windows PowerShell load its own module when the stand-in does not claim to have them.
        const string Count = "$null = 'b', 'a' | Sort-Object | ConvertTo-Json; [Console]::Out.Write(@(Get-Command Get-FileHash -ErrorAction SilentlyContinue).Count)";

        Assert.Equal("1", await RunAsync(Count, StartedFrom.Windows));
        Assert.Equal("0", await RunAsync(Count, StartedFrom.BelowPowerShell7));
    }

    [Fact]
    public async Task The_sha256_of_a_file_is_the_one_everybody_else_computes()
    {
        using var directory = new TempDirectory("hash [1]");
        directory.Write("abc.txt", "abc");
        File.WriteAllBytes(directory.File("empty.bin"), []);
        var functions = Quoted(Path.Combine(Repository, "installer", "YavInstall.ps1"));

        var hashes = await RunAsync(
            $". {functions}; Get-YavSha256 -Path {Quoted(directory.File("abc.txt"))}; Get-YavSha256 -Path {Quoted(directory.File("empty.bin"))}",
            StartedFrom.BelowPowerShell7);

        // The values every description of SHA-256 gives for "abc" and for nothing.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            hashes);
    }

    [Theory]
    [InlineData(StartedFrom.Windows)]
    [InlineData(StartedFrom.BelowPowerShell7)]
    public async Task A_package_that_was_changed_after_it_was_built_is_noticed(StartedFrom startedFrom)
    {
        using var package = new TempDirectory("package [1]");
        package.Write("yav.exe", "program");
        package.Write("docs/user-guide.md", "guide");
        package.Write("docs/[more]/notes.md", "notes");
        var functions = Quoted(Path.Combine(Repository, "installer", "YavInstall.ps1"));
        await RunAsync(
            $". {functions}; $files = @(Get-YavFileHashes -Directory {Quoted(package.Path)}); "
            + "@{ version = '" + Fixtures.ProductVersion + "'; files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "
            + Quoted(package.File("package-manifest.json")) + " -Encoding UTF8",
            startedFrom);

        var untouched = await RunAsync($". {functions}; Test-YavPackage -Directory {Quoted(package.Path)}", startedFrom);
        package.Write("yav.exe", "another program");
        package.Write("docs/[more]/notes.md", "other notes");
        File.Delete(package.File("docs/user-guide.md"));
        var changed = await RunAsync($". {functions}; Test-YavPackage -Directory {Quoted(package.Path)}", startedFrom);

        Assert.Contains("docs/[more]/notes.md", package.Read("package-manifest.json"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, untouched);
        Assert.Equal(
            ["docs/[more]/notes.md differs from the file that was packaged.", "docs/user-guide.md is missing.", "yav.exe differs from the file that was packaged."],
            changed.Split('\n').Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>A package made of the program as it was built with the tests. It needs the .NET that runs the tests.</summary>
    private static async Task<TempDirectory> PackageAsync(StartedFrom startedFrom = StartedFrom.Windows)
    {
        var package = new TempDirectory("pack [1] ünï");
        var built = Path.GetDirectoryName(YavProcess.Executable)!;
        foreach (var file in Directory.EnumerateFiles(built, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(package.Path, Path.GetRelativePath(built, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }

        package.Write("docs/user-guide.md", "guide");
        foreach (var script in new[] { "install.ps1", "uninstall.ps1", "YavInstall.ps1" })
        {
            File.Copy(Path.Combine(Repository, "installer", script), package.File(script));
        }

        await RunAsync(
            $". {Quoted(package.File("YavInstall.ps1"))}; $files = @(Get-YavFileHashes -Directory {Quoted(package.Path)}); "
            + "@{ version = '" + Fixtures.ProductVersion + "'; files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "
            + Quoted(package.File("package-manifest.json")) + " -Encoding UTF8",
            startedFrom);
        return package;
    }

    private static Task<ProcessResult> ScriptAsync(StartedFrom startedFrom, string script, params string[] arguments) => Runner.RunAsync(
        new ProcessSpec(WindowsPowerShell, ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, .. arguments], Repository, EnvironmentOf(startedFrom)),
        new CaptureOptions(Timeout: TimeSpan.FromSeconds(180)),
        CancellationToken.None);

    [Theory]
    [InlineData(StartedFrom.Windows)]
    [InlineData(StartedFrom.BelowPowerShell7)]
    public async Task It_is_installed_into_and_removed_from_a_directory_with_blanks_brackets_and_letters_of_other_alphabets(StartedFrom startedFrom)
    {
        using var package = await PackageAsync(startedFrom);
        using var parent = new TempDirectory("install [2] ünï");
        var target = parent.File("YAV Shell [test]");

        var installed = await ScriptAsync(startedFrom, package.File("install.ps1"), "-InstallDir", target, "-NoRegister");

        Assert.True(installed.ExitCode == 0, installed.StandardError + installed.StandardOutput);
        // The directory is not compared: what Windows PowerShell writes to a pipe cannot hold every letter of its name.
        Assert.Contains($"Installed YAV Shell {Fixtures.ProductVersion} in ", installed.StandardOutput, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(target, "yav.exe")));
        Assert.True(File.Exists(Path.Combine(target, "docs", "user-guide.md")));
        Assert.True(File.Exists(Path.Combine(target, "uninstall.ps1")));
        var version = await Runner.RunAsync(new ProcessSpec(Path.Combine(target, "yav.exe"), ["--version"], target), new CaptureOptions(Timeout: TimeSpan.FromSeconds(60)), CancellationToken.None);
        Assert.Equal("yav " + Fixtures.ProductVersion, version.StandardOutput.Trim());

        File.WriteAllText(Path.Combine(target, "my own [file].txt"), "mine");
        var removed = await ScriptAsync(startedFrom, Path.Combine(target, "uninstall.ps1"), "-InstallDir", target);

        Assert.True(removed.ExitCode == 0, removed.StandardError + removed.StandardOutput);
        Assert.Equal(["my own [file].txt"], Directory.EnumerateFileSystemEntries(target, "*", SearchOption.AllDirectories).Select(entry => Path.GetFileName(entry)!).ToArray());
        Assert.Contains("holds other files and was kept", removed.StandardOutput, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Installing_again_replaces_what_was_installed_and_keeps_what_the_user_put_there()
    {
        using var package = await PackageAsync();
        using var parent = new TempDirectory("install again");
        var target = parent.File("YavShell");
        var first = await ScriptAsync(StartedFrom.Windows, package.File("install.ps1"), "-InstallDir", target, "-NoRegister");
        Assert.True(first.ExitCode == 0, first.StandardError + first.StandardOutput);
        File.WriteAllText(Path.Combine(target, "notes.txt"), "mine");
        File.WriteAllText(Path.Combine(target, "docs", "user-guide.md"), "damaged");

        var again = await ScriptAsync(StartedFrom.Windows, package.File("install.ps1"), "-InstallDir", target, "-NoRegister");

        Assert.True(again.ExitCode == 0, again.StandardError + again.StandardOutput);
        Assert.Equal("guide", File.ReadAllText(Path.Combine(target, "docs", "user-guide.md")));
        Assert.Equal("mine", File.ReadAllText(Path.Combine(target, "notes.txt")));
    }

    [Fact]
    public async Task Nothing_is_removed_from_a_directory_that_holds_no_installation()
    {
        using var package = await PackageAsync();
        using var other = new TempDirectory("not an installation");
        other.Write("yav.exe", "something of the user");

        var removed = await ScriptAsync(StartedFrom.Windows, package.File("uninstall.ps1"), "-InstallDir", other.Path);

        Assert.NotEqual(0, removed.ExitCode);
        Assert.Contains("does not hold an installation of YAV Shell", removed.StandardError + removed.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("something of the user", other.Read("yav.exe"));
    }

    [Fact]
    public async Task Installing_refuses_a_directory_that_holds_something_else_and_leaves_it_alone()
    {
        using var package = new TempDirectory("package");
        using var target = new TempDirectory("occupied");
        package.Write("yav.exe", "not a program");
        target.Write("my file.txt", "mine");
        var functions = Quoted(Path.Combine(Repository, "installer", "YavInstall.ps1"));
        File.Copy(Path.Combine(Repository, "installer", "install.ps1"), package.File("install.ps1"));
        File.Copy(Path.Combine(Repository, "installer", "YavInstall.ps1"), package.File("YavInstall.ps1"));
        await RunAsync(
            $". {functions}; $files = @(Get-YavFileHashes -Directory {Quoted(package.Path)}); "
            + "@{ version = '" + Fixtures.ProductVersion + "'; files = $files } | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath "
            + Quoted(package.File("package-manifest.json")) + " -Encoding UTF8");

        var result = await ScriptAsync(StartedFrom.Windows, package.File("install.ps1"), "-InstallDir", target.Path, "-NoRegister");

        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("was not created by this installer", result.StandardError + result.StandardOutput, StringComparison.Ordinal);
        Assert.Equal("mine", target.Read("my file.txt"));
        Assert.False(target.Exists("yav.exe"));
    }
}
