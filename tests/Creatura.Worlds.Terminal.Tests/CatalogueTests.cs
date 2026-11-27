using Creatura.Worlds.Terminal;

namespace Creatura.Worlds.Terminal.Tests;

/// <summary>
/// T6 (docs/design-terminal-worlds.md): the tool catalogue is stable and honest (only a commit requires anything, only
/// close and commit promise anything, every id is <c>[a-z0-9-]</c>), and unusable options are all named before a
/// runtime is started.
/// </summary>
public class CatalogueTests
{
    [Fact]
    public async Task TheCatalogueIsSevenStableIdsAndOnlyTheTextToolsNeedAPayload()
    {
        var check = TestWorlds.Check(true);
        await using var world = await TestWorlds.OpenAsync(new SimulatedTerminal([], screen: "READY\n"), TestWorlds.Spec("catalogue", check));

        Assert.Equal(new[] { "read-screen", "type", "line", "key", "wait-for-text", "close", "commit" }, world.AllTools.Select(tool => tool.Id).ToArray());
        Assert.All(world.AllTools, tool => Assert.Matches("^[a-z0-9-]+$", tool.Id));
        Assert.Equal(new[] { "type", "line", "key", "wait-for-text" }, world.AllTools.Where(tool => tool.NeedsPayload).Select(tool => tool.Id).ToArray());
        Assert.All(world.AllTools.Where(tool => tool.NeedsPayload), tool => Assert.False(string.IsNullOrWhiteSpace(tool.PayloadHint)));
        Assert.All(world.AllTools.Where(tool => !tool.NeedsPayload), tool => Assert.Null(tool.PayloadHint));

        Assert.Empty(world.Tool("read-screen")!.Grants);
        Assert.Empty(world.Tool("type")!.Grants);
        Assert.Empty(world.Tool("line")!.Grants);
        Assert.Empty(world.Tool("key")!.Grants);
        Assert.Empty(world.Tool("wait-for-text")!.Grants);
        Assert.Equal(new[] { "session-closed" }, world.Tool("close")!.Grants);
        Assert.Equal(new[] { "done:catalogue" }, world.Tool("commit")!.Grants);

        Assert.All(world.AllTools.Where(tool => tool.Id != "commit"), tool => Assert.Empty(tool.Requires));
        Assert.Equal(world.AllTools.Select(tool => tool.Id).ToArray(), world.AvailableTools.Select(tool => tool.Id).ToArray());
        Assert.Contains("Enter", world.Tool("key")!.PayloadHint);
    }

    [Fact]
    public async Task ACommitRequiresTheFactsTheTaskNamesBeforeItIsAttempted()
    {
        var terminal = new SimulatedTerminal([SimulatedStep.Line("quit", "BYE\n", exitCode: 3)], screen: "READY\n");
        var check = TestWorlds.Check(true, "the file is on disk");
        await using var world = await TestWorlds.OpenAsync(terminal, TestWorlds.Spec("quit-app", check, "state:running"));

        Assert.Equal(new[] { "state:running" }, world.Tool("commit")!.Requires);
        Assert.True(world.Act("commit").Succeeded);
        Assert.True(world.Has("done:quit-app"));

        Assert.True(world.Act("line", "quit").Succeeded);
        var refused = world.Act("commit");
        Assert.False(refused.Succeeded);
        Assert.Contains("preconditions unmet", refused.Observation);
        Assert.Contains("state:running", refused.Observation);
    }

    [Fact]
    public async Task AKeyFromTheAllowListIsSentAndOneOutsideItIsRefused()
    {
        var terminal = new SimulatedTerminal([SimulatedStep.Key("Enter", "ACK\n")], screen: "READY\n");
        await using var world = await TestWorlds.OpenAsync(terminal);

        var sent = world.Act("key", "Enter");
        Assert.True(sent.Succeeded, sent.Observation);
        Assert.Equal(new[] { "key:Enter" }, terminal.Sent.ToArray());
        Assert.Contains("ACK", world.Current.Screen);

        var refused = world.Act("key", "F13");
        Assert.False(refused.Succeeded);
        Assert.Contains("F13", refused.Observation);
        Assert.Single(terminal.Sent);
    }

    [Fact]
    public void OutOfBoundsOptionsAreAllNamedBeforeAnythingStarts()
    {
        var options = new TerminalWorldOptions
        {
            Driver = new SimulatedTerminal([]),
            ActionTimeoutMs = 0,
            SettleMs = -1,
            WaitTimeoutMs = 0,
            MaxText = 0,
            Keys = ["Enter", "Enter", "Nope"],
            ApprovalPrompts = [""],
        };

        var problems = options.Problems();
        Assert.Contains(problems, problem => problem.Contains("ActionTimeoutMs"));
        Assert.Contains(problems, problem => problem.Contains("SettleMs"));
        Assert.Contains(problems, problem => problem.Contains("WaitTimeoutMs"));
        Assert.Contains(problems, problem => problem.Contains("MaxText"));
        Assert.Contains(problems, problem => problem.Contains("\"Nope\""));
        Assert.Contains(problems, problem => problem.Contains("twice"));
        Assert.Contains(problems, problem => problem.Contains("ApprovalPrompts"));

        var error = Assert.Throws<ArgumentException>(() => TerminalWorld.OpenAsync(TestWorlds.Spec(), options).GetAwaiter().GetResult());
        Assert.Contains("ActionTimeoutMs", error.Message);
    }

    [Fact]
    public void AWorldWithNoRuntimeAndNoSessionIsNotOpenable()
    {
        var problems = new TerminalWorldOptions().Problems();
        Assert.Contains(problems, problem => problem.Contains("no runtime"));
        Assert.Contains(problems, problem => problem.Contains("no session to create"));
    }

    [Fact]
    public void ASessionWithoutItsClientIsNamed()
    {
        var problems = new TerminalWorldOptions
        {
            Session = new Sentiora.Client.SessionHandle { SessionId = "s", Owner = "owner" },
            Create = new Sentiora.Client.CreateSessionRequest { Command = "cmd.exe" },
            ServerPath = "server.js",
        }.Problems();
        Assert.Contains(problems, problem => problem.Contains("a Session belongs to a Client"));
    }

    [Fact]
    public async Task TheDefaultsAreUsableWithOnlyADriver()
    {
        var options = new TerminalWorldOptions { Driver = new SimulatedTerminal([], screen: "READY\n") };
        Assert.Empty(options.Problems());

        await using var world = await TerminalWorld.OpenAsync(TestWorlds.Spec(), options);
        Assert.True(world.Current.Running);
        Assert.Equal(80, world.Current.Cols);
        Assert.Contains(TerminalFacts.State("running"), world.Facts);
    }
}
