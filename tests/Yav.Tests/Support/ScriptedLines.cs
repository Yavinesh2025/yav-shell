using System.Threading.Channels;

namespace Yav.Tests.Support;

/// <summary>
/// Lines that a test types, read as the shell reads a console that cannot position the cursor. Readers that wait
/// at the same time are served one line each, in the order in which they began to wait, as two readers of one
/// console would be.
/// </summary>
public sealed class ScriptedLines : TextReader
{
    private readonly Channel<string> _lines = Channel.CreateUnbounded<string>();

    /// <summary>How many lines wait to be read.</summary>
    public int Waiting => _lines.Reader.Count;

    public ScriptedLines Send(string line)
    {
        _lines.Writer.TryWrite(line);
        return this;
    }

    public void End() => _lines.Writer.TryComplete();

    public override string? ReadLine() => ReadLineAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();

    public override Task<string?> ReadLineAsync() => ReadLineAsync(CancellationToken.None).AsTask();

    public override async ValueTask<string?> ReadLineAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _lines.Reader.ReadAsync(cancellationToken);
        }
        catch (ChannelClosedException)
        {
            return null;
        }
    }
}
