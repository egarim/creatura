namespace Creatura;

public sealed record Contradiction(int Tick, string Claim, string Evidence);

/// <summary>
/// Bach's learning filter: "we basically take only in stuff that makes us more coherent, not less
/// coherent" (01:21:27). Here it is a gate — a claim that contradicts the record is quarantined, not merged,
/// and the contradiction raises the organism's stress in the hybrid design.
/// </summary>
public sealed class CoherenceAudit
{
    private readonly List<Contradiction> _found = [];

    public int Claims { get; private set; }

    public IReadOnlyList<Contradiction> Contradictions => _found;

    public double Score
    {
        get
        {
            if (Claims == 0)
            {
                return 1.0;
            }

            return Math.Clamp(1.0 - (double)_found.Count / Claims, 0.0, 1.0);
        }
    }

    /// <summary>Register that a claim was made, so the score has a denominator.</summary>
    public void Claim() => Claims++;

    /// <summary>
    /// Gate an incoming belief. Returns false when it contradicts what the agent already holds, and records
    /// why — the reason is what the agent gets to read, so a quarantined belief is not silently dropped.
    /// </summary>
    public bool Accept(string claim, bool supported, string evidence, int tick)
    {
        if (supported)
        {
            return true;
        }

        _found.Add(new Contradiction(tick, claim, evidence));
        return false;
    }

    /// <summary>An expectation that was formed and then broke — the strongest evidence of a bad model.</summary>
    public void Contradict(int tick, string claim, string evidence) => _found.Add(new Contradiction(tick, claim, evidence));
}
