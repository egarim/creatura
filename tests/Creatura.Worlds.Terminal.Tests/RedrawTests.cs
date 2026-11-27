using Creatura.Worlds.Terminal;

namespace Creatura.Worlds.Terminal.Tests;

/// <summary>
/// T2 (docs/design-terminal-worlds.md): a screen that changes with no input is found by waiting for its text. The
/// simulation's clock starts at the opening read, so the first screen is read before the redraw can happen.
/// </summary>
public class RedrawTests
{
    [Fact]
    public async Task AScreenThatChangesWithNoInputIsFoundByWaitingForItsText()
    {
        var terminal = new SimulatedTerminal([SimulatedStep.Redraw("BUILD 2 of 3\n", afterMs: 150)], screen: "BUILD 1 of 3\n");
        await using var world = await TestWorlds.OpenAsync(
            terminal,
            options: new TerminalWorldOptions { WaitTimeoutMs = 5000, SettleMs = 0 });

        Assert.Contains("BUILD 1", world.Current.Screen);

        var waited = world.Act("wait-for-text", "BUILD 2");
        Assert.True(waited.Succeeded, waited.Observation);
        Assert.Contains("BUILD 2", world.Current.Screen);
        Assert.DoesNotContain("BUILD 1", world.Current.Screen);
        Assert.Contains("BUILD 2", terminal.Screen);
    }
}
