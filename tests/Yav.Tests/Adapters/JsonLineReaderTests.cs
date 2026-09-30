using System.Text;
using Yav.Adapters.Protocol;

namespace Yav.Tests.Adapters;

public class JsonLineReaderTests
{
    /// <summary>Delivers its content in pieces of a fixed size, as a pipe may.</summary>
    private sealed class PiecewiseStream(byte[] content, int pieceSize) : Stream
    {
        private int _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => content.Length;

        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var take = Math.Min(Math.Min(count, pieceSize), content.Length - _position);
            Array.Copy(content, _position, buffer, offset, take);
            _position += take;
            return take;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var take = Math.Min(Math.Min(buffer.Length, pieceSize), content.Length - _position);
            content.AsSpan(_position, take).CopyTo(buffer.Span);
            _position += take;
            return ValueTask.FromResult(take);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<List<string>> ReadAllAsync(string text, int pieceSize, int maxFrame = 1024 * 1024)
    {
        var reader = new JsonLineReader(new PiecewiseStream(Encoding.UTF8.GetBytes(text), pieceSize), maxFrame);
        var frames = new List<string>();
        while (await reader.ReadFrameAsync(CancellationToken.None) is { } frame)
        {
            frames.Add(Encoding.UTF8.GetString(frame.Span));
        }

        return frames;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task Frames_arrive_whole_however_the_bytes_were_split(int pieceSize)
    {
        var text = "{\"a\":1}\n{\"text\":\"ünï 日本 🙂\"}\n{\"c\":[1,2,3]}\n";

        var frames = await ReadAllAsync(text, pieceSize);

        Assert.Equal(["{\"a\":1}", "{\"text\":\"ünï 日本 🙂\"}", "{\"c\":[1,2,3]}"], frames);
    }

    [Fact]
    public async Task Windows_line_endings_and_empty_lines_are_tolerated()
    {
        var frames = await ReadAllAsync("{\"a\":1}\r\n\r\n\n{\"b\":2}\r\n", 5);

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], frames);
    }

    [Fact]
    public async Task A_last_frame_without_a_line_break_is_delivered()
    {
        var frames = await ReadAllAsync("{\"a\":1}\n{\"b\":2}", 4);

        Assert.Equal(["{\"a\":1}", "{\"b\":2}"], frames);
    }

    [Fact]
    public async Task A_byte_order_mark_at_the_start_is_not_part_of_the_first_frame()
    {
        var bytes = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"a\":1}\n")).ToArray();
        var reader = new JsonLineReader(new PiecewiseStream(bytes, 2), 1024);

        var frame = await reader.ReadFrameAsync(CancellationToken.None);

        Assert.Equal("{\"a\":1}", Encoding.UTF8.GetString(frame!.Value.Span));
    }

    [Fact]
    public async Task A_frame_larger_than_the_limit_is_an_error_not_unbounded_memory()
    {
        var text = "{\"big\":\"" + new string('x', 5000) + "\"}\n{\"after\":1}\n";

        var error = await Assert.ThrowsAsync<FrameTooLargeException>(() => ReadAllAsync(text, 64, maxFrame: 1024));

        Assert.Equal(1024, error.Limit);
    }

    [Fact]
    public async Task A_large_frame_within_the_limit_is_delivered_whole()
    {
        var payload = new string('y', 300_000);

        var frames = await ReadAllAsync("{\"big\":\"" + payload + "\"}\n", 1000);

        Assert.Equal(payload.Length + 10, Assert.Single(frames).Length);
    }

    [Fact]
    public async Task The_end_of_the_stream_is_reported_as_no_frame()
    {
        var reader = new JsonLineReader(new PiecewiseStream([], 10), 1024);

        Assert.Null(await reader.ReadFrameAsync(CancellationToken.None));
        Assert.Null(await reader.ReadFrameAsync(CancellationToken.None));
    }
}
