namespace Creatura.Ratings;

/// <summary>What is rated: the model in the seat, the design in the loop, or the prompt (a persona) the seat was given.</summary>
public enum Axis
{
    Model,
    Design,
    Prompt,
}

/// <summary>
/// One run as the ratings read it (docs/design-ratings.md): where it came from, the job it ran (everything about it but
/// the seats), when, which seat sat on each axis, and how it did: <see cref="Primary"/> is the job's own order of
/// outcomes (higher is better) and <see cref="Secondary"/> breaks ties (lower is better: ticks, seconds).
/// </summary>
public sealed record Run(string Source, string Job, DateTimeOffset At, IReadOnlyDictionary<Axis, string> Seats, double Primary, double Secondary)
{
    /// <summary>The contest this run belongs to when <paramref name="axis"/> is rated: the job with the other axes held fixed; null when the run has no seat on that axis.</summary>
    public string? ContestKey(Axis axis)
    {
        if (!Seats.ContainsKey(axis)) return null;
        var others = Seats.Where(kv => kv.Key != axis).OrderBy(kv => kv.Key).Select(kv => $"{kv.Key.ToString().ToLowerInvariant()}={kv.Value}");
        return $"{Source}|{Job}|{string.Join("|", others)}";
    }
}

/// <summary>One game: several seats ranked on one job (1 is best; equal ranks are a tie), at the time of its earliest run.</summary>
public sealed record Game(string Contest, int Round, DateTimeOffset At, IReadOnlyList<(string Seat, int Rank)> Players);

/// <summary>Games from runs: the runs of one job, seat by seat, paired round by round (docs/design-ratings.md, "a contest").</summary>
public static class Contest
{
    /// <summary>
    /// The games for <paramref name="axis"/>, in the order they are played: by the earliest run's time, then by contest and
    /// round, so the same runs in any order give the same games. Round k holds the k-th run of every seat that has one;
    /// a round with one seat is no game.
    /// </summary>
    public static IReadOnlyList<Game> Games(IEnumerable<Run> runs, Axis axis)
    {
        var games = new List<Game>();
        var contests = runs.Select(r => (Key: r.ContestKey(axis), Run: r)).Where(x => x.Key is not null).GroupBy(x => x.Key!, StringComparer.Ordinal);
        foreach (var contest in contests)
        {
            var bySeat = contest
                .GroupBy(x => x.Run.Seats[axis], StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Select(x => x.Run).OrderBy(r => r.At).ThenBy(r => r.Secondary).ThenByDescending(r => r.Primary).ToList(), StringComparer.Ordinal);
            var rounds = bySeat.Values.Max(list => list.Count);
            for (var round = 0; round < rounds; round++)
            {
                var entrants = bySeat.Where(kv => kv.Value.Count > round).Select(kv => (Seat: kv.Key, Run: kv.Value[round])).OrderBy(e => e.Seat, StringComparer.Ordinal).ToList();
                if (entrants.Count < 2) continue;
                var ordered = entrants.OrderByDescending(e => e.Run.Primary).ThenBy(e => e.Run.Secondary).ThenBy(e => e.Seat, StringComparer.Ordinal).ToList();
                var players = new List<(string Seat, int Rank)>();
                var rank = 0;
                for (var i = 0; i < ordered.Count; i++)
                {
                    if (i == 0 || ordered[i].Run.Primary != ordered[i - 1].Run.Primary || ordered[i].Run.Secondary != ordered[i - 1].Run.Secondary) rank = i + 1;
                    players.Add((ordered[i].Seat, rank));
                }

                games.Add(new Game(contest.Key, round + 1, entrants.Min(e => e.Run.At), players));
            }
        }

        return games.OrderBy(g => g.At).ThenBy(g => g.Contest, StringComparer.Ordinal).ThenBy(g => g.Round).ToList();
    }
}
