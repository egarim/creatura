using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Creatura.Ratings;

/// <summary>One seat on a leaderboard: its rating, how many games it played, and how those went pair by pair.</summary>
public sealed record Entry(string Seat, Rating Rating, int Games, int Wins, int Ties, int Losses)
{
    public bool Unmet => Games == 0;
}

/// <summary>Two seats adjacent on a leaderboard that the games do not tell apart yet.</summary>
public sealed record OpenPair(string Higher, string Lower, double Probability, int GamesTogether);

/// <summary>A leaderboard for one axis: the seats by ordinal, and whether the order is settled (docs/design-ratings.md).</summary>
public sealed record Board(Axis Axis, IReadOnlyList<Entry> Entries, int Games, IReadOnlyList<OpenPair> Open)
{
    /// <summary>The adjacent pairs the games tell apart at this probability or better.</summary>
    public const double SettledAt = 0.95;

    public bool Settled => Open.Count == 0;

    /// <summary>The seats with games, in order.</summary>
    public IReadOnlyList<Entry> Rated => Entries.Where(e => !e.Unmet).ToList();
}

public static class Leaderboard
{
    /// <summary>The leaderboard of one axis from runs: every game played in order, the ratings read off at the end.</summary>
    public static Board Compute(IEnumerable<Run> runs, Axis axis)
    {
        var all = runs.ToList();
        var games = Contest.Games(all, axis);
        var ratings = new Dictionary<string, Rating>(StringComparer.Ordinal);
        var played = new Dictionary<string, (int Games, int Wins, int Ties, int Losses)>(StringComparer.Ordinal);
        foreach (var seat in all.Where(r => r.Seats.ContainsKey(axis)).Select(r => r.Seats[axis]).Distinct(StringComparer.Ordinal))
        {
            ratings[seat] = Rating.Prior;
            played[seat] = (0, 0, 0, 0);
        }

        foreach (var game in games)
        {
            var before = game.Players.Select(p => (ratings[p.Seat], p.Rank)).ToList();
            var after = WengLin.Update(before);
            for (var i = 0; i < game.Players.Count; i++)
            {
                var (seat, rank) = game.Players[i];
                ratings[seat] = after[i];
                var (g, w, t, l) = played[seat];
                foreach (var (other, otherRank) in game.Players)
                {
                    if (other == seat) continue;
                    if (rank < otherRank) w++;
                    else if (rank == otherRank) t++;
                    else l++;
                }

                played[seat] = (g + 1, w, t, l);
            }
        }

        var entries = ratings
            .Select(kv => new Entry(kv.Key, kv.Value, played[kv.Key].Games, played[kv.Key].Wins, played[kv.Key].Ties, played[kv.Key].Losses))
            .OrderByDescending(e => e.Games > 0)
            .ThenByDescending(e => e.Rating.Ordinal)
            .ThenBy(e => e.Seat, StringComparer.Ordinal)
            .ToList();
        var open = new List<OpenPair>();
        var rated = entries.Where(e => !e.Unmet).ToList();
        for (var i = 0; i + 1 < rated.Count; i++)
        {
            var p = WengLin.Probability(rated[i].Rating, rated[i + 1].Rating);
            if (p < Board.SettledAt) open.Add(new OpenPair(rated[i].Seat, rated[i + 1].Seat, p, GamesTogether(games, rated[i].Seat, rated[i + 1].Seat)));
        }

        return new Board(axis, entries, games.Count, open);
    }

    private static int GamesTogether(IReadOnlyList<Game> games, string a, string b) =>
        games.Count(g => g.Players.Any(p => p.Seat == a) && g.Players.Any(p => p.Seat == b));

    /// <summary>The boards of every axis that has a rated seat.</summary>
    public static IReadOnlyList<Board> All(IEnumerable<Run> runs)
    {
        var all = runs.ToList();
        return Enum.GetValues<Axis>().Select(axis => Compute(all, axis)).Where(b => b.Entries.Count > 0).ToList();
    }

    /// <summary>The board for a person: a table, then the verdict.</summary>
    public static string Render(Board board)
    {
        var ic = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        var noun = board.Axis switch { Axis.Model => "models", Axis.Design => "designs", _ => "prompts" };
        sb.AppendLine(string.Create(ic, $"{noun}: {board.Rated.Count} rated over {board.Games} game{(board.Games == 1 ? "" : "s")}{(board.Entries.Count > board.Rated.Count ? $", {board.Entries.Count - board.Rated.Count} unmet" : "")}"));
        sb.AppendLine($"  {"#",-3}{"seat",-32}{"ordinal",9}{"mu",8}{"sigma",8}{"games",7}  w-t-l");
        var place = 0;
        foreach (var e in board.Entries)
        {
            var mark = e.Unmet ? "-" : (++place).ToString(ic);
            sb.AppendLine(string.Create(ic, $"  {mark,-3}{Cut(e.Seat, 31),-32}{e.Rating.Ordinal,9:0.00}{e.Rating.Mu,8:0.00}{e.Rating.Sigma,8:0.00}{e.Games,7}  {e.Wins}-{e.Ties}-{e.Losses}{(e.Unmet ? "  (met nobody on a shared job: the prior)" : "")}"));
        }

        if (board.Rated.Count < 2) sb.AppendLine("  nothing to compare: fewer than two seats met on a shared job");
        else if (board.Settled) sb.AppendLine(string.Create(ic, $"  the order is settled: every adjacent pair is told apart with p >= {Board.SettledAt:0.00}; more runs would not change it"));
        else
        {
            sb.AppendLine(string.Create(ic, $"  the order is open: {board.Open.Count} adjacent pair{(board.Open.Count == 1 ? "" : "s")} not told apart (p < {Board.SettledAt:0.00}); more runs are needed between"));
            foreach (var pair in board.Open) sb.AppendLine(string.Create(ic, $"    {pair.Higher} and {pair.Lower}: p = {pair.Probability:0.00} after {pair.GamesTogether} game{(pair.GamesTogether == 1 ? "" : "s")} together"));
        }

        return sb.ToString();
    }

    public static JsonObject ToJson(Board board) => new()
    {
        ["axis"] = board.Axis.ToString().ToLowerInvariant(),
        ["games"] = board.Games,
        ["settled"] = board.Rated.Count >= 2 && board.Settled,
        ["entries"] = new JsonArray(board.Entries.Select(e => (JsonNode)new JsonObject
        {
            ["seat"] = e.Seat, ["mu"] = e.Rating.Mu, ["sigma"] = e.Rating.Sigma, ["ordinal"] = e.Rating.Ordinal,
            ["games"] = e.Games, ["wins"] = e.Wins, ["ties"] = e.Ties, ["losses"] = e.Losses,
        }).ToArray()),
        ["open"] = new JsonArray(board.Open.Select(p => (JsonNode)new JsonObject { ["higher"] = p.Higher, ["lower"] = p.Lower, ["probability"] = p.Probability, ["gamesTogether"] = p.GamesTogether }).ToArray()),
    };

    public static string ToJson(IEnumerable<Board> boards) =>
        new JsonObject { ["boards"] = new JsonArray(boards.Select(b => (JsonNode)ToJson(b)).ToArray()) }.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";
}
