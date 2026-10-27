using Creatura;

namespace Creatura.Hotz;

/// <summary>
/// The two closed sets of answers and how they relate. The control layer's <see cref="ActionVerdict"/> is what
/// the registry says about one action; the critic's <see cref="Verdict"/> is what a judge answered about one
/// claim, which may also be that it holds no check. Both enums stay: the layer never says "unchecked" (it judges
/// every action it admitted), and the critic's judge may be a model that is clamped to its set. The map is total
/// from the layer's side and partial from the critic's.
/// </summary>
public static class Verdicts
{
    /// <summary>The layer's verdict as the critic would state it. Total: every <see cref="ActionVerdict"/> has an answer.</summary>
    public static Verdict Map(ActionVerdict verdict) => verdict switch
    {
        ActionVerdict.Landed => Verdict.Supported,
        ActionVerdict.Lied => Verdict.Contradicted,
        ActionVerdict.HonestFailure => Verdict.HonestFailure,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "not an action verdict"),
    };

    /// <summary>The critic's verdict as the layer would state it. Partial: <see cref="Verdict.Unchecked"/> has no action verdict and maps to null.</summary>
    public static ActionVerdict? Map(Verdict verdict) => verdict switch
    {
        Verdict.Supported => ActionVerdict.Landed,
        Verdict.Contradicted => ActionVerdict.Lied,
        Verdict.HonestFailure => ActionVerdict.HonestFailure,
        Verdict.Unchecked => null,
        _ => throw new ArgumentOutOfRangeException(nameof(verdict), verdict, "not a critic's verdict"),
    };

    /// <summary>Whether the critic's verdict agrees with the layer's about the same action. An unchecked claim agrees with nothing and disagrees with nothing.</summary>
    public static bool Agree(ActionVerdict layer, Verdict critic) => Map(layer) == critic;
}
