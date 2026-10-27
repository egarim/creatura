using Creatura.Worlds.Samples;

namespace Creatura.Tests;

/// <summary>The in-memory habitat isolates by copying, and a dream in an isolated copy leaves the live world and the live map untouched.</summary>
public class HabitatTests
{
    [Fact]
    public void IsolateIsACloneOfACatalogueWorld()
    {
        var live = new LicenceWorld();
        var habitat = InMemoryHabitat.Live(live);
        Assert.False(habitat.Isolated);
        var isolated = habitat.Isolate(live);
        Assert.True(isolated.Isolated);
        Assert.IsType<LicenceWorld>(isolated.World);
        isolated.World.Act("check-file");
        Assert.True(isolated.World.Has("file.ok"));
        Assert.Equal(1, isolated.World.Ticks);
        Assert.False(live.Has("file.ok"));
        Assert.Equal(0, live.Ticks);
        isolated.Discard();
        Assert.Empty(isolated.World.AllTools);
    }

    [Fact]
    public void IsolateOfALearnedWorldCopiesTheOpaqueWorldAndTheMap()
    {
        var memory = new EffectMemory();
        var live = new LearnedWorld(new OpaqueWorld(RestoreServiceDomain.NewWorld(), ["deploy"]), memory);
        live.Act("inspect-logs");
        var copy = InMemoryHabitat.Live(live).Isolate(live);
        Assert.IsType<LearnedWorld>(copy.World);
        copy.World.Act("identify-component");
        Assert.True(copy.World.Has("component.identified"));
        Assert.False(live.Has("component.identified"));
        Assert.Equal(1, live.Ticks);
        Assert.False(memory.Tried("identify-component"), "the copy learned on its own map");
        Assert.True(((LearnedWorld)copy.World).Memory.Tried("identify-component"));
        Assert.DoesNotContain(copy.World.AllTools, t => t.Id == "deploy");
    }

    [Fact]
    public void AWorldThatCannotBeCopiedIsRefused()
    {
        var world = new Hotz.RouteRecorder(RestoreServiceDomain.NewWorld());
        var ex = Assert.Throws<NotSupportedException>(() => InMemoryHabitat.Live(world).Isolate(world));
        Assert.Contains("RouteRecorder", ex.Message);
    }

    [Fact]
    public void ADreamInIsolateLeavesTheLiveWorldAndTheLiveMapUntouched()
    {
        var memory = new EffectMemory();
        var journal = new DreamJournal();
        var live = new LearnedWorld(new OpaqueWorld(new LicenceWorld()), memory);
        var dream = new DreamSleepExtension(memory, journal);
        var tick = new TickContext { Agent = "t", World = live, Task = LicenceWorld.Task, Tick = 0, MaxTicks = 10, Notes = [], Trace = [] };

        // The day: the organism learns all four tools awake; the dream starts from the day's start, before any of them.
        dream.BeforeRun(tick);
        foreach (var id in new[] { "check-file", "take-payment", "issue-licence", "notify" })
        {
            var outcome = live.Act(id);
            dream.AfterAction(tick, live.Tool(id)!, outcome, ActionVerdict.Landed);
        }

        Assert.True(LicenceWorld.Contract(live.Facts));
        var factsBefore = live.Facts.Order(StringComparer.Ordinal).ToList();
        var mapBefore = memory.Snapshot();

        var sleep = new SleepContext { Agent = "t", World = live, Task = LicenceWorld.Task, LastTick = 4, GoalReached = true, Notes = [], Trace = [] };
        dream.Sleep(sleep);

        Assert.Equal(1, journal.DreamedRoutes);
        Assert.Equal(["check-file", "issue-licence", "notify", "take-payment"], journal.Prefer.Order(StringComparer.Ordinal));
        Assert.Equal(4, live.Ticks);
        Assert.Equal(factsBefore, live.Facts.Order(StringComparer.Ordinal));
        Assert.Equal(mapBefore.Entries.ToDictionary(e => e.Key, e => e.Value.Tried), memory.Snapshot().Entries.ToDictionary(e => e.Key, e => e.Value.Tried));
        Assert.Contains(sleep.Trace, e => e.Action == "dream" && e.Detail.StartsWith("night 1: 1 dreamed routes over 4 learned tools", StringComparison.Ordinal));
    }

    [Fact]
    public void ADreamActsOutWhatTheCopyWouldAnswerNotWhatTheMapBelieves()
    {
        // Day one teaches the map the whole route through apply-patch. Day two's world has apply-patch revoked. The map
        // still believes in it; the copy the dream runs in refuses it, as the world would, so no dreamed route runs
        // through it and the one alternative (apply-config) is not in the map to be dreamed with.
        var memory = new EffectMemory();
        var journal = new DreamJournal();
        var dream = new DreamSleepExtension(memory, journal);
        var dayOne = new LearnedWorld(new OpaqueWorld(RestoreServiceDomain.NewWorld()), memory);
        dream.BeforeRun(new TickContext { Agent = "t", World = dayOne, Task = RestoreServiceDomain.Task, Tick = 0, MaxTicks = 10, Notes = [], Trace = [] });
        foreach (var id in new[] { "inspect-logs", "identify-component", "apply-patch", "run-tests", "deploy", "resolve-alert" }) dayOne.Act(id);
        dream.Sleep(new SleepContext { Agent = "t", World = dayOne, Task = RestoreServiceDomain.Task, LastTick = 6, GoalReached = true, Notes = [], Trace = [] });
        Assert.Equal(1, journal.DreamedRoutes);
        Assert.Contains("apply-patch", journal.Prefer);

        var toy = RestoreServiceDomain.NewWorld();
        toy.Revoke("apply-patch");
        var dayTwo = new LearnedWorld(new OpaqueWorld(toy), memory);
        dream.BeforeRun(new TickContext { Agent = "t", World = dayTwo, Task = RestoreServiceDomain.Task, Tick = 0, MaxTicks = 10, Notes = [], Trace = [] });
        dream.Sleep(new SleepContext { Agent = "t", World = dayTwo, Task = RestoreServiceDomain.Task, LastTick = 0, GoalReached = false, Notes = [], Trace = [] });
        Assert.Equal(1, journal.DreamedRoutes);
        Assert.Empty(journal.Prefer);
        Assert.Equal(0, toy.Ticks);
    }
}
