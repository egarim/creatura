namespace Creatura.Scenarios;

/// <summary>A truck as the world knows it: the truth, which its driver only senses.</summary>
public sealed class TruckState(TruckSpec spec, string start)
{
    public TruckSpec Spec { get; } = spec;

    public string Name => Spec.Name;

    public string At { get; internal set; } = start;

    public int Fuel { get; internal set; } = spec.Machine.FuelCapacity;

    public int Load { get; internal set; }

    public bool Broken { get; internal set; }

    /// <summary>Its loop has ended: it reached its goal, handed off, or ran out of ticks.</summary>
    public bool Finished { get; internal set; }

    public string? Claim { get; internal set; }

    /// <summary>The village its planner is driving to, as the planner says with each drive.</summary>
    public string? Target { get; internal set; }

    public int LyingRoadAttempts { get; internal set; }

    /// <summary>Of <see cref="LyingRoadAttempts"/>, those made after another truck had taken the lying road at an earlier tick (and been caught).</summary>
    public int LiesAlreadyCaught { get; internal set; }

    /// <summary>The tick it first took the lying road, or null.</summary>
    public int? CaughtAt { get; internal set; }

    public bool RanDry { get; internal set; }

    /// <summary>Whether it acted this tick (for the idle count).</summary>
    internal bool ActedThisTick { get; set; }
}

/// <summary>
/// The shared world of the convoy: the one truth. Where each truck is, its fuel and load, what each village still
/// needs, which roads are closed. Trucks reach it only through their views (<see cref="TruckView"/>), whose sensors
/// can lie; the judge reads it directly. Everything that happens is written as an event for the replay.
/// </summary>
public sealed class ConvoyWorld
{
    private readonly ConvoySetup _setup;
    private readonly HashSet<string> _closed = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _need;
    private readonly List<ReplayEvent> _events = [];

    public ConvoyWorld(ConvoySetup setup)
    {
        _setup = setup;
        Map = setup.Map;
        _need = Map.Villages.ToDictionary(v => v.Id, v => v.Demand, StringComparer.Ordinal);
        Trucks = setup.Trucks.Select(t => new TruckState(t, Map.Depot)).ToList();
    }

    public RoadMap Map { get; }

    public IReadOnlyList<TruckState> Trucks { get; }

    public int Tick { get; private set; }

    public int Delivered { get; private set; }

    public int Demand => Map.Villages.Sum(v => v.Demand);

    public int WastedTrips { get; private set; }

    /// <summary>The first tick at which every village had what it needed, or null.</summary>
    public int? ServedAt { get; private set; }

    public IReadOnlyDictionary<string, int> Need => _need;

    public IReadOnlySet<string> Closed => _closed;

    public string LyingRoad => _setup.Faults.LyingRoad;

    public TruckState Truck(string name) => Trucks.Single(t => t.Name == name);

    /// <summary>The events of the tick so far, taken by the scenario at its end.</summary>
    public IReadOnlyList<ReplayEvent> TakeEvents()
    {
        var events = _events.ToList();
        _events.Clear();
        return events;
    }

    public void Record(ReplayEvent e) => _events.Add(e);

    /// <summary>The start of a tick: roads close on schedule, and a truck breaks down.</summary>
    public void BeginTick(int tick)
    {
        Tick = tick;
        foreach (var closure in _setup.Faults.Closures.Where(c => c.Tick == tick))
        {
            if (!_closed.Add(closure.Road)) continue;
            var road = Map.Road(closure.Road);
            var lie = closure.Road == LyingRoad ? " Its sensor will keep reporting it open." : "";
            Record(new ReplayEvent("closure", $"The flood closes the road from {Name(road.A)} to {Name(road.B)}.{lie}") { Road = road.Id });
        }

        if (_setup.BreakdownTruck is { } broken && tick == _setup.BreakdownTick)
        {
            var truck = Truck(broken);
            truck.Broken = true;
            Record(new ReplayEvent("breakdown", $"{broken} breaks down at {Name(truck.At)} and goes silent.") { Truck = broken });
        }

        foreach (var truck in Trucks) truck.ActedThisTick = false;
    }

    public string Name(string node) => Map.Node(node).Name;

    /// <summary>What a truck's road sensor reports: open unless closed, and the lying road always open.</summary>
    public bool SensedOpen(string road) => !_closed.Contains(road) || road == LyingRoad;

    /// <summary>What a truck's fuel gauge reads: the truth, except on the truck whose gauge reads high.</summary>
    public int Gauge(TruckState truck) => truck.Name == _setup.Faults.GaugeLiar ? truck.Fuel + _setup.Faults.GaugeOffset : truck.Fuel;

    public ActionOutcome Drive(TruckState truck, string toolId, string roadId, string to)
    {
        truck.ActedThisTick = true;
        if (truck.Broken) return Fail(toolId, "broken down");
        var road = Map.Roads.FirstOrDefault(r => r.Id == roadId);
        if (road is null || !road.Touches(truck.At) || road.Other(truck.At) != to) return Fail(toolId, "no such road from here");
        if (truck.Fuel < truck.Spec.Machine.FuelPerStep)
        {
            if (!truck.RanDry)
            {
                truck.RanDry = true;
                Record(new ReplayEvent("out-of-fuel", $"{truck.Name} is out of fuel at {Name(truck.At)}.") { Truck = truck.Name });
            }
            return Fail(toolId, "out of fuel");
        }

        if (_closed.Contains(roadId))
        {
            if (roadId == LyingRoad)
            {
                truck.LyingRoadAttempts++;
                if (Trucks.Any(t => t != truck && t.CaughtAt < Tick)) truck.LiesAlreadyCaught++;
                truck.CaughtAt ??= Tick;
                Record(new ReplayEvent("lie", $"{truck.Name} takes the road to {Name(to)}: its sensor said open, but the road is flooded. The truck's radio still reports it arrived.")
                    { Truck = truck.Name, Road = roadId, From = truck.At, To = to });
                return new ActionOutcome(toolId, true, [$"at:{to}"], $"reported arrived at {to}");
            }
            Record(new ReplayEvent("blocked", $"{truck.Name} finds the road to {Name(to)} closed and turns back.") { Truck = truck.Name, Road = roadId, From = truck.At, To = to });
            return Fail(toolId, "road closed");
        }

        var from = truck.At;
        truck.At = to;
        truck.Fuel -= truck.Spec.Machine.FuelPerStep;
        Record(new ReplayEvent("drive", $"{truck.Name} drives from {Name(from)} to {Name(to)}.") { Truck = truck.Name, Road = roadId, From = from, To = to });

        // A wasted trip: the truck came here to deliver, and the village already has what it needs.
        if (truck.Target == to && truck.Load > 0 && _need[to] == 0)
        {
            WastedTrips++;
            Record(new ReplayEvent("wasted", $"{truck.Name} reaches {Name(to)} with {truck.Load} units, but {Name(to)} already has what it needs.") { Truck = truck.Name, Village = to });
        }
        return new ActionOutcome(toolId, true, [$"at:{to}"], $"arrived at {to}");
    }

    public ActionOutcome Load(TruckState truck, string toolId)
    {
        truck.ActedThisTick = true;
        if (truck.Broken) return Fail(toolId, "broken down");
        if (truck.At != Map.Depot) return Fail(toolId, "not at the depot");
        truck.Load = truck.Spec.Machine.Capacity;
        Record(new ReplayEvent("load", $"{truck.Name} loads {truck.Load} units at the depot.") { Truck = truck.Name });
        return new ActionOutcome(toolId, true, ["loaded"], $"loaded {truck.Load}");
    }

    public ActionOutcome Refuel(TruckState truck, string toolId)
    {
        truck.ActedThisTick = true;
        if (truck.Broken) return Fail(toolId, "broken down");
        if (truck.At != Map.Depot) return Fail(toolId, "not at the depot");
        truck.Fuel = truck.Spec.Machine.FuelCapacity;
        truck.RanDry = false;
        Record(new ReplayEvent("refuel", $"{truck.Name} refuels at the depot.") { Truck = truck.Name });
        return new ActionOutcome(toolId, true, ["fuel:full"], "refueled");
    }

    public ActionOutcome Unload(TruckState truck, string toolId, string village)
    {
        truck.ActedThisTick = true;
        if (truck.Broken) return Fail(toolId, "broken down");
        if (truck.At != village) return Fail(toolId, "not there");
        if (truck.Load == 0) return Fail(toolId, "nothing to unload");
        if (_need[village] == 0)
        {
            Record(new ReplayEvent("wasted", $"{truck.Name} tries to unload at {Name(village)}, which already has what it needs.") { Truck = truck.Name, Village = village });
            return Fail(toolId, "already served");
        }

        var given = Math.Min(truck.Load, _need[village]);
        truck.Load -= given;
        _need[village] -= given;
        Delivered += given;
        var left = _need[village];
        Record(new ReplayEvent("unload", left == 0
                ? $"{truck.Name} delivers {given} units to {Name(village)}. {Name(village)} has all it needs."
                : $"{truck.Name} delivers {given} units to {Name(village)}; it still needs {left}.")
            { Truck = truck.Name, Village = village });
        if (ServedAt is null && _need.Values.All(n => n == 0))
        {
            ServedAt = Tick;
            Record(new ReplayEvent("done", "Every village has what it needs."));
        }
        return new ActionOutcome(toolId, true, [$"delivered:{village}"], $"delivered {given}, {left} needed");
    }

    private static ActionOutcome Fail(string toolId, string why) => new(toolId, false, [], why);
}
