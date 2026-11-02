using System.Text.Json.Nodes;
using Creatura.Bench;
using Creatura.Cli;
using Creatura.Ratings;

namespace Creatura.Tests;

/// <summary>Ratings (docs/design-ratings.md, slice R1): G1 to G5.</summary>
public class RatingsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 2, 10, 0, 0, TimeSpan.Zero);

    private static Run Run(string job, string model, string design, double primary, double secondary = 0, int minute = 0, string? prompt = null, string source = "bench")
    {
        var seats = new Dictionary<Axis, string> { [Axis.Model] = model, [Axis.Design] = design };
        if (prompt is not null) seats[Axis.Prompt] = prompt;
        return new Run(source, job, T0.AddMinutes(minute), seats, primary, secondary);
    }

    /// <summary>G1: the update behaves, and a worked example matches the published formulas.</summary>
    [Fact]
    public void G1_TheWengLinUpdateBehavesAndMatchesTheFormulas()
    {
        var prior = Rating.Prior;
        var after = WengLin.Update([(prior, 1), (prior, 2)]);
        // c = √(2σ² + 2β²) = 13.17616; Ω = σ²/c · ½ = 2.63518; γ = σ/c = 0.632456; Δ = γ σ²/c² · ¼ = 0.0632456; σ' = σ √(1 − Δ) = 8.06551.
        Assert.Equal(27.63518, after[0].Mu, 4);
        Assert.Equal(22.36482, after[1].Mu, 4);
        Assert.Equal(8.06551, after[0].Sigma, 4);
        Assert.Equal(after[0].Sigma, after[1].Sigma, 9);
        Assert.Equal(after[0].Mu - prior.Mu, prior.Mu - after[1].Mu, 9);         // the winner rises by what the loser falls
        Assert.True(after[0].Sigma < prior.Sigma);

        var tie = WengLin.Update([(prior, 1), (prior, 1)]);
        Assert.Equal(prior.Mu, tie[0].Mu, 9);
        Assert.Equal(prior.Mu, tie[1].Mu, 9);
        Assert.True(tie[0].Sigma < prior.Sigma);                                   // a tie says something: both are alike

        // σ never grows, over many games of every result between three seats.
        var ratings = new List<Rating> { prior, prior, prior };
        var random = new Random(7);
        for (var game = 0; game < 200; game++)
        {
            var ranks = Enumerable.Range(0, 3).Select(_ => random.Next(1, 4)).ToList();
            var next = WengLin.Update(ratings.Select((r, i) => (r, ranks[i])).ToList());
            for (var i = 0; i < 3; i++) Assert.True(next[i].Sigma <= ratings[i].Sigma + 1e-12);
            ratings = next.ToList();
        }

        Assert.Equal(0.5, WengLin.Probability(prior, prior), 9);
        Assert.True(WengLin.Probability(new Rating(35, 2), new Rating(15, 2)) > 0.9);
    }

    /// <summary>G2: a contest holds one job with the other axes fixed; an unmet seat keeps the prior and is marked.</summary>
    [Fact]
    public void G2_AContestIsOneJobWithTheOtherAxesFixedAndAnUnmetSeatKeepsThePrior()
    {
        var runs = new[]
        {
            Run("w1", "a", "plain", 3, minute: 0), Run("w1", "b", "plain", 1, minute: 1),    // a and b met on w1 under plain
            Run("w2", "a", "plain", 1, minute: 2),                                            // a alone on w2: no game
            Run("w1", "a", "bach", 3, minute: 3),                                             // a under bach: a different job for the model axis
            Run("w1", "c", "orienting", 3, minute: 4),                                        // c met nobody
        };
        var models = Contest.Games(runs, Axis.Model);
        var game = Assert.Single(models);
        Assert.Equal("bench|w1|design=plain", game.Contest);
        Assert.Equal([("a", 1), ("b", 2)], game.Players);

        // Rating the designs holds the model fixed: on w1 with model a, plain and bach each have one run, a tie at 3.
        var designs = Contest.Games(runs, Axis.Design);
        var dg = Assert.Single(designs);
        Assert.Equal("bench|w1|model=a", dg.Contest);
        Assert.Equal([("bach", 1), ("plain", 1)], dg.Players);
        Assert.Empty(Contest.Games(runs, Axis.Prompt));                                    // nobody has a prompt

        var board = Leaderboard.Compute(runs, Axis.Model);
        var c = Assert.Single(board.Entries, e => e.Seat == "c");
        Assert.True(c.Unmet);
        Assert.Equal(Rating.Prior, c.Rating);
        Assert.Contains("met nobody", Leaderboard.Render(board));
        Assert.Equal(["a", "b", "c"], board.Entries.Select(e => e.Seat));                 // rated first, the unmet last
    }

    /// <summary>G3: a sweep settles the pair; alternating wins leave it open, named, with games together.</summary>
    [Fact]
    public void G3_ASweepSettlesThePairAndAlternatingWinsLeaveItOpen()
    {
        var sweep = Enumerable.Range(0, 40).SelectMany(i => new[] { Run("w", "strong", "plain", 3, minute: i * 2), Run("w", "weak", "plain", 0, minute: i * 2 + 1) }).ToList();
        var board = Leaderboard.Compute(sweep, Axis.Model);
        Assert.Equal(["strong", "weak"], board.Entries.Select(e => e.Seat));
        Assert.True(board.Settled);
        Assert.Equal(40, board.Games);
        Assert.Equal((40, 0, 0), (board.Entries[0].Wins, board.Entries[0].Ties, board.Entries[0].Losses));
        Assert.Contains("the order is settled", Leaderboard.Render(board));

        var even = Enumerable.Range(0, 12).SelectMany(i => new[] { Run("w", "x", "plain", i % 2 == 0 ? 3 : 0, minute: i * 2), Run("w", "y", "plain", i % 2 == 0 ? 0 : 3, minute: i * 2 + 1) }).ToList();
        var open = Leaderboard.Compute(even, Axis.Model);
        Assert.False(open.Settled);
        var pair = Assert.Single(open.Open);
        Assert.Equal(12, pair.GamesTogether);
        Assert.True(pair.Probability < 0.95);
        var text = Leaderboard.Render(open);
        Assert.Contains("the order is open", text);
        Assert.Contains("more runs are needed between", text);
        Assert.Contains("after 12 games together", text);
    }

    /// <summary>G4: the same rows in any order, or split across files, give the same leaderboard.</summary>
    [Fact]
    public void G4_TheOrderOfTheRowsDoesNotMatter()
    {
        var runs = new List<Run>();
        var random = new Random(3);
        for (var i = 0; i < 40; i++) runs.Add(Run(i % 3 == 0 ? "w1" : "w2", new[] { "a", "b", "c" }[i % 3], i % 2 == 0 ? "plain" : "bach", random.Next(0, 4), random.Next(1, 30), minute: i));
        var one = Leaderboard.Compute(runs, Axis.Model);
        var shuffled = runs.OrderBy(_ => random.Next()).ToList();
        var two = Leaderboard.Compute(shuffled, Axis.Model);
        Assert.Equal(one.Entries.Select(e => (e.Seat, e.Rating.Mu, e.Rating.Sigma, e.Games)), two.Entries.Select(e => (e.Seat, e.Rating.Mu, e.Rating.Sigma, e.Games)));
        Assert.Equal(Leaderboard.Render(one), Leaderboard.Render(two));
    }

    /// <summary>G5: the command reads the three sources, prints the boards, and --json carries the same numbers.</summary>
    [Fact]
    public void G5_TheCommandReadsTheThreeSourcesAndPrintsTheBoards()
    {
        var dir = Path.Combine(Path.GetTempPath(), "creatura-rate-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "bench"));
        Directory.CreateDirectory(Path.Combine(dir, "ux"));
        Directory.CreateDirectory(Path.Combine(dir, "pairs", "run-1"));
        Directory.CreateDirectory(Path.Combine(dir, "pairs", "run-2"));
        try
        {
            // The bench, in both CSV shapes: the model seat deepseek beats bfs twice on one world under plain.
            Row Bench(string seat, OutcomeClass outcome, int ticks, int minute, string world = "restore") => new()
            {
                At = T0.AddMinutes(minute), Battery = "b3", Test = "T1", Seat = seat, World = world, Agent = "plain", Arm = "A", Outcome = outcome, Ticks = ticks,
            };
            var scoreboard = Path.Combine(dir, "bench", "scoreboard.csv");
            foreach (var row in new[] { Bench("deepseek-chat", OutcomeClass.Real, 5, 0), Bench("bfs", OutcomeClass.HandedOff, 9, 1) }) LegacyCsv.Append(scoreboard, LegacyShape.Scoreboard, row);
            var results = Path.Combine(dir, "bench", "results.csv");
            foreach (var row in new[] { Bench("deepseek-chat", OutcomeClass.Real, 6, 2), Bench("bfs", OutcomeClass.Claimed, 4, 3) }) LegacyCsv.Append(results, LegacyShape.BatteryResults, row);

            // A matrix: two testers and two designs on one task; plain done, bach done the same, script:demo done, model:x said done and was not.
            File.WriteAllText(Path.Combine(dir, "ux", "report.json"), """
                {"app":"notes-demo","at":"2026-11-02T10:30:00+00:00","design":"plain, bach","barriers":["save-lies"],"cells":[
                  {"task":"remember-milk","persona":"hurried","environment":"phone","tester":"script:demo","design":"plain","dataDone":true,"uncaughtLie":false,"falseDone":false,"ticks":2},
                  {"task":"remember-milk","persona":"hurried","environment":"phone","tester":"model:x","design":"plain","dataDone":false,"uncaughtLie":true,"falseDone":true,"ticks":4},
                  {"task":"remember-milk","persona":"hurried","environment":"phone","tester":"script:demo","design":"bach","dataDone":true,"uncaughtLie":false,"falseDone":false,"ticks":3},
                  {"task":"remember-milk","persona":"careful","environment":"phone","tester":"script:demo","design":"plain","dataDone":true,"uncaughtLie":false,"falseDone":false,"ticks":5},
                  {"task":"remember-milk","persona":"hurried","environment":"phone","tester":"model:x","design":"bach","dataDone":false,"error":"the app did not open","ticks":0}
                ]}
                """);

            // Two pairs on the same arm with roles: one 5/5 early, one 5/5 late with a false claim.
            File.WriteAllText(Path.Combine(dir, "pairs", "run-1", "summary.json"), """{"run":"2026-10-31T14-38-20-DD-LM-R","arm":"DD-LM","agents":["deepseek","deepseek"],"channel":"LM","roles":true,"score":5,"shipped":true,"firstShipSeconds":488,"falseClaims":0}""");
            File.WriteAllText(Path.Combine(dir, "pairs", "run-2", "summary.json"), """{"run":"2026-10-31T13-53-10-DD-M-R","arm":"DD-M","agents":["deepseek","deepseek"],"channel":"M","roles":true,"score":5,"shipped":true,"firstShipSeconds":1522,"falseClaims":0}""");

            var runs = Sources.Read([dir]);
            Assert.Equal(4, runs.Count(r => r.Source == "bench"));
            Assert.Equal(4, runs.Count(r => r.Source == "ux"));                     // the cell that did not run is no run
            Assert.Equal(2, runs.Count(r => r.Source == "two-creatures"));
            Assert.Equal("channel-LM", runs.First(r => r.Job == "DD-roles" && r.Secondary == 488).Seats[Axis.Design]);

            var output = new StringWriter();
            var error = new StringWriter();
            Assert.Equal(0, RateCommand.Run(["rate", dir], output, error));
            var text = output.ToString();
            Assert.Equal("", error.ToString());
            Assert.Contains("10 runs (4 bench, 2 two-creatures, 4 ux)", text);
            Assert.Contains("models:", text);
            Assert.Contains("designs:", text);
            Assert.Contains("prompts:", text);
            Assert.Contains("deepseek-chat", text);
            Assert.Contains("channel-LM", text);
            var modelLines = text.Split('\n').SkipWhile(l => !l.StartsWith("models:", StringComparison.Ordinal)).Skip(2).TakeWhile(l => l.StartsWith("  ", StringComparison.Ordinal) && !l.Contains("the order", StringComparison.Ordinal)).ToList();
            Assert.StartsWith("  1  deepseek-chat", modelLines[0]);                 // two wins over bfs on the bench

            output = new StringWriter();
            Assert.Equal(0, RateCommand.Run(["rate", dir, "--by", "model", "--json"], output, error));
            var json = JsonNode.Parse(output.ToString())!;
            var board = Assert.Single(json["boards"]!.AsArray());
            Assert.Equal("model", (string?)board!["axis"]);
            var first = board["entries"]![0]!;
            Assert.Equal("deepseek-chat", (string?)first["seat"]);
            var ordinal = (double)first["ordinal"]!;
            Assert.Contains(ordinal.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture), text);

            Assert.Equal(2, RateCommand.Run(["rate"], new StringWriter(), new StringWriter()));
            Assert.Equal(1, RateCommand.Run(["rate", Path.Combine(dir, "nothing-here")], new StringWriter(), new StringWriter()));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheRunIdGivesTheTime()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 31, 14, 38, 20, TimeSpan.Zero), Sources.ParseRunId("2026-10-31T14-38-20-DD-LM-R"));
        Assert.Equal(DateTimeOffset.UnixEpoch, Sources.ParseRunId("nope"));
    }
}
