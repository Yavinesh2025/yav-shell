using System.Text;
using Yav.Console.Rendering;

namespace Yav.Console.Output;

/// <summary>The window of the console.</summary>
public sealed class SystemTerminal : ITerminal
{
    private readonly TextWriter _writer;

    public SystemTerminal()
    {
        // One writer for the whole session, so that nothing is buffered elsewhere and written out of order.
        _writer = new StreamWriter(System.Console.OpenStandardOutput(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), 16 * 1024)
        {
            AutoFlush = false,
        };
    }

    public int Width
    {
        get
        {
            try
            {
                return System.Console.WindowWidth;
            }
            catch (IOException)
            {
                return 120;
            }
        }
    }

    public int Height
    {
        get
        {
            try
            {
                return System.Console.WindowHeight;
            }
            catch (IOException)
            {
                return 30;
            }
        }
    }

    public void Write(string text) => _writer.Write(text);

    public void Flush() => _writer.Flush();
}

/// <summary>A stream that is not a window: a pipe or a file. Lines are as long as they are.</summary>
public sealed class StreamTerminal(TextWriter writer) : ITerminal
{
    public int Width => int.MaxValue;

    public int Height => 1000;

    public void Write(string text) => writer.Write(text);

    public void Flush() => writer.Flush();
}
