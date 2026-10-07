using System.Runtime.InteropServices;

namespace Yav.Console.Shell;

/// <summary>Folders of the user that .NET does not name, as Windows itself knows them.</summary>
internal static partial class KnownFolders
{
    // FOLDERID_Downloads, as Windows names the user's Downloads folder.
    private static readonly Guid DownloadsId = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>
    /// The user's Downloads folder, wherever it was moved to; %USERPROFILE%\Downloads when Windows cannot say.
    /// A program that was downloaded and opened from there starts in it.
    /// </summary>
    public static string Downloads { get; } = Read(DownloadsId)
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");

    private static string? Read(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(in id, 0, 0, out var path) != 0 || path == 0)
            {
                Marshal.FreeCoTaskMem(path);
                return null;
            }

            try
            {
                return Marshal.PtrToStringUni(path) is { Length: > 0 } text ? text : null;
            }
            finally
            {
                Marshal.FreeCoTaskMem(path);
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _ = ex;
            return null;
        }
    }

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}
