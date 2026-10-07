using System.Text;

namespace Yav.Console.Install;

/// <summary>Most of what the installation commands and the start-up offer say, as plain text, so that it is tested without a console.</summary>
public static class InstallReport
{
    public const string SourceTreeBuild =
        "This yav.exe was built from the source tree and needs the files next to it, so it cannot install itself. "
        + "scripts\\package.ps1 builds dist\\yav.exe, which holds everything in one file: install that one.";

    /// <summary>
    /// A build of the source tree, the one the tests start among others, never falls back to the installation of the
    /// user: it removes only an installation named on its command line.
    /// </summary>
    public const string SourceTreeUninstall =
        "This yav.exe was built from the source tree, so it removes only an installation named with --dir: "
        + "'yav uninstall --dir <path>'. Nothing was removed. The installed YAV Shell removes itself with 'yav uninstall'.";

    public const string CloseWindow = "Press Enter to close this window.";

    /// <summary>What an installation did, and how YAV Shell is started now.</summary>
    /// <param name="ownWindow">True in a window Windows made for yav alone, which closes when it ends: a new console is needed anyway.</param>
    /// <param name="inheritedPath">
    /// The PATH of this process, as the program that started it passed it on: in a console that was open already, the
    /// PATH of the shell in it.
    /// </param>
    public static IReadOnlyList<string> Installed(InstallOutcome outcome, string version, bool ownWindow, string inheritedPath)
    {
        var directory = outcome.Directory;
        var lines = new List<string>
        {
            !outcome.ProgramCopied ? $"Repaired YAV Shell {version} in {directory}: its files were written again."
            : outcome.EarlierVersion is null ? $"Installed YAV Shell {version} in {directory}."
            : outcome.EarlierVersion == version ? $"Installed YAV Shell {version} in {directory} again, over the same version."
            : $"Updated YAV Shell from {outcome.EarlierVersion} to {version} in {directory}.",
        };

        lines.Add(outcome.Path switch
        {
            PathChange.Added => $"Added {directory} to the PATH of your user account: consoles opened from now on find 'yav'.",
            PathChange.WasThere => $"The PATH of your user account names {directory} already.",
            _ => $"The PATH was left as it is (--no-path). YAV Shell starts with:  & {PowerShellQuoted(outcome.Executable)}",
        });

        // A console that was open before keeps the PATH it started with. When the PATH of the account names the
        // directory now (not with --no-path), that console is told how to add it, unless it closes with yav anyway or
        // its own PATH names the directory already.
        var findsIt = outcome.Path != PathChange.NotAsked;
        if (findsIt && !ownWindow && !PathList.Contains(inheritedPath, directory))
        {
            lines.Add($"This console was opened before, so it does not know the new PATH. In it, run first:  $env:Path += {PowerShellQuoted(";" + directory)}");
            lines.Add($"(In CMD:  set \"PATH=%PATH%;{directory}\")");
        }

        lines.Add(outcome.Registered
            ? "\"Installed apps\" lists YAV Shell; it is removed there, or with 'yav uninstall'."
            : $"YAV Shell was not added to \"Installed apps\" (--no-register). 'yav uninstall --dir {CommandQuoted(directory)}' removes it.");

        lines.Add(string.Empty);
        if (ownWindow)
        {
            lines.Add("To start it, open PowerShell or Windows Terminal in the folder of a project (in Explorer: right-click the folder,");
            lines.Add(findsIt ? "Open in Terminal) and type:  yav" : $"Open in Terminal) and type:  & {PowerShellQuoted(outcome.Executable)}");
        }
        else
        {
            lines.Add(findsIt ? "Start it in the folder of a project:  yav" : $"Start it in the folder of a project:  & {PowerShellQuoted(outcome.Executable)}");
        }

        lines.Add("Then type what you want done. The first request asks which models to use.");
        return lines;
    }

    /// <summary>What a removal did.</summary>
    public static IReadOnlyList<string> Removed(UninstallOutcome outcome)
    {
        var directory = outcome.Directory;
        var left = outcome.ProgramLeftAt;
        var deletesItself = left is not null && InstallSurroundings.CanDeleteAfterExit(left);
        var lines = new List<string>
        {
            outcome.DirectoryRemoved ? $"Removed YAV Shell from {directory}."
            : outcome.OtherFilesKept ? $"Removed the files of YAV Shell. {directory} holds other files and was kept."
            : left is null ? $"Removed YAV Shell from {directory}. The empty folder is still in use by another program and stays; delete it later."
            : deletesItself ? $"Removed YAV Shell from {directory}. The program and its folder are deleted as soon as it has ended."
            : $"Removed YAV Shell from {directory}, except this program, which runs from there.",
        };

        if (outcome.Path == PathChange.Removed)
        {
            lines.Add($"Removed {directory} from the PATH of your user account.");
        }

        if (outcome.RegistrationRemoved)
        {
            lines.Add("Removed YAV Shell from \"Installed apps\".");
        }

        if (left is not null && !deletesItself)
        {
            lines.Add($"This program cannot delete itself in {directory}: its path holds a character that the deletion after the end "
                + (outcome.OtherFilesKept
                    ? $"would not pass on safely. Delete {Path.GetFileName(left)} and {Installer.ManifestName} there once YAV Shell has ended."
                    : "would not pass on safely. Delete the folder once YAV Shell has ended."));
        }
        else if (left is not null && outcome.OtherFilesKept)
        {
            lines.Add("This program is deleted as soon as it has ended.");
        }

        return lines;
    }

    /// <summary>What 'yav' says before it asks <see cref="InstallQuestion"/>, when the program that runs is not installed.</summary>
    public static IReadOnlyList<string> InstallOffer(string version, string directory) =>
    [
        $"YAV Shell {version} is not installed for your user account.",
        $"Installing it copies yav.exe to {directory} and adds that folder to the PATH of your user account,",
        "so that 'yav' starts YAV Shell in every console opened from now on. It is listed under \"Installed apps\", where it",
        "can be removed. No administrator rights are needed, and your data stays where it is.",
    ];

    public const string InstallQuestion = "Install now? Y (or Enter) installs, N starts YAV without installing it: ";

    /// <summary>What 'yav' says before it asks <see cref="ReplaceQuestion"/>, in a window opened from Explorer, when an older version is installed.</summary>
    public static IReadOnlyList<string> ReplaceOffer(string installedVersion, string directory, string version) =>
    [
        $"YAV Shell {installedVersion} is installed in {directory}. This is version {version}.",
    ];

    public const string ReplaceQuestion = "Replace the installed version with this one? Y (or Enter) replaces it, N starts this copy without installing it: ";

    public const string AnswerAgain = "Type Y or N, then Enter: ";

    /// <summary>What follows the third answer that was neither yes nor no, in place of a prompt that would not be read.</summary>
    public const string NotUnderstood = "Not installed: none of the three answers was Y or N.";

    /// <summary>
    /// A text as PowerShell reads it literally: in single quotes, with every character PowerShell takes for a single
    /// quote written twice. Those are ' and the typographic ones, U+2018 to U+201B; a profile directory such as
    /// C:\Users\O’Brien would otherwise end the text early.
    /// </summary>
    internal static string PowerShellQuoted(string text)
    {
        var quoted = new StringBuilder(text.Length + 2).Append('\'');
        foreach (var character in text)
        {
            quoted.Append(character);
            if (character is '\'' or (>= '\u2018' and <= '\u201B'))
            {
                quoted.Append(character);
            }
        }

        return quoted.Append('\'').ToString();
    }

    /// <summary>A path as a command line of yav takes it: in double quotes when it holds a blank.</summary>
    internal static string CommandQuoted(string path) => path.Contains(' ', StringComparison.Ordinal) ? "\"" + path + "\"" : path;
}
