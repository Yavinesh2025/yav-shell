using System.Runtime.InteropServices;

namespace Yav.Console.Install;

/// <summary>Whether the console of this process has a window, and what that says about who can answer in it.</summary>
public static partial class ConsoleWindow
{
    private const uint StartfUseShowWindow = 0x1;
    private const ushort SwHide = 0;

    /// <summary>
    /// True when the console of this process has a window somebody can see: the window of the console host, or the one
    /// that stands for a pseudo console, as in Windows Terminal. A process started without a window (CREATE_NO_WINDOW)
    /// has a console nobody sees, although its input is a console all the same; so does one whose window was created
    /// hidden (Start-Process -WindowStyle Hidden), which has a window nobody sees.
    /// </summary>
    public static bool Exists => GetConsoleWindow() != 0 && !StartedHidden;

    /// <summary>True when the program that started this process asked for its window to be hidden (STARTF_USESHOWWINDOW with SW_HIDE).</summary>
    internal static bool StartedHidden { get; } = IsHidden(StartupFlags(out var show), show);

    /// <summary>
    /// True when Windows made the console for this process alone, as it does for yav.exe opened from Explorer: no
    /// other process uses it, and it has a window. Such a window closes as soon as the process ends.
    /// </summary>
    /// <param name="processesSharingConsole">ConsoleHost.ProcessesSharingConsole(): 1 for a console of its own, 2 or more under a shell.</param>
    public static bool IsOwn(int processesSharingConsole, bool hasWindow) => processesSharingConsole == 1 && hasWindow;

    /// <summary>True when somebody can answer a question: input and output are a console that somebody sees.</summary>
    public static bool CanAsk(bool interactive, bool hasWindow) => interactive && hasWindow;

    /// <summary>True for the start-up information of a window that was asked to be hidden.</summary>
    internal static bool IsHidden(uint flags, ushort showWindow) => (flags & StartfUseShowWindow) != 0 && showWindow == SwHide;

    private static uint StartupFlags(out ushort showWindow)
    {
        var info = new StartupInfo { Size = (uint)Marshal.SizeOf<StartupInfo>() };
        GetStartupInfoW(ref info);
        showWindow = info.ShowWindow;
        return info.Flags;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GetConsoleWindow();

    [LibraryImport("kernel32.dll")]
    private static partial void GetStartupInfoW(ref StartupInfo startupInfo);

    /// <summary>STARTUPINFOW.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfo
    {
        public uint Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public uint X;
        public uint Y;
        public uint XSize;
        public uint YSize;
        public uint XCountChars;
        public uint YCountChars;
        public uint FillAttribute;
        public uint Flags;
        public ushort ShowWindow;
        public ushort Reserved2Size;
        public nint Reserved2;
        public nint StandardInput;
        public nint StandardOutput;
        public nint StandardError;
    }
}
