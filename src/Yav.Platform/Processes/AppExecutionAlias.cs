using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Yav.Platform.Processes;

/// <summary>What an app execution alias starts: the package it belongs to, and the program.</summary>
/// <param name="PackageFamily">The family of the package, such as "Microsoft.PowerShell_8wekyb3d8bbwe".</param>
/// <param name="Program">The full path of the program the alias starts.</param>
public sealed record AppExecutionAliasTarget(string PackageFamily, string Program);

/// <summary>
/// Reads the program an app execution alias starts, without starting it. Windows puts such aliases into
/// %LOCALAPPDATA%\Microsoft\WindowsApps for programs of the Microsoft Store, and for the stand-ins of App
/// Installer that only lead to the Store.
/// </summary>
public static partial class AppExecutionAlias
{
    private const uint ReparseTagAppExecLink = 0x8000001B;
    private const uint GetReparsePoint = 0x000900A8;
    private const uint ReadAttributes = 0x0080;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;

    /// <summary>What the alias starts, or null when the file is not an app execution alias or cannot be read.</summary>
    public static AppExecutionAliasTarget? Read(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        using var handle = CreateFileW(path, ReadAttributes, (uint)(FileShare.ReadWrite | FileShare.Delete), 0, OpenExisting, OpenReparsePoint | BackupSemantics, 0);
        if (handle.IsInvalid)
        {
            return null;
        }

        var buffer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, GetReparsePoint, 0, 0, buffer, buffer.Length, out var returned, 0)
            || returned < 12
            || BitConverter.ToUInt32(buffer, 0) != ReparseTagAppExecLink)
        {
            return null;
        }

        // The tag is followed by the length of the data and a reserved word. The data is a version number and then
        // null-terminated strings: the package family, the application user model ID, the program and the app type.
        var textLength = Math.Min(BitConverter.ToUInt16(buffer, 4) - 4, returned - 12);
        if (textLength <= 0)
        {
            return null;
        }

        var strings = Encoding.Unicode.GetString(buffer, 12, textLength).Split('\0');
        return strings.Length > 2 && strings[0].Length > 0 && Path.IsPathFullyQualified(strings[2])
            ? new AppExecutionAliasTarget(strings[0], strings[2])
            : null;
    }

    [LibraryImport("kernel32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DeviceIoControl(SafeFileHandle device, uint code, nint input, int inputSize, [Out] byte[] output, int outputSize, out int returned, nint overlapped);
}
