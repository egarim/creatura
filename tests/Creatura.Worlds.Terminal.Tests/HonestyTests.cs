using Creatura.Worlds.Terminal;
using Sentiora.Client;

namespace Creatura.Worlds.Terminal.Tests;

/// <summary>
/// T5 (docs/design-terminal-worlds.md): the world cannot be fooled. A driver that claims a text the screen does not
/// show cannot make a wait succeed, and a screen that says "success" cannot make a commit land: only the independent
/// check grants the done fact. T6's companion: a commit the check backs does land, so the test is not merely "commit
/// always fails".
/// </summary>
public class HonestyTests
{
    [Fact]
    public async Task ADriverThatClaimsATextTheScreenDoesNotShowCannotMakeTheWaitSucceed()
    {
        var inner = new SimulatedTerminal([], screen: "READY\n");
        await using var world = await TerminalWorld.OpenAsync(TestWorlds.Spec(), new TerminalWorldOptions { Driver = new LyingDriver(inner) });

        var wait = world.Act("wait-for-text", "SAVED");
        Assert.False(wait.Succeeded);
        Assert.Contains("does not show", wait.Observation);
        Assert.DoesNotContain("SAVED", world.Current.Screen);
        Assert.Equal(ActionVerdict.HonestFailure, ((IWorldView)world).Verdict(world.Tool("wait-for-text")!, wait));
    }

    [Fact]
    public async Task AScreenThatSaysSuccessDoesNotMakeTheCommitLandWithoutTheCheck()
    {
        var terminal = new SimulatedTerminal([SimulatedStep.Line("save", "Saved! SUCCESS\n")], screen: "EDITOR\n");
        var check = TestWorlds.Check(false, "the note file is not on disk");
        await using var world = await TestWorlds.OpenAsync(terminal, TestWorlds.Spec("save-note", check));

        Assert.True(world.Act("line", "save").Succeeded);
        Assert.True(world.Act("wait-for-text", "SUCCESS").Succeeded);

        var commit = world.Act("commit");
        Assert.False(commit.Succeeded);
        Assert.Contains("the check does not agree", commit.Observation);
        Assert.Contains("the note file is not on disk", commit.Observation);
        Assert.False(world.Has("done:save-note"));
        Assert.Equal(ActionVerdict.HonestFailure, ((IWorldView)world).Verdict(world.Tool("commit")!, commit));
    }

    [Fact]
    public async Task ACommitTheCheckBacksGrantsTheDoneFactAndLands()
    {
        var terminal = new SimulatedTerminal([], screen: "DONE\n");
        var check = TestWorlds.Check(true, "the note file is on disk");
        await using var world = await TestWorlds.OpenAsync(terminal, TestWorlds.Spec("save-note", check));

        var commit = world.Act("commit");
        Assert.True(commit.Succeeded, commit.Observation);
        Assert.Contains("the check agrees", commit.Observation);
        Assert.True(world.Has("done:save-note"));
        Assert.True(world.EffectsLanded(world.Tool("commit")!));
        Assert.Equal(ActionVerdict.Landed, ((IWorldView)world).Verdict(world.Tool("commit")!, commit));
    }

    [Fact]
    public async Task ACommitWithNoCheckFailsHonestly()
    {
        await using var world = await TestWorlds.OpenAsync(new SimulatedTerminal([], screen: "READY\n"));

        var commit = world.Act("commit");
        Assert.False(commit.Succeeded);
        Assert.Contains("no independent check", commit.Observation);
        Assert.Equal(ActionVerdict.HonestFailure, ((IWorldView)world).Verdict(world.Tool("commit")!, commit));
    }

    [Fact]
    public async Task AnApprovalPromptOnScreenIsNamedAndNotAnswered()
    {
        var terminal = new SimulatedTerminal([], screen: "Delete the file? [y/N]\n");
        await using var world = await TestWorlds.OpenAsync(terminal);

        Assert.True(world.Act("read-screen").Succeeded);
        Assert.True(world.AwaitingHuman);
        Assert.Equal("[y/N]", world.Handoff!.Prompt);

        var typed = world.Act("type", "y");
        Assert.False(typed.Succeeded);
        Assert.Contains("a person is needed", typed.Observation);
        Assert.Contains("[y/N]", typed.Observation);
        Assert.Empty(terminal.Sent);
    }

    [Fact]
    public async Task AStaleRevisionIsAFailedInputInTheRuntimeSWordsNotAFault()
    {
        var inner = new SimulatedTerminal([], screen: "READY\n");
        var driver = new ThrowingDriver(inner, () => new SentioraException("terminal.type kept returning a stale revision", "terminal.type"));
        await using var world = await TerminalWorld.OpenAsync(TestWorlds.Spec(), new TerminalWorldOptions { Driver = driver });

        var typed = world.Act("type", "hello");
        Assert.False(typed.Succeeded);
        Assert.Contains("stale revision", typed.Observation);
        Assert.False(world.Faulted);
        Assert.True(world.Act("read-screen").Succeeded);
    }

    [Fact]
    public async Task ARuntimeThatStoppedAnsweringIsReportedAndTheWorldSaysItCannotAct()
    {
        var inner = new SimulatedTerminal([], screen: "READY\n");
        var driver = new ThrowingDriver(inner, () => new SentioraException("runtime exited before answering", "terminal.type"));
        await using var world = await TerminalWorld.OpenAsync(TestWorlds.Spec(), new TerminalWorldOptions { Driver = driver });

        var typed = world.Act("type", "hello");
        Assert.False(typed.Succeeded);
        Assert.Contains("runtime exited before answering", typed.Observation);
        Assert.Contains("could not be restarted", typed.Observation);
        Assert.True(world.Faulted);
        Assert.DoesNotContain(world.Facts, fact => fact.StartsWith("session:", StringComparison.Ordinal) || fact.StartsWith("revision:", StringComparison.Ordinal));

        var read = world.Act("read-screen");
        Assert.False(read.Succeeded);
        Assert.Contains("the runtime is not usable", read.Observation);
    }

    /// <summary>A real driver's shape with one lie in it: a text wait reports success without the screen changing.</summary>
    private sealed class LyingDriver(ITerminalDriver inner) : ITerminalDriver
    {
        public bool ForcedShutdown => inner.ForcedShutdown;

        public Task<TerminalObservation> ObserveAsync(CancellationToken cancellationToken) => inner.ObserveAsync(cancellationToken);

        public Task<long> TypeAsync(string text, CancellationToken cancellationToken) => inner.TypeAsync(text, cancellationToken);

        public Task<long> SendLineAsync(string line, CancellationToken cancellationToken) => inner.SendLineAsync(line, cancellationToken);

        public Task<long> SendKeyAsync(string key, CancellationToken cancellationToken) => inner.SendKeyAsync(key, cancellationToken);

        public Task<TerminalObservation> WaitAsync(long afterRevision, int timeoutMs, CancellationToken cancellationToken) =>
            inner.WaitAsync(afterRevision, timeoutMs, cancellationToken);

        public Task WaitForTextAsync(string needle, int timeoutMs, CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> CloseAsync(CancellationToken cancellationToken) => inner.CloseAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>A driver whose input fails with the given runtime error; every other member is the real shape.</summary>
    private sealed class ThrowingDriver(ITerminalDriver inner, Func<Exception> failure) : ITerminalDriver
    {
        public bool ForcedShutdown => inner.ForcedShutdown;

        public Task<TerminalObservation> ObserveAsync(CancellationToken cancellationToken) => inner.ObserveAsync(cancellationToken);

        public Task<long> TypeAsync(string text, CancellationToken cancellationToken) => Task.FromException<long>(failure());

        public Task<long> SendLineAsync(string line, CancellationToken cancellationToken) => Task.FromException<long>(failure());

        public Task<long> SendKeyAsync(string key, CancellationToken cancellationToken) => Task.FromException<long>(failure());

        public Task<TerminalObservation> WaitAsync(long afterRevision, int timeoutMs, CancellationToken cancellationToken) =>
            inner.WaitAsync(afterRevision, timeoutMs, cancellationToken);

        public Task WaitForTextAsync(string needle, int timeoutMs, CancellationToken cancellationToken) =>
            inner.WaitForTextAsync(needle, timeoutMs, cancellationToken);

        public Task<bool> CloseAsync(CancellationToken cancellationToken) => inner.CloseAsync(cancellationToken);

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}
