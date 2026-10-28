using System.Text;
using Creatura.Seats;

namespace Creatura.Scenarios;

/// <summary>
/// A language model in a truck's seat (arm S, docs/convoy-models.md). It wraps any seat from Creatura's catalog, in
/// practice a <see cref="ModelPolicy"/>, and gives it what the offline planner knows by construction: what the
/// convoy's facts mean and how a delivery goes. The model still sees only the truck's facts and visible tools, and
/// still answers with one tool; the regulator, the control layer and the judge around it do not change. When the model
/// answers with no tool, or its call fails, the truck waits instead of spending its attempts.
/// </summary>
public sealed class ConvoyModelSeat(IPolicy model, RoadMap map, PhysicalMachine machine, string truck) : IPolicy
{
    public string Name => model.Name;

    /// <summary>The seat inside, for its telemetry.</summary>
    public IPolicy Model => model;

    /// <summary>The briefing the model reads before the facts, every tick.</summary>
    public string Briefing { get; } = Brief(map, machine, truck);

    public async Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var suggestion = await model.NextAsync(Briefed(context, Briefing), cancellationToken).ConfigureAwait(false);
        if (suggestion.Tool is null && context.Tools.Any(t => t.Id == "wait"))
            return new PolicySuggestion("wait", $"the model chose no action ({suggestion.Rationale})");
        return suggestion;
    }

    /// <summary>The context as the model reads it: the convoy's goal in words and the briefing before the truck's own notes.</summary>
    public static PolicyContext Briefed(PolicyContext context, string briefing) => context with
    {
        GoalText = "Get supplies to every village that still needs them, together with the other trucks, without doing the same job twice.",
        Notes = [briefing, .. context.Notes],
    };

    /// <summary>The briefing for one truck on one map: what a dataset puts before each decision, as the seat does.</summary>
    public static string Brief(RoadMap map, PhysicalMachine machine, string truck)
    {
        var text = new StringBuilder();
        text.AppendLine($"You drive {truck}, one of three trucks. It carries {machine.Capacity} units and holds fuel for {machine.FuelCapacity / Math.Max(1, machine.FuelPerStep)} roads; every road you drive costs {machine.FuelPerStep} fuel.");
        text.AppendLine($"Places: {string.Join(", ", map.Nodes.Select(n => $"{n.Id} ({n.Name}{(n.Kind == NodeKind.Village ? $", a village that needed {n.Demand} units" : n.Kind == NodeKind.Depot ? ", where you load and refuel" : "")})"))}.");
        text.AppendLine($"Roads, each one tick long: {string.Join(", ", map.Roads.Select(r => $"{r.Id} {r.A}-{r.B}"))}.");
        text.AppendLine("Facts: at:<place> is where you are. fuel:<n> is what your fuel gauge reads, load:<n> what you carry. road:<id>:open is what your road sensor reports, and it can be wrong. need:<village>:<n> is what you believe a village still needs; served:<village> means it needs nothing. mine:<village> is your claim on the shared ledger; claimed:<village>:<truck> means another truck holds that village; quiet:<village>:<truck> means its holder went silent or stalled and you may take it over.");
        text.AppendLine("How a delivery goes: claim one village that still needs supplies and that no other truck holds (claim:<village>). If you carry nothing, drive to the depot and load. Drive road by road (drive:<road>:<next place>; you can only take roads that start where you are). At the village, unload (unload:<village>). Before a trip your fuel cannot make there and back to the depot, refuel at the depot. If nothing is left for you to do, wait. When every village is served, report-done.");
        text.Append("If a drive reported success but at:<place> did not change, that road lied: avoid it. Notes that say avoid:<road> are roads you have already caught lying.");
        return text.ToString();
    }
}
