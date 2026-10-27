namespace Creatura.Hotz;

/// <summary>
/// The right to interrupt, earned by precision. Hotz's standard is mypy: a checker at 5% false positives is
/// used, one at 60% is turned off (02:29:23). The popup is a capability the agent earns with a budget of
/// false positives and loses when it overspends; the log continues either way.
/// </summary>
public sealed class InterruptBudget
{
    /// <summary>Precision below which the popup is withheld. mypy, not the 60% tool.</summary>
    public double Threshold { get; init; } = 0.95;

    /// <summary>Flags raised before precision is believed; a new agent is heard until it has a record.</summary>
    public int Warmup { get; init; } = 4;

    public int Raised { get; private set; }

    public int Upheld { get; private set; }

    public int Dismissed => Raised - Upheld;

    public double Precision => Raised == 0 ? 1.0 : (double)Upheld / Raised;

    /// <summary>True while the record supports being heard. Below the threshold the agent judges and logs, silently.</summary>
    public bool MayInterrupt => Raised < Warmup || Precision >= Threshold;

    /// <summary>A flag was upheld (the registry agreed, or the person acted on it) or dismissed (waved away, or contradicted).</summary>
    public void Record(bool upheld)
    {
        Raised++;
        if (upheld)
        {
            Upheld++;
        }
    }

    /// <summary>A flag settled as upheld turns out dismissed: the person's word outranks the default.</summary>
    public void Overturn()
    {
        if (Upheld > 0)
        {
            Upheld--;
        }
    }

    public InterruptBudget Fresh() => new() { Threshold = Threshold, Warmup = Warmup };

    /// <summary>The budget's record as data: raised, upheld and dismissed. The threshold and the warm-up are configuration, not record.</summary>
    public InterruptSnapshot Snapshot() => new(Raised, Upheld, Dismissed);

    /// <summary>Replace the record with a snapshot's, in place; the threshold and the warm-up stay as configured.</summary>
    public void Restore(InterruptSnapshot snapshot)
    {
        Raised = snapshot.Raised;
        Upheld = snapshot.Upheld;
    }

    public override string ToString() => $"precision {Precision:P0} ({Upheld}/{Raised}), {(MayInterrupt ? "may interrupt" : "silenced")}";
}

/// <summary>An <see cref="InterruptBudget"/>'s record as data: what an <see cref="AgentRecord"/> keeps of it. <c>Dismissed</c> is written for readers; <see cref="InterruptBudget.Restore"/> reads raised and upheld.</summary>
public sealed record InterruptSnapshot(int Raised, int Upheld, int Dismissed);
