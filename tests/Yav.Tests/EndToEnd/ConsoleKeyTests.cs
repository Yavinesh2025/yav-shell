using Yav.Tests.Support;

namespace Yav.Tests.EndToEnd;

/// <summary>
/// What a program receives when a terminal sends a key through the pseudo console of Windows. The keys
/// YAV documents rest on these facts, so they are tested and not assumed.
/// </summary>
[Collection(nameof(InteractiveConsoleTests))]
public class ConsoleKeyTests
{
    /// <summary>The key event as a terminal with win32-input-mode sends it: key, scan code, character, down, modifiers, repeat.</summary>
    public static string Win32Key(int virtualKey, int scanCode, int character, int modifiers) =>
        $"\u001b[{virtualKey};{scanCode};{character};1;{modifiers};1_\u001b[{virtualKey};{scanCode};{character};0;{modifiers};1_";

    public const int Shift = 0x10;
    public const int LeftControl = 0x08;

    private static async Task<string> DeliveredAsync(string sent)
    {
        using var directory = new TempDirectory("keys");
        using var console = PseudoConsole.Start(Fixtures.FakeAgent, ["tool", "keys"], directory.Path, new Dictionary<string, string?>(), 100, 30);
        await console.WaitForAsync("ready");
        await console.PressAsync(sent);
        await console.WaitForAsync("key=");
        await console.PressAsync("q");
        await console.WaitForExitAsync();
        return console.Screen.Lines.First(line => line.StartsWith("key=", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Enter_arrives_as_enter()
    {
        Assert.Equal("key=Enter char=U+000D modifiers=0", await DeliveredAsync("\r"));
    }

    [Fact]
    public async Task Shift_enter_arrives_with_the_shift_key()
    {
        Assert.Equal("key=Enter char=U+000D modifiers=2", await DeliveredAsync(Win32Key(13, 28, 13, Shift)));
    }

    [Fact]
    public async Task Control_j_arrives_as_a_line_feed_with_the_control_key()
    {
        var delivered = await DeliveredAsync(Win32Key(0x4A, 0x24, 0x0A, LeftControl));

        Assert.Equal("key=J char=U+000A modifiers=4", delivered);
    }

    [Fact]
    public async Task A_line_feed_that_a_terminal_sends_as_a_character_is_known_for_what_it_is()
    {
        var delivered = await DeliveredAsync("\n");

        Assert.Contains("char=U+000A", delivered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Control_c_arrives_as_a_key_when_the_program_asked_for_that()
    {
        Assert.Equal("key=C char=U+0003 modifiers=4", await DeliveredAsync("\u0003"));
    }
}
