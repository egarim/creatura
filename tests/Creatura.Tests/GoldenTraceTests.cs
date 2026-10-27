using Creatura.Worlds;
using Creatura.Worlds.Samples;

namespace Creatura.Tests;

/// <summary>
/// The golden traces: migration step 0 of the research repository's design-framework.md, moved here with the
/// worlds at step 11. Every file under tests/golden is one run of one agent in one world with the planner in the
/// seat, recorded before the first refactor of the loops (or, for the orienting agent, fresh at step 8). The theory
/// rebuilds the scenario named by the file from the <see cref="AgentCatalog"/> and the <see cref="WorldCatalog"/>,
/// runs it, and compares row by row; the first row that moved is the failure message. Any drift is a named
/// decision in the commit that re-records the file (see tests/golden/README.md).
/// </summary>
public class GoldenTraceTests
{
    /// <summary>Set to <c>all</c> or to a comma-separated list of golden names to (re)record instead of compare.</summary>
    public const string RecordVariable = "CREATURA_RECORD_GOLDENS";

    public static TheoryData<string> Goldens
    {
        get
        {
            var data = new TheoryData<string>();
            var names = GoldenScenarios.Names.ToList();
            // A file nobody can rebuild is a failure too, not a silent orphan.
            if (Directory.Exists(Directory_))
            {
                foreach (var f in Directory.GetFiles(Directory_, "*.jsonl").Select(Path.GetFileNameWithoutExtension).Order(StringComparer.Ordinal))
                {
                    if (f is not null && !names.Contains(f)) names.Add(f);
                }
            }

            foreach (var n in names) data.Add(n);
            return data;
        }
    }

    /// <summary>tests/Creatura.Tests/golden, found from the solution file so the theory runs from any build output folder and records into the source tree.</summary>
    public static string Directory_ { get; } = Locate();

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Creatura.slnx"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("Creatura.slnx not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "tests", "Creatura.Tests", "golden");
    }

    private static bool Recording(string name)
    {
        var value = Environment.GetEnvironmentVariable(RecordVariable);
        if (string.IsNullOrWhiteSpace(value)) return false;
        return value.Trim() == "all" || value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Contains(name, StringComparer.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Goldens))]
    public async Task TheRunMatchesItsGolden(string name)
    {
        var path = Path.Combine(Directory_, name + ".jsonl");
        var actual = await GoldenScenarios.RunAsync(name);

        if (Recording(name))
        {
            actual.WriteJsonl(path);
            // The file must read back as what was run, or the golden is worthless from the start.
            AssertSame(actual, AgentRun.ReadJsonl(path), name);
            return;
        }

        Assert.True(File.Exists(path), $"no golden recorded for {name}; record it on purpose with {RecordVariable}={name} (see tests/golden/README.md)");
        AssertSame(AgentRun.ReadJsonl(path), actual, name);
    }

    /// <summary>Row for row and note for note; the first difference is the message.</summary>
    internal static void AssertSame(AgentRun expected, AgentRun actual, string name)
    {
        Assert.True(expected.Agent == actual.Agent, $"{name}: agent '{expected.Agent}' became '{actual.Agent}'");
        for (var i = 0; i < Math.Max(expected.Trace.Count, actual.Trace.Count); i++)
        {
            var e = i < expected.Trace.Count ? expected.Trace[i] : null;
            var a = i < actual.Trace.Count ? actual.Trace[i] : null;
            if (Equals(e, a)) continue;
            Assert.Fail($"{name}: trace row {i + 1} of {expected.Trace.Count} (golden) / {actual.Trace.Count} (now) differs" + Environment.NewLine +
                        $"  golden: {Show(e)}" + Environment.NewLine +
                        $"  now:    {Show(a)}");
        }

        for (var i = 0; i < Math.Max(expected.Notes.Count, actual.Notes.Count); i++)
        {
            var e = i < expected.Notes.Count ? expected.Notes[i] : null;
            var a = i < actual.Notes.Count ? actual.Notes[i] : null;
            if (e == a) continue;
            Assert.Fail($"{name}: note {i + 1} of {expected.Notes.Count} (golden) / {actual.Notes.Count} (now) differs" + Environment.NewLine +
                        $"  golden: {e ?? "<none>"}" + Environment.NewLine +
                        $"  now:    {a ?? "<none>"}");
        }

        Assert.True(expected.GoalReached == actual.GoalReached, $"{name}: goalReached {expected.GoalReached} became {actual.GoalReached}");
        Assert.True(expected.Ticks == actual.Ticks, $"{name}: ticks {expected.Ticks} became {actual.Ticks}");
    }

    private static string Show(TraceEvent? e) =>
        e is null ? "<none>" : $"{e.Tick} {e.Level} {e.SetPoint} stress={e.Stress:R} freedom={e.Freedom:R} {e.Action} {e.Kind} {e.Detail.Replace(Environment.NewLine, " / ")}";
}

/// <summary>
/// The scenarios behind the golden files. A file name is <c>world-barriers-agent[-extensions]</c>: no token
/// holds a dash; extensions are joined with <c>+</c> and a <c>:</c> in an extension name is written <c>_</c>
/// (so <c>explore:scaled</c> is <c>explore_scaled</c>), as the battery runner writes its call logs. Everything
/// here is offline and deterministic: the planner (bfs) sits in every seat, and no run draws on a random
/// source (the only seeded agent, the random walker, is not in the set), so the opaque learned world has no
/// seed to fix. Row order is the agents' own; details that embed line breaks use the platform newline, so the
/// goldens are recorded and compared on one platform (macOS/Linux, LF).
///
/// Ticks: 30 everywhere, as the offline tests use, except the opaque learned world (120, the battery's series
/// length, since exploring a world without the map takes longer).
///
/// The four <c>restore-*-controlled</c> goldens (the Agent Framework host, offline) stay in the research
/// repository: the host needs a package the Core never references.
/// </summary>
internal static class GoldenScenarios
{
    private static readonly string[] Designs = ["levin", "bach", "hybrid", "peterson", "adversary"];

    public static IReadOnlyList<string> Names { get; } = Build();

    private static List<string> Build()
    {
        var names = new List<string>();
        // The four-barrier toy suite (Core/RestoreServiceDomain.cs) for the five agents the Core builds.
        foreach (var b in new[] { "clean", "patchrevoked", "patchlies", "deployrevoked" })
        {
            foreach (var a in Designs) names.Add($"restore-{b}-{a}");
        }

        // The three demo worlds and the flood world, barriers off and on, the careless control beside the designs; then with honest loaded.
        foreach (var w in new[] { "permit", "irrigation", "livestock", "flood" })
        {
            foreach (var b in new[] { "none", "all" })
            {
                foreach (var a in Designs.Append("careless")) names.Add($"{w}-{b}-{a}");
            }

            foreach (var a in new[] { "levin", "bach", "hybrid", "peterson" }) names.Add($"{w}-all-{a}-honest");
        }

        // LearnedWorld(OpaqueWorld(PermitOffice All)) with honest and scaled exploration, for Levin and Bach.
        names.Add("permit.opaque-all-levin-honest+explore_scaled");
        names.Add("permit.opaque-all-bach-honest+explore_scaled");
        // The hybrid with a carried record over two LicenceWorld cases: case 2 is run after case 1 with the same record.
        names.Add("licence-case1-hybrid-record");
        names.Add("licence-case2-hybrid-record");
        // LiarWorld over the permit office for the map-keeper: the lie-then-honest-failure path of refuted claim 2.
        names.Add("permit.liar-all-peterson");
        // Where the hybrid's stuck-regulation alarm fires (see the README for how it was found).
        names.Add(StuckRegulationGolden);
        // The orienting agent and its behaviourist control (migration step 8): recorded fresh, owed to no earlier loop.
        foreach (var b in new[] { "clean", "patchrevoked", "patchlies", "deployrevoked" })
        {
            names.Add($"restore-{b}-orienting");
            names.Add($"restore-{b}-behaviourist");
        }

        foreach (var w in new[] { "permit", "flood" })
        {
            foreach (var b in new[] { "none", "all" })
            {
                names.Add($"{w}-{b}-orienting");
                names.Add($"{w}-{b}-behaviourist");
            }

            names.Add($"{w}-all-orienting-honest");
            names.Add($"{w}.liar-all-orienting");
        }

        // Worlds whose tasks state no valence table: the table is inferred from the catalogue.
        names.Add("irrigation-all-orienting");
        names.Add("livestock-all-orienting");
        return names.Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// A scenario where the hybrid hands off with "stuck regulation" within its ticks. Found on 2026-10-27 by
    /// running the hybrid over every barrier subset of the four demo worlds, plain, under LiarWorld and under
    /// the opaque learned world, with and without honest: the alarm never fires on a plain world or on the toy
    /// suite; it fires on the flood world under LiarWorld (tick 6, with or without the ferry lying) and on the
    /// livestock world under the opaque learned world. This one is the shortest.
    /// </summary>
    public const string StuckRegulationGolden = "flood.liar-all-hybrid";

    public static async Task<AgentRun> RunAsync(string name)
    {
        var parts = name.Split('-');
        if (parts.Length is < 3 or > 4) throw new ArgumentException($"'{name}' is not world-barriers-agent[-extensions]");
        var (world, barriers, agent) = (parts[0], parts[1], parts[2]);
        var extensions = parts.Length == 4 ? parts[3].Split('+') : [];

        if (world == "licence")
        {
            // The record is the same instance across the cases; case n replays the cases before it.
            var @case = barriers switch { "case1" => 1, "case2" => 2, _ => throw new ArgumentException($"'{name}': licence cases are case1 and case2") };
            var options = Options(extensions, new EffectMemory());
            AgentRun run = null!;
            for (var i = 1; i <= @case; i++) run = await AgentCatalog.Make(agent, new PlannerPolicy(), LicenceWorld.Task, options).RunAsync(new LicenceWorld(), 30);
            return run;
        }

        if (world == "restore")
        {
            // The toy's barriers are one at a time: "clean" is none, otherwise the one named.
            var toy = WorldCatalog.Restore;
            var set = barriers switch
            {
                "clean" => BarrierSet.None(toy),
                "patchrevoked" or "patchlies" or "deployrevoked" => BarrierSet.Parse(toy, barriers),
                _ => throw new ArgumentException($"'{name}': restore barriers are clean, patchrevoked, patchlies, deployrevoked"),
            };
            return await AgentCatalog.Make(agent, new PlannerPolicy(), toy.Task, Options(extensions, new EffectMemory())).RunAsync(toy.Create(set), 30);
        }

        var modifier = world.Contains('.') ? world[(world.IndexOf('.') + 1)..] : "";
        var manifest = WorldCatalog.Find(world.Split('.')[0]);
        var inner = manifest.Create(BarriersFor(manifest, barriers, name));
        var learned = new EffectMemory();
        var ticks = 30;
        IWorld target = modifier switch
        {
            "" => inner,
            "liar" => new LiarWorld(inner),
            "opaque" => Opaque(inner, learned, ref ticks),
            _ => throw new ArgumentException($"'{name}': world modifiers are liar and opaque"),
        };
        return await AgentCatalog.Make(agent, new PlannerPolicy(), manifest.Task, Options(extensions, learned)).RunAsync(target, ticks);
    }

    private static IWorld Opaque(IWorld inner, EffectMemory memory, ref int ticks)
    {
        ticks = 120;
        return new LearnedWorld(new OpaqueWorld(inner), memory);
    }

    /// <summary>As the battery runner reads --barriers: none keeps only the shortcut button, as on the demo page; all turns every barrier on.</summary>
    private static BarrierSet BarriersFor(WorldManifest manifest, string set, string name) => set switch
    {
        "none" => BarrierSet.None(manifest),
        "all" => BarrierSet.All(manifest),
        _ => throw new ArgumentException($"'{name}': barrier sets are none and all"),
    };

    /// <summary>The switchboard, through the catalog; a file name writes <c>explore:scaled</c> as <c>explore_scaled</c>, and the exploration extension shares the learned world's memory.</summary>
    private static AgentOptions Options(IReadOnlyList<string> names, EffectMemory memory) =>
        ExtensionCatalog.Parse(names.Select(n => n.Replace('_', ':')), memory);
}
