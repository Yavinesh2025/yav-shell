using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;
using Yav.Platform.Native;

namespace Yav.Platform.Install;

/// <summary>The PATH of the user account as the registry keeps it, not as a process sees it.</summary>
/// <param name="Value">The text, with %VARIABLES% not expanded, so that they are written back as they were.</param>
/// <param name="Expandable">True when Windows expands the value (REG_EXPAND_SZ), as it does for the PATH Windows creates.</param>
public sealed record UserPath(string Value, bool Expandable);

/// <summary>What "Installed apps" shows for YAV Shell, and what its removal needs to know.</summary>
public sealed record InstallRegistration(string InstallLocation, string DisplayVersion);

/// <summary>
/// Everything an installation of YAV Shell changes or looks at outside the directory it installs into. All of
/// it belongs to the current user: nothing here needs administrator rights.
/// </summary>
public interface IInstallSystem
{
    /// <summary>
    /// Where YAV Shell is installed when no directory is named and none is registered. For Windows that is
    /// %LOCALAPPDATA%\Programs\YavShell; a stand-in of a test names a directory of its own, so that no test can
    /// reach the installation of the user by leaving the directory out.
    /// </summary>
    string DefaultDirectory { get; }

    /// <summary>
    /// The PATH of the user account. An account without one has an empty one. Throws <see cref="InvalidDataException"/>
    /// when the PATH is stored as something other than text, which YAV does not rewrite.
    /// </summary>
    UserPath ReadUserPath();

    void WriteUserPath(UserPath path);

    /// <summary>Tells running programs, Explorer above all, that the environment changed, so that consoles opened from now on see it.</summary>
    void AnnounceEnvironmentChange();

    InstallRegistration? ReadRegistration();

    /// <summary>Adds YAV Shell to "Installed apps", with <paramref name="executable"/> as the program that removes it.</summary>
    void WriteRegistration(InstallRegistration registration, string executable, long installedBytes);

    void DeleteRegistration();

    /// <summary>The processes, of any user this one may ask about, whose program is this file.</summary>
    IReadOnlyList<int> ProcessesRunning(string executable);
}

/// <summary>The installation's changes to Windows: the registry of the current user, and the processes that run.</summary>
public sealed partial class WindowsInstallSystem : IInstallSystem
{
    public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\YavShell";
    private const string EnvironmentKey = "Environment";
    private const string PathValue = "Path";

    public string DefaultDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "YavShell");

    public UserPath ReadUserPath()
    {
        using var key = Registry.CurrentUser.OpenSubKey(EnvironmentKey, writable: false);
        if (key is null || !key.GetValueNames().Contains(PathValue, StringComparer.OrdinalIgnoreCase))
        {
            // Windows creates the PATH of an account as a value it expands.
            return new UserPath(string.Empty, Expandable: true);
        }

        var kind = key.GetValueKind(PathValue);
        if (kind is not (RegistryValueKind.String or RegistryValueKind.ExpandString))
        {
            // Written back as text, a value of another kind would lose what it holds.
            throw new InvalidDataException($"The PATH of the user account is stored as {kind}, not as text, so YAV leaves it as it is.");
        }

        // Read without expanding, so that %VARIABLES% in the PATH are written back as they were.
        var value = key.GetValue(PathValue, string.Empty, RegistryValueOptions.DoNotExpandEnvironmentNames) as string ?? string.Empty;
        return new UserPath(value, kind == RegistryValueKind.ExpandString);
    }

    public void WriteUserPath(UserPath path)
    {
        using var key = Registry.CurrentUser.CreateSubKey(EnvironmentKey, writable: true);
        key.SetValue(PathValue, path.Value, path.Expandable ? RegistryValueKind.ExpandString : RegistryValueKind.String);
    }

    public void AnnounceEnvironmentChange()
    {
        // A program that does not answer within five seconds is not waited for any longer.
        _ = NativeMethods.SendMessageTimeout(
            NativeMethods.HWND_BROADCAST, NativeMethods.WM_SETTINGCHANGE, 0, "Environment", NativeMethods.SMTO_ABORTIFHUNG, 5000, out _);
    }

    public InstallRegistration? ReadRegistration()
    {
        using var key = Registry.CurrentUser.OpenSubKey(UninstallKey, writable: false);
        if (key?.GetValue("InstallLocation") is not string location || location.Length == 0)
        {
            return null;
        }

        return new InstallRegistration(location, key.GetValue("DisplayVersion") as string ?? string.Empty);
    }

    public void WriteRegistration(InstallRegistration registration, string executable, long installedBytes)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
        key.SetValue("DisplayName", "YAV Shell", RegistryValueKind.String);
        key.SetValue("DisplayVersion", registration.DisplayVersion, RegistryValueKind.String);
        key.SetValue("Publisher", "YAV", RegistryValueKind.String);
        key.SetValue("InstallLocation", registration.InstallLocation, RegistryValueKind.String);
        key.SetValue("DisplayIcon", executable, RegistryValueKind.String);
        key.SetValue("UninstallString", $"\"{executable}\" uninstall", RegistryValueKind.String);
        key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture), RegistryValueKind.String);
        key.SetValue("EstimatedSize", (int)Math.Clamp((installedBytes + 1023) / 1024, 0, int.MaxValue), RegistryValueKind.DWord);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    public void DeleteRegistration() => Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);

    public IReadOnlyList<int> ProcessesRunning(string executable)
    {
        // Compared in the long form: a program started by its short name is still the same file.
        var wanted = LongPath.Of(executable);
        var found = new List<int>();
        if (!File.Exists(wanted))
        {
            return found;
        }

        // Windows may name the image of a process by the file it opened, after junctions, directory links and subst
        // drives were followed, or by the path it was started with. So both are compared by where they lead as well.
        var final = FinalPathOf(wanted) ?? wanted;

        // Every process is looked at, not only those of the same name: a program started by its short name
        // has the short name as the name of its process.
        var name = Path.GetFileName(wanted);
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (ImageOf(process.Id) is { } image
                    && (string.Equals(Path.GetFileName(image), name, StringComparison.OrdinalIgnoreCase) || image.Contains('~', StringComparison.Ordinal))
                    && (LongPath.Same(image, wanted) || LongPath.Same(image, final) || (FinalPathOf(image) is { } leadsTo && LongPath.Same(leadsTo, final))))
                {
                    found.Add(process.Id);
                }
            }
        }

        return found;
    }

    /// <summary>The path of a file after every junction, link and subst drive on the way was followed. Null when it cannot be found out.</summary>
    internal static unsafe string? FinalPathOf(string path)
    {
        try
        {
            using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var buffer = new char[32768];
            uint length;
            fixed (char* text = buffer)
            {
                length = GetFinalPathNameByHandle(handle, text, (uint)buffer.Length, 0);
            }

            if (length == 0 || length >= buffer.Length)
            {
                return null;
            }

            var final = new string(buffer, 0, (int)length);

            // VOLUME_NAME_DOS gives \\?\C:\... or \\?\UNC\server\share\...: written back the way a process image is named.
            if (final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase))
            {
                return @"\\" + final[8..];
            }

            return final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Then the program is compared by the name it was given only.
            _ = ex;
            return null;
        }
    }

    [System.Runtime.InteropServices.LibraryImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", SetLastError = true)]
    private static unsafe partial uint GetFinalPathNameByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle hFile, char* lpszFilePath, uint cchFilePath, uint dwFlags);

    /// <summary>The file a process was started from. Null when the process has ended, or may not be asked.</summary>
    private static unsafe string? ImageOf(int processId)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.PROCESS_QUERY_LIMITED_INFORMATION, false, processId);
        if (handle == 0)
        {
            return null;
        }

        try
        {
            // Long enough for the longest path Windows has.
            var buffer = new char[32768];
            var length = (uint)buffer.Length;
            fixed (char* text = buffer)
            {
                return NativeMethods.QueryFullProcessImageName(handle, 0, text, ref length) ? new string(text, 0, (int)length) : null;
            }
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }
}
