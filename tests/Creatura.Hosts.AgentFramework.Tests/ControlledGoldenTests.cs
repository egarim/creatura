using Creatura;

namespace Creatura.Hosts.AgentFramework.Tests;

/// <summary>
/// The four golden traces of the Agent Framework host: <see cref="ControlledAgent"/> run offline (the planner in the
/// model's seat) over the four-barrier toy suite. Recorded in the research repository at its migration step 0, kept
/// there when the other goldens moved at step 11, and moved here byte for byte with the host at step 16. They live
/// in this project's own golden folder, not beside Creatura.Tests' 112, because rebuilding them needs the package
/// that project must never reference. Same file format, same comparison, same switch (<c>CREATURA_RECORD_GOLDENS</c>),
/// same rule: any drift is a named decision in the commit that re-records the file (see tests/Creatura.Tests/golden/README.md).
/// </summary>
public class ControlledGoldenTests
{
    /// <summary>Set to <c>all</c> or to a comma-separated list of golden names to (re)record instead of compare.</summary>
    public const string RecordVariable = "CREATURA_RECORD_GOLDENS";

    public static readonly string[] Barriers = ["clean", "patchrevoked", "patchlies", "deployrevoked"];

    public static TheoryData<string> Goldens
    {
        get
        {
            var data = new TheoryData<string>();
            var names = Barriers.Select(b => $"restore-{b}-controlled").ToList();
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

    /// <summary>tests/Creatura.Hosts.AgentFramework.Tests/golden, found from the solution file so the theory runs from any build output folder and records into the source tree.</summary>
    public static string Directory_ { get; } = Locate();

    private static string Locate()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Creatura.slnx"))) dir = dir.Parent;
        if (dir is null) throw new DirectoryNotFoundException("Creatura.slnx not found above " + AppContext.BaseDirectory);
        return Path.Combine(dir.FullName, "tests", "Creatura.Hosts.AgentFramework.Tests", "golden");
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
        var actual = await RunAsync(name);

        if (Recording(name))
        {
            actual.WriteJsonl(path);
            AssertSame(actual, AgentRun.ReadJsonl(path), name);
            return;
        }

        Assert.True(File.Exists(path), $"no golden recorded for {name}; record it on purpose with {RecordVariable}={name} (see tests/Creatura.Tests/golden/README.md)");
        AssertSame(AgentRun.ReadJsonl(path), actual, name);
    }

    private static async Task<AgentRun> RunAsync(string name)
    {
        var parts = name.Split('-');
        if (parts.Length != 3 || parts[0] != "restore" || parts[2] != "controlled") throw new ArgumentException($"'{name}' is not restore-barriers-controlled");
        var toy = RestoreServiceDomain.NewWorld();
        switch (parts[1])
        {
            case "clean": break;
            case "patchrevoked": toy.Revoke("apply-patch"); break;
            case "patchlies": toy.MakeLiar("apply-patch"); break;
            case "deployrevoked": toy.Revoke("deploy"); break;
            default: throw new ArgumentException($"'{name}': restore barriers are clean, patchrevoked, patchlies, deployrevoked");
        }

        return await ControlledAgent.Offline(RestoreServiceDomain.Task).RunAsync(toy, 30);
    }

    /// <summary>Row for row and note for note; the first difference is the message. The same comparison as Creatura.Tests' <c>GoldenTraceTests</c>.</summary>
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
