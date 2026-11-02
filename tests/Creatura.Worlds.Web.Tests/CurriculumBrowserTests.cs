using Creatura.Ratings;
using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>G11: a matrix run with --barriers next runs the picked barrier and the report says why (docs/design-ratings.md, R3).</summary>
[Trait("Category", "Browser")]
[Collection("browser")]
public class CurriculumBrowserTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    [BrowserFact]
    public async Task TheMatrixRunsTheBarrierTheCurriculumPicksAndSaysWhy()
    {
        var dir = Path.Combine(Path.GetTempPath(), "creatura-curriculum-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var adapter = demo.App.Adapter();
            var spec = new UxMatrixSpec { Tasks = ["remember-milk"], Personas = ["hurried"], Environments = ["desktop"] };
            // Two earlier reports: the script loses under the lie and wins under none.
            var lied = await UxMatrix.RunAsync(demo.Browser, adapter, spec with { Barriers = ["save-lies"] }, new UxSeats());
            var clean = await UxMatrix.RunAsync(demo.Browser, adapter, spec, new UxSeats());
            Assert.False(lied.Cells.Single().DataDone);
            Assert.True(clean.Cells.Single().DataDone);
            lied.Write(Path.Combine(dir, "lied"));
            clean.Write(Path.Combine(dir, "clean"));
            Assert.Null(clean.BarrierPick);

            var rated = Sources.Read([dir]);
            Assert.Equal(2, rated.Count);
            var ratings = Curriculum.Ratings(rated);
            Assert.True(ratings["opponent:ux|notes-demo/remember-milk/desktop/save-lies"].Rating.Mu > ratings["seat:script:demo"].Rating.Mu);
            Assert.True(ratings["opponent:ux|notes-demo/remember-milk/desktop/"].Rating.Mu < ratings["seat:script:demo"].Rating.Mu);

            // The pick: the least known outcome, an unmet barrier at the prior; the matrix runs it and says why.
            var picked = UxMatrix.Pick(adapter, spec with { BarriersNext = true, Rated = rated })!.Value;
            var expected = Curriculum.Next(ratings, "script:demo", new[] { "" }.Concat(adapter.Barriers.Select(b => b.Id)).Select(b => $"ux|notes-demo/remember-milk/desktop/{b}"))!;
            var expectedId = expected.Opponent[(expected.Opponent.LastIndexOf('/') + 1)..];
            Assert.Equal(expectedId.Length == 0 ? [] : [expectedId], picked.Barriers);
            Assert.Contains(adapter.Barriers, b => b.Id == expectedId);
            Assert.Contains("nearest the seat's level", picked.Why);

            var next = await UxMatrix.RunAsync(demo.Browser, adapter, spec with { BarriersNext = true, Rated = rated }, new UxSeats());
            Assert.Equal(picked.Barriers, next.Barriers);
            Assert.Equal(picked.Why, next.BarrierPick);
            Assert.Equal(picked.Barriers, next.Cells.Single().Barriers);
            next.Write(Path.Combine(dir, "next"));
            Assert.Equal(picked.Why, UxReport.Load(Path.Combine(dir, "next")).BarrierPick);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
        }
    }
}
