using Creatura.Worlds.Terminal;

namespace Creatura.Worlds.Terminal.Tests;

/// <summary>
/// T3 (docs/design-terminal-worlds.md): a wait that exceeds its bound fails honestly and says what it was waiting for.
/// The application's latency is the script's; the bound is the world's.
/// </summary>
public class DelayedResponseTests
{
    [Fact]
    public async Task AWaitThatExceedsItsBoundFailsAndSaysWhatItWaitedFor()
    {
        var terminal = new SimulatedTerminal(
            [SimulatedStep.Line("go", "RESULT:done\n", delayMs: 800)],
            screen: "READY\n");
        await using var world = await TestWorlds.OpenAsync(
            terminal,
            options: new TerminalWorldOptions { WaitTimeoutMs = 150, SettleMs = 0 });

        Assert.True(world.Act("line", "go").Succeeded);

        var wait = world.Act("wait-for-text", "RESULT:done");
        Assert.False(wait.Succeeded);
        Assert.Contains("not seen within 150ms", wait.Observation);
        Assert.Contains("RESULT:done", wait.Observation);
        Assert.Equal(ActionVerdict.HonestFailure, ((IWorldView)world).Verdict(world.Tool("wait-for-text")!, wait));
        Assert.DoesNotContain("RESULT:done", world.Current.Screen);
    }
}
