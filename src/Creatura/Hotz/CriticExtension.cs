using Creatura;

namespace Creatura.Hotz;

/// <summary>
/// The citizen's agent riding any <see cref="ReferenceLoop"/> (or any host that runs the extension hooks) as an
/// observer with no hands: it opens a case file before the run and hands every judged action to the critic after
/// it. It shapes nothing, re-judges nothing, vetoes nothing and stops nothing: a loop with it loaded leaves rows,
/// notes and the record exactly as the same loop without it. Whoever owns the <see cref="HotzAgent"/> registers
/// it (under <c>critic</c> in a catalog, if one is used); the critic keeps its precision record across cases.
/// </summary>
public sealed class CriticExtension(HotzAgent citizen) : IAgentExtension
{
    public string Name => "critic";

    public HotzAgent Citizen => citizen;

    /// <summary>A new case file over the world the loop is about to act on.</summary>
    public void BeforeRun(TickContext context) => citizen.Open(context.World);

    /// <summary>One judged action becomes one claim, judged against the registry as it stands right after.</summary>
    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict) =>
        citizen.ObserveAction(tool, outcome, context.World);
}
