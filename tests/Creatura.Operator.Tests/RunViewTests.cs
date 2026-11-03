using System.Text;
using System.Text.Json;
using Creatura.Operator;
using Creatura.Scenarios;

namespace Creatura.Operator.Tests;

/// <summary>The Run view over the convoy replays (docs/design-console.md, slice U1): U1.1 to U1.4.</summary>
public class RunViewTests
{
    private static async Task<string> Replay(ConvoyArm arm, int seed)
    {
        var text = new StringWriter();
        await ConvoyScenario.Create(arm, seed).RunAsync(new JsonlReplayWriter(text));
        return text.ToString();
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "creatura-console-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>U1.1: a replay file read whole and the same run followed live show the same ticks, in the same order.</summary>
    [Fact]
    public async Task U1_1_ReadWholeAndFollowedLiveShowTheSameRows()
    {
        // Live: the run in this process, watched through the observer, with its file written beside it.
        var file = new StringWriter();
        var live = new LiveRun(new RunModel());
        await ConvoyScenario.Create(ConvoyArm.E, 0).RunAsync(new Tee(live, new JsonlReplayWriter(file)));
        var text = file.ToString();
        var whole = RunModel.Read(new StringReader(text));

        Assert.True(whole.Ended);
        Assert.NotEmpty(whole.Rows);
        Assert.Equal(whole.Rows, live.Model.Rows);
        Assert.Equal(whole.Outcome, live.Model.Outcome);
        Assert.Equal(whole.Cells, live.Model.Cells);

        // Followed: the file grows in uneven pieces, a line and a character split between polls, and is polled between them.
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "run.jsonl");
            var follower = new ReplayFollower(path, new RunModel());
            Assert.Equal(0, follower.Poll());                       // no file yet: nothing, not an error
            var bytes = Encoding.UTF8.GetBytes(text);
            var cuts = new List<int> { 0 };
            for (var at = 1; at < bytes.Length; at += 997) cuts.Add(at);
            cuts.Add(bytes.Length);
            var ticksSeen = new List<int>();
            for (var i = 0; i + 1 < cuts.Count; i++)
            {
                using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    stream.Write(bytes, cuts[i], cuts[i + 1] - cuts[i]);
                follower.Poll();
                ticksSeen.Add(follower.Model.Tick);
            }

            Assert.Equal(whole.Rows, follower.Model.Rows);
            Assert.Equal(whole.Outcome, follower.Model.Outcome);
            Assert.True(follower.Model.Ended);
            Assert.Equal(ticksSeen.OrderBy(t => t), ticksSeen);    // the ticks only ever went forward
            Assert.True(ticksSeen.Distinct().Count() > 5);          // and were seen arriving, not all at once

            // A new run over the same name starts the model again.
            File.WriteAllText(path, text[..text.IndexOf('\n')] + "\n");
            follower.Poll();
            Assert.True(follower.Restarted);
            Assert.Empty(follower.Model.Rows);
            Assert.NotNull(follower.Model.Header);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>U1.2: every event kind the format defines today renders; an unknown property, kind or line type is ignored, not an error.</summary>
    [Fact]
    public void U1_2_EveryKindRendersAndTheUnknownIsIgnored()
    {
        var lines = new List<string>
        {
            """{"type":"header","format":"creatura.replay/1","scenario":"convoy","seed":3,"arm":"Z","armText":"a test arm","maxTicks":9,"future":{"a":1},"trucks":[{"name":"Truck 1","cell":"","design":"plain","capacity":6,"fuelCapacity":10,"start":"depot"}],"nodes":[],"roads":[],"faults":[]}""",
            """{"type":"comment","text":"a line type no reader knows yet"}""",
            "not json at all",
            "",
        };
        var events = RunModel.KnownKinds.Select(k => $$"""{"kind":"{{k}}","text":"a {{k}} happened","truck":"Truck 1","road":"r01","from":"depot","to":"alba","village":"alba","shiny":true}""");
        lines.Add($$"""{"type":"tick","tick":1,"trucks":[{"name":"Truck 1","at":"alba","fuel":7,"fuelBelieved":9,"load":2,"state":"acting","claim":"alba","mood":"new"}],"needs":{},"closed":[],"events":[{{string.Join(",", events)}}],"weather":"rain"}""");
        lines.Add("""{"type":"tick","tick":2,"events":[{"kind":"teleport","text":"a kind no reader knows yet","truck":"Truck 1"},{"kind":"closure","text":"the road closes"},{"kind":"drive","text":"drove","truck":"Truck 1","offer":"drive r01 or wait","decision":"drive: the road is open"}]}""");
        lines.Add("""{"type":"end","measures":{"delivered":3,"allServed":false},"outcome":["Not every village was served."],"extra":[1,2]}""");

        var model = RunModel.Read(new StringReader(string.Join("\n", lines)));

        Assert.Equal(lines.Count(l => l.Length > 0), model.Lines);
        Assert.Equal(2, model.Skipped);                                  // the unknown type and the line that is not JSON
        Assert.Equal(RunModel.KnownKinds.Count + 3, model.Rows.Count);
        foreach (var kind in RunModel.KnownKinds)
        {
            var row = Assert.Single(model.Rows, r => r.Kind == kind && r.Tick == 1);
            Assert.StartsWith(kind, row.Act);
            Assert.Equal($"a {kind} happened", row.Answer);
            Assert.Equal("at depot, gauge 10, load 0", row.Observation);  // what it saw before the tick: the header's start
        }

        var unknown = Assert.Single(model.Rows, r => r.Kind == "teleport");
        Assert.Equal("teleport", unknown.Act);
        Assert.Equal("at alba, gauge 9, load 2, holds alba", unknown.Observation);  // the gauge, not the tank
        Assert.Equal(RunModel.World, Assert.Single(model.Rows, r => r.Kind == "closure" && r.Tick == 2).Cell);
        var decided = Assert.Single(model.Rows, r => r.Tick == 2 && r.Kind == "drive");
        Assert.Equal(("drive r01 or wait", "drive: the road is open"), (decided.Offer, decided.Decision));
        Assert.Equal(["Truck 1", RunModel.World], model.Cells);
        Assert.True(model.Ended);
        Assert.Contains(new KeyValuePair<string, string>("delivered", "3"), model.Measures);

        var text = RunText.Render(model);
        Assert.Contains("convoy, arm Z (a test arm), seed 3: tick 2 of 9, ended", text);
        Assert.Contains("decision: drive: the road is open", text);
        Assert.Contains("Not every village was served.", text);
        Assert.Equal("waiting for the run's first line", RunText.Title(new RunModel()));
    }

    /// <summary>U1.3: a blocked row, a refused action and a flag are marked; counted against the replay itself.</summary>
    [Fact]
    public async Task U1_3_BlockedRefusedAndFlaggedRowsAreMarkedAndCounted()
    {
        // Arm E, seed 0: refused actions and lies told and caught. No arm writes a blocked road today, so a tick adds one.
        var text = await Replay(ConvoyArm.E, 0);
        text += """{"type":"tick","tick":99,"events":[{"kind":"blocked","text":"Truck 2 finds the road closed and stays.","truck":"Truck 2","road":"r03"},{"kind":"unload","text":"unloaded, it says","truck":"Truck 2","verdict":"Lied"}]}""" + "\n";
        var model = RunModel.Read(new StringReader(text));

        // The replay's own events, counted apart from the model.
        int blocked = 0, refused = 0, flagged = 0;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.GetProperty("type").GetString() != "tick" || !doc.RootElement.TryGetProperty("events", out var evs)) continue;
            foreach (var e in evs.EnumerateArray())
            {
                var kind = e.GetProperty("kind").GetString();
                var lied = e.TryGetProperty("verdict", out var v) && v.GetString() == "Lied";
                if (kind == "blocked") blocked++;
                else if (kind == "refused") refused++;
                else if (kind is "lie" or "caught" || lied) flagged++;
            }
        }

        Assert.True(refused > 10 && flagged > 2);
        Assert.Equal(1, blocked);
        Assert.Equal(blocked, model.Count(RowMark.Blocked));
        Assert.Equal(refused, model.Count(RowMark.Refused));
        Assert.Equal(flagged, model.Count(RowMark.Flagged));
        Assert.All(model.Rows.Where(r => r.Kind is "lie" or "caught"), r => Assert.Equal(RowMark.Flagged, r.Mark));
        Assert.Equal(RowMark.Flagged, model.Rows[^1].Mark);                  // a landed-looking act whose verdict is a lie

        var text2 = RunText.Render(model);
        Assert.Equal(blocked, CountOf(text2, "  BLOCKED"));
        Assert.Equal(refused, CountOf(text2, "  REFUSED"));
        Assert.Equal(flagged, CountOf(text2, "  FLAG"));
        var perCell = model.Cells.Sum(c => model.Count(RowMark.Refused, c));
        Assert.Equal(refused, perCell);
    }

    /// <summary>U1.4: nothing is written to disk by opening a replay, whole or followed, as text or by the program.</summary>
    [Fact]
    public async Task U1_4_OpeningAReplayWritesNothing()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "run.jsonl");
            await File.WriteAllTextAsync(path, await Replay(ConvoyArm.A, 1));
            var before = Snapshot(dir);

            var model = RunModel.ReadFile(path);
            new ReplayFollower(path, new RunModel()).Poll();
            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(0, ConsoleProgram.Run(["run", path, "--print"], output, error, redirected: false));
            Assert.Equal(0, ConsoleProgram.Run(["run", path, "--cell", "Truck 2"], output, error, redirected: true));

            // Read while a run still holds the file open for writing.
            using (new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                Assert.Equal(model.Rows, RunModel.ReadFile(path).Rows);

            Assert.Equal(before, Snapshot(dir));
            Assert.Contains("every cell: " + model.Rows.Count + " rows", output.ToString());
            Assert.Contains("cell Truck 2: ", output.ToString());
            Assert.Equal("", error.ToString());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheProgramSaysWhatIsWrong()
    {
        var output = new StringWriter();
        var error = new StringWriter();
        Assert.Equal(2, ConsoleProgram.Run([], output, error, redirected: true));
        Assert.Contains("creatura-console run <replay.jsonl | folder>", output.ToString());
        Assert.Equal(3, ConsoleProgram.Run(["run", "/no/such/replay.jsonl"], output, error, redirected: true));
        Assert.Contains("no replay at", error.ToString());
        Assert.Equal(2, ConsoleProgram.Run(["run", "x.jsonl", "--nope"], output, error, redirected: true));
        Assert.Contains("unknown argument '--nope'", error.ToString());
        Assert.Equal(3, ConsoleProgram.Run(["convert", Path.GetTempPath()], output, error, redirected: true));
        Assert.Contains("name a two-creatures record", error.ToString());
        Assert.Equal(3, ConsoleProgram.Run(["run", Path.GetTempPath()], output, error, redirected: true));
        Assert.Contains("is neither a two-creatures record", error.ToString());
    }

    private static int CountOf(string text, string what) =>
        text.Split('\n').Count(l => l.TrimEnd('\r').EndsWith(what, StringComparison.Ordinal));

    private static string Snapshot(string dir) => string.Join("\n", Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.TopDirectoryOnly)
        .Order(StringComparer.Ordinal)
        .Select(p => File.Exists(p)
            ? $"{Path.GetFileName(p)} {new FileInfo(p).Length} {File.GetLastWriteTimeUtc(p):O} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(p)))}"
            : $"{Path.GetFileName(p)}/ {Directory.GetLastWriteTimeUtc(p):O}"));

    /// <summary>Hands the run to two observers: the live view and the file.</summary>
    private sealed class Tee(IScenarioObserver a, IScenarioObserver b) : IScenarioObserver
    {
        public void Start(ReplayHeader header)
        {
            a.Start(header);
            b.Start(header);
        }

        public void Tick(ReplayTick tick)
        {
            a.Tick(tick);
            b.Tick(tick);
        }

        public void End(ReplaySummary summary)
        {
            a.End(summary);
            b.End(summary);
        }
    }
}
