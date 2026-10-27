namespace Creatura.Tests;

/// <summary>
/// The proof that the control layer's verbs are host-neutral (migration step 16): <see cref="FakeHost"/> drives the four
/// controlled scenarios through <c>BeginRound</c> / <c>Stop</c> / <c>Assemble</c> / <c>BeginTick</c> / <c>Offer</c> /
/// <c>Admit</c> / <c>ReportOutcome</c> / <c>Judge</c> / <c>Close</c> / <c>Sleep</c> with the planner as the model and no
/// Agent Framework anywhere, and its runs are the Agent Framework host's goldens byte for byte. Tick = model call on both
/// hosts, so the ticks agree without translation. Two strings in each file are the host describing itself, not the layer:
/// the agent's name and the first note's framework; the byte comparison hands the fake the real host's two strings, and
/// the second run shows that with its own they are the only lines that differ.
/// </summary>
public class FakeHostTests
{
    public static TheoryData<string> Scenarios => ["clean", "patchrevoked", "patchlies", "deployrevoked"];

    /// <summary>tests/Creatura.Hosts.AgentFramework.Tests/golden: the host's goldens, read here as data (no reference to that project).</summary>
    private static string HostGoldens { get; } = Path.GetFullPath(Path.Combine(GoldenTraceTests.Directory_, "..", "..", "Creatura.Hosts.AgentFramework.Tests", "golden"));

    private static World Toy(string barrier)
    {
        var world = RestoreServiceDomain.NewWorld();
        switch (barrier)
        {
            case "clean": break;
            case "patchrevoked": world.Revoke("apply-patch"); break;
            case "patchlies": world.MakeLiar("apply-patch"); break;
            case "deployrevoked": world.Revoke("deploy"); break;
            default: throw new ArgumentException(barrier);
        }

        return world;
    }

    /// <summary>The model's seat as the host's offline client fills it: breadth-first search over exactly the tools the call offers.</summary>
    private static Func<FakeModelCall, FakeModelReply> Bfs(IWorld world, AgentTask task) => call =>
    {
        var offered = call.Offered.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var tools = world.AllTools.Where(t => offered.Contains(t.Id) && !t.NeedsPayload).ToList();
        var plan = Planner.Find(world.Facts, tools, task.Goal, 8, task.Habit);
        return plan is null ? FakeModelReply.Say("NO PATH: no offered tool can make progress toward the goal.")
            : plan.Count == 0 ? FakeModelReply.Say("The goal is already met.")
            : FakeModelReply.Call(plan[0]);
    };

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task TheFakeHostRunsTheAgentFrameworkHostsGoldenByteForByte(string barrier)
    {
        var name = $"restore-{barrier}-controlled";
        var golden = Path.Combine(HostGoldens, name + ".jsonl");
        Assert.True(File.Exists(golden), $"{golden} is missing: the host's goldens live in tests/Creatura.Hosts.AgentFramework.Tests/golden");

        var host = new FakeHost(Bfs, "bfs", RestoreServiceDomain.Task, name: "Controlled (AF)", framework: "Microsoft.Agents.AI");
        var run = await host.RunAsync(Toy(barrier), 30);

        GoldenTraceTests.AssertSame(AgentRun.ReadJsonl(golden), run, name + " (fake host)");

        var written = Path.Combine(Path.GetTempPath(), $"creatura-fakehost-{Guid.NewGuid():N}.jsonl");
        try
        {
            run.WriteJsonl(written);
            Assert.Equal(File.ReadAllBytes(golden), File.ReadAllBytes(written));
        }
        finally
        {
            File.Delete(written);
        }
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task UnderItsOwnNameOnlyTheHostsSelfDescriptionDiffers(string barrier)
    {
        var golden = AgentRun.ReadJsonl(Path.Combine(HostGoldens, $"restore-{barrier}-controlled.jsonl"));

        var run = await new FakeHost(Bfs, "bfs").RunAsync(Toy(barrier), 30);

        Assert.Equal("fake host", run.Agent);
        Assert.Equal("policy bfs; framework none (in-process)", run.Notes[0]);
        Assert.Equal("policy bfs; framework Microsoft.Agents.AI", golden.Notes[0]);
        Assert.Equal(golden.Notes.Skip(1), run.Notes.Skip(1));
        Assert.Equal(golden.Trace, run.Trace);
        Assert.Equal((golden.GoalReached, golden.Ticks), (run.GoalReached, run.Ticks));
    }

    [Fact]
    public async Task AToolNamedFromMemoryIsRefusedByAdmitAndTheRoundGoesOn()
    {
        // The model names the revoked patch before anything else; the layer refuses it (not visible), the call costs a
        // tick and a failure, the feedback says why, and the next call in the same round is offered the visible set.
        var world = Toy("patchrevoked");
        var seen = new List<FakeModelCall>();
        Func<FakeModelCall, FakeModelReply> Stubborn(IWorld w, AgentTask t)
        {
            var bfs = Bfs(w, t);
            return call =>
            {
                seen.Add(call);
                return call.Call == 1 && call.Round == 1 ? FakeModelReply.Call("apply-patch") : bfs(call);
            };
        }

        var run = await new FakeHost(Stubborn, "stubborn").RunAsync(world, 30);

        Assert.True(run.GoalReached);
        Assert.DoesNotContain(seen[0].Offered, t => t.Id == "apply-patch");
        Assert.StartsWith("REFUSED: apply-patch is not available", Assert.Single(seen[1].Feedback), StringComparison.Ordinal);
        Assert.Equal(1, seen[1].Round);
        Assert.DoesNotContain(run.Trace, e => e.Action == "apply-patch" && e.Kind == TraceKind.Acted);
    }

    [Fact]
    public async Task AFunctionTheWorldDoesNotHaveReachesNoVerb()
    {
        // The framework answers an unknown function itself; the layer never hears of it, so no refusal row and no failure.
        var world = Toy("clean");
        var first = true;
        Func<FakeModelCall, FakeModelReply> Inventing(IWorld w, AgentTask t)
        {
            var bfs = Bfs(w, t);
            return call =>
            {
                if (!first) return bfs(call);
                first = false;
                return FakeModelReply.Call("reboot-everything");
            };
        }

        var plain = await new FakeHost(Bfs, "bfs").RunAsync(Toy("clean"), 30);
        var run = await new FakeHost(Inventing, "bfs").RunAsync(world, 30);

        Assert.True(run.GoalReached);
        Assert.Equal(plain.Ticks + 1, run.Ticks);
        Assert.DoesNotContain(run.Trace, e => e.Action == "reboot-everything");
        Assert.Equal(plain.Trace.Select(e => (e.Action, e.Kind)), run.Trace.Select(e => (e.Action, e.Kind)));
    }
}
