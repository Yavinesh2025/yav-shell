using System.Globalization;
using System.Text;

namespace Yav.Core.Text;

/// <summary>
/// Removes terminal control sequences from untrusted text (model output, tool output, file names)
/// so that it cannot move the cursor, rewrite earlier lines, change the window title, write to the
/// clipboard, or imitate a YAV prompt. The sanitizer keeps state between calls because an escape
/// sequence may be split across streamed chunks.
/// Cleaning removes a sequence together with what it contains. That is right for what an agent writes,
/// and wrong for what it asks to do: <see cref="Visible(string?)"/> shows that instead, with nothing left out.
/// </summary>
public sealed class TerminalSanitizer
{
    // A control string that never terminates must not swallow the rest of the output.
    private const int MaxControlStringLength = 4096;

    // The first half of every tag character (U+E0000 to U+E007F).
    private const char TagHighSurrogate = '\uDB40';

    private enum State
    {
        Ground,
        Escape,
        EscapeIntermediate,
        Csi,
        ControlString,
        ControlStringEscape,
    }

    private State _state = State.Ground;
    private int _controlStringLength;
    private bool _pendingCarriageReturn;

    // The first half of a character whose second half has not arrived yet, so that a tag character is dropped whole.
    private char? _pendingHighSurrogate;

    /// <summary>Sanitizes a complete piece of text in one call.</summary>
    public static string Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        if (!NeedsWork(text))
        {
            return text;
        }

        var sanitizer = new TerminalSanitizer();
        var result = sanitizer.Append(text);
        return result + sanitizer.Flush();
    }

    /// <summary>
    /// Sanitizes text that is shown as one line and keeps its spaces, because indentation and alignment
    /// carry meaning. A line break becomes a space and a tab becomes four.
    /// </summary>
    public static string CleanLine(string? text)
    {
        var cleaned = Clean(text);
        if (cleaned.AsSpan().IndexOfAny('\n', '\t') < 0)
        {
            return cleaned;
        }

        return cleaned.Replace('\n', ' ').Replace("\t", "    ", StringComparison.Ordinal);
    }

    /// <summary>Sanitizes text for display on a single line: line breaks and tabs become spaces.</summary>
    public static string CleanSingleLine(string? text, int maxLength = int.MaxValue)
    {
        var cleaned = Clean(text);
        if (cleaned.Length == 0)
        {
            return cleaned;
        }

        var builder = new StringBuilder(Math.Min(cleaned.Length, 256));
        var lastWasSpace = false;
        foreach (var c in cleaned)
        {
            var isSpace = c is '\n' or '\t' or ' ';
            if (isSpace)
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                }

                lastWasSpace = true;
                continue;
            }

            builder.Append(c);
            lastWasSpace = false;
        }

        var result = builder.ToString().TrimEnd();
        if (result.Length > maxLength)
        {
            result = maxLength <= 3 ? result[..maxLength] : string.Concat(result.AsSpan(0, maxLength - 3), "...");
        }

        return result;
    }

    /// <summary>
    /// Shows text as it is, for what an agent wants to do or did. Every character a terminal prints is kept, and
    /// every other one is written out as a token that it prints (<c>\x1B</c>, <c>&lt;U+202E&gt;</c>), so that
    /// nothing is swallowed and nothing is hidden. Line feeds and tabs are kept; a carriage return that ends a
    /// line together with a line feed becomes that line feed.
    /// </summary>
    public static string Visible(string? text) => Visible(text, out _);

    /// <param name="writtenOut">True when at least one character had to be written out as a token.</param>
    public static string Visible(string? text, out bool writtenOut)
    {
        writtenOut = false;
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length + 16);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                // The end of a line as Windows writes it. The line feed that follows ends the line.
                continue;
            }

            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                var code = char.ConvertToUtf32(c, text[i + 1]);
                i++;
                if (IsTag(code))
                {
                    AppendToken(builder, code);
                    writtenOut = true;
                }
                else
                {
                    builder.Append(c).Append(text[i]);
                }

                continue;
            }

            if (char.IsSurrogate(c) || IsWrittenOut(c))
            {
                AppendToken(builder, c);
                writtenOut = true;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Shows text as it is on one line: line breaks and tabs become spaces, every other space stays, and every
    /// character a terminal does not print is written out. Text that is cut ends with "...".
    /// </summary>
    public static string VisibleSingleLine(string? text, int maxLength = int.MaxValue)
    {
        var shown = Visible(text);
        if (shown.AsSpan().IndexOfAny('\n', '\t') >= 0)
        {
            shown = shown.Replace('\n', ' ').Replace('\t', ' ');
        }

        shown = shown.TrimEnd();
        if (shown.Length <= maxLength)
        {
            return shown;
        }

        if (maxLength <= 3)
        {
            return shown[..maxLength];
        }

        // A character of two halves is not cut in two.
        var keep = maxLength - 3;
        if (char.IsHighSurrogate(shown[keep - 1]))
        {
            keep--;
        }

        return string.Concat(shown.AsSpan(0, keep), "...");
    }

    /// <summary>
    /// True for a character of the Basic Multilingual Plane that a terminal does not print as it is and that
    /// <see cref="Visible(string?)"/> therefore writes out: controls other than line feed and tab, the characters
    /// that reorder text, and the ones that take no room. Surrogates and tag characters are handled as pairs.
    /// </summary>
    public static bool IsWrittenOut(char c) =>
        (c < ' ' && c is not '\n' and not '\t') || c is >= '\u007f' and <= '\u009f' || IsDirectionControl(c) || IsInvisible(c);

    /// <summary>Sanitizes the next chunk of a stream.</summary>
    public string Append(ReadOnlySpan<char> chunk)
    {
        if (chunk.IsEmpty)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(chunk.Length);
        foreach (var c in chunk)
        {
            Step(c, builder);
        }

        return builder.ToString();
    }

    /// <summary>Ends the stream. An unfinished escape sequence is dropped.</summary>
    public string Flush()
    {
        _state = State.Ground;
        _controlStringLength = 0;
        if (_pendingHighSurrogate is { } high)
        {
            // Its second half never came, so it is no tag character.
            _pendingHighSurrogate = null;
            return high.ToString();
        }

        if (_pendingCarriageReturn)
        {
            _pendingCarriageReturn = false;
            return "\n";
        }

        return string.Empty;
    }

    private void Step(char c, StringBuilder output)
    {
        switch (_state)
        {
            case State.Ground:
                Ground(c, output);
                break;

            case State.Escape when c < ' ' && c != '\u001b':
            case State.EscapeIntermediate when c < ' ' && c != '\u001b':
                // A terminal carries out a control that interrupts an escape. Swallowed, a line feed would join two lines.
                _state = State.Ground;
                Ground(c, output);
                break;

            case State.Escape:
                _state = c switch
                {
                    '[' => State.Csi,
                    ']' or 'P' or '_' or '^' or 'X' => StartControlString(),
                    // Intermediate bytes introduce a longer escape such as a character-set designation.
                    >= ' ' and <= '/' => State.EscapeIntermediate,
                    '\u001b' => State.Escape,
                    // Any other byte completes a two-character escape.
                    _ => State.Ground,
                };
                break;

            case State.EscapeIntermediate:
                if (c is >= ' ' and <= '/')
                {
                    break;
                }

                _state = c == '\u001b' ? State.Escape : State.Ground;
                break;

            case State.Csi:
                if (c is >= '@' and <= '~')
                {
                    _state = State.Ground;
                }
                else if (c == '\u001b')
                {
                    _state = State.Escape;
                }
                else if (c is < ' ' or > '?')
                {
                    // Not a valid parameter or intermediate byte: the sequence is malformed. Reprocess as text.
                    _state = State.Ground;
                    Ground(c, output);
                }

                break;

            case State.ControlString:
                if (c == '\u0007' || c == '\u009c')
                {
                    _state = State.Ground;
                }
                else if (c == '\u001b')
                {
                    _state = State.ControlStringEscape;
                }
                else if (++_controlStringLength > MaxControlStringLength)
                {
                    _state = State.Ground;
                }

                break;

            case State.ControlStringEscape:
                // ESC \ is the string terminator. Anything else starts a new escape sequence.
                if (c == '\\')
                {
                    _state = State.Ground;
                }
                else
                {
                    _state = State.Escape;
                    Step(c, output);
                }

                break;
        }
    }

    private State StartControlString()
    {
        _controlStringLength = 0;
        return State.ControlString;
    }

    private void Ground(char c, StringBuilder output)
    {
        if (_pendingCarriageReturn)
        {
            _pendingCarriageReturn = false;
            output.Append('\n');
            if (c == '\n')
            {
                return;
            }
        }

        if (_pendingHighSurrogate is { } high)
        {
            _pendingHighSurrogate = null;
            if (c is >= '\uDC00' and <= '\uDC7F')
            {
                // A tag character: it takes no room, and a sequence of them can carry text nobody sees.
                return;
            }

            output.Append(high);
        }

        if (c == TagHighSurrogate)
        {
            _pendingHighSurrogate = c;
            return;
        }

        switch (c)
        {
            case '\u001b':
                _state = State.Escape;
                return;
            case '\n':
            case '\t':
                output.Append(c);
                return;
            case '\r':
                // A bare carriage return would let later text overwrite the start of the line.
                _pendingCarriageReturn = true;
                return;
            case '\u009b':
                _state = State.Csi;
                return;
            case '\u0090' or '\u009d' or '\u009e' or '\u009f' or '\u0098':
                _state = StartControlString();
                return;
        }

        if (c < ' ' || c == '\u007f' || (c >= '\u0080' && c <= '\u009f'))
        {
            return;
        }

        if (IsDirectionControl(c) || IsInvisible(c))
        {
            return;
        }

        output.Append(c);
    }

    /// <summary>Characters that reorder displayed text and can make code or paths read differently than they are.</summary>
    private static bool IsDirectionControl(char c) =>
        c is (>= (char)0x202A and <= (char)0x202E) or (>= (char)0x2066 and <= (char)0x2069) or (char)0x200E or (char)0x200F or (char)0x061C;

    /// <summary>
    /// Characters that take no room, so that text can hide in them or between them: zero-width characters,
    /// the soft hyphen, the byte order mark, separators of lines and paragraphs, invisible operators and the
    /// marks of interlinear annotations.
    /// </summary>
    private static bool IsInvisible(char c) =>
        c is (>= (char)0x200B and <= (char)0x200D) or (char)0x2060 or (char)0xFEFF or (char)0x00AD or (char)0x180E or (char)0x2028 or (char)0x2029
            or (>= (char)0x2061 and <= (char)0x2064) or (>= (char)0x206A and <= (char)0x206F) or (>= (char)0xFFF9 and <= (char)0xFFFB);

    /// <summary>Tag characters (U+E0000 to U+E007F): invisible, and able to spell out text nobody sees.</summary>
    private static bool IsTag(int code) => code is >= 0xE0000 and <= 0xE007F;

    /// <summary>A control of the ASCII range becomes <c>\x1B</c>; every other character <c>&lt;U+202E&gt;</c>.</summary>
    private static void AppendToken(StringBuilder builder, int code)
    {
        if (code is < 0x20 or 0x7F)
        {
            builder.Append("\\x").Append(code.ToString("X2", CultureInfo.InvariantCulture));
            return;
        }

        builder.Append("<U+").Append(code.ToString(code > 0xFFFF ? "X5" : "X4", CultureInfo.InvariantCulture)).Append('>');
    }

    private static bool NeedsWork(string text)
    {
        foreach (var c in text)
        {
            if (c == '\n' || c == '\t')
            {
                continue;
            }

            if (c < ' ' || c == '\u007f' || (c >= '\u0080' && c <= '\u009f') || IsDirectionControl(c) || IsInvisible(c) || c == TagHighSurrogate)
            {
                return true;
            }
        }

        return false;
    }
}
