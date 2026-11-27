using Creatura.Worlds.Terminal;

namespace Creatura.Worlds.Terminal.Tests;

/// <summary>
/// T1 (docs/design-terminal-worlds.md): a menu is driven by a line and redrawn, and a read of the screen is what the
/// persona gets — the next menu, not the previous one.
/// </summary>
public class MenuTests
{
    [Fact]
    public async Task TypingANumberAndALineMovesTheScreenToTheNextMenu()
    {
        var terminal = new SimulatedTerminal(
            [
                SimulatedStep.Line("2", "SETTINGS\n1) Sound\n2) Back\n"),
                SimulatedStep.Line("1", "SOUND\nVolume: 3\n1) Back\n"),
            ],
            screen: "MAIN MENU\n1) Notes\n2) Settings\n");
        await using var world = await TestWorlds.OpenAsync(terminal);

        var main = world.Act("read-screen");
        Assert.True(main.Succeeded, main.Observation);
        Assert.Contains("MAIN MENU", main.Observation);

        var chosen = world.Act("line", "2");
        Assert.True(chosen.Succeeded, chosen.Observation);
        Assert.Contains("SETTINGS", world.Current.Screen);
        Assert.DoesNotContain("MAIN MENU", world.Current.Screen);

        var read = world.Act("read-screen");
        Assert.True(read.Succeeded, read.Observation);
        Assert.Contains("SETTINGS", read.Observation);
        Assert.Contains("1) Sound", read.Observation);
        Assert.Equal(["2"], terminal.Sent);
    }
}
