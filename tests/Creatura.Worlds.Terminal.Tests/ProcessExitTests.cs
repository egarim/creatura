using Creatura.Worlds.Terminal;

namespace Creatura.Worlds.Terminal.Tests;

/// <summary>
/// T4 (docs/design-terminal-worlds.md): a child that exits is reported with its exit code by a read and by a close,
/// sending to it fails, and a commit after the exit fails honestly because the independent check does not agree.
/// </summary>
public class ProcessExitTests
{
    [Fact]
    public async Task AnExitedChildIsReportedWithItsExitCodeAndACommitAfterItFailsHonestly()
    {
        var terminal = new SimulatedTerminal([SimulatedStep.Line("quit", "BYE\n", exitCode: 7)], screen: "READY\n");
        var check = TestWorlds.Check(false, "the file was never written");
        await using var world = await TestWorlds.OpenAsync(terminal, TestWorlds.Spec("quit-app", check));

        var quiet = world.Act("line", "quit");
        Assert.True(quiet.Succeeded, quiet.Observation);
        Assert.Equal(7, world.Current.ExitCode);

        var read = world.Act("read-screen");
        Assert.True(read.Succeeded, read.Observation);
        Assert.Contains("exited with code 7", read.Observation);
        Assert.Contains("BYE", read.Observation);
        Assert.True(world.Has(TerminalFacts.Exited(7)));
        Assert.False(world.Has(TerminalFacts.State("running")));

        var typed = world.Act("type", "x");
        Assert.False(typed.Succeeded);
        Assert.Contains("exited with code 7", typed.Observation);

        var commit = world.Act("commit");
        Assert.False(commit.Succeeded);
        Assert.Contains("the check does not agree", commit.Observation);
        Assert.Contains("the file was never written", commit.Observation);
        Assert.False(world.Has("done:quit-app"));
        Assert.Equal(ActionVerdict.HonestFailure, ((IWorldView)world).Verdict(world.Tool("commit")!, commit));

        var closed = world.Act("close");
        Assert.True(closed.Succeeded, closed.Observation);
        Assert.Contains("exit code 7", closed.Observation);
        Assert.True(world.Has("session-closed"));
        Assert.False(world.Has(TerminalFacts.Session("sim-1")));
    }
}
