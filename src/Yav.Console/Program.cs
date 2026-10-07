using System.Diagnostics;
using Yav.Console.Cli;
using Yav.Console.Composition;
using Yav.Console.Input;
using Yav.Console.Install;
using Yav.Console.Output;
using Yav.Console.Rendering;
using Yav.Console.Shell;
using Yav.Core.Settings;
using Yav.Platform.Consoles;
using Yav.Storage;

// Measured from the first instruction of YAV's own code. What the operating system and the runtime need
// before that is added from the process start time, so the number is what the user waited.
var entered = Stopwatch.GetTimestamp();
var sinceProcessStart = DateTime.Now - Process.GetCurrentProcess().StartTime;
var processStarted = entered - (long)(Math.Clamp(sinceProcessStart.TotalSeconds, 0, 60) * Stopwatch.Frequency);

var options = CommandLine.Parse(args);
switch (options.Mode)
{
    case CliMode.Help:
        System.Console.Out.WriteLine(CommandLine.Usage);
        return ExitCodes.Success;

    case CliMode.Version:
        System.Console.Out.WriteLine("yav " + AppServices.Version);
        return ExitCodes.Success;

    case CliMode.Invalid:
        if (args.Contains("--json"))
        {
            System.Console.Out.WriteLine(JsonOutput.Failure(options.Error!, ExitCodes.Invalid));
        }
        else
        {
            System.Console.Error.WriteLine("yav: " + options.Error);
            System.Console.Error.WriteLine("See 'yav --help'.");
        }

        return ExitCodes.Invalid;
}

var host = ConsoleHost.Initialize();
if (options.Mode is CliMode.Install or CliMode.Uninstall)
{
    // The first Control+C stops between two files, so that what was written is listed and can be removed or
    // completed; a second one ends the process.
    using var cancel = new CancellationTokenSource();
    System.Console.CancelKeyPress += (_, e) =>
    {
        if (!cancel.IsCancellationRequested)
        {
            e.Cancel = true;
            cancel.Cancel();
        }
    };

    // Before the data directory is opened: a removal that takes the data with it must find it closed.
    try
    {
        return options.Mode == CliMode.Install
            ? await InstallCommand.InstallAsync(options, host, cancel.Token)
            : await InstallCommand.UninstallAsync(options, host, cancel.Token);
    }
    catch (OperationCanceledException)
    {
        System.Console.Error.WriteLine(options.Mode == CliMode.Install
            ? "yav: stopped before the installation was complete. 'yav install' completes it; 'yav uninstall' removes what was written."
            : "yav: stopped before the removal was complete. 'yav uninstall' removes what is left.");
        KeepOwnWindowOpen();
        return ExitCodes.Failed;
    }
    catch (Exception ex) when (ex is not OutOfMemoryException)
    {
        // Whatever went wrong is shown, also in a window of its own that would otherwise close at once.
        System.Console.Error.WriteLine($"yav: {(options.Mode == CliMode.Install ? "the installation" : "the removal")} failed: {ex.GetType().Name}: {ex.Message}");
        KeepOwnWindowOpen();
        return ExitCodes.Failed;
    }
    finally
    {
        host.RestoreOriginal();
    }
}

AppServices services;
try
{
    services = AppServices.Create(YavPaths.Resolve());
}
catch (DatabaseVersionException ex)
{
    System.Console.Error.WriteLine("yav: " + ex.Message);
    KeepOwnWindowOpen();
    return ExitCodes.Failed;
}
catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
{
    System.Console.Error.WriteLine($"yav: the data directory could not be opened: {ex.Message}");
    System.Console.Error.WriteLine($"     Set {YavPaths.HomeVariable} to a directory you can write to.");
    KeepOwnWindowOpen();
    return ExitCodes.Failed;
}

await using (services)
{
    using var stop = new CancellationTokenSource();

    // 'yav' alone, from a copy that is not installed: it offers to install itself first.
    if (options.Mode == CliMode.Interactive && options.ProjectPath is null)
    {
        var offered = Stopwatch.GetTimestamp();
        if (await InstallOffer.OfferAsync(services, host, stop.Token) is { } ended)
        {
            host.RestoreOriginal();
            return ended;
        }

        // The time the offer waited for an answer, and installed, is not part of starting the shell: the measured
        // start of the console begins again where the offer ended.
        processStarted += Stopwatch.GetTimestamp() - offered;
    }

    if (options.Mode is CliMode.Run or CliMode.Doctor)
    {
        var interrupts = 0;
        System.Console.CancelKeyPress += (_, e) =>
        {
            // The first Control+C stops the run in an orderly way. A second one ends the process.
            if (Interlocked.Increment(ref interrupts) == 1)
            {
                e.Cancel = true;
                stop.Cancel();
            }
        };

        try
        {
            if (options.Mode == CliMode.Doctor)
            {
                return await DoctorCommand.ExecuteAsync(options, services, host.Capabilities, System.Console.Out, stop.Token);
            }

            // In a window rows are broken by YAV, so that nothing an agent wrote begins in the first column.
            // A stream gets every line as it is.
            var output = new Screen(
                host.Capabilities.OutputIsTerminal ? new SystemTerminal() : new StreamTerminal(System.Console.Out),
                new ScreenOptions(Rich: false, Color: false, Unicode: host.Capabilities.Unicode));
            return await RunCommand.ExecuteAsync(options, services, output, System.Console.Error, stop.Token);
        }
        catch (OperationCanceledException)
        {
            return ExitCodes.Interrupted;
        }
    }

    var plain = options.Plain || services.Settings.PlainOutput || !host.Capabilities.Interactive || !host.Capabilities.Rich;
    var color = !plain && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"));
    IShellInput input;
    Screen screen;
    InteractiveShell? shell = null;
    if (plain)
    {
        screen = new Screen(
            host.Capabilities.OutputIsTerminal ? new SystemTerminal() : new StreamTerminal(System.Console.Out),
            new ScreenOptions(Rich: false, Color: false, Unicode: host.Capabilities.Unicode));
        input = PlainInput.For(screen, System.Console.In, host.Capabilities);
    }
    else
    {
        // Control+C is a key like any other: it empties the line, stops a run, or leaves.
        System.Console.TreatControlCAsInput = true;
        screen = new Screen(new SystemTerminal(), new ScreenOptions(Rich: true, color, host.Capabilities.Unicode));
        var keys = new ConsoleKeySource();
        var history = new InputHistory();
        input = new TerminalInput(
            screen, keys, history,
            new IdlePrompt(screen, keys, services.Paths.HistoryFile, () => shell?.Session.ProjectPath));
    }

    shell = new InteractiveShell(services, screen, input, host, processStarted);
    if (plain)
    {
        // Without control over the keyboard Control+C arrives as a signal. It stops a run; without a run it ends YAV.
        System.Console.CancelKeyPress += (_, e) => e.Cancel = shell.Interrupt();
    }

    try
    {
        // Opened from Explorer, the Start menu or the Run dialog, the current directory is where yav.exe lies, not a
        // project the user chose: the shell starts without one, and the first request asks for it.
        var startedOutsideAShell = ConsoleWindow.IsOwn(ConsoleHost.ProcessesSharingConsole(), ConsoleWindow.Exists);
        return await shell.RunAsync(options.ProjectPath, stop.Token, startedOutsideAShell);
    }
    finally
    {
        host.RestoreOriginal();
    }
}

// A window Windows made for yav alone closes as soon as yav ends. What went wrong before anything else could be shown
// stays readable there until Enter is pressed.
static void KeepOwnWindowOpen()
{
    if (System.Console.IsInputRedirected || !ConsoleWindow.IsOwn(ConsoleHost.ProcessesSharingConsole(), ConsoleWindow.Exists))
    {
        return;
    }

    System.Console.Error.WriteLine();
    System.Console.Error.Write("Press Enter to close this window.");
    _ = System.Console.ReadLine();
}
