namespace Creatura.Ratings;

/// <summary>A seat's rating: a Gaussian belief about its skill. <see cref="Ordinal"/> (μ − 3σ) is what a leaderboard sorts by: a conservative estimate.</summary>
public sealed record Rating(double Mu, double Sigma)
{
    public const double DefaultMu = 25.0;

    public const double DefaultSigma = 25.0 / 3.0;

    public static Rating Prior => new(DefaultMu, DefaultSigma);

    public double Ordinal => Mu - 3 * Sigma;
}

/// <summary>
/// The Weng-Lin update (Weng and Lin, 2011, "A Bayesian approximation method for online ranking"), in its Bradley-Terry
/// full-pairing form (docs/design-ratings.md): a game ranks any number of seats at once, ties included, and every pair
/// in it updates both sides. With c = √(σᵢ² + σⱼ² + 2β²), pᵢⱼ = e^{μᵢ/c} / (e^{μᵢ/c} + e^{μⱼ/c}) and sᵢⱼ ∈ {1, ½, 0},
/// μᵢ += Σⱼ (σᵢ²/c)(sᵢⱼ − pᵢⱼ) and σᵢ² *= max(1 − Σⱼ (σᵢ²/c²) γᵢⱼ pᵢⱼ (1 − pᵢⱼ), ε) with γᵢⱼ = σᵢ/c. All from the ratings
/// as they were before the game, so the order of the players changes nothing.
/// </summary>
public static class WengLin
{
    public const double Beta = 25.0 / 6.0;

    public const double Epsilon = 0.0001;

    /// <summary>The probability that <paramref name="a"/> beats <paramref name="b"/> in one game, by the model.</summary>
    public static double Probability(Rating a, Rating b, double beta = Beta)
    {
        var c = Math.Sqrt(a.Sigma * a.Sigma + b.Sigma * b.Sigma + 2 * beta * beta);
        return 1 / (1 + Math.Exp((b.Mu - a.Mu) / c));
    }

    /// <summary>The ratings after one game. <paramref name="players"/> gives each seat's rating and its rank (1 is best; equal ranks are a tie).</summary>
    public static IReadOnlyList<Rating> Update(IReadOnlyList<(Rating Rating, int Rank)> players, double beta = Beta)
    {
        var n = players.Count;
        var result = new Rating[n];
        for (var i = 0; i < n; i++)
        {
            var (ri, ranki) = players[i];
            double omega = 0, delta = 0;
            var si2 = ri.Sigma * ri.Sigma;
            for (var j = 0; j < n; j++)
            {
                if (j == i) continue;
                var (rj, rankj) = players[j];
                var c = Math.Sqrt(si2 + rj.Sigma * rj.Sigma + 2 * beta * beta);
                var p = 1 / (1 + Math.Exp((rj.Mu - ri.Mu) / c));
                var s = ranki < rankj ? 1.0 : ranki == rankj ? 0.5 : 0.0;
                var gamma = ri.Sigma / c;
                omega += si2 / c * (s - p);
                delta += gamma * si2 / (c * c) * p * (1 - p);
            }

            result[i] = new Rating(ri.Mu + omega, ri.Sigma * Math.Sqrt(Math.Max(1 - delta, Epsilon)));
        }

        return result;
    }
}
