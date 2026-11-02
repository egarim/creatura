using System.Globalization;

namespace Creatura.Ratings;

/// <summary>An opponent: a world or task under a set of barriers, as the seats meet it. Rated on the seats' scale.</summary>
public sealed record Opponent(string Key, string Job, string Barriers);

/// <summary>A seat's and the opponents' ratings from the duels, and the pick for the seat (docs/design-ratings.md, R3).</summary>
public sealed record CurriculumPick(string Seat, Rating SeatRating, string Opponent, Rating OpponentRating, double Probability, string Why);

/// <summary>
/// The barrier curriculum (docs/design-ratings.md, R3): worlds and barriers as opponents with ratings of their own, on
/// the same scale as the seats'. Every run is a duel between the seat (the model) and its opponent (the job with its
/// barriers): the seat wins when the run was done, the opponent when it was not; the Weng-Lin update moves both. So an
/// opponent rated above a seat usually beats it, one rated below is usually beaten, and the one rated nearest is the one
/// whose outcome is least known: the next barrier to run.
/// </summary>
public static class Curriculum
{
    /// <summary>The seats' and the opponents' ratings from every run, duel by duel in time order. Keys: seats as <c>seat:&lt;name&gt;</c>, opponents as <c>opponent:&lt;key&gt;</c>.</summary>
    public static IReadOnlyDictionary<string, (Rating Rating, int Duels, int Wins)> Ratings(IEnumerable<Run> runs, Axis axis = Axis.Model)
    {
        var table = new Dictionary<string, (Rating Rating, int Duels, int Wins)>(StringComparer.Ordinal);
        foreach (var run in runs.Where(r => r.Seats.ContainsKey(axis)).OrderBy(r => r.At).ThenBy(r => r.Job, StringComparer.Ordinal).ThenBy(r => r.Seats[axis], StringComparer.Ordinal))
        {
            var seat = "seat:" + run.Seats[axis];
            var opponent = "opponent:" + OpponentOf(run).Key;
            var s = table.GetValueOrDefault(seat, (Rating: Rating.Prior, Duels: 0, Wins: 0));
            var o = table.GetValueOrDefault(opponent, (Rating: Rating.Prior, Duels: 0, Wins: 0));
            var seatWon = Done(run);
            var after = WengLin.Update([(s.Rating, seatWon ? 1 : 2), (o.Rating, seatWon ? 2 : 1)]);
            table[seat] = (after[0], s.Duels + 1, s.Wins + (seatWon ? 1 : 0));
            table[opponent] = (after[1], o.Duels + 1, o.Wins + (seatWon ? 0 : 1));
        }

        return table;
    }

    /// <summary>A run is a win for the seat when the job was done: the bench's Real, a matrix's data done, a pair's 5 of 5.</summary>
    public static bool Done(Run run) => run.Source switch
    {
        Sources.Bench => run.Primary >= 3,
        Sources.Ux => run.Primary >= 2,
        Sources.Pairs => run.Primary >= 5,
        _ => run.Primary >= 2,
    };

    /// <summary>The opponent a run met: its job and the barriers in it (the last part of a matrix's job, after the environment).</summary>
    public static Opponent OpponentOf(Run run)
    {
        var parts = run.Job.Split('/');
        var barriers = run.Source == Sources.Ux && parts.Length >= 4 ? parts[^1] : "";
        var job = run.Source == Sources.Ux && parts.Length >= 4 ? string.Join("/", parts[..^1]) : run.Job;
        return new Opponent($"{run.Source}|{run.Job}", job, barriers.Length == 0 ? "none" : barriers);
    }

    /// <summary>
    /// The next opponent for a seat among <paramref name="candidates"/> (opponent keys): the one rated nearest the seat,
    /// so its outcome is the least known; one the seat is settled against (it wins or loses with p at or past
    /// <see cref="Board.SettledAt"/>) is passed over while another is left. An unmet candidate is at the prior. Null when
    /// there is no candidate.
    /// </summary>
    public static CurriculumPick? Next(IReadOnlyDictionary<string, (Rating Rating, int Duels, int Wins)> ratings, string seat, IEnumerable<string> candidates)
    {
        var s = ratings.GetValueOrDefault("seat:" + seat, (Rating: Rating.Prior, Duels: 0, Wins: 0)).Rating;
        var scored = candidates.Distinct(StringComparer.Ordinal)
            .Select(key => (Key: key, Rating: ratings.GetValueOrDefault("opponent:" + key, (Rating: Rating.Prior, Duels: 0, Wins: 0)).Rating))
            .Select(c => (c.Key, c.Rating, Probability: WengLin.Probability(s, c.Rating)))
            .OrderBy(c => Math.Abs(c.Probability - 0.5))
            .ThenBy(c => c.Key, StringComparer.Ordinal)
            .ToList();
        if (scored.Count == 0) return null;
        var open = scored.Where(c => c.Probability < Board.SettledAt && c.Probability > 1 - Board.SettledAt).ToList();
        var pick = open.Count > 0 ? open[0] : scored[0];
        var why = open.Count > 0
            ? string.Create(CultureInfo.InvariantCulture, $"nearest the seat's level: the seat wins it with p = {pick.Probability:0.00}, the least known outcome among {scored.Count} candidate{(scored.Count == 1 ? "" : "s")}")
            : string.Create(CultureInfo.InvariantCulture, $"every candidate is settled against this seat; the nearest is p = {pick.Probability:0.00}");
        return new CurriculumPick(seat, s, pick.Key, pick.Rating, pick.Probability, why);
    }

    /// <summary>The opponents' leaderboard, strongest first, for a person.</summary>
    public static string Render(IReadOnlyDictionary<string, (Rating Rating, int Duels, int Wins)> ratings)
    {
        var ic = CultureInfo.InvariantCulture;
        var lines = new List<string> { $"  {"kind",-9}{"name",-50}{"ordinal",9}{"mu",8}{"sigma",8}{"duels",7}{"wins",6}" };
        foreach (var (key, (rating, duels, wins)) in ratings.OrderByDescending(kv => kv.Value.Rating.Ordinal).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            var kind = key.StartsWith("seat:", StringComparison.Ordinal) ? "seat" : "opponent";
            var name = key[(key.IndexOf(':') + 1)..];
            lines.Add(string.Create(ic, $"  {kind,-9}{(name.Length <= 49 ? name : name[..48] + "…"),-50}{rating.Ordinal,9:0.00}{rating.Mu,8:0.00}{rating.Sigma,8:0.00}{duels,7}{wins,6}"));
        }

        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }
}
