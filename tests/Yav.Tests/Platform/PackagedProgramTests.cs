using System.Diagnostics;
using System.Text;
using Yav.Core.Ports;
using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Platform;

/// <summary>Reported as skipped unless PowerShell 7 from the Microsoft Store is installed on this machine.</summary>
public sealed class StorePowerShellFactAttribute : FactAttribute
{
    public StorePowerShellFactAttribute(bool foundOnPath = false)
    {
        if (StorePowerShell.Installed is not { } store)
        {
            Skip = @"Needs PowerShell 7 from the Microsoft Store: the app execution alias pwsh.exe in %LOCALAPPDATA%\Microsoft\WindowsApps.";
        }
        else if (foundOnPath && !store.IsFoundOnPath)
        {
            Skip = "The pwsh that PATH finds on this machine is not PowerShell 7 from the Microsoft Store.";
        }
    }
}

/// <summary>
/// PowerShell 7 from the Microsoft Store: its app execution alias, and the packaged program the alias starts as the
/// alias itself names it.
/// </summary>
public sealed record StorePowerShell(string Alias, string Packaged)
{
    public static StorePowerShell? Installed { get; } = Find();

    /// <summary>True when looking up the name "pwsh" on PATH finds the alias or the packaged program.</summary>
    public bool IsFoundOnPath =>
        ExecutableResolver.Resolve("pwsh") is { } found
        && (string.Equals(found, Alias, StringComparison.OrdinalIgnoreCase) || string.Equals(found, Packaged, StringComparison.OrdinalIgnoreCase));

    private static StorePowerShell? Find()
    {
        var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "pwsh.exe");
        return AppExecutionAlias.Read(alias) is { } target && File.Exists(target.Program) ? new StorePowerShell(alias, target.Program) : null;
    }
}

/// <summary>
/// A packaged program (Microsoft Store, MSIX) is started by the runner like any other program: by its name, through
/// its app execution alias, or by the path of the packaged program. PowerShell 7 from the Store stands for all of them.
/// </summary>
public class PackagedProgramTests
{
    private const string SixTimesSeven = "[Console]::Out.Write(6*7)";

    [StorePowerShellFact(foundOnPath: true)]
    public async Task Store_PowerShell_named_by_its_name_starts_and_answers()
    {
        var result = await RunPowerShellAsync("pwsh", SixTimesSeven);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("42\n", result.StandardOutput);
    }

    [StorePowerShellFact]
    public async Task Store_PowerShell_named_by_its_app_execution_alias_starts_and_answers()
    {
        var result = await RunPowerShellAsync(StorePowerShell.Installed!.Alias, SixTimesSeven);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("42\n", result.StandardOutput);
    }

    [StorePowerShellFact]
    public async Task Store_PowerShell_named_by_the_path_of_the_packaged_program_starts_and_answers()
    {
        var result = await RunPowerShellAsync(StorePowerShell.Installed!.Packaged, SixTimesSeven);

        Assert.Equal(0, result.ExitCode);
        Assert.Equal("42\n", result.StandardOutput);
    }

    [StorePowerShellFact]
    public async Task A_name_on_path_as_both_the_alias_and_the_packaged_program_resolves_to_a_program_that_starts()
    {
        var store = StorePowerShell.Installed!;
        var aliasDirectory = Path.GetDirectoryName(store.Alias)!;
        var packagedDirectory = Path.GetDirectoryName(store.Packaged)!;

        var packagedFirst = ExecutableResolver.Resolve("pwsh", pathVariable: packagedDirectory + ";" + aliasDirectory);
        var aliasFirst = ExecutableResolver.Resolve("pwsh", pathVariable: aliasDirectory + ";" + packagedDirectory);

        Assert.Equal(store.Packaged, packagedFirst, ignoreCase: true);
        Assert.Equal(store.Alias, aliasFirst, ignoreCase: true);
        foreach (var resolved in new[] { packagedFirst!, aliasFirst! })
        {
            var result = await RunPowerShellAsync(resolved, SixTimesSeven);
            Assert.Equal(0, result.ExitCode);
            Assert.Equal("42\n", result.StandardOutput);
        }
    }

    [StorePowerShellFact]
    public async Task A_timeout_ends_Store_PowerShell_and_the_program_it_started()
    {
        var childId = 0;

        // The fixture is not part of any package, so Windows would start it outside PowerShell's package by default.
        var result = await RunPowerShellAsync(
            StorePowerShell.Installed!.Alias,
            "$child = Start-Process -FilePath $env:YAV_TEST_CHILD -ArgumentList 'tool','sleep','120' -NoNewWindow -PassThru; "
                + "[Console]::Out.WriteLine($child.Id); Start-Sleep -Seconds 120",
            TimeSpan.FromSeconds(15),
            new Dictionary<string, string?> { ["YAV_TEST_CHILD"] = Fixtures.FakeAgent },
            line =>
            {
                if (int.TryParse(line, out var id))
                {
                    childId = id;
                }
            });

        Assert.True(result.TimedOut);
        Assert.NotEqual(0, childId);
        Assert.True(await WaitUntilDeadAsync(childId), $"Program {childId}, started by Store PowerShell, survived the timeout.");
    }

    [StorePowerShellFact]
    public async Task Cancelling_Store_PowerShell_leaves_an_unrelated_Store_PowerShell_running()
    {
        var store = StorePowerShell.Installed!;

        // Started without YAV's process runner: this stands for a PowerShell window the user has open.
        var unrelatedStart = new ProcessStartInfo(store.Alias) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in new[] { "-NoProfile", "-Command", "Start-Sleep -Seconds 60" })
        {
            unrelatedStart.ArgumentList.Add(argument);
        }

        using var unrelated = Process.Start(unrelatedStart)!;
        try
        {
            var ownedId = 0;
            using var cancellation = new CancellationTokenSource();

            var result = await RunPowerShellAsync(
                store.Alias,
                "[Console]::Out.WriteLine($PID); Start-Sleep -Seconds 60",
                null,
                null,
                line =>
                {
                    if (int.TryParse(line, out var id))
                    {
                        // Stopped once it is known to run, however long PowerShell took to start.
                        ownedId = id;
                        cancellation.CancelAfter(TimeSpan.FromMilliseconds(200));
                    }
                },
                cancellation.Token);

            Assert.True(result.Cancelled);
            Assert.NotEqual(0, ownedId);
            Assert.True(await WaitUntilDeadAsync(ownedId), "The Store PowerShell YAV started survived cancellation.");
            Assert.False(unrelated.HasExited, "An unrelated Store PowerShell was ended.");
        }
        finally
        {
            if (!unrelated.HasExited)
            {
                unrelated.Kill();
            }
        }
    }

    [StorePowerShellFact]
    public async Task Store_PowerShell_inherits_only_the_pipes_it_was_given()
    {
        using var directory = new TempDirectory("handles");
        var marker = Guid.NewGuid().ToString("N");
        var path = directory.Write("marker.txt", marker);

        // An inheritable handle the runner was not asked to pass on. Only a program that inherited it can read the
        // marker through its number.
        using var stray = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Inheritable);
        var number = stray.SafeFileHandle.DangerousGetHandle().ToInt64();
        var probe = "try { $stream = [System.IO.FileStream]::new([Microsoft.Win32.SafeHandles.SafeFileHandle]::new([IntPtr]" + number
            + ", $false), [System.IO.FileAccess]::Read); $stream.Position = 0; "
            + "if ([System.IO.StreamReader]::new($stream).ReadToEnd().Contains('" + marker + "')) { 'inherited' } else { 'not inherited' } } "
            + "catch { 'not inherited' }";

        // The same probe started the way System.Diagnostics.Process does it, which passes on every inheritable handle,
        // shows that the probe can see an inherited handle at all.
        var control = new ProcessStartInfo(StorePowerShell.Installed!.Alias) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var argument in new[] { "-NoProfile", "-Command", probe })
        {
            control.ArgumentList.Add(argument);
        }

        using var controlProcess = Process.Start(control)!;
        var controlOutput = await controlProcess.StandardOutput.ReadToEndAsync().WaitAsync(TimeSpan.FromSeconds(60));
        await controlProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(60));

        var result = await RunPowerShellAsync(StorePowerShell.Installed.Alias, probe);

        Assert.Equal("inherited", controlOutput.Trim());
        Assert.Equal(0, result.ExitCode);
        Assert.Equal("not inherited\n", result.StandardOutput);
    }

    [StorePowerShellFact]
    public async Task A_Store_program_that_a_program_of_the_run_starts_is_not_ended_with_the_run()
    {
        // A limit of Windows, and not a wish: a packaged program is put into the job of its package and taken out of
        // the job of the program that started it. What the documents say about it rests on this, and on the program
        // beside it that is not packaged, is started in the same way, and is ended.
        const string Script =
            "$started = foreach ($program in $env:YAV_TEST_STORE, $env:YAV_TEST_PLAIN) { "
            + "$arguments = if ($program -eq $env:YAV_TEST_STORE) { '-NoProfile', '-Command', 'Start-Sleep -Seconds 90' } else { 'tool', 'sleep', '90' }; "
            + "(Start-Process -FilePath $program -ArgumentList $arguments -PassThru -WindowStyle Hidden).Id }; "
            + "[Console]::Out.WriteLine($started -join ' '); Start-Sleep -Seconds 90";
        var windowsPowerShell = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        using var directory = new TempDirectory("store");
        int[] started = [];
        try
        {
            var result = await new ProcessRunner().RunAsync(
                new ProcessSpec(
                    windowsPowerShell,
                    ["-NoProfile", "-Command", Script],
                    directory.Path,
                    new Dictionary<string, string?> { ["YAV_TEST_STORE"] = StorePowerShell.Installed!.Alias, ["YAV_TEST_PLAIN"] = Fixtures.FakeAgent }),
                new CaptureOptions(
                    Timeout: TimeSpan.FromSeconds(30),
                    OnOutputLine: line =>
                    {
                        var numbers = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        if (numbers.Length == 2 && numbers.All(number => int.TryParse(number, out _)))
                        {
                            started = [.. numbers.Select(int.Parse)];
                        }
                    }),
                CancellationToken.None);

            Assert.True(result.TimedOut);
            Assert.Equal(2, started.Length);
            Assert.True(await WaitUntilDeadAsync(started[1]), "The program that is not packaged survived the end of the run.");
            Assert.True(
                ProcessRunner.IsProcessAlive(started[0], null),
                "The Store program was ended with the run. Windows keeps it in the job now: the limit the documents describe is gone.");
        }
        finally
        {
            foreach (var id in started)
            {
                try
                {
                    using var left = Process.GetProcessById(id);
                    left.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    // It has ended.
                }
            }
        }
    }

    [Fact]
    public async Task A_program_placed_in_its_job_before_it_runs_uses_its_pipes_and_reports_its_exit_code()
    {
        using var directory = new TempDirectory("placed");

        var process = NativeProcess.Start(Fixtures.Tool(directory.Path, "stdin-lines"), Fixtures.FakeAgent, JobPlacement.BeforeItRuns);
        await using (process)
        {
            await process.StandardInput.WriteAsync("hello\n"u8.ToArray());
            await process.StandardInput.FlushAsync();
            using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8, false, 1024, leaveOpen: true);
            var reply = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20));

            var exitedOnItsOwn = await process.ShutdownAsync(TimeSpan.FromSeconds(15));

            Assert.Equal("got:hello", reply);
            Assert.True(exitedOnItsOwn);
            Assert.Equal(0, process.ExitCode);
        }
    }

    [Fact]
    public async Task Terminating_a_program_placed_in_its_job_before_it_runs_ends_everything_it_started()
    {
        using var directory = new TempDirectory("placed");
        int childId;

        var process = NativeProcess.Start(Fixtures.Tool(directory.Path, "spawn-tree", "120"), Fixtures.FakeAgent, JobPlacement.BeforeItRuns);
        await using (process)
        {
            using var reader = new StreamReader(process.StandardOutput, Encoding.UTF8, false, 1024, leaveOpen: true);
            childId = int.Parse((await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(20)))!);

            process.Terminate();

            await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
            Assert.True(await WaitUntilDeadAsync(childId), $"Child {childId} survived the end of the job.");
        }
    }

    private static async Task<ProcessResult> RunPowerShellAsync(
        string fileName,
        string command,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? environment = null,
        Action<string>? onOutputLine = null,
        CancellationToken cancellationToken = default)
    {
        using var directory = new TempDirectory("store");
        var runner = new ProcessRunner();
        return await runner.RunAsync(
            new ProcessSpec(fileName, ["-NoProfile", "-Command", command], directory.Path, environment),
            new CaptureOptions(Timeout: timeout ?? TimeSpan.FromSeconds(60), OnOutputLine: onOutputLine),
            cancellationToken);
    }

    private static async Task<bool> WaitUntilDeadAsync(int processId)
    {
        for (var i = 0; i < 100; i++)
        {
            if (!ProcessRunner.IsProcessAlive(processId, null))
            {
                return true;
            }

            await Task.Delay(100);
        }

        return false;
    }
}
