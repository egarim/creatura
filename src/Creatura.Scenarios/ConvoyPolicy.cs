using System.Globalization;

namespace Creatura.Scenarios;

/// <summary>
/// The trucks' offline planner (D3). It reads only the facts of the truck's view and the paper map, and knows what
/// the Core's breadth-first planner cannot: a truck is in one place at a time, its tank empties, its load runs out.
/// Each tick:
/// <list type="number">
/// <item>every village served, as far as it knows: report the work done;</item>
/// <item>no target yet: the nearest village still in need that no other truck holds (or whose holder went quiet,
/// which is a takeover), and with a radio, claim it first;</item>
/// <item>empty: back to the depot to load; short of fuel for the round trip, as its gauge says: refuel;</item>
/// <item>at the target with a load: unload; otherwise the next road on the shortest path the truck believes open and
/// its control layer still offers;</item>
/// <item>nothing it can do: wait.</item>
/// </list>
/// It is deliberately naive about lies: it believes its sensors. Catching a road that lies is the control layer's
/// job, and M3 tests exactly that.
/// </summary>
public sealed class ConvoyPolicy(RoadMap map, PhysicalMachine machine) : IPolicy
{
    public string Name => "convoy-planner";

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(Next(context));

    private PolicySuggestion Next(PolicyContext context)
    {
        var facts = context.Facts;
        var tools = context.Tools.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var at = Value(facts, "at") ?? map.Depot;
        var fuel = Number(facts, "fuel");
        var load = Number(facts, "load");
        var radio = facts.Contains("radio");

        PolicySuggestion Use(string tool, string why) =>
            tools.Contains(tool) ? new PolicySuggestion(tool, why) : Wait($"{tool} is not offered");
        PolicySuggestion Wait(string why) =>
            tools.Contains("wait") ? new PolicySuggestion("wait", why) : new PolicySuggestion(null, why);

        if (facts.Contains("all.served")) return Use("report-done", "every village has what it needs");

        // A road is usable when its sensor says open and, if it leaves from here, the control layer still offers it.
        bool Usable(MapRoad road) =>
            facts.Contains($"road:{road.Id}:open")
            && (!road.Touches(at) || tools.Contains($"drive:{road.Id}:{road.Other(at)}"));
        int? Distance(string from, string to) => map.Distance(from, to, Usable);

        var target = map.Villages.Select(v => v.Id).FirstOrDefault(v => facts.Contains($"mine:{v}") && Need(facts, v) > 0);
        if (target is null)
        {
            var open = map.Villages
                .Select(v => v.Id)
                .Where(v => Need(facts, v) > 0 && !facts.Any(f => f.StartsWith($"claimed:{v}:", StringComparison.Ordinal)))
                .Select(v => (Village: v, Distance: Distance(load > 0 ? at : map.Depot, v)))
                .Where(c => c.Distance is not null)
                .OrderBy(c => c.Distance).ThenBy(c => c.Village, StringComparer.Ordinal)
                .ToList();
            if (open.Count == 0) return Wait("nothing left that no other truck holds");
            target = open[0].Village;
            if (radio) return Use($"claim:{target}", $"claim {target}: nearest village still in need");
        }

        string goal;
        if (load == 0)
        {
            if (at == map.Depot) return fuel < machine.FuelCapacity && tools.Contains("refuel") ? Use("refuel", "top up before the trip") : Use("load", $"load for {target}");
            goal = map.Depot;
        }
        else if (at == target)
        {
            return Use($"unload:{target}", $"unload at {target}");
        }
        else
        {
            var there = Distance(at, target);
            var back = Distance(target, map.Depot);
            var needed = ((there ?? 99) + (back ?? 99)) * machine.FuelPerStep;
            goal = fuel >= needed ? target : map.Depot;
            if (goal == map.Depot && at == map.Depot) return Use("refuel", $"fuel {fuel} is short of {needed} for {target} and back");
        }

        var path = map.Path(at, goal, Usable);
        if (path is null or { Count: 0 }) return Wait($"no open road to {goal}");
        var next = path[0].Other(at);
        var drive = Use($"drive:{path[0].Id}:{next}", $"towards {goal}: {string.Join(" → ", path.Select(r => r.Id))}");

        // The drive says where the truck is heading, so the world can tell a trip to a village from passing through it.
        return drive.Tool is null || goal == map.Depot ? drive : drive with { Content = $"target={goal}" };
    }

    private static string? Value(IReadOnlySet<string> facts, string key) =>
        facts.FirstOrDefault(f => f.StartsWith(key + ":", StringComparison.Ordinal) && f.Count(c => c == ':') == 1)?[(key.Length + 1)..];

    private static int Number(IReadOnlySet<string> facts, string key) =>
        facts.Where(f => f.StartsWith(key + ":", StringComparison.Ordinal))
            .Select(f => int.TryParse(f[(key.Length + 1)..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
            .FirstOrDefault(n => n is not null) ?? 0;

    private static int Need(IReadOnlySet<string> facts, string village) => Number(facts, $"need:{village}");
}
