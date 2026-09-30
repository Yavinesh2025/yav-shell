using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace Yav.Tests.Support;

/// <summary>
/// A console that exists without a window: the pseudo console of Windows, which is what Windows Terminal
/// itself uses to host a program. A test is the terminal here. It sends what a keyboard would send and
/// reads the screen the program drew.
/// </summary>
public sealed partial class PseudoConsole : IDisposable
{
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const int ProcThreadAttributePseudoConsole = 0x00020016;
    private const int StartfUseStdHandles = 0x00000100;
    private const uint StillActive = 259;

    private readonly nint _console;
    private readonly nint _process;
    private readonly FileStream _input;
    private readonly FileStream _output;
    private readonly Task _reader;
    private readonly Lock _gate = new();
    private readonly StringBuilder _raw = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<(TimeSpan At, int Length)> _arrivals = [];
    private bool _closed;

    private PseudoConsole(nint console, nint process, int processId, FileStream input, FileStream output, PtyScreen screen)
    {
        _console = console;
        _process = process;
        ProcessId = processId;
        _input = input;
        _output = output;
        Screen = screen;
        _reader = Task.Run(ReadAsync);
    }

    public int ProcessId { get; }

    /// <summary>What the program drew, as a terminal shows it.</summary>
    public PtyScreen Screen { get; }

    /// <summary>Everything the program wrote, as it arrived at the terminal.</summary>
    public string Raw
    {
        get
        {
            lock (_gate)
            {
                return _raw.ToString();
            }
        }
    }

    /// <summary>The time since the program was started.</summary>
    public TimeSpan Elapsed => _clock.Elapsed;

    /// <summary>
    /// When the output arrived that completed the text for the given time (the first, the second, ...),
    /// counted from the start of the program. Control sequences are not part of the text that is searched.
    /// The time is the one at which the output was read, so it does not depend on how often somebody looks.
    /// </summary>
    public TimeSpan? ArrivalOf(string text, int occurrence = 1)
    {
        string raw;
        (TimeSpan At, int Length)[] arrivals;
        lock (_gate)
        {
            raw = _raw.ToString();
            arrivals = [.. _arrivals];
        }

        foreach (var (at, length) in arrivals)
        {
            var shown = ControlSequences().Replace(raw[..length], string.Empty);
            var found = 0;
            for (var index = shown.IndexOf(text, StringComparison.Ordinal); index >= 0; index = shown.IndexOf(text, index + text.Length, StringComparison.Ordinal))
            {
                found++;
            }

            if (found >= occurrence)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>When output arrived for the last time.</summary>
    public TimeSpan? LastArrival
    {
        get
        {
            lock (_gate)
            {
                return _arrivals.Count == 0 ? null : _arrivals[^1].At;
            }
        }
    }

    /// <summary>When output arrived for the first time after the given time.</summary>
    public TimeSpan? FirstArrivalAfter(TimeSpan time)
    {
        lock (_gate)
        {
            foreach (var (at, _) in _arrivals)
            {
                if (at > time)
                {
                    return at;
                }
            }
        }

        return null;
    }

    /// <summary>Sends a key now and says when that was.</summary>
    public TimeSpan Press(string sequence)
    {
        var at = _clock.Elapsed;
        Send(sequence);
        return at;
    }

    [GeneratedRegex(@"\u001b\[[0-?]*[ -/]*[@-~]|\u001b\][^\u0007\u001b]*(\u0007|\u001b\\)|\u001b[@-_]")]
    private static partial Regex ControlSequences();

    public bool HasExited => GetExitCodeProcess(_process, out var code) && code != StillActive;

    public int ExitCode => GetExitCodeProcess(_process, out var code) ? unchecked((int)code) : -1;

    public static PseudoConsole Start(
        string executable,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        int columns = 120,
        int rows = 40)
    {
        if (!CreatePipe(out var inputRead, out var inputWrite, 0, 0) || !CreatePipe(out var outputRead, out var outputWrite, 0, 0))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "A pipe for the pseudo console could not be created.");
        }

        var result = CreatePseudoConsole(new Coord((short)columns, (short)rows), inputRead, outputWrite, 0, out var console);
        if (result != 0)
        {
            throw new Win32Exception(result, "The pseudo console could not be created.");
        }

        // The console holds its own copies of these ends.
        inputRead.Dispose();
        outputWrite.Dispose();

        var attributes = nint.Zero;
        var environmentBlock = nint.Zero;
        try
        {
            nint size = 0;
            InitializeProcThreadAttributeList(nint.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)
                || !UpdateProcThreadAttribute(attributes, 0, ProcThreadAttributePseudoConsole, console, nint.Size, nint.Zero, nint.Zero))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "The pseudo console could not be given to the process.");
            }

            var startup = new StartupInfoEx
            {
                // Without this the program would get the redirected handles of the test process instead of
                // those of its console: "use these handles", and none are given.
                StartupInfo = new StartupInfo { Size = Marshal.SizeOf<StartupInfoEx>(), Flags = StartfUseStdHandles },
                AttributeList = attributes,
            };
            environmentBlock = Marshal.StringToHGlobalUni(EnvironmentBlock(environment));
            var commandLine = new StringBuilder(CommandLine(executable, arguments));
            if (!CreateProcessW(
                null, commandLine, nint.Zero, nint.Zero, false,
                ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                environmentBlock, workingDirectory, ref startup, out var information))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"'{executable}' could not be started in the pseudo console.");
            }

            CloseHandle(information.Thread);
            return new PseudoConsole(
                console,
                information.Process,
                information.ProcessId,
                new FileStream(inputWrite, FileAccess.Write, 1, isAsync: false),
                new FileStream(outputRead, FileAccess.Read, 4096, isAsync: false),
                new PtyScreen(columns, rows));
        }
        catch
        {
            ClosePseudoConsole(console);
            inputWrite.Dispose();
            outputRead.Dispose();
            throw;
        }
        finally
        {
            if (attributes != nint.Zero)
            {
                DeleteProcThreadAttributeList(attributes);
                Marshal.FreeHGlobal(attributes);
            }

            if (environmentBlock != nint.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    /// <summary>Sends text as a keyboard would: one key after the other, with the time a person needs between them.</summary>
    public async Task TypeAsync(string text, int millisecondsBetweenKeys = 25)
    {
        foreach (var element in text.EnumerateRunes())
        {
            Send(element.ToString());
            await Task.Delay(millisecondsBetweenKeys);
        }
    }

    /// <summary>Sends text at once, as it arrives when it is pasted.</summary>
    public void Paste(string text) => Send(text.ReplaceLineEndings("\r"));

    public async Task EnterAsync()
    {
        // A pause before the key, so that it is a key that was pressed and not the end of something pasted.
        await Task.Delay(120);
        Send("\r");
    }

    public async Task PressAsync(string sequence)
    {
        await Task.Delay(120);
        Send(sequence);
    }

    public Task ControlCAsync() => PressAsync("\u0003");

    public void Resize(int columns, int rows)
    {
        var result = ResizePseudoConsole(_console, new Coord((short)columns, (short)rows));
        if (result != 0)
        {
            throw new Win32Exception(result, "The pseudo console could not be resized.");
        }

        Screen.Resize(columns, rows);
    }

    public async Task WaitForAsync(string text, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!Screen.Shows(text))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"'{text}' did not appear within {seconds} seconds. The screen:\n{Screen.Text}");
            }

            if (HasExited && _reader.IsCompleted && !Screen.Shows(text))
            {
                Assert.Fail($"The program ended with exit code {ExitCode} before '{text}' appeared. The screen:\n{Screen.Text}");
            }

            await Task.Delay(25);
        }
    }

    public async Task WaitUntilAsync(Func<PtyScreen, bool> reached, string what, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!reached(Screen))
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Within {seconds} seconds the screen did not show {what}. The screen:\n{Screen.Text}");
            }

            await Task.Delay(25);
        }
    }

    /// <summary>Waits until the line the cursor is on is that text, which is how a prompt that waits for input looks.</summary>
    public async Task WaitForCursorLineAsync(string text, int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (Screen.CursorLine != text.TrimEnd())
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"The cursor did not come to rest on '{text}' within {seconds} seconds; it is on '{Screen.CursorLine}'. The screen:\n{Screen.Text}");
            }

            await Task.Delay(25);
        }
    }

    public async Task<int> WaitForExitAsync(int seconds = 60)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (!HasExited)
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"The program did not end within {seconds} seconds. The screen:\n{Screen.Text}");
            }

            await Task.Delay(25);
        }

        return ExitCode;
    }

    public void Dispose()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        if (!HasExited)
        {
            TerminateProcess(_process, 1);
            WaitForSingleObject(_process, 5000);
        }

        try
        {
            _input.Dispose();
        }
        catch (IOException)
        {
        }

        // Closing the console ends what is still attached to it and closes the output, which ends the reader.
        ClosePseudoConsole(_console);
        try
        {
            _reader.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
        }

        _output.Dispose();
        CloseHandle(_process);
    }

    private void Send(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        _input.Write(bytes, 0, bytes.Length);
        _input.Flush();
    }

    private void ReadAsync()
    {
        var buffer = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var characters = new char[8192];
        try
        {
            while (true)
            {
                var read = _output.Read(buffer, 0, buffer.Length);
                if (read <= 0)
                {
                    return;
                }

                var count = decoder.GetChars(buffer, 0, read, characters, 0);
                var text = new string(characters, 0, count);
                lock (_gate)
                {
                    _raw.Append(text);
                    _arrivals.Add((_clock.Elapsed, _raw.Length));
                }

                Screen.Write(text);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The console was closed.
        }
    }

    private static string EnvironmentBlock(IReadOnlyDictionary<string, string?> changes)
    {
        var variables = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            variables[(string)entry.Key] = (string?)entry.Value ?? string.Empty;
        }

        foreach (var (name, value) in changes)
        {
            if (value is null)
            {
                variables.Remove(name);
            }
            else
            {
                variables[name] = value;
            }
        }

        var block = new StringBuilder();
        foreach (var (name, value) in variables)
        {
            block.Append(name).Append('=').Append(value).Append('\0');
        }

        return block.Append('\0').ToString();
    }

    private static string CommandLine(string executable, IReadOnlyList<string> arguments)
    {
        var line = new StringBuilder();
        Quote(line, executable);
        foreach (var argument in arguments)
        {
            line.Append(' ');
            Quote(line, argument);
        }

        return line.ToString();
    }

    // The rules of the C runtime for command lines.
    private static void Quote(StringBuilder line, string argument)
    {
        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '"']) < 0)
        {
            line.Append(argument);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                line.Append('\\', (backslashes * 2) + 1).Append('"');
            }
            else
            {
                line.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        line.Append('\\', backslashes * 2).Append('"');
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct Coord(short x, short y)
    {
        public readonly short X = x;
        public readonly short Y = y;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int Size;
        public nint Reserved;
        public nint Desktop;
        public nint Title;
        public int X;
        public int Y;
        public int XSize;
        public int YSize;
        public int XCountChars;
        public int YCountChars;
        public int FillAttribute;
        public int Flags;
        public short ShowWindow;
        public short Reserved2;
        public nint Reserved3;
        public nint StdInput;
        public nint StdOutput;
        public nint StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process;
        public nint Thread;
        public int ProcessId;
        public int ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreatePipe(out SafeFileHandle read, out SafeFileHandle write, nint attributes, int size);

    [DllImport("kernel32.dll")]
    private static extern int CreatePseudoConsole(Coord size, SafeFileHandle input, SafeFileHandle output, uint flags, out nint console);

    [DllImport("kernel32.dll")]
    private static extern int ResizePseudoConsole(nint console, Coord size);

    [DllImport("kernel32.dll")]
    private static extern void ClosePseudoConsole(nint console);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value, nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string? application,
        StringBuilder commandLine,
        nint processAttributes,
        nint threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint flags,
        nint environment,
        string currentDirectory,
        ref StartupInfoEx startup,
        out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(nint process, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(nint handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}

/// <summary>
/// The screen of a terminal, as far as a test needs it: it understands the sequences a console host
/// writes to position the cursor and to erase, and it ignores the ones that only change how text looks.
/// </summary>
public sealed class PtyScreen
{
    private readonly Lock _gate = new();
    private readonly List<string> _scrolledOff = [];
    private char[][] _rows;
    private int _columns;
    private int _row;
    private int _column;
    private bool _pendingWrap;
    private int _savedRow;
    private int _savedColumn;
    private string _carry = string.Empty;

    public PtyScreen(int columns, int rows)
    {
        _columns = columns;
        _rows = Enumerable.Range(0, rows).Select(_ => Blank(columns)).ToArray();
    }

    public string Title { get; private set; } = string.Empty;

    /// <summary>The rows that are on the screen and the ones that left it at the top, without blanks at their end.</summary>
    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (_gate)
            {
                var lines = _scrolledOff.Concat(_rows.Select(Read)).ToList();
                while (lines.Count > 0 && lines[^1].Length == 0)
                {
                    lines.RemoveAt(lines.Count - 1);
                }

                return lines;
            }
        }
    }

    public string Text => string.Join('\n', Lines);

    public string CursorLine
    {
        get
        {
            lock (_gate)
            {
                return Read(_rows[_row]);
            }
        }
    }

    public int CursorColumn
    {
        get
        {
            lock (_gate)
            {
                return _column;
            }
        }
    }

    /// <summary>True when the screen says that, wherever its rows end and however many blanks separate the words.</summary>
    public bool Shows(string text) => ShellHarness.Flatten(Text).Contains(ShellHarness.Flatten(text), StringComparison.Ordinal);

    public void Resize(int columns, int rows)
    {
        lock (_gate)
        {
            var resized = Enumerable.Range(0, rows).Select(_ => Blank(columns)).ToArray();
            for (var r = 0; r < Math.Min(rows, _rows.Length); r++)
            {
                Array.Copy(_rows[r], resized[r], Math.Min(columns, _columns));
            }

            _rows = resized;
            _columns = columns;
            _row = Math.Min(_row, rows - 1);
            _column = Math.Min(_column, columns - 1);
            _pendingWrap = false;
        }
    }

    public void Write(string text)
    {
        lock (_gate)
        {
            text = _carry + text;
            _carry = string.Empty;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                switch (c)
                {
                    case '\u001b':
                    {
                        var end = Escape(text, i);
                        if (end < 0)
                        {
                            // The sequence continues in what arrives next.
                            _carry = text[i..];
                            return;
                        }

                        i = end;
                        break;
                    }

                    case '\r':
                        _column = 0;
                        _pendingWrap = false;
                        break;
                    case '\n':
                        LineFeed();
                        break;
                    case '\b':
                        _column = Math.Max(0, _column - 1);
                        _pendingWrap = false;
                        break;
                    case '\t':
                        _column = Math.Min(_columns - 1, ((_column / 8) + 1) * 8);
                        break;
                    case '\a':
                        break;
                    default:
                        if (!char.IsControl(c))
                        {
                            if (char.IsHighSurrogate(c) && i + 1 < text.Length)
                            {
                                Put(text.Substring(i, 2));
                                i++;
                            }
                            else
                            {
                                Put(c.ToString());
                            }
                        }

                        break;
                }
            }
        }
    }

    private static char[] Blank(int columns)
    {
        var row = new char[columns];
        Array.Fill(row, ' ');
        return row;
    }

    private static string Read(char[] row) => new string(row).Replace("\0", string.Empty, StringComparison.Ordinal).TrimEnd();

    private void Put(string element)
    {
        var width = Math.Max(1, PrettyPrompt.Rendering.UnicodeWidth.GetWidth(element));
        if (_pendingWrap || _column + width > _columns)
        {
            _column = 0;
            _pendingWrap = false;
            LineFeed();
        }

        var row = _rows[_row];
        row[_column] = element[0];
        for (var i = 1; i < width && _column + i < _columns; i++)
        {
            row[_column + i] = '\0';
        }

        _column += width;
        if (_column >= _columns)
        {
            _column = _columns - 1;
            _pendingWrap = true;
        }
    }

    private void LineFeed()
    {
        _pendingWrap = false;
        if (_row < _rows.Length - 1)
        {
            _row++;
            return;
        }

        _scrolledOff.Add(Read(_rows[0]));
        Array.Copy(_rows, 1, _rows, 0, _rows.Length - 1);
        _rows[^1] = Blank(_columns);
    }

    /// <summary>Returns the index of the last character of the sequence, or -1 when it is not complete yet.</summary>
    private int Escape(string text, int start)
    {
        if (start + 1 >= text.Length)
        {
            return -1;
        }

        switch (text[start + 1])
        {
            case '[':
            {
                var end = start + 2;
                while (end < text.Length && !(text[end] >= '@' && text[end] <= '~'))
                {
                    end++;
                }

                if (end >= text.Length)
                {
                    return -1;
                }

                Control(text[(start + 2)..end], text[end]);
                return end;
            }

            case ']':
            {
                // Ends with BEL or with ESC \.
                for (var end = start + 2; end < text.Length; end++)
                {
                    if (text[end] == '\a')
                    {
                        Command(text[(start + 2)..end]);
                        return end;
                    }

                    if (text[end] == '\u001b' && end + 1 < text.Length && text[end + 1] == '\\')
                    {
                        Command(text[(start + 2)..end]);
                        return end + 1;
                    }
                }

                return -1;
            }

            case '7':
                (_savedRow, _savedColumn) = (_row, _column);
                return start + 1;
            case '8':
                (_row, _column) = (Math.Min(_savedRow, _rows.Length - 1), Math.Min(_savedColumn, _columns - 1));
                return start + 1;
            case '(' or ')':
                return start + 2 < text.Length ? start + 2 : -1;
            default:
                return start + 1;
        }
    }

    private void Command(string body)
    {
        if (body.StartsWith("0;", StringComparison.Ordinal) || body.StartsWith("2;", StringComparison.Ordinal))
        {
            Title = body[2..];
        }
    }

    private void Control(string parameters, char command)
    {
        if (parameters.StartsWith('?') || parameters.StartsWith('>') || parameters.StartsWith('='))
        {
            // Modes of the terminal, such as whether the cursor is shown.
            return;
        }

        var numbers = parameters.Split(';').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        int Number(int index, int fallback) => index < numbers.Length && numbers[index] > 0 ? numbers[index] : fallback;

        _pendingWrap = false;
        switch (command)
        {
            case 'H' or 'f':
                _row = Math.Clamp(Number(0, 1) - 1, 0, _rows.Length - 1);
                _column = Math.Clamp(Number(1, 1) - 1, 0, _columns - 1);
                break;
            case 'A':
                _row = Math.Max(0, _row - Number(0, 1));
                break;
            case 'B':
                _row = Math.Min(_rows.Length - 1, _row + Number(0, 1));
                break;
            case 'C':
                _column = Math.Min(_columns - 1, _column + Number(0, 1));
                break;
            case 'D':
                _column = Math.Max(0, _column - Number(0, 1));
                break;
            case 'G':
                _column = Math.Clamp(Number(0, 1) - 1, 0, _columns - 1);
                break;
            case 'd':
                _row = Math.Clamp(Number(0, 1) - 1, 0, _rows.Length - 1);
                break;
            case 'E':
                _row = Math.Min(_rows.Length - 1, _row + Number(0, 1));
                _column = 0;
                break;
            case 'K':
                switch (numbers.Length > 0 ? numbers[0] : 0)
                {
                    case 0:
                        Array.Fill(_rows[_row], ' ', _column, _columns - _column);
                        break;
                    case 1:
                        Array.Fill(_rows[_row], ' ', 0, _column + 1);
                        break;
                    default:
                        Array.Fill(_rows[_row], ' ');
                        break;
                }

                break;
            case 'J':
                switch (numbers.Length > 0 ? numbers[0] : 0)
                {
                    case 0:
                        Array.Fill(_rows[_row], ' ', _column, _columns - _column);
                        for (var r = _row + 1; r < _rows.Length; r++)
                        {
                            Array.Fill(_rows[r], ' ');
                        }

                        break;
                    case 1:
                        Array.Fill(_rows[_row], ' ', 0, _column + 1);
                        for (var r = 0; r < _row; r++)
                        {
                            Array.Fill(_rows[r], ' ');
                        }

                        break;
                    case 2:
                        foreach (var row in _rows)
                        {
                            Array.Fill(row, ' ');
                        }

                        break;
                    default:
                        _scrolledOff.Clear();
                        break;
                }

                break;
            case 'X':
                Array.Fill(_rows[_row], ' ', _column, Math.Min(Number(0, 1), _columns - _column));
                break;
            case 'P':
            {
                var count = Math.Min(Number(0, 1), _columns - _column);
                Array.Copy(_rows[_row], _column + count, _rows[_row], _column, _columns - _column - count);
                Array.Fill(_rows[_row], ' ', _columns - count, count);
                break;
            }

            case '@':
            {
                var count = Math.Min(Number(0, 1), _columns - _column);
                Array.Copy(_rows[_row], _column, _rows[_row], _column + count, _columns - _column - count);
                Array.Fill(_rows[_row], ' ', _column, count);
                break;
            }

            case 'L':
                for (var n = 0; n < Number(0, 1); n++)
                {
                    Array.Copy(_rows, _row, _rows, _row + 1, _rows.Length - _row - 1);
                    _rows[_row] = Blank(_columns);
                }

                break;
            case 'M':
                for (var n = 0; n < Number(0, 1); n++)
                {
                    Array.Copy(_rows, _row + 1, _rows, _row, _rows.Length - _row - 1);
                    _rows[^1] = Blank(_columns);
                }

                break;
            case 'S':
                for (var n = 0; n < Number(0, 1); n++)
                {
                    _scrolledOff.Add(Read(_rows[0]));
                    Array.Copy(_rows, 1, _rows, 0, _rows.Length - 1);
                    _rows[^1] = Blank(_columns);
                }

                break;
        }
    }
}
