using Creatura.Bench;
using Creatura.Worlds;

namespace Creatura.Tests;

/// <summary>The fine-tune seam (migration step 14, fourth commit): one example per judged action, the class at sleep, no row changed.</summary>
public class BenchExamplesTests
{
    [Fact]
    public async Task TheRecorderWritesOneExamplePerActionAndTheClassAtSleepAndChangesNoRow()
    {
        var sink = new InMemoryExampleSink();
        var toy = WorldCatalog.Restore;
        var lies = toy.Create(BarrierSet.Parse(toy, "patchlies"));
        var recorded = await AgentCatalog.Make("bach", new PlannerPolicy(), toy.Task, AgentOptions.With(new ExampleRecorder(sink))).RunAsync(lies, 30);
        var plain = await AgentCatalog.Make("bach", new PlannerPolicy(), toy.Task).RunAsync(toy.Create(BarrierSet.Parse(toy, "patchlies")), 30);

        // Row for row the same; the only note that moves is the first, which names every loaded extension.
        GoldenTraceTests.AssertSame(plain with { Notes = plain.Notes.Skip(1).ToList() }, recorded with { Notes = recorded.Notes.Skip(1).ToList() }, "bach with the recorder");
        Assert.Equal("policy bfs; extensions: examples", recorded.Notes[0]);
        var acted = recorded.Trace.Count(e => e.Level == "cell" && e.Kind is TraceKind.Acted or TraceKind.Blocked);
        Assert.Equal(acted, sink.Examples.Count);
        Assert.Contains(sink.Examples, e => e.Verdict == ActionVerdict.Lied && e.Suggestion == "apply-patch");
        Assert.All(sink.Examples, e => Assert.Contains("facts:", e.Context));
        Assert.All(sink.Examples, e => Assert.Contains("self-model:", e.Context));
        Assert.All(sink.Examples, e => Assert.Equal("Bach agent#1", e.Run));
        Assert.Equal(OutcomeClass.Real, sink.OutcomeOf("Bach agent#1"));
        Assert.DoesNotContain(sink.Examples, e => e.Context.Contains("prompt", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task TheJsonlSinkAppendsExampleAndCloseLines()
    {
        var path = Path.Combine(Path.GetTempPath(), "creatura-examples-" + Guid.NewGuid().ToString("N"), "examples.jsonl");
        try
        {
            using (var sink = new JsonlExampleSink(path))
            {
                var recorder = new ExampleRecorder(sink);
                var options = AgentOptions.With(recorder);
                await AgentCatalog.Make("levin", new PlannerPolicy(), WorldCatalog.Permit.Task, options).RunAsync(WorldCatalog.Permit.NewWorld(WorldCatalog.Permit.Set("none")), 30);
                await AgentCatalog.Make("levin", new PlannerPolicy(), WorldCatalog.Permit.Task, options).RunAsync(WorldCatalog.Permit.NewWorld(WorldCatalog.Permit.Set("all")), 30);
                Assert.Equal("Levin colony#2", recorder.CurrentRun);
            }

            var lines = File.ReadAllLines(path);
            Assert.Equal(2, lines.Count(l => l.Contains("\"Close\":", StringComparison.Ordinal)));
            Assert.True(lines.Length > 2);
            Assert.Contains(lines, l => l.Contains("\"Run\":\"Levin colony#1\"", StringComparison.Ordinal));
            Assert.Contains(lines, l => l.Contains("\"Outcome\":\"real\"", StringComparison.Ordinal));
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
