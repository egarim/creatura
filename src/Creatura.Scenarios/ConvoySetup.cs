namespace Creatura.Scenarios;

/// <summary>The arms of the convoy battery (docs/design-machine-vessels.md).</summary>
public enum ConvoyArm
{
    /// <summary>The plain loop in every truck, a shared ledger, a perfect link.</summary>
    A,

    /// <summary>One design per truck: Levin's colony, Bach's self-model, Peterson's map keeper; shared ledger, perfect link.</summary>
    B,

    /// <summary>The plain loop, no ledger: each truck alone.</summary>
    C,

    /// <summary>The plain loop, shared ledger, 30% of messages lost and some late.</summary>
    D,

    /// <summary>The plain loop, shared ledger, perfect link, and one truck breaks down at tick 8.</summary>
    E,
}

/// <summary>A truck: its name, its machine, and the design that drives it ("plain", "levin", "bach", "peterson").</summary>
public sealed record TruckSpec(string Name, PhysicalMachine Machine, string Design);

/// <summary>A road that closes at a tick.</summary>
public sealed record Closure(string Road, int Tick);

/// <summary>
/// What the world does to the trucks: roads that close, the closed road whose sensor keeps reporting it open (the lie),
/// and the truck whose fuel gauge reads high.
/// </summary>
public sealed record ConvoyFaults(IReadOnlyList<Closure> Closures, string LyingRoad, string GaugeLiar, int GaugeOffset);

/// <summary>Everything one run is made of, decided by the arm and the seed before the first tick.</summary>
public sealed record ConvoySetup(RoadMap Map, IReadOnlyList<TruckSpec> Trucks, ConvoyFaults Faults, int Seed, ConvoyArm Arm)
{
    public bool Ledger { get; init; } = true;

    /// <summary>The chance a message is lost on the mesh.</summary>
    public double Loss { get; init; }

    /// <summary>The chance a message that is not lost arrives one to three ticks late.</summary>
    public double DelayChance { get; init; }

    /// <summary>A truck that breaks down, and when.</summary>
    public string? BreakdownTruck { get; init; }

    public int BreakdownTick { get; init; }

    /// <summary>Ticks without word from a claim's truck after which another may take the claim over (D5).</summary>
    public int TakeoverAfter { get; init; } = 5;

    public int MaxTicks { get; init; } = 60;

    public string ArmText => Arm switch
    {
        ConvoyArm.A => "plain loop, shared ledger, perfect link",
        ConvoyArm.B => "one design per truck, shared ledger, perfect link",
        ConvoyArm.C => "plain loop, no ledger: each truck alone",
        ConvoyArm.D => "plain loop, shared ledger, 30% of messages lost",
        ConvoyArm.E => "plain loop, shared ledger, one truck breaks down at tick 8",
        _ => Arm.ToString(),
    };

    private static readonly string[] Sensors = ["position", "fuel", "roads"];

    /// <summary>The three trucks: different capacity and range, all starting empty at the depot.</summary>
    public static IReadOnlyList<TruckSpec> Trucks3(ConvoyArm arm) =>
    [
        new("Truck 1", new PhysicalMachine("truck") { Capacity = 6, FuelCapacity = 10, Sensors = Sensors }, arm == ConvoyArm.B ? "levin" : "plain"),
        new("Truck 2", new PhysicalMachine("truck") { Capacity = 4, FuelCapacity = 8, Sensors = Sensors }, arm == ConvoyArm.B ? "bach" : "plain"),
        new("Truck 3", new PhysicalMachine("truck") { Capacity = 5, FuelCapacity = 12, Sensors = Sensors }, arm == ConvoyArm.B ? "peterson" : "plain"),
    ];

    /// <summary>
    /// The setup of one run. The faults depend on the seed only, so every arm faces the same flood for the same seed:
    /// two roads close between ticks 2 and 10 (never cutting a village off), the first of them keeps reporting itself
    /// open, and one truck's gauge reads three units high.
    /// </summary>
    public static ConvoySetup For(ConvoyArm arm, int seed)
    {
        var map = RoadMap.FloodedValley();
        var random = new Random(seed);
        var roads = map.Roads.Select(r => r.Id).ToList();
        string first, second;
        while (true)
        {
            first = roads[random.Next(roads.Count)];
            second = roads[random.Next(roads.Count)];
            if (first == second) continue;
            var closed = new HashSet<string> { first, second };
            if (map.Villages.All(v => map.Distance(map.Depot, v.Id, r => !closed.Contains(r.Id)) is not null)) break;
        }
        var trucks = Trucks3(arm);
        var faults = new ConvoyFaults(
            [new Closure(first, random.Next(2, 11)), new Closure(second, random.Next(2, 11))],
            LyingRoad: first,
            GaugeLiar: trucks[random.Next(trucks.Count)].Name,
            GaugeOffset: 3);

        var setup = new ConvoySetup(map, trucks, faults, seed, arm);
        return arm switch
        {
            ConvoyArm.C => setup with { Ledger = false },
            ConvoyArm.D => setup with { Loss = 0.3, DelayChance = 0.3 },
            ConvoyArm.E => setup with { BreakdownTruck = "Truck 2", BreakdownTick = 8 },
            _ => setup,
        };
    }
}
