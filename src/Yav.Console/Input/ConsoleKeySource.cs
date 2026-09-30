using System.Diagnostics;

namespace Yav.Console.Input;

/// <summary>
/// Reads the keyboard of the console. It looks whether a key is there instead of waiting inside the
/// console, so that it can step aside at once when another program is given the console.
/// </summary>
public sealed class ConsoleKeySource : IKeySource
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(8);

    private readonly PasteDetector _paste = new();
    private int _paused;

    public bool KeyAvailable => Volatile.Read(ref _paused) == 0 && Available();

    public async ValueTask<KeyStroke?> ReadAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Volatile.Read(ref _paused) == 0 && Available())
            {
                ConsoleKeyInfo key;
                try
                {
                    key = System.Console.ReadKey(intercept: true);
                }
                catch (InvalidOperationException)
                {
                    // The input is no longer a console.
                    return null;
                }

                var timestamp = Stopwatch.GetElapsedTime(0).Ticks;
                return new KeyStroke(key, _paste.IsPasted(timestamp, Available()));
            }

            await Task.Delay(Interval, cancellationToken).ConfigureAwait(false);
        }
    }

    public IDisposable Pause()
    {
        Interlocked.Increment(ref _paused);
        return new Resume(this);
    }

    private static bool Available()
    {
        try
        {
            return System.Console.KeyAvailable;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private sealed class Resume(ConsoleKeySource owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._paused);
            }
        }
    }
}
