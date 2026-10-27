using Creatura.Bench;
using Creatura.Seats;
using Creatura.Worlds;

namespace Creatura.Tests;

/// <summary>
/// The seat seam (migration step 15, third commit): a SeatSpec resolves to the planner, a script or a catalogued model;
/// the resolver is what BenchOptions.Seats takes; the scripted seat replays a file and cannot route around the
/// architecture; the one factory builds the DeepSeek seat without a key in hand.
/// </summary>
public class SeatResolverTests
{
    private static ModelCatalog Stub => ModelCatalog.Parse("""{ "models": [ { "name": "stub", "provider": "openai", "baseUrl": "https://stub.example", "model": "stub", "keyEnv": "STUB_KEY", "nativeTools": false } ] }""", "stub", _ => null);

    [Fact]
    public void TheThreeKindsResolveAndAnUnknownNameIsRefusedWithTheCatalogsNames()
    {
        var handler = SeatStubHandler.OpenAi("""{"tool":null,"why":"ok"}""");
        var resolver = new SeatResolver(SeatFixtures.Client(handler), Stub);

        Assert.IsType<PlannerPolicy>(resolver.Resolve(SeatSpec.Planner));
        Assert.Null(resolver.LastModel);

        var model = Assert.IsType<ModelPolicy>(resolver.Resolve(new SeatSpec("model:stub")));
        Assert.Same(model, resolver.LastModel);
        Assert.Equal("stub", model.Name);
        Assert.Same(model.Endpoint, resolver.EndpointFor(new SeatSpec("stub")));
        Assert.IsType<ModelPolicy>(resolver.Resolve(new SeatSpec("stub")));
        Assert.Null(resolver.EndpointFor(SeatSpec.Planner));

        var refused = Assert.Throws<ArgumentException>(() => resolver.Resolve(new SeatSpec("model:nope")));
        Assert.Contains("no model 'nope' in the catalog (stub); known: stub", refused.Message);
        Assert.Throws<ArgumentException>(() => resolver.Resolve(new SeatSpec("scripted:/no/such/file")));

        Assert.Equal(SeatKind.Planner, SeatResolver.KindOf(SeatSpec.Planner));
        Assert.Equal(SeatKind.Scripted, SeatResolver.KindOf(new SeatSpec("scripted:x")));
        Assert.Equal(SeatKind.Model, SeatResolver.KindOf(new SeatSpec("deepseek-chat")));
    }

    [Fact]
    public void TheResolverIsWhatBenchOptionsSeatsTakesAndLogsWhereItIsTold()
    {
        var handler = SeatStubHandler.OpenAi("""{"tool":null,"why":"ok"}""");
        var dir = Path.Combine(Path.GetTempPath(), "creatura-seats-" + Guid.NewGuid().ToString("N"));
        try
        {
            var seen = new List<string>();
            var resolver = new SeatResolver(SeatFixtures.Client(handler), Stub, spec => Path.Combine(dir, spec.Name.Replace(':', '_') + ".jsonl"));
            resolver.Seated += (spec, policy) => seen.Add($"{spec.Name}->{policy.Name}");
            var bench = new BenchOptions { Seats = resolver.Seats };
            Assert.IsType<PlannerPolicy>(bench.SeatFor(SeatSpec.Planner));
            var model = Assert.IsType<ModelPolicy>(bench.SeatFor(new SeatSpec("model:stub")));
            Assert.Equal(Path.Combine(dir, "model_stub.jsonl"), model.Log.Path);
            Assert.Equal(["bfs->bfs", "model:stub->stub"], seen);
            model.Log.Dispose();
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheScriptedSeatReplaysItsFileAndIsRefusedWhereTheArchitectureHidesTheTool()
    {
        var path = Path.Combine(Path.GetTempPath(), "creatura-script-" + Guid.NewGuid().ToString("N") + ".txt");
        File.WriteAllLines(path, ["# the toy restore, by hand", "inspect-logs", "identify-component", "apply-config   # the shortcut: hidden by the design? no, visible", "-", "apply-patch", "run-tests", "deploy", "mark-resolved"]);
        try
        {
            var policy = new SeatResolver(catalog: Stub).Resolve(new SeatSpec("scripted:" + path));
            Assert.Equal("scripted:" + Path.GetFileName(path), policy.Name);
            var run = await AgentCatalog.Make("bach", policy, RestoreServiceDomain.Task).RunAsync(RestoreServiceDomain.NewWorld(), 20);
            var acted = run.Trace.Where(e => e.Level == "cell" && e.Kind == TraceKind.Acted).Select(e => e.Action).ToList();
            Assert.Equal("inspect-logs", acted[0]);
            Assert.Equal("identify-component", acted[1]);
            Assert.Equal(8, ((ScriptedPolicy)policy).Length);

            var hidden = new ScriptedPolicy(["apply-config"]);
            var context = SeatFixtures.Context();
            var refused = await hidden.NextAsync(context);
            Assert.Null(refused.Tool);
            Assert.StartsWith("proposed unavailable action 'apply-config'", refused.Rationale);
            Assert.Equal("script exhausted", (await hidden.NextAsync(context)).Rationale);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheOneFactoryBuildsTheDeepSeekSeatWithoutReadingAKey()
    {
        var catalog = ModelCatalog.Compile(_ => null);
        var seat = SeatFactory.DeepSeek(catalog: catalog);
        Assert.Equal("deepseek-chat", seat.Name);
        Assert.Equal("https://api.deepseek.com", seat.Endpoint.BaseUrl);
        Assert.Equal("DEEPSEEK_API_KEY|deepseek", seat.Endpoint.KeyEnv);
        Assert.Equal("tools", seat.Protocol);

        var reasoner = SeatFactory.DeepSeekEndpoint("deepseek-reasoner", catalog);
        Assert.True(reasoner.Reasoning);
        var fresh = SeatFactory.DeepSeekEndpoint("deepseek-v4-flash", catalog);
        Assert.Equal(("deepseek-v4-flash", "deepseek-v4-flash", "https://api.deepseek.com", false), (fresh.Name, fresh.Model, fresh.BaseUrl, fresh.Reasoning));

        var refused = Assert.Throws<ArgumentException>(() => SeatFactory.Model("gpt-4.1-mini", catalog: catalog));
        Assert.Contains("AZURE_AOAI_BASE is not set", refused.Message);
        Assert.Equal("SOME_KEY_THAT_IS_NOT_SET_0F3A", SeatFactory.MissingKey(seat.Endpoint with { KeyEnv = "SOME_KEY_THAT_IS_NOT_SET_0F3A" }));
        Assert.Null(SeatFactory.MissingKey(seat.Endpoint with { KeyEnv = null }));
    }
}

/// <summary>ExamOptions.CarryRecord (added with the seat seam at step 15): an exam may run an arm with exactly the extensions it names and nothing carried.</summary>
public class ExamCarryRecordTests
{
    private static Battery One(string name) => new("probe", "test", [new Hypothesis("X1", "nothing", "nothing", _ => HypothesisVerdict.Held)], [new Arm("plain", "bach", "restore"), new Arm("honest", "bach", "restore") { Extensions = ["honest"] }], Repeats: 1);

    [Fact]
    public async Task WithCarryRecordOffTheRowsNameOnlyTheArmsOwnExtensions()
    {
        var carried = await Exam.RunAsync(One("carried"), new InMemoryRecordStore(), "k");
        Assert.Equal(["record", "honest-failure+record"], carried.Rows.Select(r => r.Extensions));

        var bare = await Exam.RunAsync(One("bare"), new InMemoryRecordStore(), "k", new ExamOptions { CarryRecord = false });
        Assert.Equal(["none", "honest-failure"], bare.Rows.Select(r => r.Extensions));
        Assert.Equal(2, bare.SavesRefused); // the loop's own store is still the frozen one: one refused save per run
        Assert.All(bare.Rows, r => Assert.Equal(OutcomeClass.Real, r.Outcome));
    }
}
