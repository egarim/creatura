namespace Creatura.Orienting;

/// <summary>
/// The two systems of design-sokolov.md, claims 4 to 6 and 10: approach <see cref="A"/> (felt as hope and curiosity)
/// and inhibition <see cref="I"/> (felt as anxiety), kept apart and never summed. Promises, satisfactions and relief
/// raise approach; punishments and threats raise inhibition; each inhibits the other by a small coupling; each decays
/// toward its resting level on every tick nothing happens. A deliberate approach to something threatening that ends in
/// control lowers the resting level of inhibition itself (claim 10, voluntary exposure), and that level is what a
/// record carries to the next case.
/// </summary>
public sealed class Affect
{
    public double A { get; private set; }

    public double I { get; private set; }

    /// <summary>Where approach settles with nothing happening.</summary>
    public double RestingA { get; init; } = 0.2;

    /// <summary>Where inhibition settles with nothing happening; lowered by courage, never below <see cref="MinRestingI"/>.</summary>
    public double RestingI { get; private set; } = 0.2;

    public double MinRestingI { get; init; } = 0.05;

    /// <summary>How much of the gap to resting each channel closes per quiet tick.</summary>
    public double Decay { get; init; } = 0.25;

    /// <summary>How strongly a rise in one channel pushes the other down.</summary>
    public double Coupling { get; init; } = 0.2;

    /// <summary>How far one successful deliberate approach lowers resting inhibition.</summary>
    public double CourageStep { get; init; } = 0.03;

    public Affect(double restingI = 0.2)
    {
        RestingI = restingI;
        A = RestingA;
        I = RestingI;
    }

    /// <summary>
    /// Freedom for the policy and the extensions: hope widens the search and anxiety narrows it, freedom =
    /// min(A, 1 − I), floored at Levin's 0.2 so a frightened agent still has one move.
    /// </summary>
    public double Freedom => Math.Clamp(Math.Min(A, 1 - I), 0.2, 1.0);

    /// <summary>The conflict Peterson calls paralysis: both systems high at once.</summary>
    public bool Conflicted => A > 0.6 && I > 0.6;

    /// <summary>Apply one classified outcome. Satisfactions and promises raise approach; punishments and threats raise inhibition.</summary>
    public void Feel(ValenceReading reading)
    {
        var up = 0.0;
        if (reading.Has(ValenceKind.Satisfaction)) up += 0.3;
        if (reading.Has(ValenceKind.Promise)) up += 0.2;
        if (reading.Has(ValenceKind.Relief)) up += 0.2;
        var down = 0.0;
        if (reading.Has(ValenceKind.Punishment)) down += 0.4 * reading.PunishmentWeight;
        if (reading.Has(ValenceKind.Threat)) down += 0.2;
        Raise(up, down);
    }

    /// <summary>The unclassified carries both at full strength (claim 5): meeting it raises both channels.</summary>
    public void Orient() => Raise(0.15, 0.15);

    /// <summary>A quiet tick: both channels drift back toward rest.</summary>
    public void Settle()
    {
        A += (RestingA - A) * Decay;
        I += (RestingI - I) * Decay;
    }

    /// <summary>A deliberate approach to a threat ended in control: resting inhibition falls a step (claim 10).</summary>
    public void Courage() => RestingI = Math.Max(MinRestingI, RestingI - CourageStep);

    private void Raise(double up, double down)
    {
        A = Math.Clamp(A + up - Coupling * down, 0, 1);
        I = Math.Clamp(I + down - Coupling * up, 0, 1);
    }
}
