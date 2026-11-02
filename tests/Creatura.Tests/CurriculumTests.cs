using Creatura.Ratings;

namespace Creatura.Tests;

/// <summary>The barrier curriculum (docs/design-ratings.md, slice R3): G9 and G10, no browser.</summary>
public class CurriculumTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 12, 0, 0, TimeSpan.Zero);

    private static Run Ux(string barriers, string tester, bool done, int minute) =>
        new(Sources.Ux, $"notes-demo/remember-milk/desktop/{barriers}", T0.AddMinutes(minute),
            new Dictionary<Axis, string> { [Axis.Model] = tester, [Axis.Design] = "plain", [Axis.Prompt] = "hurried" }, done ? 3 : 0, 2);

    /// <summary>G9: a barrier's rating rises when seats lose under it and falls when they win, from the same games that rate the seats.</summary>
    [Fact]
    public void G9_ABarrierRisesWhenSeatsLoseUnderItAndFallsWhenTheyWin()
    {
        var runs = new[]
        {
            Ux("", "script:demo", done: true, 0),            // the seat beats "none"
            Ux("save-lies", "script:demo", done: false, 1),  // the lie beats the seat
            Ux("save-lies", "model:x", done: false, 2),      // and beats another seat
            Ux("", "model:x", done: true, 3),
        };
        var ratings = Curriculum.Ratings(runs);
        var none = ratings["opponent:ux|notes-demo/remember-milk/desktop/"];
        var lie = ratings["opponent:ux|notes-demo/remember-milk/desktop/save-lies"];
        var seat = ratings["seat:script:demo"];
        Assert.True(lie.Rating.Mu > Rating.DefaultMu);
        Assert.True(none.Rating.Mu < Rating.DefaultMu);
        Assert.Equal((2, 2), (lie.Duels, lie.Wins));
        Assert.Equal((2, 0), (none.Duels, none.Wins));
        Assert.Equal((2, 1), (seat.Duels, seat.Wins));
        Assert.True(lie.Rating.Mu > seat.Rating.Mu && seat.Rating.Mu > none.Rating.Mu);

        // The duel is the same update R1 uses: one win between equals moves both by the worked example's amount.
        var one = Curriculum.Ratings([Ux("", "script:demo", true, 0)]);
        Assert.Equal(27.63518, one["seat:script:demo"].Rating.Mu, 4);
        Assert.Equal(22.36482, one["opponent:ux|notes-demo/remember-milk/desktop/"].Rating.Mu, 4);

        // Done, by source: the bench's Real, a matrix's data done, a pair's 5 of 5.
        Assert.True(Curriculum.Done(new Run(Sources.Bench, "b/t/w/A/none", T0, new Dictionary<Axis, string> { [Axis.Model] = "bfs" }, 3, 1)));
        Assert.False(Curriculum.Done(new Run(Sources.Bench, "b/t/w/A/none", T0, new Dictionary<Axis, string> { [Axis.Model] = "bfs" }, 2, 1)));
        Assert.False(Curriculum.Done(new Run(Sources.Pairs, "DD", T0, new Dictionary<Axis, string> { [Axis.Model] = "deepseek+deepseek" }, 4, 1)));
        Assert.Equal(("notes-demo/remember-milk/desktop", "save-lies"), (Curriculum.OpponentOf(runs[1]).Job, Curriculum.OpponentOf(runs[1]).Barriers));
        Assert.Equal("none", Curriculum.OpponentOf(runs[0]).Barriers);
    }

    /// <summary>G10: the next barrier picked for a seat is the one rated nearest the seat's, never one already settled far above or below it.</summary>
    [Fact]
    public void G10_TheNextBarrierIsTheNearestAndNeverASettledOne()
    {
        // Wins over "none" and losses to "save-lies" until both are settled (the seat is pulled both ways, so it takes
        // more duels than a sweep between two seats); "save-slow" met once and lost; "save-fails" unmet.
        var runs = new List<Run> { Ux("save-slow", "script:demo", done: false, 100000) }; // the last duel, after the sweeps
        string Key(string b) => $"ux|notes-demo/remember-milk/desktop/{b}";
        IReadOnlyDictionary<string, (Rating Rating, int Duels, int Wins)> ratings;
        var i = 0;
        do
        {
            runs.Add(Ux("", "script:demo", done: true, i * 3));
            runs.Add(Ux("save-lies", "script:demo", done: false, i * 3 + 1));
            i++;
            ratings = Curriculum.Ratings(runs);
        }
        while (i < 500 && (WengLin.Probability(ratings["seat:script:demo"].Rating, ratings[$"opponent:{Key("")}"].Rating) < 0.95
            || WengLin.Probability(ratings["seat:script:demo"].Rating, ratings[$"opponent:{Key("save-lies")}"].Rating) > 0.05));
        Assert.True(i < 500, "the duels never settled");
        var candidates = new[] { Key(""), Key("save-lies"), Key("save-slow"), Key("save-fails") };

        var pick = Curriculum.Next(ratings, "script:demo", candidates)!;
        Assert.NotEqual(Key(""), pick.Opponent);               // settled: the seat always wins it
        Assert.NotEqual(Key("save-lies"), pick.Opponent);      // settled: the seat always loses it
        Assert.Contains(pick.Opponent, new[] { Key("save-slow"), Key("save-fails") });
        Assert.True(pick.Probability > 0.05 && pick.Probability < 0.95);
        Assert.Contains("nearest the seat's level", pick.Why);
        var seatP = WengLin.Probability(ratings["seat:script:demo"].Rating, ratings[$"opponent:{Key("")}"].Rating);
        Assert.True(seatP >= 0.95);

        // The nearest of the open ones: whichever the seat beats with p closest to one half.
        var slow = WengLin.Probability(ratings["seat:script:demo"].Rating, ratings[$"opponent:{Key("save-slow")}"].Rating);
        var fails = WengLin.Probability(ratings["seat:script:demo"].Rating, Rating.Prior);
        Assert.Equal(Math.Abs(slow - 0.5) <= Math.Abs(fails - 0.5) ? Key("save-slow") : Key("save-fails"), pick.Opponent);

        // Only settled candidates left: the nearest is picked and the reason says so. No candidates: nothing.
        var settledOnly = Curriculum.Next(ratings, "script:demo", [Key(""), Key("save-lies")])!;
        Assert.Contains("every candidate is settled", settledOnly.Why);
        Assert.Null(Curriculum.Next(ratings, "script:demo", []));

        // An unknown seat sits at the prior: the unmet candidate, at the prior too, is exactly p = 0.5, the least known.
        var fresh = Curriculum.Next(ratings, "model:new", candidates)!;
        Assert.Equal(0.5, fresh.Probability, 9);
        Assert.Equal(Key("save-fails"), fresh.Opponent);
        Assert.Contains("seat", Curriculum.Render(ratings));
    }
}
