using System.Text;
using Yav.Platform.Native;

namespace Yav.Platform.Processes;

/// <summary>
/// Turns a byte stream from a child process into text lines. Bytes are split on line feeds first and
/// decoded afterwards, so a multi-byte character that spans two reads is never broken. Lines that are
/// not valid UTF-8 are decoded with the system's OEM code page, which is what many Windows tools emit
/// when their output is redirected.
/// </summary>
public sealed class OutputDecoder
{
    // A line without a line feed (a progress bar, binary output) is flushed at this size.
    private const int MaxPendingBytes = 256 * 1024;

    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private static readonly Lazy<Encoding> Fallback = new(CreateFallback);

    private readonly Action<string> _onLine;
    private byte[] _pending = new byte[4096];
    private int _pendingLength;
    private bool _atStart = true;

    public OutputDecoder(Action<string> onLine)
    {
        _onLine = onLine;
    }

    public void Append(ReadOnlySpan<byte> bytes)
    {
        while (!bytes.IsEmpty)
        {
            var newline = bytes.IndexOf((byte)'\n');
            if (newline < 0)
            {
                AddPending(bytes);
                if (_pendingLength >= MaxPendingBytes)
                {
                    FlushPartial();
                }

                return;
            }

            AddPending(bytes[..newline]);
            EmitPending();
            bytes = bytes[(newline + 1)..];
        }
    }

    /// <summary>Emits whatever remains after the stream ended.</summary>
    public void Complete()
    {
        if (_pendingLength > 0)
        {
            EmitPending();
        }
    }

    public static string Decode(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return string.Empty;
        }

        try
        {
            return StrictUtf8.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Fallback.Value.GetString(bytes);
        }
    }

    private void AddPending(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            return;
        }

        var required = _pendingLength + bytes.Length;
        if (required > _pending.Length)
        {
            Array.Resize(ref _pending, Math.Max(required, _pending.Length * 2));
        }

        bytes.CopyTo(_pending.AsSpan(_pendingLength));
        _pendingLength = required;
    }

    private void EmitPending()
    {
        var span = _pending.AsSpan(0, _pendingLength);
        if (_atStart && span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
        {
            span = span[3..];
        }

        _atStart = false;
        if (!span.IsEmpty && span[^1] == (byte)'\r')
        {
            span = span[..^1];
        }

        var line = Decode(span);
        _pendingLength = 0;
        _onLine(line);
    }

    private void FlushPartial()
    {
        // Cut at a character boundary so the remainder still decodes correctly.
        var cut = _pendingLength;
        var back = 0;
        while (cut > 0 && back < 4 && (_pending[cut - 1] & 0xC0) == 0x80)
        {
            cut--;
            back++;
        }

        if (cut > 0 && (_pending[cut - 1] & 0xC0) == 0xC0)
        {
            cut--;
        }

        if (cut == 0)
        {
            cut = _pendingLength;
        }

        var tail = _pending.AsSpan(cut, _pendingLength - cut).ToArray();
        _pendingLength = cut;
        EmitPending();
        AddPending(tail);
    }

    private static Encoding CreateFallback()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            var codePage = (int)NativeMethods.GetOEMCP();
            if (codePage is not (0 or 65001))
            {
                return Encoding.GetEncoding(codePage);
            }
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
        }

        return Encoding.Latin1;
    }
}

/// <summary>
/// Keeps the beginning and the end of a long text and counts what was left out in between.
/// The complete text is written to a log file by the caller; this only bounds memory.
/// </summary>
public sealed class BoundedTextBuffer
{
    private readonly int _headLimit;
    private readonly int _tailLimit;
    private readonly StringBuilder _head = new();
    private readonly Queue<string> _tail = new();
    private int _tailLength;
    private long _omittedLines;

    public BoundedTextBuffer(int maxCharacters)
    {
        var limit = Math.Max(1024, maxCharacters);
        _headLimit = limit / 4;
        _tailLimit = limit - _headLimit;
    }

    public long TotalCharacters { get; private set; }

    public bool Truncated => _omittedLines > 0;

    public void AppendLine(string line)
    {
        TotalCharacters += line.Length + 1;
        if (_omittedLines == 0 && _tail.Count == 0 && _head.Length + line.Length + 1 <= _headLimit)
        {
            _head.Append(line).Append('\n');
            return;
        }

        _tail.Enqueue(line);
        _tailLength += line.Length + 1;
        while (_tailLength > _tailLimit && _tail.Count > 1)
        {
            var removed = _tail.Dequeue();
            _tailLength -= removed.Length + 1;
            _omittedLines++;
        }
    }

    public override string ToString()
    {
        var builder = new StringBuilder(_head.Length + _tailLength + 64);
        builder.Append(_head);
        if (_omittedLines > 0)
        {
            builder.Append("[... ").Append(_omittedLines).Append(" line(s) omitted here; the complete output is in the log file ...]\n");
        }

        foreach (var line in _tail)
        {
            builder.Append(line).Append('\n');
        }

        return builder.ToString();
    }

    /// <summary>The last lines, up to the given number of characters.</summary>
    public string Tail(int maxCharacters)
    {
        var text = ToString();
        if (text.Length <= maxCharacters)
        {
            return text;
        }

        var start = text.Length - maxCharacters;
        var lineStart = text.IndexOf('\n', start);
        return lineStart >= 0 && lineStart + 1 < text.Length ? text[(lineStart + 1)..] : text[start..];
    }
}
