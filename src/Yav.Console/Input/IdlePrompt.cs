using PrettyPrompt;
using PrettyPrompt.Completion;
using PrettyPrompt.Consoles;
using PrettyPrompt.Documents;
using PrettyPrompt.Highlighting;
using Yav.Console.Rendering;

namespace Yav.Console.Input;

/// <summary>The editor that is used while no run is active.</summary>
public interface IIdleEditor : IAsyncDisposable
{
    Task<InputResult> ReadAsync(Line prompt);
}

/// <summary>
/// The prompt that is used while no run is active: PrettyPrompt, with its editing, history, completion and
/// input over several lines. While it has the cursor the screen holds other output back, so the two never
/// write into each other.
/// </summary>
public sealed class IdlePrompt : IIdleEditor
{
    private readonly Screen _screen;
    private readonly PromptConfiguration _configuration;
    private readonly Prompt _prompt;

    public IdlePrompt(Screen screen, IKeySource keys, string historyFile, Func<string?> project)
    {
        _screen = screen;
        var color = screen.Options.Color;
        _configuration = new PromptConfiguration(
            keyBindings: new KeyBindings(
                // Enter always sends what was typed. Only Tab takes a completion, so that a command that was
                // typed in full is not held back by the list that offers it.
                commitCompletion: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.Tab)),
                submitPrompt: new KeyPressPatterns(new KeyPressPattern(ConsoleKey.Enter)),

                // Control+J reaches a program as that key or, from a terminal that sends a plain line feed,
                // as Control+Enter.
                newLine: new KeyPressPatterns(
                    new KeyPressPattern(ConsoleModifiers.Shift, ConsoleKey.Enter),
                    new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.J),
                    new KeyPressPattern(ConsoleModifiers.Control, ConsoleKey.Enter))),
            prompt: new FormattedString("YAV> "),

            // Named colors of the terminal's own palette, or none at all when the user asked for none.
            completionBoxBorderFormat: color ? new ConsoleFormat(Foreground: AnsiColor.BrightBlack) : ConsoleFormat.None,
            completionItemDescriptionPaneBackground: null,
            selectedCompletionItemMarkSymbol: color
                ? new FormattedString(">", new FormatSpan(0, 1, AnsiColor.Cyan))
                : new FormattedString(">"),
            selectedCompletionItemBackground: null,
            selectedTextBackground: null);
        _prompt = new Prompt(
            persistentHistoryFilepath: historyFile,
            callbacks: new Callbacks(project),
            console: new PromptConsole(keys),
            configuration: _configuration);
    }

    public async Task<InputResult> ReadAsync(Line prompt)
    {
        _configuration.Prompt = Format(prompt, _screen.Options.Color);
        using (_screen.Suspend())
        {
            try
            {
                // PrettyPrompt waits for keys on the thread it is called on.
                var result = await Task.Run(_prompt.ReadLineAsync).ConfigureAwait(false);
                return result.IsSuccess
                    ? new InputResult(InputOutcome.Submitted, result.Text.ReplaceLineEndings("\n"))
                    : new InputResult(InputOutcome.Interrupted, string.Empty);
            }
            catch (EndOfInputException)
            {
                return new InputResult(InputOutcome.EndOfInput, string.Empty);
            }
        }
    }

    public ValueTask DisposeAsync() => _prompt.DisposeAsync();

    private static FormattedString Format(Line line, bool color)
    {
        var builder = new FormattedStringBuilder();
        foreach (var segment in line.Segments)
        {
            builder.Append(segment.Text, color ? new ConsoleFormat(Bold: segment.Bold || segment.Tone == Tone.Prompt) : ConsoleFormat.None);
        }

        return builder.ToFormattedString();
    }

    private sealed class EndOfInputException : Exception;

    /// <summary>PrettyPrompt's own console, with the keys taken from the one reader of the keyboard.</summary>
    private sealed class PromptConsole(IKeySource keys) : SystemConsole, IConsole
    {
        bool IConsole.KeyAvailable => keys.KeyAvailable;

        /// <summary>
        /// Control+C is a key in YAV at all times: it empties the line, stops a run, or leaves. The editor
        /// would otherwise turn it into a signal as soon as a line was sent, and a run could not be stopped.
        /// </summary>
        bool IConsole.CaptureControlC
        {
            get => true;
            set
            {
            }
        }

        public override ConsoleKeyInfo ReadKey(bool intercept)
        {
            var stroke = keys.ReadAsync(CancellationToken.None).AsTask().GetAwaiter().GetResult();
            return stroke?.Key ?? throw new EndOfInputException();
        }
    }

    private sealed class Callbacks(Func<string?> project) : PromptCallbacks
    {
        protected override Task<TextSpan> GetSpanToReplaceByCompletionAsync(string text, int caret, CancellationToken cancellationToken)
        {
            var completion = Complete(text, caret);

            // The editor insists on a part of the text that contains the caret.
            return Task.FromResult(completion.Start <= caret && caret <= completion.Start + completion.Length
                ? new TextSpan(completion.Start, completion.Length)
                : new TextSpan(caret, 0));
        }

        protected override Task<IReadOnlyList<CompletionItem>> GetCompletionItemsAsync(
            string text,
            int caret,
            TextSpan spanToBeReplaced,
            CancellationToken cancellationToken)
        {
            IReadOnlyList<CompletionItem> items = Complete(text, caret).Candidates
                .Select(candidate => new CompletionItem(
                    replacementText: candidate.Text,
                    getExtendedDescription: candidate.Description.Length == 0
                        ? null
                        : _ => Task.FromResult(new FormattedString(candidate.Description))))
                .ToList();
            return Task.FromResult(items);
        }

        protected override Task<bool> ShouldOpenCompletionWindowAsync(string text, int caret, KeyPress keyPress, CancellationToken cancellationToken)
        {
            // Offered for commands only. A request is never completed.
            return Task.FromResult(text.StartsWith('/') && !text.Contains('\n') && Complete(text, caret).Candidates.Count > 0);
        }

        /// <summary>Completion is a convenience: whatever goes wrong in it, there is simply nothing to offer.</summary>
        private CompletionResult Complete(string text, int caret)
        {
            try
            {
                return InputCompletion.For(text, caret, project());
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return new CompletionResult(Math.Clamp(caret, 0, text.Length), 0, []);
            }
        }
    }
}
