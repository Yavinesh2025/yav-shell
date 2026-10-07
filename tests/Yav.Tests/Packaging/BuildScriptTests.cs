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
/// The scripts that build, package and check YAV Shell. The check of a package runs in Windows PowerShell 5.1,
/// because that is what a new Windows has, and the functions the scripts share have to work there as well, so
/// that is what runs them here.
/// </summary>
public class BuildScriptTests
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

    private static string Tools => Quoted(Path.Combine(Repository, "scripts", "package-tools.ps1"));

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

    public static TheoryData<string> Scripts()
    {
        var data = new TheoryData<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Repository, "scripts"), "*.ps1").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(Repository, file));
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

        var hashes = await RunAsync(
            $". {Tools}; Get-YavSha256 -Path {Quoted(directory.File("abc.txt"))}; Get-YavSha256 -Path {Quoted(directory.File("empty.bin"))}",
            StartedFrom.BelowPowerShell7);

        // The values every description of SHA-256 gives for "abc" and for nothing.
        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad\ne3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            hashes);
    }

    [Theory]
    [InlineData(StartedFrom.Windows)]
    [InlineData(StartedFrom.BelowPowerShell7)]
    public async Task The_checksum_of_the_package_is_written_the_way_sha256sum_reads_it(StartedFrom startedFrom)
    {
        using var directory = new TempDirectory("checksum [1] ünï");
        directory.Write("my yav.exe", "abc");

        var returned = await RunAsync($". {Tools}; Write-YavChecksumFile -Path {Quoted(directory.File("my yav.exe"))}", startedFrom);

        const string Abc = "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad";
        Assert.Equal(Abc, returned);
        Assert.Equal(System.Text.Encoding.ASCII.GetBytes(Abc + "  my yav.exe\n"), File.ReadAllBytes(directory.File("my yav.exe.sha256")));
    }

    [Theory]
    [InlineData("unchanged", "")]
    [InlineData("changed", "yav.exe has the SHA-256 ")]
    [InlineData("no checksum", "yav.exe.sha256 does not exist.")]
    [InlineData("of another file", "yav.exe.sha256 is the checksum of 'other.exe', not of yav.exe.")]
    [InlineData("not a checksum", "yav.exe.sha256 does not have the form '<sha256>  <name>'.")]
    [InlineData("no program", "yav.exe does not exist.")]
    public async Task A_package_that_is_not_the_one_its_checksum_was_written_for_is_noticed(string what, string expected)
    {
        using var directory = new TempDirectory("checked [1]");
        directory.Write("yav.exe", "program");
        var program = Quoted(directory.File("yav.exe"));
        await RunAsync($". {Tools}; $null = Write-YavChecksumFile -Path {program}");
        switch (what)
        {
            case "changed":
                directory.Write("yav.exe", "another program");
                break;
            case "no checksum":
                File.Delete(directory.File("yav.exe.sha256"));
                break;
            case "of another file":
                directory.Write("yav.exe.sha256", directory.Read("yav.exe.sha256").Replace("yav.exe", "other.exe", StringComparison.Ordinal));
                break;
            case "not a checksum":
                directory.Write("yav.exe.sha256", "it is fine\n");
                break;
            case "no program":
                File.Delete(directory.File("yav.exe"));
                break;
        }

        var problems = await RunAsync($". {Tools}; Test-YavChecksumFile -Path {program}", StartedFrom.BelowPowerShell7);

        if (expected.Length == 0)
        {
            Assert.Equal(string.Empty, problems);
        }
        else
        {
            Assert.StartsWith(expected, problems, StringComparison.Ordinal);
            Assert.DoesNotContain('\n', problems);
        }
    }

    [Fact]
    public async Task The_build_scripts_read_the_version_yav_reports()
    {
        var version = await RunAsync($". {Tools}; Get-YavProductVersion -Repository {Quoted(Repository)}", StartedFrom.BelowPowerShell7);

        Assert.Equal(Fixtures.ProductVersion, version);
    }

    [Fact]
    public async Task The_package_is_published_as_one_self_contained_program_that_holds_its_native_library()
    {
        // Building the package takes minutes, so what the script asks the SDK for is read from the script: the
        // list of arguments that begins the publishing, one element per line, as it is written.
        var output = await RunAsync(
            "$errors = $null; $tokens = $null; "
            + $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quoted(Path.Combine(Repository, "scripts", "package.ps1"))}, [ref]$tokens, [ref]$errors); "
            + "$lists = @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.ArrayLiteralAst] }, $true) | "
            + "Where-Object { $_.Elements.Count -gt 0 -and $_.Elements[0].Value -eq 'publish' }); "
            + "[Console]::Out.WriteLine($lists.Count); $lists | ForEach-Object { $_.Elements } | ForEach-Object { [Console]::Out.WriteLine($_.Extent.Text) }");

        var lines = output.Split('\n').Select(line => line.Trim()).ToList();
        Assert.Equal("1", lines[0]);
        var publish = lines.Skip(1).Select(element => element.Trim('\'')).ToList();
        Assert.Equal("true", publish[publish.IndexOf("--self-contained") + 1]);
        Assert.Equal("win-x64", publish[publish.IndexOf("-r") + 1]);
        Assert.Equal("Release", publish[publish.IndexOf("-c") + 1]);
        Assert.Contains("-p:PublishSingleFile=true", publish);
        Assert.Contains("-p:IncludeNativeLibrariesForSelfExtract=true", publish);
        Assert.Contains("-p:PublishReadyToRun=true", publish);
        Assert.Contains("-p:DebugType=none", publish);
    }

    [Fact]
    public async Task The_package_keeps_its_assemblies_inside_the_file()
    {
        // IncludeAllContentForSelfExtract unpacks the assemblies to disk, which gives them a location. yav.exe then
        // takes itself for a build of the source tree and refuses to install itself.
        var output = await RunAsync(
            "$errors = $null; $tokens = $null; "
            + $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quoted(Path.Combine(Repository, "scripts", "package.ps1"))}, [ref]$tokens, [ref]$errors); "
            + "$ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.ArrayLiteralAst] }, $true) | "
            + "Where-Object { $_.Elements.Count -gt 0 -and $_.Elements[0].Value -eq 'publish' } | ForEach-Object { $_.Elements } | ForEach-Object { [Console]::Out.WriteLine($_.Extent.Text) }");

        var publish = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("'-p:PublishSingleFile=true'", publish);
        Assert.DoesNotContain(publish, a => a.Contains("IncludeAllContentForSelfExtract", StringComparison.OrdinalIgnoreCase));
        foreach (var file in new[] { Path.Combine("scripts", "package.ps1"), Path.Combine("src", "Yav.Console", "Yav.Console.csproj"), "Directory.Build.props" })
        {
            Assert.DoesNotContain("IncludeAllContentForSelfExtract", File.ReadAllText(Path.Combine(Repository, file)), StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task The_files_the_package_check_expects_installed_are_those_yav_exe_carries()
    {
        var listed = await RunAsync($". {Tools}; Get-YavPackageFiles -Repository {Quoted(Repository)}", StartedFrom.BelowPowerShell7);

        var expected = Yav.Console.Install.PackageFiles.All.Select(f => f.Path.Replace('/', '\\')).Order(StringComparer.OrdinalIgnoreCase).ToArray();
        Assert.Equal(expected, listed.Split('\n').Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.Contains(@"licenses\dotnet-runtime.txt", expected);
    }

    [Fact]
    public async Task The_files_the_package_check_expects_are_matched_by_their_extension_exactly()
    {
        using var repository = new TempDirectory("package files [1]");
        foreach (var file in new[] { "docs/guide.md", "docs/guide.mdx", "docs/sub/deeper.md", "licenses/a.txt", "licenses/b.txt2", "examples/x/y.json", "examples/z" })
        {
            repository.Write(file, "text");
        }

        var listed = await RunAsync($". {Tools}; Get-YavPackageFiles -Repository {Quoted(repository.Path)}", StartedFrom.BelowPowerShell7);

        Assert.Equal(
            new[] { "docs\\guide.md", "examples\\x\\y.json", "examples\\z", "LICENSE.txt", "licenses\\a.txt", "README.md", "THIRD-PARTY-NOTICES.md" }.Order(StringComparer.OrdinalIgnoreCase),
            listed.Split('\n').Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_package_check_is_given_every_parameter_it_requires_wherever_it_is_started()
    {
        var output = await RunAsync(
            "$errors = $null; $tokens = $null; "
            + $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quoted(Path.Combine(Repository, "scripts", "clean-machine-check.ps1"))}, [ref]$tokens, [ref]$errors); "
            + "$ast.ParamBlock.Parameters | Where-Object { @($_.Attributes | Where-Object { $_.TypeName.Name -eq 'Parameter' -and $_.NamedArguments.ArgumentName -contains 'Mandatory' }).Count -gt 0 } | "
            + "ForEach-Object { [Console]::Out.WriteLine($_.Name.VariablePath.UserPath) }");

        var required = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Contains("Expected", required);
        // The two lines that start the check: the command Windows Sandbox runs, and the start on this machine.
        var starts = File.ReadAllLines(Path.Combine(Repository, "scripts", "verify-package.ps1"))
            .Where(line => line.Contains("-File", StringComparison.Ordinal) && line.Contains("clean-machine-check.ps1", StringComparison.Ordinal))
            .ToList();
        Assert.Equal(2, starts.Count);
        foreach (var name in required)
        {
            foreach (var start in starts)
            {
                Assert.True(
                    System.Text.RegularExpressions.Regex.IsMatch(start, @"(?<![\w-])-" + name + @"\b"),
                    $"verify-package.ps1 does not pass -{name} to clean-machine-check.ps1 in: {start.Trim()}");
            }
        }
    }

    [Theory]
    [InlineData(0, "")]
    [InlineData(900, "--blame-hang-timeout|900s|--blame-hang-dump-type|none")]
    public async Task The_tests_have_a_time_limit_for_a_test_that_hangs_only_when_one_is_given(int seconds, string expected)
    {
        var output = await RunAsync(
            "$errors = $null; $tokens = $null; "
            + $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quoted(Path.Combine(Repository, "scripts", "test.ps1"))}, [ref]$tokens, [ref]$errors); "
            + "foreach ($function in $ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.FunctionDefinitionAst] }, $false)) { . ([ScriptBlock]::Create($function.Extent.Text)) }; "
            + $"[Console]::Out.Write((@(Get-HangArguments -HangSeconds {seconds}) -join '|'))");

        Assert.Equal(expected, output);
    }

    [Fact]
    public async Task Without_a_time_limit_the_tests_run_as_before_and_the_package_hands_a_given_one_on()
    {
        // The default of each script, as it is written, and how scripts\package.ps1 starts scripts\test.ps1.
        var output = await RunAsync(string.Join(
            "; ",
            new[] { "test.ps1", "package.ps1" }.Select(script =>
                "$errors = $null; $tokens = $null; "
                + $"$ast = [System.Management.Automation.Language.Parser]::ParseFile({Quoted(Path.Combine(Repository, "scripts", script))}, [ref]$tokens, [ref]$errors); "
                + "$parameter = @($ast.ParamBlock.Parameters | Where-Object { $_.Name.VariablePath.UserPath -eq 'HangSeconds' }); "
                + "[Console]::Out.WriteLine(('{0} {1}' -f $parameter.Count, $parameter[0].DefaultValue.Extent.Text))")));

        Assert.Equal("1 0\n1 0", output);
        var starts = File.ReadAllLines(Path.Combine(Repository, "scripts", "package.ps1"))
            .Where(line => line.Contains("'test.ps1'", StringComparison.Ordinal))
            .ToList();
        var start = Assert.Single(starts);
        Assert.Contains("-HangSeconds $HangSeconds", start, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_summary_of_the_tests_is_written_to_the_file_it_is_given_and_to_no_file_of_the_repository()
    {
        using var directory = new TempDirectory("summary [1]");
        directory.Write(
            "run.trx",
            """
            <?xml version="1.0" encoding="utf-8"?>
            <TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
              <Times creation="2026-10-07T10:03:00.0000000+00:00" start="2026-10-07T10:00:00.0000000+00:00" finish="2026-10-07T10:03:00.0000000+00:00" />
              <Results>
                <UnitTestResult testId="a" testName="Yav.Tests.Some.A" outcome="Passed" />
                <UnitTestResult testId="b" testName="Yav.Tests.Some.B" outcome="Failed" />
              </Results>
              <TestDefinitions>
                <UnitTest id="a"><TestMethod className="Yav.Tests.Some" name="A" /></UnitTest>
                <UnitTest id="b"><TestMethod className="Yav.Tests.Some" name="B" /></UnitTest>
              </TestDefinitions>
              <ResultSummary outcome="Failed"><Counters total="2" executed="2" passed="1" failed="1" /></ResultSummary>
            </TestRun>
            """);
        var inRepository = Path.Combine(Repository, "docs", "test-results.md");
        var before = File.ReadAllBytes(inRepository);
        var summary = directory.File(Path.Combine("not there yet", "summary.md"));

        await RunAsync(
            $"& {Quoted(Path.Combine(Repository, "scripts", "summarize-tests.ps1"))} -Results {Quoted(directory.File("run.trx"))} -Output {Quoted(summary)} | Out-Null");

        var lines = File.ReadAllLines(summary);
        Assert.Contains("| Tests | 2 |", lines);
        Assert.Contains("| Failed | 1 |", lines);
        Assert.Contains("| Time | 3 minutes |", lines);
        Assert.Contains("| Some | 1 | 1 |", lines);
        Assert.Equal(before, File.ReadAllBytes(inRepository));
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void A_script_is_read_the_same_by_every_powershell(string script)
    {
        // Without a byte order mark Windows PowerShell 5.1 reads a file in the code page of the machine, and
        // PowerShell 7 as UTF-8. A file in ASCII, or one with the mark, reads the same in both.
        var bytes = File.ReadAllBytes(Path.Combine(Repository, script));
        var hasMark = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;

        Assert.True(hasMark || bytes.All(b => b < 0x80), $"{script} has a character outside ASCII and no UTF-8 byte order mark.");
    }

    [Fact]
    public async Task A_relative_path_is_taken_from_the_location_of_powershell()
    {
        using var directory = new TempDirectory("relative [1]");
        directory.Write("my yav.exe", "abc");

        // The current directory of the process stays the repository; only the location of PowerShell is changed.
        var returned = await RunAsync($". {Tools}; Set-Location -LiteralPath {Quoted(directory.Path)}; Write-YavChecksumFile -Path 'my yav.exe'", StartedFrom.BelowPowerShell7);

        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", returned);
        Assert.True(File.Exists(directory.File("my yav.exe.sha256")));
        Assert.False(File.Exists(Path.Combine(Repository, "my yav.exe.sha256")));
    }

    [Theory]
    [InlineData("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD  yav.exe\n")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad *yav.exe\n")]
    [InlineData("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad  yav.exe\r\n")]
    public async Task A_checksum_in_another_form_that_sha256sum_reads_is_accepted(string checksum)
    {
        using var directory = new TempDirectory("forms [1]");
        directory.Write("yav.exe", "abc");
        directory.Write("yav.exe.sha256", checksum);

        Assert.Equal(string.Empty, await RunAsync($". {Tools}; Test-YavChecksumFile -Path {Quoted(directory.File("yav.exe"))}"));
    }

    [Theory]
    [InlineData("<PropertyGroup><Product>YAV Shell</Product></PropertyGroup>")]
    [InlineData("<PropertyGroup><Version>0.2.0</Version></PropertyGroup><PropertyGroup><Version>0.3.0</Version></PropertyGroup>")]
    [InlineData("<PropertyGroup><Version></Version></PropertyGroup>")]
    public async Task A_props_file_that_does_not_name_exactly_one_version_is_refused(string groups)
    {
        using var repository = new TempDirectory("props [1]");
        repository.Write("Directory.Build.props", "<Project>" + groups + "</Project>");

        var output = await RunAsync(
            $". {Tools}; try {{ Get-YavProductVersion -Repository {Quoted(repository.Path)}; 'no error' }} catch {{ $_.Exception.Message }}");

        Assert.Equal("Directory.Build.props does not name exactly one Version.", output);
    }
}
