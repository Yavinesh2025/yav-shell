using System.Diagnostics;
using System.Globalization;
using Yav.Console.Composition;
using Yav.Core.Ports;
using Yav.Core.Settings;
using Yav.Platform.Consoles;
using Yav.Platform.Install;
using Yav.Platform.Processes;
using Yav.Platform.Security;

namespace Yav.Console.Install;

/// <summary>
/// Everything the installation commands and the start-up offer use outside themselves: the console they talk
/// to and the machine they change. Tests stand in for all of it, so that no test touches the registry, the
/// PATH or the data of the user.
/// </summary>
public sealed record InstallSurroundings
{
    public required Installer Installer { get; init; }

    /// <summary>The yav.exe that runs.</summary>
    public required string Program { get; init; }

    /// <summary>True when the program holds everything it needs in one file and can therefore install itself.</summary>
    public required bool IsSingleFile { get; init; }

    /// <summary>What is installed next to the program.</summary>
    public required IReadOnlyList<PackageFile> Files { get; init; }

    /// <summary>
    /// The directory that installations and removals use when none is named on the command line, in place of the one
    /// the installer chooses itself. Tests set it, so that nothing is installed into %LOCALAPPDATA%; for this process it
    /// is null. Then an installation goes where "Installed apps" says YAV Shell is installed, otherwise to
    /// <see cref="IInstallSystem.DefaultDirectory"/>, and a removal takes the directory "Installed apps" names, otherwise the
    /// directory of the program that runs when it holds yav-install.json.
    /// </summary>
    public string? InstallDirectory { get; init; }

    /// <summary>The data directory of the user, which '--remove-data' removes.</summary>
    public required YavPaths Data { get; init; }

    /// <summary>Where the API key of this data directory is kept.</summary>
    public required ICredentialStore Credentials { get; init; }

    public required TextWriter Output { get; init; }

    public required TextWriter Error { get; init; }

    /// <summary>True when somebody can answer: input and output are a console that has a window (see <see cref="ConsoleWindow.CanAsk"/>).</summary>
    public required bool CanAsk { get; init; }

    /// <summary>
    /// True when Windows made the console for this process alone, as it does when yav.exe is opened from Explorer.
    /// That window closes as soon as the process ends.
    /// </summary>
    public required bool OwnWindow { get; init; }

    /// <summary>Reads a line the user typed. Null at the end of the input.</summary>
    public required Func<CancellationToken, Task<string?>> ReadLine { get; init; }

    /// <summary>
    /// The PATH of this process, as the program that started it passed it on: in a console that was open already, the
    /// PATH of the shell in it.
    /// </summary>
    public required string InheritedPath { get; init; }

    /// <summary>
    /// Deletes the program that runs and the manifest beside it once this process has ended, and then the directory
    /// when nothing else is left in it. Best effort: what cannot be deleted stays. It deletes only while the manifest
    /// still holds the text of the removal (the second argument, <see cref="UninstallOutcome.Removal"/>), so that an
    /// installation into the directory in the meantime keeps its files. Called as the very last thing the process does.
    /// </summary>
    public required Action<string, string> DeleteAfterExit { get; init; }

    /// <summary>The surroundings of this process: its console, the registry of the current user, the Credential Manager.</summary>
    public static InstallSurroundings ForThisProcess(ConsoleHost host)
    {
        var data = YavPaths.Resolve();
        var window = ConsoleWindow.Exists;
        return new InstallSurroundings
        {
            Installer = new Installer(new WindowsInstallSystem(), AppServices.Version, data.Home, ReportedVersionAsync),
            Program = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, Installer.ExecutableName),
            IsSingleFile = IsSingleFileProgram,
            Files = PackageFiles.All,
            Data = data,
            Credentials = new WindowsCredentialStore(data.CredentialScope),
            Output = System.Console.Out,
            Error = System.Console.Error,
            CanAsk = ConsoleWindow.CanAsk(host.Capabilities.Interactive, window),
            OwnWindow = ConsoleWindow.IsOwn(ConsoleHost.ProcessesSharingConsole(), window),
            ReadLine = cancellationToken => Task.Run(System.Console.In.ReadLine, cancellationToken),
            InheritedPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty,
            DeleteAfterExit = DeleteOnceEnded,
        };
    }

    /// <summary>
    /// True for the single-file package. Its assemblies are read from inside yav.exe and have no file of their
    /// own, which is what an empty location says; a build of the source tree has yav.dll and the rest beside it.
    /// </summary>
#pragma warning disable IL3000 // An empty location is exactly what is asked for here: it tells the single-file package apart.
    public static bool IsSingleFileProgram { get; } = string.IsNullOrEmpty(typeof(InstallSurroundings).Assembly.Location);
#pragma warning restore IL3000

    /// <summary>
    /// Starts an installed yav.exe with --version, as the installation's last check, and returns what it wrote, or why
    /// it wrote nothing that counts: it could not be started, did not end within a minute, or ended with an exit code
    /// other than 0 (with the first line of its error output).
    /// </summary>
    private static async Task<VersionAnswer> ReportedVersionAsync(string executable, CancellationToken cancellationToken)
    {
        ProcessResult result;
        try
        {
            result = await new ProcessRunner().RunAsync(
                new ProcessSpec(executable, ["--version"], Path.GetDirectoryName(executable)!),
                new CaptureOptions(Timeout: TimeSpan.FromSeconds(60)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not (OperationCanceledException or OutOfMemoryException))
        {
            return new VersionAnswer(null, $"could not be started: {ex.Message}");
        }

        if (result.Cancelled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.TimedOut)
        {
            return new VersionAnswer(null, "gave no answer within 60 seconds");
        }

        if (result.ExitCode != 0)
        {
            var first = result.StandardError.ReplaceLineEndings("\n").Split('\n').FirstOrDefault(line => line.Trim().Length > 0)?.Trim();
            return new VersionAnswer(null, string.Create(
                CultureInfo.InvariantCulture,
                $"ended with exit code {result.ExitCode}{(first is null ? string.Empty : ": " + first)}"));
        }

        return new VersionAnswer(result.StandardOutput);
    }

    /// <summary>
    /// True when the program that runs can be deleted after the end of the process through CMD. CMD expands %NAME% even
    /// inside quotes, and !NAME! where delayed expansion is turned on (it is turned off for the deletion, but a path
    /// with it is left alone all the same); a quote would end the path. A path with any of them is left for the user to
    /// delete instead. Inside the quotes, &amp;, ^, ( and ) are taken as they are, and &lt;, &gt; and | cannot be part of a path.
    /// </summary>
    internal static bool CanDeleteAfterExit(string program) =>
        Path.GetDirectoryName(program) is not null && program.IndexOfAny(['%', '!', '"']) < 0;

    /// <summary>
    /// How the deletion after the end of the process is started: a hidden CMD that waits <paramref name="waitSeconds"/> for
    /// this process to end, and then, only while the manifest still holds <paramref name="removal"/>, deletes the program
    /// (once more after <paramref name="retrySeconds"/> when it was still in use), and the manifest and the directory only
    /// when the program is gone. rmdir without /s removes only an empty directory. PING and FINDSTR are named by their full
    /// path, and CMD runs in the system directory: a program of the same name in the working directory, or in the PATH,
    /// is never started. /d: no AutoRun commands of the user; /v:off: no delayed expansion, whatever the registry says;
    /// /s: the command is taken as it is between the outer quotes.
    /// </summary>
    internal static ProcessStartInfo DeletionStart(string program, string removal, int waitSeconds = 3, int retrySeconds = 10)
    {
        // The text goes onto the command line as it is: it may be nothing but hexadecimal digits.
        if (removal.Length == 0 || !removal.All(char.IsAsciiHexDigit))
        {
            throw new ArgumentException("The text of a removal is hexadecimal digits.", nameof(removal));
        }

        var system = Environment.GetFolderPath(Environment.SpecialFolder.System);
        var directory = Path.GetDirectoryName(program)!;
        var manifest = Path.Combine(directory, Installer.ManifestName);
        var ping = Path.Combine(system, "PING.EXE");
        var findstr = Path.Combine(system, "findstr.exe");
        // Each IF stands in parentheses of its own: CMD takes what follows "IF ... (...)" on the same line for part of
        // the IF, and would skip it with the IF. FINDSTR reads the manifest from its input, which CMD opens: given the
        // path itself, it would not find a file whose name has letters outside the code page, such as C:\Users\李明.
        var unchanged = $"\"{findstr}\" /l /c:{removal} <\"{manifest}\" >nul";
        var command = string.Create(
            CultureInfo.InvariantCulture,
            $"\"{ping}\" -n {waitSeconds + 1} 127.0.0.1 >nul & {unchanged} && ("
            + $"del /f /q \"{program}\" 2>nul"
            + $" & (if exist \"{program}\" (\"{ping}\" -n {retrySeconds + 1} 127.0.0.1 >nul & {unchanged} && del /f /q \"{program}\" 2>nul))"
            + $" & (if not exist \"{program}\" (del /f /q \"{manifest}\" 2>nul & rmdir \"{directory}\" 2>nul)))");
        return new ProcessStartInfo(Path.Combine(system, "cmd.exe"), $"/d /v:off /s /c \"{command}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = system,
        };
    }

    /// <summary>
    /// A program cannot delete its own file while it runs, and must not move it either: a program that is one file goes
    /// on reading its parts from where it was started. The deletion (see <see cref="DeletionStart"/>) is not part of any
    /// job of YAV, so it outlives this process.
    /// </summary>
    private static void DeleteOnceEnded(string program, string removal)
    {
        if (!CanDeleteAfterExit(program))
        {
            return;
        }

        try
        {
            using var deleting = Process.Start(DeletionStart(program, removal));
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            // Best effort: the program and its manifest stay; the directory is still an installation that 'yav install' or
            // 'yav uninstall --dir' accepts.
            _ = ex;
        }
    }
}
