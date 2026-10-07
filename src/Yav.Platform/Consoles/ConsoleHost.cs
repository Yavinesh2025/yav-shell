using System.Text;
using Yav.Platform.Native;

namespace Yav.Platform.Consoles;

public sealed record ConsoleCapabilities(
    bool InputIsTerminal,
    bool OutputIsTerminal,
    bool ErrorIsTerminal,
    // True when the terminal processes ANSI escape sequences.
    bool VirtualTerminal,
    bool Unicode,
    string Host)
{
    /// <summary>Interactive editing needs a real terminal on both sides.</summary>
    public bool Interactive => InputIsTerminal && OutputIsTerminal;

    /// <summary>Rich formatting needs a terminal that understands escape sequences.</summary>
    public bool Rich => OutputIsTerminal && VirtualTerminal;
}

/// <summary>
/// Remembers the console's modes and code pages so they can be put back exactly, for example after a
/// foreground shell changed them.
/// </summary>
public sealed class ConsoleHost
{
    private readonly uint? _originalOutputMode;
    private readonly uint? _originalInputMode;
    private readonly uint _originalOutputCodePage;
    private readonly uint _originalInputCodePage;
    private uint? _yavOutputMode;
    private uint? _yavInputMode;

    private ConsoleHost(uint? outputMode, uint? inputMode, uint outputCodePage, uint inputCodePage, ConsoleCapabilities capabilities)
    {
        _originalOutputMode = outputMode;
        _originalInputMode = inputMode;
        _originalOutputCodePage = outputCodePage;
        _originalInputCodePage = inputCodePage;
        Capabilities = capabilities;
    }

    public ConsoleCapabilities Capabilities { get; }

    /// <summary>Detects what the console can do and, where possible, turns on escape-sequence processing and UTF-8.</summary>
    public static ConsoleHost Initialize()
    {
        var outputHandle = NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE);
        var inputHandle = NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE);

        var outputIsTerminal = !Console.IsOutputRedirected && NativeMethods.GetConsoleMode(outputHandle, out var outputMode);
        var inputIsTerminal = !Console.IsInputRedirected && NativeMethods.GetConsoleMode(inputHandle, out var inputMode);
        var errorIsTerminal = !Console.IsErrorRedirected;

        uint? savedOutput = outputIsTerminal ? ReadMode(outputHandle) : null;
        uint? savedInput = inputIsTerminal ? ReadMode(inputHandle) : null;
        var outputCodePage = NativeMethods.GetConsoleOutputCP();
        var inputCodePage = NativeMethods.GetConsoleCP();

        var virtualTerminal = false;
        if (outputIsTerminal && savedOutput is { } mode)
        {
            var wanted = mode | NativeMethods.ENABLE_PROCESSED_OUTPUT | NativeMethods.ENABLE_VIRTUAL_TERMINAL_PROCESSING;
            virtualTerminal = (mode & NativeMethods.ENABLE_VIRTUAL_TERMINAL_PROCESSING) != 0
                || NativeMethods.SetConsoleMode(outputHandle, wanted);
        }

        var unicode = true;
        try
        {
            // Without a byte order mark: a mark written to a pipe would corrupt the first line for the reader.
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = utf8;
            if (inputIsTerminal)
            {
                Console.InputEncoding = utf8;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            unicode = false;
        }

        var host = DescribeHost();
        var capabilities = new ConsoleCapabilities(inputIsTerminal, outputIsTerminal, errorIsTerminal, virtualTerminal, unicode, host);
        var result = new ConsoleHost(savedOutput, savedInput, outputCodePage, inputCodePage, capabilities);
        result.CaptureYavModes();
        return result;
    }

    /// <summary>Remembers the modes YAV itself uses, so they can be reinstated after another program ran in the console.</summary>
    public void CaptureYavModes()
    {
        if (Capabilities.OutputIsTerminal)
        {
            _yavOutputMode = ReadMode(NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE));
        }

        if (Capabilities.InputIsTerminal)
        {
            _yavInputMode = ReadMode(NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE));
        }
    }

    /// <summary>Hands the console to another program in the state YAV found it in.</summary>
    public void RestoreOriginal()
    {
        Apply(_originalOutputMode, _originalInputMode);
        NativeMethods.SetConsoleOutputCP(_originalOutputCodePage);
        NativeMethods.SetConsoleCP(_originalInputCodePage);
    }

    /// <summary>Takes the console back after another program used it.</summary>
    public void RestoreYav()
    {
        Apply(_yavOutputMode, _yavInputMode);
        try
        {
            var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.OutputEncoding = utf8;
            if (Capabilities.InputIsTerminal)
            {
                Console.InputEncoding = utf8;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private void Apply(uint? outputMode, uint? inputMode)
    {
        if (Capabilities.OutputIsTerminal && outputMode is { } output)
        {
            NativeMethods.SetConsoleMode(NativeMethods.GetStdHandle(NativeMethods.STD_OUTPUT_HANDLE), output);
        }

        if (Capabilities.InputIsTerminal && inputMode is { } input)
        {
            NativeMethods.SetConsoleMode(NativeMethods.GetStdHandle(NativeMethods.STD_INPUT_HANDLE), input);
        }
    }

    /// <summary>
    /// How many processes use the console of this process, this one included. One means that Windows made the
    /// console for this process alone, as it does when the process is started from Explorer, the Start menu, the Run
    /// dialog or "Installed apps", not from a shell. Zero when the process has no console.
    /// </summary>
    public static unsafe int ProcessesSharingConsole()
    {
        // Only the number is wanted. A list that is too short is not filled, but the number is still returned.
        var list = stackalloc uint[4];
        return (int)NativeMethods.GetConsoleProcessList(list, 4);
    }

    private static uint? ReadMode(nint handle) => NativeMethods.GetConsoleMode(handle, out var mode) ? mode : null;

    private static string DescribeHost()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WT_SESSION")))
        {
            return "Windows Terminal";
        }

        var program = Environment.GetEnvironmentVariable("TERM_PROGRAM");
        if (!string.IsNullOrEmpty(program))
        {
            return program;
        }

        return "Windows Console Host";
    }
}
