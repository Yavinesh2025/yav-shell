using System.ComponentModel;
using System.Runtime.InteropServices;
using Yav.Console.Cli;
using Yav.Console.Composition;
using Yav.Console.Output;
using Yav.Platform.Consoles;

namespace Yav.Console.Install;

/// <summary>'yav install' and 'yav uninstall'.</summary>
public static class InstallCommand
{
    /// <summary>Installs the yav.exe that runs for the current user. The exit code is that of the command.</summary>
    public static async Task<int> InstallAsync(CliOptions options, ConsoleHost host, CancellationToken cancellationToken) =>
        await Surroundings(host).ConfigureAwait(false) is { } world
            ? await InstallAsync(options, world, cancellationToken).ConfigureAwait(false)
            : ExitCodes.Failed;

    /// <summary>Removes the installation of the current user. The exit code is that of the command.</summary>
    public static async Task<int> UninstallAsync(CliOptions options, ConsoleHost host, CancellationToken cancellationToken) =>
        await Surroundings(host).ConfigureAwait(false) is { } world
            ? await UninstallAsync(options, world, cancellationToken).ConfigureAwait(false)
            : ExitCodes.Failed;

    /// <summary>
    /// The surroundings of this process. Null when they cannot be made; what went wrong was written to the error output
    /// then, and a window of its own waits for Enter, so that it can be read.
    /// </summary>
    private static async Task<InstallSurroundings?> Surroundings(ConsoleHost host)
    {
        try
        {
            return InstallSurroundings.ForThisProcess(host);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            await System.Console.Error.WriteLineAsync($"yav: {ex.Message}").ConfigureAwait(false);
            if (host.Capabilities.Interactive && ConsoleWindow.IsOwn(ConsoleHost.ProcessesSharingConsole(), ConsoleWindow.Exists))
            {
                await System.Console.Out.WriteLineAsync().ConfigureAwait(false);
                await System.Console.Out.WriteAsync(InstallReport.CloseWindow).ConfigureAwait(false);
                _ = System.Console.In.ReadLine();
            }

            return null;
        }
    }

    public static async Task<int> InstallAsync(CliOptions options, InstallSurroundings world, CancellationToken cancellationToken)
    {
        int code;
        try
        {
            code = await InstallCoreAsync(options, world, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            // Said here, before the window waits for Enter, so that it can be read there.
            await world.Error.WriteLineAsync($"yav: the installation failed: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            code = ExitCodes.Failed;
        }

        await CloseWindowAsync(world).ConfigureAwait(false);
        return code;
    }

    public static async Task<int> UninstallAsync(CliOptions options, InstallSurroundings world, CancellationToken cancellationToken)
    {
        UninstallOutcome? outcome = null;
        var handedOver = 0;

        // The program that runs can only be deleted once it has ended. Handed over once, and only when its path can be.
        void HandOver()
        {
            if (outcome is { ProgramLeftAt: { } left, Removal: { } removal }
                && InstallSurroundings.CanDeleteAfterExit(left)
                && Interlocked.Exchange(ref handedOver, 1) == 0)
            {
                world.DeleteAfterExit(left, removal);
            }
        }

        int code;
        try
        {
            code = await UninstallCoreAsync(options, world, removed => outcome = removed, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Stopped after the program was removed: it is deleted all the same once the process has ended.
            HandOver();
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            await world.Error.WriteLineAsync($"yav: the removal failed: {ex.GetType().Name}: {ex.Message}").ConfigureAwait(false);
            code = ExitCodes.Failed;
        }

        // A window closed with its X button ends the process inside the wait for Enter, before the code after it runs:
        // the deletion is handed over then, as the process is told that its console closes.
        using (outcome?.ProgramLeftAt is not null && world.OwnWindow ? PosixSignalRegistration.Create(PosixSignal.SIGHUP, _ => HandOver()) : null)
        {
            await CloseWindowAsync(world).ConfigureAwait(false);
        }

        // The very last thing the process does: the deletion waits only a few seconds for the process to end, and a
        // window that waited for Enter kept it running until now.
        HandOver();
        return code;
    }

    /// <summary>
    /// Installs and says what was done. Used by 'yav install' and by the start-up offer. Null when the installation was
    /// refused, failed, or was stopped with Control+C; what went wrong, and what an installation that stopped part-way
    /// left in place, was written to the error output then.
    /// </summary>
    internal static async Task<InstallOutcome?> InstallAndReportAsync(InstallSurroundings world, InstallOptions options, CancellationToken cancellationToken)
    {
        if (!world.IsSingleFile)
        {
            await world.Error.WriteLineAsync("yav: " + InstallReport.SourceTreeBuild).ConfigureAwait(false);
            return null;
        }

        InstallOutcome outcome;
        try
        {
            outcome = await world.Installer.InstallAsync(world.Program, world.Files, options, cancellationToken).ConfigureAwait(false);
        }
        catch (InstallException ex)
        {
            await world.Error.WriteLineAsync("yav: " + ex.Message).ConfigureAwait(false);
            return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            await world.Error.WriteLineAsync($"yav: the installation could not be completed: {ex.Message}").ConfigureAwait(false);
            return null;
        }

        foreach (var line in InstallReport.Installed(outcome, AppServices.Version, world.OwnWindow, world.InheritedPath))
        {
            await world.Output.WriteLineAsync(line).ConfigureAwait(false);
        }

        return outcome;
    }

    private static async Task<int> InstallCoreAsync(CliOptions options, InstallSurroundings world, CancellationToken cancellationToken)
    {
        var installOptions = new InstallOptions
        {
            Directory = options.InstallDirectory ?? world.InstallDirectory,
            AddToPath = !options.NoPath,
            Register = !options.NoRegister,
        };
        return await InstallAndReportAsync(world, installOptions, cancellationToken).ConfigureAwait(false) is null
            ? ExitCodes.Failed
            : ExitCodes.Success;
    }

    /// <param name="removed">Told what the removal of the program did, as soon as it is done, so that the program left behind is deleted whatever follows.</param>
    private static async Task<int> UninstallCoreAsync(CliOptions options, InstallSurroundings world, Action<UninstallOutcome> removed, CancellationToken cancellationToken)
    {
        if (!world.IsSingleFile && options.InstallDirectory is null)
        {
            // The tests start a build of the source tree with 'uninstall': it must never reach the installation of the user.
            await world.Error.WriteLineAsync("yav: " + InstallReport.SourceTreeUninstall).ConfigureAwait(false);
            return ExitCodes.Failed;
        }

        var home = world.Data.Home;
        var removeData = false;
        if (options.RemoveData)
        {
            // Asked before anything is removed: the answer decides about the data, not about the program.
            if (!world.CanAsk)
            {
                await world.Error.WriteLineAsync(
                    $"yav: --remove-data removes {home} and needs your confirmation, but nobody can be asked here. Nothing was removed.").ConfigureAwait(false);
                return ExitCodes.Failed;
            }

            if (DataDirectory.Problem(home) is { } problem)
            {
                await world.Error.WriteLineAsync($"yav: {problem} Nothing was removed.").ConfigureAwait(false);
                return ExitCodes.Failed;
            }

            // Looked for before anything is asked: the data goes only together with an installation, and a yes that
            // would remove nothing is not asked for.
            try
            {
                if (world.Installer.InstallationToRemove(options.InstallDirectory ?? world.InstallDirectory, world.Program) is null)
                {
                    await world.Error.WriteLineAsync("yav: " + NotInstalled(world)).ConfigureAwait(false);
                    return ExitCodes.Failed;
                }
            }
            catch (InstallException ex)
            {
                await world.Error.WriteLineAsync("yav: " + ex.Message).ConfigureAwait(false);
                return ExitCodes.Failed;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
            {
                await world.Error.WriteLineAsync($"yav: the removal could not be completed: {ex.Message}").ConfigureAwait(false);
                return ExitCodes.Failed;
            }

            var hasData = Directory.Exists(home);
            if (hasData || KeyStored(world))
            {
                await world.Output.WriteLineAsync(hasData
                    ? $"This also removes {home}: your settings, history and isolated workspaces, including changes that were never "
                        + "applied to a project, and the API key YAV stored for them."
                    : $"This also removes the API key YAV stored for {home}, which does not exist any more.").ConfigureAwait(false);
                await world.Output.WriteAsync("Type yes to remove your data as well: ").ConfigureAwait(false);
                var answer = await world.ReadLine(cancellationToken).ConfigureAwait(false);
                removeData = string.Equals(answer?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
                if (!removeData)
                {
                    // At the end of the input nothing ended the line of the prompt, so a line break comes first.
                    await world.Output.WriteLineAsync(answer is null ? Environment.NewLine + "Not confirmed: your data is kept." : "Not confirmed: your data is kept.").ConfigureAwait(false);
                }
            }
            else
            {
                await world.Output.WriteLineAsync($"There is no data of YAV to remove: {home} does not exist, and no API key is stored for it.").ConfigureAwait(false);
            }
        }

        UninstallOutcome outcome;
        try
        {
            outcome = world.Installer.Uninstall(options.InstallDirectory ?? world.InstallDirectory, world.Program);
        }
        catch (InstallException ex)
        {
            await world.Error.WriteLineAsync("yav: " + ex.Message).ConfigureAwait(false);
            return ExitCodes.Failed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or ArgumentException or NotSupportedException)
        {
            await world.Error.WriteLineAsync($"yav: the removal could not be completed: {ex.Message}").ConfigureAwait(false);
            return ExitCodes.Failed;
        }

        removed(outcome);
        foreach (var line in InstallReport.Removed(outcome))
        {
            await world.Output.WriteLineAsync(line).ConfigureAwait(false);
        }

        if (removeData)
        {
            return await RemoveDataAsync(world).ConfigureAwait(false) ? ExitCodes.Success : ExitCodes.Failed;
        }

        if (Directory.Exists(home))
        {
            // 'yav uninstall --remove-data' is no advice now: no installation is left that it could remove first.
            await world.Output.WriteLineAsync(KeyStored(world)
                ? $"Your data was kept: {home}. To remove it as well, delete that folder, and the API key YAV stored in the Windows Credential Manager (its name begins with YavShell/)."
                : $"Your data was kept: {home}. To remove it as well, delete that folder.").ConfigureAwait(false);
        }

        return ExitCodes.Success;
    }

    /// <summary>
    /// What --remove-data says when there is no installation to remove: nothing was removed, and the data, which goes only
    /// together with an installation, is removed by hand. The directory was found to be YAV's alone before.
    /// </summary>
    private static string NotInstalled(InstallSurroundings world)
    {
        var home = world.Data.Home;
        var keyStored = KeyStored(world);
        var data = Directory.Exists(home)
            ? keyStored
                ? $"Your data is in {home}; to remove it, delete that folder, and the API key YAV stored in the Windows Credential Manager (its name begins with YavShell/)."
                : $"Your data is in {home}; to remove it, delete that folder."
            : keyStored
                ? $"{home} does not exist; to remove the API key YAV stored for it, delete it in the Windows Credential Manager (its name begins with YavShell/)."
                : $"There is no data of YAV to remove either: {home} does not exist, and no API key is stored for it.";
        return "YAV Shell is not installed for this user account, so nothing was removed. " + data;
    }

    /// <summary>Removes the data directory and the API key, each on its own, and says what was removed and what was kept. False when anything was kept.</summary>
    private static async Task<bool> RemoveDataAsync(InstallSurroundings world)
    {
        var home = world.Data.Home;
        var complete = true;
        if (Directory.Exists(home))
        {
            try
            {
                DataDirectory.Remove(home);
                await world.Output.WriteLineAsync($"Removed {home}.").ConfigureAwait(false);
            }
            catch (InstallException ex)
            {
                await world.Error.WriteLineAsync("yav: " + ex.Message).ConfigureAwait(false);
                complete = false;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await world.Error.WriteLineAsync(
                    $"yav: {home} could not be removed completely: {ex.Message} What is left of it stays; delete it once nothing uses it.").ConfigureAwait(false);
                complete = false;
            }
        }

        try
        {
            if (world.Credentials.IsAvailable && world.Credentials.Delete(AppServices.AnthropicKeyName))
            {
                await world.Output.WriteLineAsync("Removed the API key YAV stored.").ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            await world.Error.WriteLineAsync(
                $"yav: the API key YAV stored could not be removed: {ex.Message} It stays in the Windows Credential Manager (its name begins with YavShell/).").ConfigureAwait(false);
            complete = false;
        }

        return complete;
    }

    /// <summary>True when an API key is stored for the data directory. A Credential Manager that cannot be asked counts as none.</summary>
    private static bool KeyStored(InstallSurroundings world)
    {
        try
        {
            return world.Credentials.IsAvailable && world.Credentials.Exists(AppServices.AnthropicKeyName);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            _ = ex;
            return false;
        }
    }

    /// <summary>
    /// A window Windows made for yav alone closes when yav ends. It stays until the user has read what was done, also
    /// after Control+C stopped the work: what that left is said just before.
    /// </summary>
    internal static async Task CloseWindowAsync(InstallSurroundings world)
    {
        if (!world.OwnWindow || !world.CanAsk)
        {
            return;
        }

        await world.Output.WriteLineAsync().ConfigureAwait(false);
        await world.Output.WriteAsync(InstallReport.CloseWindow).ConfigureAwait(false);
        await world.ReadLine(CancellationToken.None).ConfigureAwait(false);
    }
}
