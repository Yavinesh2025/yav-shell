using Yav.Console.Composition;
using Yav.Console.Output;
using Yav.Platform.Consoles;
using Yav.Platform.Install;

namespace Yav.Console.Install;

public enum OfferKind
{
    /// <summary>Nothing is asked; the shell starts.</summary>
    None,

    /// <summary>
    /// No installation is listed under "Installed apps", or its program is gone, and the program that runs is not in an
    /// installation directory: installing it is offered. One made with --no-register is not listed; only its own program
    /// knows that it is installed.
    /// </summary>
    Install,

    /// <summary>An older version is installed: replacing it with the program that runs is offered.</summary>
    Replace,
}

/// <summary>What decides whether 'yav' offers to install itself.</summary>
/// <param name="CanAsk">Input and output are a console that has a window, so somebody can answer (see <see cref="ConsoleWindow.CanAsk"/>).</param>
/// <param name="OwnWindow">Windows made the console for yav alone, as when yav.exe is opened from Explorer.</param>
/// <param name="DeclinedBefore">The user answered no before, in a console that was open already.</param>
/// <param name="Installed">The installation "Installed apps" knows of and whose program exists. Null when there is none.</param>
/// <param name="FromInstallation">
/// The folder of the program holds a readable yav-install.json of YAV Shell: the program is an installed copy, whether
/// "Installed apps" lists it or not (see <see cref="Installer.RunsFromInstallation"/>).
/// </param>
public sealed record OfferSituation(
    bool IsSingleFile,
    bool CanAsk,
    bool OwnWindow,
    bool DeclinedBefore,
    string Program,
    string Version,
    InstallRegistration? Installed,
    bool FromInstallation);

/// <summary>
/// What 'yav' does before the shell starts when the program that runs is not installed: it offers to install
/// itself, so that running yav.exe is all an installation takes.
/// </summary>
public static class InstallOffer
{
    private const string OfferFailed = "yav: the offer to install YAV Shell failed while ";

    /// <summary>An exit code when the process ends instead of starting the shell; null when the shell starts.</summary>
    public static async Task<int?> OfferAsync(AppServices services, ConsoleHost host, CancellationToken cancellationToken)
    {
        // A build of the source tree, a pipe, a file or a console without a window never asks. Nothing else is looked at then.
        if (!InstallSurroundings.IsSingleFileProgram || !ConsoleWindow.CanAsk(host.Capabilities.Interactive, ConsoleWindow.Exists))
        {
            return null;
        }

        InstallSurroundings world;
        try
        {
            world = InstallSurroundings.ForThisProcess(host);
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            await TryWriteAsync(System.Console.Error, Failed("finding out what is installed", ex)).ConfigureAwait(false);
            return null;
        }

        return await OfferAsync(
            world,
            services.Settings.InstallOfferDeclined,
            () => services.Update(settings => settings with { InstallOfferDeclined = true }),
            cancellationToken).ConfigureAwait(false);
    }

    public static OfferKind Decide(OfferSituation situation)
    {
        if (!situation.IsSingleFile || !situation.CanAsk)
        {
            return OfferKind.None;
        }

        if (situation.FromInstallation)
        {
            // Started from an installation directory, the program is installed, also when "Installed apps" does not list
            // it: an installation made with --no-register is neither installed again nor registered after all.
            return OfferKind.None;
        }

        if (situation.Installed is not { } installed)
        {
            // Opened from Explorer, a program asks every time: that is how a download is installed.
            return situation.OwnWindow || !situation.DeclinedBefore ? OfferKind.Install : OfferKind.None;
        }

        if (SamePath(situation.Program, Path.Combine(installed.InstallLocation, Installer.ExecutableName)))
        {
            return OfferKind.None;
        }

        return situation.OwnWindow && IsOlder(installed.DisplayVersion, situation.Version) ? OfferKind.Replace : OfferKind.None;
    }

    /// <param name="rememberDecline">Records that the user answered no in a console that was open already. False when it could not be saved.</param>
    public static async Task<int?> OfferAsync(InstallSurroundings world, bool declinedBefore, Func<bool> rememberDecline, CancellationToken cancellationToken)
    {
        var step = new Step();
        try
        {
            return await OfferCoreAsync(world, declinedBefore, rememberDecline, step, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsFailure(ex, cancellationToken))
        {
            // The offer is a convenience. Nothing that goes wrong in it may keep the shell from starting: not finding out
            // what is installed, not asking, not installing, and not remembering a no.
            await TryWriteAsync(world.Error, Failed(step.Doing, ex)).ConfigureAwait(false);
            return null;
        }
    }

    private static async Task<int?> OfferCoreAsync(InstallSurroundings world, bool declinedBefore, Func<bool> rememberDecline, Step step, CancellationToken cancellationToken)
    {
        // A program in an installation directory is installed: the registry is not read for it.
        var asks = world.IsSingleFile && world.CanAsk;
        var fromInstallation = asks && Installer.RunsFromInstallation(world.Program);
        var installed = asks && !fromInstallation ? world.Installer.Current() : null;
        var kind = Decide(new OfferSituation(
            world.IsSingleFile, world.CanAsk, world.OwnWindow, declinedBefore, world.Program, AppServices.Version, installed, fromInstallation));
        if (kind == OfferKind.None)
        {
            return null;
        }

        string question;
        string? directory = null;
        if (kind == OfferKind.Install)
        {
            // The directory that is shown is the one that is installed into.
            directory = world.Installer.DirectoryFor(world.InstallDirectory);
            foreach (var line in InstallReport.InstallOffer(AppServices.Version, directory))
            {
                await world.Output.WriteLineAsync(line).ConfigureAwait(false);
            }

            question = InstallReport.InstallQuestion;
        }
        else
        {
            foreach (var line in InstallReport.ReplaceOffer(installed!.DisplayVersion, installed.InstallLocation, AppServices.Version))
            {
                await world.Output.WriteLineAsync(line).ConfigureAwait(false);
            }

            question = InstallReport.ReplaceQuestion;
        }

        step.Doing = "asking";
        var answer = await AskAsync(world, question, cancellationToken).ConfigureAwait(false);

        // Control+C while it asked stops the offer, whatever line the console handed over after it: nothing is installed.
        cancellationToken.ThrowIfCancellationRequested();
        if (answer is null)
        {
            // Neither yes nor no: the input ended, or three answers were something else. Nothing is installed or
            // remembered, and the shell starts. At the end of the input the line break ends the line of the prompt;
            // after the three answers it leaves a blank line, as after a no.
            await world.Output.WriteLineAsync().ConfigureAwait(false);
            return null;
        }

        if (answer == false)
        {
            if (kind == OfferKind.Install && !world.OwnWindow)
            {
                step.Doing = "remembering your answer";
                await world.Output.WriteLineAsync(rememberDecline()
                    ? "Not installed. 'yav install' installs it; in a console it is not offered again."
                    : "Not installed. 'yav install' installs it. Your answer could not be saved, so it is offered again next time.").ConfigureAwait(false);
            }
            else
            {
                await world.Output.WriteLineAsync("Not installed.").ConfigureAwait(false);
            }

            await world.Output.WriteLineAsync().ConfigureAwait(false);
            return null;
        }

        step.Doing = "installing";
        var outcome = await InstallCommand.InstallAndReportAsync(
            world, new InstallOptions { Directory = directory ?? world.InstallDirectory }, cancellationToken).ConfigureAwait(false);
        if (world.OwnWindow)
        {
            // The window closes when yav ends; it was opened to install, and a console that knows the new PATH is a new one.
            await InstallCommand.CloseWindowAsync(world).ConfigureAwait(false);
            return outcome is null ? ExitCodes.Failed : ExitCodes.Success;
        }

        // In a console that was open already, the shell starts after the installation, or after what kept it from happening.
        await world.Output.WriteLineAsync().ConfigureAwait(false);
        return null;
    }

    /// <summary>
    /// True for yes, false for no. Null at the end of the input, and after three answers that were neither; then a line
    /// says that nothing was installed, in place of a prompt that would not be read.
    /// </summary>
    private static async Task<bool?> AskAsync(InstallSurroundings world, string question, CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            await world.Output.WriteAsync(attempt == 0 ? question : InstallReport.AnswerAgain).ConfigureAwait(false);
            var answer = await world.ReadLine(cancellationToken).ConfigureAwait(false);
            switch (answer?.Trim().ToLowerInvariant())
            {
                case null:
                    return null;
                case "" or "y" or "yes":
                    return true;
                case "n" or "no":
                    return false;
            }
        }

        await world.Output.WriteLineAsync(InstallReport.NotUnderstood).ConfigureAwait(false);
        return null;
    }

    /// <summary>True when the installed version is lower than this one. A version that cannot be read is not called older.</summary>
    internal static bool IsOlder(string installed, string version) =>
        Parse(installed) is { } old && Parse(version) is { } current && old < current;

    /// <summary>
    /// Everything that can go wrong in the offer, whatever it is: the registry, the file system, the console, a malformed
    /// path, or a defect. Only running out of memory, and a stop the caller asked for, are passed on.
    /// </summary>
    private static bool IsFailure(Exception ex, CancellationToken cancellationToken) =>
        ex is not OutOfMemoryException && !(ex is OperationCanceledException && cancellationToken.IsCancellationRequested);

    private static string Failed(string doing, Exception ex) =>
        $"{OfferFailed}{doing}: {ex.GetType().Name}: {ex.Message} The shell starts all the same.";

    private static async Task TryWriteAsync(TextWriter writer, string line)
    {
        try
        {
            await writer.WriteLineAsync(line).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // An error output that cannot be written to says nothing; the shell starts all the same.
            _ = ex;
        }
    }

    private static Version? Parse(string text)
    {
        // "0.2.0", or "0.2.0-preview.1+abc": what follows the numbers does not count here.
        var numbers = text.Trim().Split('-', '+')[0];
        return System.Version.TryParse(numbers, out var parsed) ? parsed : null;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>What the offer is doing, for the message when it fails.</summary>
    private sealed class Step
    {
        public string Doing { get; set; } = "finding out what is installed";
    }
}
