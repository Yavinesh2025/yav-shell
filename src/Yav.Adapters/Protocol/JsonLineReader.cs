namespace Yav.Adapters.Protocol;

public sealed class FrameTooLargeException(int limit)
    : IOException($"The agent sent a single message larger than {limit / 1024} KB. The connection was closed rather than buffering without bound.")
{
    public int Limit { get; } = limit;
}

/// <summary>
/// Reads newline-delimited JSON from a byte stream. Frames are cut at line feeds on the byte level, so a
/// character or a message that spans several reads is never decoded in pieces.
/// </summary>
public sealed class JsonLineReader
{
    public const int DefaultMaxFrameBytes = 64 * 1024 * 1024;

    private readonly Stream _stream;
    private readonly int _maxFrameBytes;
    private byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;
    private bool _ended;
    private bool _atStreamStart = true;

    public JsonLineReader(Stream stream, int maxFrameBytes = DefaultMaxFrameBytes)
    {
        _stream = stream;
        _maxFrameBytes = maxFrameBytes;
    }

    /// <summary>The next complete frame, or null when the stream has ended.</summary>
    public async Task<ReadOnlyMemory<byte>?> ReadFrameAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            var available = _buffer.AsSpan(_start, _end - _start);
            var newline = available.IndexOf((byte)'\n');
            if (newline >= 0)
            {
                var frame = Take(newline);
                _start += newline + 1;
                if (frame.Length > 0)
                {
                    return frame;
                }

                continue;
            }

            if (_ended)
            {
                if (_end > _start)
                {
                    var last = Take(_end - _start);
                    _start = _end;
                    if (last.Length > 0)
                    {
                        return last;
                    }
                }

                return null;
            }

            if (_end - _start > _maxFrameBytes)
            {
                throw new FrameTooLargeException(_maxFrameBytes);
            }

            MakeRoom();
            var read = await _stream.ReadAsync(_buffer.AsMemory(_end), cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                _ended = true;
            }
            else
            {
                _end += read;
            }
        }
    }

    private byte[] Take(int length)
    {
        var span = _buffer.AsSpan(_start, length);
        if (_atStreamStart)
        {
            _atStreamStart = false;
            if (span.Length >= 3 && span[0] == 0xEF && span[1] == 0xBB && span[2] == 0xBF)
            {
                span = span[3..];
            }
        }

        if (!span.IsEmpty && span[^1] == (byte)'\r')
        {
            span = span[..^1];
        }

        return span.ToArray();
    }

    private void MakeRoom()
    {
        if (_start > 0)
        {
            Buffer.BlockCopy(_buffer, _start, _buffer, 0, _end - _start);
            _end -= _start;
            _start = 0;
        }

        if (_end == _buffer.Length)
        {
            var size = Math.Min((long)_buffer.Length * 2, (long)_maxFrameBytes + 4096);
            if (size <= _buffer.Length)
            {
                throw new FrameTooLargeException(_maxFrameBytes);
            }

            Array.Resize(ref _buffer, (int)size);
        }
    }
}
