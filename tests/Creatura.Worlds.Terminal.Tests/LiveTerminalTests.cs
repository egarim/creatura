using Creatura.Worlds.Terminal;
using Sentiora.Client;

namespace Creatura.Worlds.Terminal.Tests;

/// <summary>
/// The env-gated fixture (docs/design-terminal-worlds.md, T7): drives the real runtime over a real PTY against
/// <c>test/fixtures/interactive.mjs</c> — create a session, read the screen, type a line, see the echo, close. Every
/// call is bounded, and without node or the runtime the fact is skipped with the reason and the fix
/// (<see cref="TerminalFactAttribute"/>). It is in the <c>Terminal</c> category.
/// </summary>
[Trait("Category", "Terminal")]
public class LiveTerminalTests
{
    [TerminalFact]
    public async Task TheRealRuntimeSpawnsAChildTypesALineAndSeesItsEcho()
    {
        var options = new TerminalWorldOptions
        {
            ServerPath = TerminalProbe.Server,
            NodeExecutable = TerminalProbe.Node,
            WorkingDirectory = TerminalProbe.Runtime,
            ActionTimeoutMs = 30000,
            SettleMs = 500,
            WaitTimeoutMs = 30000,
            Create = new CreateSessionRequest
            {
                Command = TerminalProbe.Node,
                Args = [Path.Combine(TerminalProbe.Runtime, "test", "fixtures", "interactive.mjs")],
                Cols = 100,
                Rows = 30,
            },
        };

        await using var world = await TerminalWorld.OpenAsync(TestWorlds.Spec("echo"), options);

        var ready = world.Act("wait-for-text", "FIXTURE READY");
        Assert.True(ready.Succeeded, ready.Observation);
        Assert.True(world.Current.Running);
        Assert.True(world.Has(TerminalFacts.Session(world.Current.SessionId)));

        var line = world.Act("line", "echo hello");
        Assert.True(line.Succeeded, line.Observation);

        var echo = world.Act("wait-for-text", "ECHO:hello");
        Assert.True(echo.Succeeded, echo.Observation);
        Assert.Contains("ECHO:hello", world.Current.Screen);

        var closed = world.Act("close");
        Assert.True(closed.Succeeded, closed.Observation);
        Assert.True(world.Has("session-closed"));

        await world.DisposeAsync();
        Assert.False(world.ForcedShutdown, world.Shutdown);
    }
}
