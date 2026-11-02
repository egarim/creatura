using Creatura.Bench;
using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>Persona memory and the design axis, without a browser.</summary>
public class MemoryTests
{
    private static readonly UxEnvironment Phone = new("phone", 390, 844, Mobile: true);

    [Fact]
    public void ALessonIsWrittenFromTheVerdictsAlone()
    {
        Assert.Equal("\"remember-milk\" on your phone: it worked, in 2 steps.", UxMemory.Lesson(ReportTests.Cell("p", done: true), Phone));
        Assert.Equal("\"remember-milk\" on your phone: the app said it was saved, but it was not kept; you noticed.",
            UxMemory.Lesson(ReportTests.Cell("p", done: false, pageSaid: true, lies: 1), Phone));
        Assert.Equal("\"remember-milk\" on your phone: the app said it was saved, but it was not kept; you did not notice.",
            UxMemory.Lesson(ReportTests.Cell("p", done: false, pageSaid: true), Phone));
        Assert.Equal("\"remember-milk\" on your computer: you thought you were done, but it had not worked.",
            UxMemory.Lesson(ReportTests.Cell("p", done: false, believed: 3), new UxEnvironment("desktop", 1280, 800)));
    }

    [Fact]
    public void CarryKeepsTheNewestLessonsAndReadKeepsNothing()
    {
        var store = new InMemoryRecordStore();
        var carry = new UxMemory(store, UxMemoryMode.Carry);
        var key = UxMemory.Key("hurried", "script:demo", "bach");
        Assert.Equal("hurried-script-demo-bach", key);
        for (var i = 1; i <= UxMemory.MaxLessons + 2; i++) carry.Remember(key, ReportTests.Cell("hurried", done: true, ticks: i), Phone);
        var lessons = carry.Lessons(key);
        Assert.Equal(UxMemory.MaxLessons, lessons.Count);
        Assert.Contains("in 3 steps", lessons[0]);       // the two oldest are gone
        Assert.Contains("in 10 steps", lessons[^1]);

        var read = new UxMemory(store, UxMemoryMode.Read);
        read.Remember(key, ReportTests.Cell("hurried", done: false), Phone);
        Assert.Equal(lessons, read.Lessons(key));
        Assert.IsType<ReadOnlyRecordStore>(read.ForCell());
        Assert.Same(store, carry.ForCell());
    }

    [Fact]
    public void ThePersonaIsShownWhatItRemembersBeforeTheScreen()
    {
        var context = new PolicyContext
        {
            Facts = new HashSet<string>(), Tools = [], Goal = _ => false, GoalText = "x", Tick = 1, MaxTicks = 5, Freedom = 0.5,
        };
        var user = PersonaPolicy.User(context, ObservationTests.Screen(), memories: ["\"remember-milk\" on your phone: the app said it was saved, but it was not kept; you noticed."]);
        Assert.StartsWith("WHAT YOU REMEMBER FROM USING THIS APP BEFORE", user);
        Assert.True(user.IndexOf("not kept", StringComparison.Ordinal) < user.IndexOf("SCREEN:", StringComparison.Ordinal));
        Assert.DoesNotContain("REMEMBER", PersonaPolicy.User(context, ObservationTests.Screen()));
    }

    [Fact]
    public void TheDesignIsAnAxisOfTheMatrixAndPartOfTheKey()
    {
        var adapter = DemoFiles.Adapter();
        var cells = UxMatrix.Cells(adapter, new UxMatrixSpec { Tasks = ["remember-milk"], Personas = ["hurried"], Environments = ["phone"], Designs = ["plain", "bach", "levin"] });
        Assert.Equal(["plain", "bach", "levin"], cells.Select(c => c.Design));
        Assert.Throws<ArgumentException>(() => UxMatrix.Cells(adapter, new UxMatrixSpec { Designs = ["nobody"] }));
        Assert.EndsWith("|bach", (ReportTests.Cell("p", true) with { Design = "bach" }).Key);
        Assert.True(UxDesigns.Known("plain"));
        Assert.Equal("Bach agent", UxDesigns.Make("bach", new PlannerPolicy(), WebTask.For(adapter.Task(null))).Name);
    }

    [Fact]
    public void TheReportComparesDesignsOnTheSameCells()
    {
        var report = ReportTests.Report(
            ReportTests.Cell("a", false, pageSaid: true) with { Design = "plain" },
            ReportTests.Cell("b", false, pageSaid: true) with { Design = "plain" },
            ReportTests.Cell("a", false, pageSaid: true, lies: 1, ticks: 5) with { Design = "bach" },
            ReportTests.Cell("b", false, pageSaid: true, lies: 1, ticks: 5) with { Design = "bach" });
        var byDesign = report.ByDesign();
        Assert.Equal(["plain", "bach"], byDesign.Select(d => d.Design));
        Assert.Equal((2, 0), (byDesign[0].UncaughtLies, byDesign[0].LiesCaught));
        Assert.Equal((0, 2), (byDesign[1].UncaughtLies, byDesign[1].LiesCaught));
        Assert.Contains("| bach | 0 of 2 | 0 | 2 | 0 | 2 |", report.ToMarkdown());
    }
}

/// <summary>Memory and designs over the demo, in a real browser.</summary>
[Trait("Category", "Browser")]
[Collection("browser")]
public class MemoryBrowserTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    [BrowserFact]
    public async Task APersonaCarriesItsRecordAndLessonsCellAfterCell()
    {
        var store = new InMemoryRecordStore();
        var spec = new UxMatrixSpec { Tasks = ["remember-milk"], Personas = ["hurried"], Designs = ["bach"], Memory = new UxMemory(store, UxMemoryMode.Carry) };
        var first = await UxMatrix.RunAsync(demo.Browser, demo.App.Adapter(), spec, new UxSeats());
        Assert.Equal([0, 1], first.Cells.Select(c => c.CasesBefore));          // phone, then desktop: the second remembers the first
        Assert.Empty(first.Cells[0].Remembered);
        Assert.Single(first.Cells[1].Remembered);
        var key = UxMemory.Key("hurried", "script:demo", "bach");
        Assert.All(first.Cells, c => Assert.Equal(key, c.Memory));

        var record = store.Load(key)!;
        Assert.Equal(2, record.Cases);
        Assert.NotEmpty(record.Skills);                                        // Bach's self-model carried what it learned of the controls
        Assert.Equal(2, record.GetPart<List<string>>(UxMemory.LessonsPart)!.Count);

        var again = await UxMatrix.RunAsync(demo.Browser, demo.App.Adapter(), spec, new UxSeats());
        Assert.Equal([2, 3], again.Cells.Select(c => c.CasesBefore));
        Assert.Contains("remembered 2 earlier cells", again.Cells[0].Summary());
    }

    [BrowserFact]
    public async Task AReadMemoryStartsEveryCellFromTheSameRecordAndSavesNothing()
    {
        var store = new InMemoryRecordStore();
        var adapter = demo.App.Adapter();
        var spec = new UxMatrixSpec { Tasks = ["remember-milk"], Personas = ["careful"], Environments = ["phone"], Designs = ["bach"] };
        await UxMatrix.RunAsync(demo.Browser, adapter, spec with { Memory = new UxMemory(store, UxMemoryMode.Carry) }, new UxSeats());
        var key = UxMemory.Key("careful", "script:demo", "bach");
        var frozen = store.Load(key)!.ToJson();

        var read = await UxMatrix.RunAsync(demo.Browser, adapter, spec with { Environments = [], Memory = new UxMemory(store, UxMemoryMode.Read) }, new UxSeats());
        Assert.Equal([1, 1], read.Cells.Select(c => c.CasesBefore));
        Assert.Equal(frozen, store.Load(key)!.ToJson());
        Assert.Equal(UxMemoryMode.Read, read.Memory);
    }

    [BrowserFact]
    public async Task UnderTheLieTheDesignsAreComparedOnTheSameCells()
    {
        var spec = new UxMatrixSpec { Tasks = ["remember-milk"], Personas = ["hurried"], Environments = ["desktop"], Designs = ["plain", "bach", "peterson", "orienting"], Barriers = ["save-lies"] };
        var report = await UxMatrix.RunAsync(demo.Browser, demo.App.Adapter(), spec, new UxSeats());
        Assert.Equal(["plain", "bach", "peterson", "orienting"], report.Cells.Select(c => c.Design));
        Assert.Equal(["plain loop", "Bach agent", "Peterson map-keeper", "orienting agent"], report.Cells.Select(c => c.Agent));
        // One lie told in each cell, and each design blocks it at once, in its own words; counted by the world's truth and the
        // trace's blocked row, not by the words (the bench's word list does not know the orienting agent's "punishment").
        Assert.All(report.Cells, c => Assert.Equal((false, 1, 1), (c.DataDone, c.LiesTold, c.LiesCaught)));
        Assert.All(report.ByDesign(), d => Assert.Equal(0, d.UncaughtLies));
        var orienting = report.Cells.Single(c => c.Design == "orienting").Run!.Trace;
        Assert.Equal(0, Creatura.Bench.Measures.LiesCaught(orienting));
        Assert.Contains(orienting, e => e.Kind == TraceKind.Blocked && e.Detail.Contains("punishment", StringComparison.Ordinal));
    }
}
