namespace Yav.Console.Input;

/// <param name="Pasted">True when the key arrived as part of text that was pasted, not typed.</param>
public readonly record struct KeyStroke(ConsoleKeyInfo Key, bool Pasted);

/// <summary>The one reader of the keyboard. Everything that needs keys gets them from here.</summary>
public interface IKeySource
{
    bool KeyAvailable { get; }

    /// <summary>The next key, or null when there is no more input.</summary>
    ValueTask<KeyStroke?> ReadAsync(CancellationToken cancellationToken);

    /// <summary>Stops reading while another program owns the console, until the scope ends.</summary>
    IDisposable Pause();
}
