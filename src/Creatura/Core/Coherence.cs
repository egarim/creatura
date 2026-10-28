namespace Creatura;

public sealed record Contradiction(int Tick, string Claim, string Evidence);

/// <summary>
/// Bach's learning filter: a mind takes in only what makes it more coherent, never what makes it less
/// (Lex Fridman Podcast #392, 01:21:27). Here it is a gate — a claim that contradicts the record is quarantined, not merged,
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

    /// <summary>The audit as data: the claims counted and every contradiction found, in order.</summary>
    public CoherenceSnapshot Snapshot() => new(Claims, [.. _found]);

    /// <summary>Replace the audit's state with a snapshot's, in place.</summary>
    public void Restore(CoherenceSnapshot snapshot)
    {
        Claims = snapshot.Claims;
        _found.Clear();
        _found.AddRange(snapshot.Contradictions);
    }
}

/// <summary>A <see cref="CoherenceAudit"/> as data: what a record keeps of it.</summary>
public sealed record CoherenceSnapshot(int Claims, List<Contradiction> Contradictions);
