namespace Creatura;

/// <summary>
/// Parts of a design that can be switched off, so a battery can price them. The question this exists to ask is
/// the zombie-cost one: a reflexive component ships only if removing it makes the run worse, or longer, or
/// dearer — otherwise the component was decoration and a cheaper imitation would have done.
///
/// Ablating <see cref="Coherence"/> does not touch the effect check: a tool that reports success and changes
/// nothing is still a lie, whoever is keeping score of beliefs.
/// </summary>
[Flags]
public enum Ablations
{
    None = 0,

    /// <summary>No contradiction record and no belief quarantine; the landed/lied judgement is untouched.</summary>
    Coherence = 1,

    /// <summary>Release runs without the prediction record: the stage interlock is off.</summary>
    Stage = 2,

    /// <summary>A tool that lied stays visible.</summary>
    Quarantine = 4,
}

/// <summary>Reading the flags.</summary>
public static class AblationExtensions
{
    public static bool Has(this Ablations ablations, Ablations part) => (ablations & part) == part;
}
