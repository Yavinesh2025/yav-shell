namespace Yav.Console.Input;

/// <summary>
/// Input that arrives as lines: a pipe, a file, or a terminal that cannot position the cursor. Each line is one
/// input. It is the one reader of the input, so that two readers never take turns at lines by chance: a question
/// that waits gets the next line before anything else, and a line that was typed before the question was asked
/// stays with the prompt it was typed at.
/// </summary>
public sealed class LineSource(TextReader reader)
{
    private readonly Lock _gate = new();
    private readonly List<Waiter> _waiting = [];

    // Lines that came while nothing waited. They were typed for the prompt, never for a question.
    private readonly Queue<string> _unclaimed = new();
    private bool _reading;
    private bool _ended;

    /// <summary>The next line, or null at the end of the input.</summary>
    /// <param name="question">
    /// True for the answer to a question: it is served before anything else that waits, and never with a line that
    /// was typed before it was asked.
    /// </param>
    public async Task<string?> ReadLineAsync(bool question, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var waiter = new Waiter(question);
        var start = false;
        lock (_gate)
        {
            if (!question && _unclaimed.Count > 0)
            {
                return _unclaimed.Dequeue();
            }

            if (_ended)
            {
                return null;
            }

            _waiting.Add(waiter);
            if (!_reading)
            {
                _reading = true;
                start = true;
            }
        }

        if (start)
        {
            // A console's reader blocks the thread it reads on, so the one reader has a thread of its own.
            _ = Task.Run(ReadLinesAsync, CancellationToken.None);
        }

        using (cancellationToken.Register(() => Withdraw(waiter, cancellationToken)))
        {
            return await waiter.Line.Task.ConfigureAwait(false);
        }
    }

    private void Withdraw(Waiter waiter, CancellationToken cancellationToken)
    {
        bool withdrawn;
        lock (_gate)
        {
            withdrawn = _waiting.Remove(waiter);
        }

        // A waiter that was served a line in the meantime keeps it: the line was read for it.
        if (withdrawn)
        {
            waiter.Line.TrySetCanceled(cancellationToken);
        }
    }

    private async Task ReadLinesAsync()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_waiting.Count == 0)
                {
                    _reading = false;
                    return;
                }
            }

            string? line;
            try
            {
                line = await reader.ReadLineAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                line = null;
            }

            Waiter? served = null;
            List<Waiter> ended = [];
            lock (_gate)
            {
                if (line is null)
                {
                    _ended = true;
                    _reading = false;
                    ended.AddRange(_waiting);
                    _waiting.Clear();
                }
                else
                {
                    served = _waiting.Find(w => w.Question) ?? _waiting.FirstOrDefault();
                    if (served is null)
                    {
                        _unclaimed.Enqueue(line);
                    }
                    else
                    {
                        _waiting.Remove(served);
                    }
                }
            }

            foreach (var waiter in ended)
            {
                waiter.Line.TrySetResult(null);
            }

            if (line is null)
            {
                return;
            }

            served?.Line.TrySetResult(line);
        }
    }

    private sealed class Waiter(bool question)
    {
        public bool Question => question;

        public TaskCompletionSource<string?> Line { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
