using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Yav.Tests.Support;

/// <summary>
/// Nobody types into the console of a test run. A program that a test starts and that takes over the input
/// of this process finds its end at once, instead of waiting for keys: a shell that a defect started would
/// otherwise wait for ever, and keep the output of the whole run open behind it.
/// </summary>
internal static class NoKeyboard
{
    private const int StandardInput = -10;
    private const uint GenericRead = 0x80000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;

    [SuppressMessage("Usage", "CA2255:The 'ModuleInitializer' attribute should not be used in libraries", Justification = "The input has to be replaced before the first test starts a program.")]
    [ModuleInitializer]
    internal static void Apply()
    {
        // Inheritable, so that a program started with the handles of this process gets the same nothing.
        var inherited = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), InheritHandle = 1 };
        var nothing = CreateFileW("NUL", GenericRead, ShareReadWrite, ref inherited, OpenExisting, 0, IntPtr.Zero);
        if (nothing == new IntPtr(-1) || !SetStdHandle(StandardInput, nothing))
        {
            throw new InvalidOperationException($"The input of the tests could not be emptied: error {Marshal.GetLastPInvokeError()}.");
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public int Length;
        public IntPtr Descriptor;
        public int InheritHandle;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, ref SecurityAttributes security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetStdHandle(int which, IntPtr handle);
}
