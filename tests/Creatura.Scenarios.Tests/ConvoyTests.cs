using System.Text.Json;
using Creatura.Bench;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// The mechanics of the convoy (docs/design-machine-vessels.md): the world as the only truth, the lie caught, the
/// replay repeatable, the ledgers converging, the breakdown covered, and the replay a stream any UI can read. The
/// hypotheses M1 to M7 are not asserted here: they are experiments, scored by the battery and reported as found.
/// </summary>
public sealed class ConvoyTests
{
    private static Task<ConvoyRun> Run(ConvoyArm arm, int seed) => ConvoyScenario.Create(arm, seed).RunAsync();

    /// <summary>A world that counts the ticks the loop tells it about.</summary>
    private sealed class CountingWorld : ITickedWorld
    {
        private readonly World _inner = RestoreServiceDomain.NewWorld();
        public List<int> Begun { get; } = [];
        public void BeginTick(int tick) => Begun.Add(tick);
        public int Ticks => _inner.Ticks;
        public ActionOutcome Act(string toolId, string? payload = null) => _inner.Act(toolId, payload);
        public IReadOnlySet<string> Facts => _inner.Facts;
        public IReadOnlyList<Tool> AvailableTools => _inner.AvailableTools;
        public IReadOnlyList<Tool> AllTools => _inner.AllTools;
        public bool Has(string fact) => _inner.Has(fact);
        public Tool? Tool(string id) => _inner.Tool(id);
        public bool EffectsLanded(Tool tool) => _inner.EffectsLanded(tool);
    }

    [Fact]
    public async Task TheLoopTellsATickedWorldEveryTickOnceInOrder()
    {
        var world = new CountingWorld();
        var run = await new ReferenceLoop(new PlainRegulator(new Identity("operator", "you"))).RunAsync(world, maxTicks: 40);
        Assert.Equal(Enumerable.Range(1, run.Ticks), world.Begun);
    }

    [Theory]
    [InlineData(ConvoyArm.A, 1)]
    [InlineData(ConvoyArm.B, 2)]
    [InlineData(ConvoyArm.C, 3)]
    [InlineData(ConvoyArm.D, 4)]
    [InlineData(ConvoyArm.D, 0)]
    [InlineData(ConvoyArm.D, 9)]
    [InlineData(ConvoyArm.E, 5)]
    public async Task TheSameSeedGivesTheSameReplay(ConvoyArm arm, int seed) =>
        Assert.True(await ConvoyBattery.ReplaysMatchAsync(arm, seed));

    [Fact]
    public async Task DifferentSeedsGiveDifferentFloods()
    {
        var one = ConvoySetup.For(ConvoyArm.A, 1).Faults;
        var other = ConvoySetup.For(ConvoyArm.A, 2).Faults;
        Assert.NotEqual(one.Closures, other.Closures);
        Assert.Equal(ConvoySetup.For(ConvoyArm.C, 1).Faults.Closures, one.Closures);
        Assert.Empty((await Run(ConvoyArm.A, 1)).Errors);
    }

    [Theory]
    [InlineData(ConvoyArm.A, 1)]
    [InlineData(ConvoyArm.C, 1)]
    [InlineData(ConvoyArm.D, 7)]
    public async Task OnlyTheWorldSaysWhatWasDelivered(ConvoyArm arm, int seed)
    {
        var run = await Run(arm, seed);
        Assert.Empty(run.Errors);
        var unloaded = run.Ticks.SelectMany(t => t.Events).Count(e => e.Kind == "unload");
        Assert.True(unloaded > 0);
        Assert.InRange(run.Measures.Delivered, 1, run.Measures.Demand);
        var last = run.Ticks[^1];
        Assert.Equal(run.Measures.Demand - run.Measures.Delivered, last.Needs.Values.Sum());
    }

    [Fact]
    public async Task ALieMovesNoTruckAndIsCaughtTheSameTick()
    {
        var lies = 0;
        for (var seed = 0; seed < 6; seed++)
        {
            var run = await Run(ConvoyArm.A, seed);
            for (var i = 0; i < run.Ticks.Count; i++)
            {
                foreach (var lie in run.Ticks[i].Events.Where(e => e.Kind == "lie"))
                {
                    lies++;
                    Assert.Equal(lie.From, run.Ticks[i].Trucks.Single(t => t.Name == lie.Truck).At);
                    Assert.Contains(run.Ticks[i].Events, e => e.Kind == "caught" && e.Truck == lie.Truck && e.Road == lie.Road);
                    Assert.Contains(lie.Road!, run.Ticks[i].Closed);
                }
            }
        }
        Assert.True(lies > 0, "no seed of 0 to 5 put a truck on the lying road");
    }

    /// <summary>
    /// The world directly: a closed road refuses honestly and the truck stays; the lying road reports arrival and the
    /// truck stays; an open road moves it and spends fuel. In a run the planner never meets the first, because the
    /// road sensors report ordinary closures truthfully.
    /// </summary>
    [Fact]
    public void TheWorldRefusesClosedRoadsHonestlyAndTheLyingRoadReportsArrival()
    {
        var setup = ConvoySetup.For(ConvoyArm.A, 1) with
        {
            Faults = new ConvoyFaults([new Closure("r01", 1), new Closure("r12", 1)], LyingRoad: "r12", GaugeLiar: "Truck 3", GaugeOffset: 3),
        };
        var world = new ConvoyWorld(setup);
        world.BeginTick(1);
        var truck = world.Truck("Truck 1");

        var blocked = world.Drive(truck, "drive:r01:j1", "r01", "j1");
        Assert.False(blocked.Succeeded);
        Assert.Equal("depot", truck.At);
        Assert.False(world.SensedOpen("r01"));

        var lie = world.Drive(truck, "drive:r12:alba", "r12", "alba");
        Assert.True(lie.Succeeded);
        Assert.Equal("depot", truck.At);
        Assert.True(world.SensedOpen("r12"));
        Assert.Equal(1, truck.LyingRoadAttempts);

        var fuel = truck.Fuel;
        Assert.True(world.Drive(truck, "drive:r02:j2", "r02", "j2").Succeeded);
        Assert.Equal(("j2", fuel - 1), (truck.At, truck.Fuel));
    }

    [Fact]
    public async Task TheGaugeThatLiesReadsHighOnOneTruckOnly()
    {
        var run = await Run(ConvoyArm.A, 3);
        var liar = run.Setup.Faults.GaugeLiar;
        foreach (var frame in run.Ticks.SelectMany(t => t.Trucks))
        {
            Assert.Equal(frame.Name == liar ? frame.Fuel + 3 : frame.Fuel, frame.FuelBelieved);
        }
    }

    [Fact]
    public async Task WithoutALedgerThereIsNoRadio()
    {
        var run = await Run(ConvoyArm.C, 1);
        Assert.All(run.Header.Trucks, t => Assert.Equal("", t.Cell));
        Assert.Equal(0, run.Measures.MessagesSent);
        Assert.DoesNotContain(run.Ticks.SelectMany(t => t.Events), e => e.Kind is "claim" or "takeover" or "release" or "message");
    }

    [Fact]
    public async Task LedgersConvergeAfterTheFinalSyncDespiteLoss()
    {
        for (var seed = 0; seed < 5; seed++)
        {
            var run = await Run(ConvoyArm.D, seed);
            Assert.True(run.Measures.MessagesLost > 0, $"seed {seed} lost nothing");
            Assert.True(run.Measures.LedgersIdentical, $"seed {seed}: the ledgers differ after the final sync");
        }
    }

    [Fact]
    public async Task ABrokenTrucksClaimIsTakenOver()
    {
        var takeovers = 0;
        for (var seed = 0; seed < 6; seed++)
        {
            var run = await Run(ConvoyArm.E, seed);
            var broken = run.Ticks.First(t => t.Tick == 8).Trucks.Single(t => t.Name == "Truck 2");
            Assert.Equal("broken", broken.State);
            if (broken.Claim is not { } village) continue;
            var later = run.Ticks.Where(t => t.Tick > 8).SelectMany(t => t.Events).ToList();
            var takeover = later.FirstOrDefault(e => e.Kind == "takeover" && e.Village == village);
            Assert.True(takeover is not null || run.Ticks[^1].Needs[village] == 0, $"seed {seed}: {village} was held by the broken truck and neither taken over nor served");
            if (takeover is not null)
            {
                takeovers++;
                var at = run.Ticks.First(t => t.Events.Contains(takeover)).Tick;
                Assert.True(at - 8 >= run.Setup.TakeoverAfter - 1, $"seed {seed}: taken over after {at - 8} ticks, before the takeover period");
            }
        }
        Assert.True(takeovers > 0, "no seed of 0 to 5 left the broken truck holding a claim");
    }

    [Fact]
    public async Task TheReplayIsAStreamOfJsonLines()
    {
        using var text = new StringWriter();
        var run = await ConvoyScenario.Create(ConvoyArm.D, 2).RunAsync(new JsonlReplayWriter(text));
        var lines = text.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => JsonDocument.Parse(l).RootElement).ToList();
        Assert.Equal("header", lines[0].GetProperty("type").GetString());
        Assert.Equal("creatura.replay/1", lines[0].GetProperty("format").GetString());
        Assert.Equal("end", lines[^1].GetProperty("type").GetString());
        Assert.Equal(run.Ticks.Count, lines.Count(l => l.GetProperty("type").GetString() == "tick"));

        var nodes = lines[0].GetProperty("nodes").EnumerateArray().Select(n => n.GetProperty("id").GetString()).ToHashSet();
        foreach (var tick in lines.Where(l => l.GetProperty("type").GetString() == "tick"))
        {
            Assert.Equal(3, tick.GetProperty("trucks").GetArrayLength());
            foreach (var truck in tick.GetProperty("trucks").EnumerateArray()) Assert.Contains(truck.GetProperty("at").GetString(), nodes);
            foreach (var e in tick.GetProperty("events").EnumerateArray()) Assert.False(string.IsNullOrWhiteSpace(e.GetProperty("text").GetString()));
        }
        Assert.True(lines[^1].GetProperty("measures").GetProperty("delivered").GetInt32() > 0);
    }

    [Fact]
    public void TheBatteryScoresTheHypothesesFromTheMeasures()
    {
        static ConvoyMeasures M(int delivered, int wasted, int lies = 0, int repeats = 0, bool identical = true, int idle = 0) =>
            new(delivered, 30, wasted, 0, lies, repeats, 20, identical, idle, 10, 0);
        var runs = new Dictionary<ConvoyArm, IReadOnlyList<ConvoyMeasures>>
        {
            [ConvoyArm.A] = [M(30, 0, lies: 2)],
            [ConvoyArm.B] = [M(30, 0, lies: 1)],
            [ConvoyArm.C] = [M(28, 5)],
            [ConvoyArm.D] = [M(30, 1)],
            [ConvoyArm.E] = [M(30, 0)],
        };
        var verdicts = ConvoyBattery.Score(runs, replaysMatch: true).ToDictionary(v => v.Id, v => v.Verdict);
        Assert.All(verdicts.Values, v => Assert.Equal(HypothesisVerdict.Held, v));

        runs[ConvoyArm.A] = [M(30, 6, lies: 2, repeats: 1)];
        runs[ConvoyArm.D] = [M(30, 1, identical: false)];
        runs[ConvoyArm.E] = [M(25, 0, idle: 3)];
        runs[ConvoyArm.B] = [M(29, 0, lies: 1)];
        verdicts = ConvoyBattery.Score(runs, replaysMatch: false).ToDictionary(v => v.Id, v => v.Verdict);
        Assert.All(verdicts, v => Assert.Equal(v.Key == "M2" ? HypothesisVerdict.Held : HypothesisVerdict.Failed, v.Value));
    }
}
