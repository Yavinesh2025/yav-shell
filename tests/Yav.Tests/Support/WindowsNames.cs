using System.Runtime.InteropServices;
using System.Text;

namespace Yav.Tests.Support;

/// <summary>
/// The two names Windows keeps for a file or directory: the one it was given, and a short one for programs
/// that know no blanks and no long names. Where a volume keeps no short names, both are the same.
/// </summary>
public static class WindowsNames
{
    public static string Long(string path) => Convert(path, GetLongPathNameW);

    public static string Short(string path) => Convert(path, GetShortPathNameW);

    private delegate uint Converter(string path, StringBuilder buffer, uint size);

    private static string Convert(string path, Converter convert)
    {
        var buffer = new StringBuilder(1024);
        var length = convert(path, buffer, (uint)buffer.Capacity);
        if (length == 0 || length > buffer.Capacity)
        {
            throw new IOException($"Windows did not name '{path}': error {Marshal.GetLastPInvokeError()}.");
        }

        return buffer.ToString();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathNameW(string path, StringBuilder buffer, uint size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetShortPathNameW(string path, StringBuilder buffer, uint size);
}
