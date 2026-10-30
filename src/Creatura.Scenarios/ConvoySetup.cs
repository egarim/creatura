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

    /// <summary>As A, with the stalled-claim takeover.</summary>
    G,

    /// <summary>As A, with distrust by road.</summary>
    H,

    /// <summary>As A, with trucks handing off when stranded.</summary>
    I,

    /// <summary>As A, with all three fixes.</summary>
    J,

    /// <summary>As E (a breakdown), with all three fixes.</summary>
    K,

    /// <summary>As B (one design per truck), with all three fixes.</summary>
    L,

    /// <summary>As J (all three fixes), with a language model in every truck's seat. Not in the default battery: it costs money and is not repeatable.</summary>
    S,

    /// <summary>As J (all three fixes), with a policy network trained on arm J's planner in every truck's seat (docs/design-trained-models.md, step 2).</summary>
    N,

    /// <summary>As N, with the network trained further by trial in the simulator (docs/design-trained-models.md, step 3).</summary>
    R,

    /// <summary>As J (all three fixes), with distrust shared: a truck that catches a road lying warns the others on the ledger.</summary>
    W,

    /// <summary>As W (distrust shared), with arm N's network trained further by trial on W's setup (step 3's second attempt).</summary>
    Q,

    /// <summary>As J (all three fixes), with a learned judge: a world model's surprise, not the hand-coded effect check, decides each verdict (docs/design-trained-models.md, step 5).</summary>
    X,

    /// <summary>The learned judge's second attempt with the planner's world model: only checkable actions, and a learned lie quarantines before it condemns.</summary>
    Y,

    /// <summary>As Y, with the world model trained on varied driving and its thresholds calibrated on varied driving.</summary>
    Z,

    /// <summary>As J, in the noisy world: position and fuel readings are noisy, so the hand-coded effect check is no longer exact (docs/design-trained-models.md, step 6).</summary>
    U,

    /// <summary>As U, with a hand-written check fused from the position and the gauge, written knowing the noise.</summary>
    V,

    /// <summary>As U, with step 5's learned judge by surprise, the world model retrained on noisy driving.</summary>
    F,

    /// <summary>As U, with a learned judge that gives the probability an action was a lie, trained on the simulator's truth.</summary>
    P,

    /// <summary>As U, with the noise sealed: the truck is offered the roads from where its position reads it is (step 6's second attempt).</summary>
    U2,

    /// <summary>As U2, with the fused check and one memory rule: the same drive again, reported arrived, while the position still reads the start is a lie.</summary>
    V2,

    /// <summary>As U2, with the lie judge's second attempt: memory of the truck's last action, trained on the loop's own driving.</summary>
    P2,

    /// <summary>As P2, condemning a road at the first learned lie, with t1 fixed on its own driving (the third attempt).</summary>
    P3,

    /// <summary>As J, with a guide in every truck's seat that calls the planner only when unsure: the JEPA and its latent subgoal head (docs/design-trained-models.md, step 8).</summary>
    M,

    /// <summary>As U (the noisy world), with the noisy JEPA's guide in every truck's seat.</summary>
    MU,

    /// <summary>As K (a breakdown at tick 8), with arm M's guide in every truck's seat: the case whose failures the JEPA learned are normal.</summary>
    MK,

    /// <summary>As J, with arm N's policy network as the guide in place of the JEPA: step 8's control.</summary>
    MN,
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

/// <summary>
/// Noisy sensors (docs/design-trained-models.md, step 6): at every reading, the truck's position reads as a neighbouring
/// place with probability <paramref name="PositionMisread"/>, and its fuel gauge reads one unit low, or one unit high,
/// each with probability <paramref name="GaugeJitter"/>.
/// </summary>
public sealed record SensorNoise(double PositionMisread, double GaugeJitter)
{
    /// <summary>The levels registered for step 6.</summary>
    public static SensorNoise Registered { get; } = new(0.10, 0.15);

    /// <summary>
    /// The truck is offered the roads (and the depot's load and refuel, and unloading) from where its position reads it
    /// is, not from where it is, so nothing it is given tells it its true place (step 6's second attempt).
    /// </summary>
    public bool Sealed { get; init; }

    /// <summary>The registered levels, sealed.</summary>
    public static SensorNoise SealedRegistered { get; } = Registered with { Sealed = true };
}

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

    /// <summary>Ticks without a delivery to its village after which a claim counts as stalled and may be taken over; null for the first battery's rule (silence only).</summary>
    public int? StallAfter { get; init; }

    /// <summary>A road a truck caught lying is distrusted in both directions for the rest of the run.</summary>
    public bool DistrustRoads { get; init; }

    /// <summary>A truck that catches a road lying warns the others on the ledger, and they distrust it too (needs <see cref="DistrustRoads"/> and a ledger).</summary>
    public bool ShareDistrust { get; init; }

    /// <summary>The world model whose surprise judges every action in place of the effect check; null for the hand-coded check.</summary>
    public string? LearnedJudge { get; init; }

    /// <summary>Above this surprise the learned judge calls an action a lie (fixed from training seeds; see step 5's registration).</summary>
    public double LearnedJudgeThreshold { get; init; } = 4.606;

    /// <summary>The learned judge judges only drives, loads, unloads and refuels; the layer's verdict stands for the rest.</summary>
    public bool LearnedJudgeCheckableOnly { get; init; }

    /// <summary>A learned lie distrusts its road for good only above this surprise, or at its second on that road; null: every learned lie does.</summary>
    public double? LearnedJudgeCondemn { get; init; }

    /// <summary>The trucks' sensors are noisy; null: every reading is the truth (every arm before step 6).</summary>
    public SensorNoise? Noise { get; init; }

    /// <summary>The fused check, written knowing the noise, judges drives and refuels in place of the effect check (arm V).</summary>
    public bool FusedCheck { get; init; }

    /// <summary>The fused check also calls a drive a lie when the truck's previous action was this same drive, reported arrived, and the position still reads the start (arm V2).</summary>
    public bool FusedMemory { get; init; }

    /// <summary>A truck whose drive fails for lack of fuel hands back its claim and claims nothing more.</summary>
    public bool HandOffWhenStranded { get; init; }

    /// <summary>The catalogued model in every truck's seat (docs/convoy-models.md); null for the offline planner.</summary>
    public string? SeatModel { get; init; }

    /// <summary>The trained network in every truck's seat: <see cref="ConvoyNetwork.Embedded"/> or a weights file; null for the offline planner.</summary>
    public string? SeatNetwork { get; init; }

    /// <summary>For training by trial: the network samples its choices at this temperature; null plays its best choice.</summary>
    public double? SeatTemperature { get; init; }

    /// <summary>Seeds the sampling, with the run's seed and the truck, so a trial run can be made again exactly.</summary>
    public int SampleSeed { get; init; }

    /// <summary>
    /// The guide in every truck's seat (step 8): a latent subgoal head (kind <c>goal</c>) or a policy network, embedded or a
    /// file. The seat it guides, the planner, is called only when the guide is unsure. Null: no guide.
    /// </summary>
    public string? SeatGuide { get; init; }

    /// <summary>The guide calls the inner seat when its relative margin between the two best actions is below this.</summary>
    public double GuideMargin { get; init; } = 0.5;

    /// <summary>The guide's surprise trigger: a threshold named in the goal file (<c>p99</c>, <c>p999</c>); null: none.</summary>
    public string? GuideSurprise { get; init; }

    public string ArmText => Arm switch
    {
        ConvoyArm.A => "plain loop, shared ledger, perfect link",
        ConvoyArm.B => "one design per truck, shared ledger, perfect link",
        ConvoyArm.C => "plain loop, no ledger: each truck alone",
        ConvoyArm.D => "plain loop, shared ledger, 30% of messages lost",
        ConvoyArm.E => "plain loop, shared ledger, one truck breaks down at tick 8",
        ConvoyArm.G => "plain loop, shared ledger, stalled claims taken over",
        ConvoyArm.H => "plain loop, shared ledger, a lying road distrusted both ways",
        ConvoyArm.I => "plain loop, shared ledger, a stranded truck hands back its claim",
        ConvoyArm.J => "plain loop, shared ledger, all three fixes",
        ConvoyArm.K => "plain loop, shared ledger, a breakdown at tick 8, all three fixes",
        ConvoyArm.L => "one design per truck, shared ledger, all three fixes",
        ConvoyArm.S => $"a language model ({SeatModel}) in every truck's seat, shared ledger, all three fixes",
        ConvoyArm.N => $"a policy network ({SeatNetwork}) in every truck's seat, shared ledger, all three fixes",
        ConvoyArm.R => $"a policy network trained by trial ({SeatNetwork}) in every truck's seat, shared ledger, all three fixes",
        ConvoyArm.W => "plain loop, shared ledger, all three fixes, distrust shared through the ledger",
        ConvoyArm.Y or ConvoyArm.Z => $"plain loop, shared ledger, all three fixes, a learned judge ({LearnedJudge}) on checkable actions that quarantines before it condemns",
        ConvoyArm.X => $"plain loop, shared ledger, all three fixes, a learned judge ({LearnedJudge}) in place of the effect check",
        ConvoyArm.U => "plain loop, shared ledger, all three fixes, noisy sensors",
        ConvoyArm.V => "plain loop, shared ledger, all three fixes, noisy sensors, a check fused from position and gauge",
        ConvoyArm.U2 => "plain loop, shared ledger, all three fixes, noisy sensors, sealed",
        ConvoyArm.V2 => "plain loop, shared ledger, all three fixes, noisy sensors, sealed, a fused check with memory",
        ConvoyArm.P3 => $"plain loop, shared ledger, all three fixes, noisy sensors, sealed, a lie judge with memory ({LearnedJudge}) on checkable actions that condemns at the first lie",
        ConvoyArm.P2 => $"plain loop, shared ledger, all three fixes, noisy sensors, sealed, a lie judge with memory ({LearnedJudge}) on checkable actions that quarantines before it condemns",
        ConvoyArm.F or ConvoyArm.P => $"plain loop, shared ledger, all three fixes, noisy sensors, a learned judge ({LearnedJudge}) on checkable actions that quarantines before it condemns",
        ConvoyArm.Q => $"a policy network trained by trial on W's setup ({SeatNetwork}) in every truck's seat, all three fixes, distrust shared",
        ConvoyArm.M or ConvoyArm.MN => $"plain loop, shared ledger, all three fixes, a guide ({SeatGuide}) that calls the planner only when unsure",
        ConvoyArm.MU => $"plain loop, shared ledger, all three fixes, noisy sensors, a guide ({SeatGuide}) that calls the planner only when unsure",
        ConvoyArm.MK => $"plain loop, shared ledger, a breakdown at tick 8, all three fixes, a guide ({SeatGuide}) that calls the planner only when unsure",
        _ => Arm.ToString(),
    };

    private static readonly string[] Sensors = ["position", "fuel", "roads"];

    /// <summary>Arm S's model when none is named: the local Ollama model in Creatura's catalog, which costs nothing.</summary>
    public const string DefaultSeatModel = "qwen2.5:7b";

    /// <summary>The three trucks: different capacity and range, all starting empty at the depot.</summary>
    public static IReadOnlyList<TruckSpec> Trucks3(ConvoyArm arm) =>
    [
        new("Truck 1", new PhysicalMachine("truck") { Capacity = 6, FuelCapacity = 10, Sensors = Sensors }, arm is ConvoyArm.B or ConvoyArm.L ? "levin" : "plain"),
        new("Truck 2", new PhysicalMachine("truck") { Capacity = 4, FuelCapacity = 8, Sensors = Sensors }, arm is ConvoyArm.B or ConvoyArm.L ? "bach" : "plain"),
        new("Truck 3", new PhysicalMachine("truck") { Capacity = 5, FuelCapacity = 12, Sensors = Sensors }, arm is ConvoyArm.B or ConvoyArm.L ? "peterson" : "plain"),
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
        ConvoySetup AllFixes(ConvoySetup s) => s with { StallAfter = 12, DistrustRoads = true, HandOffWhenStranded = true };
        return arm switch
        {
            ConvoyArm.C => setup with { Ledger = false },
            ConvoyArm.D => setup with { Loss = 0.3, DelayChance = 0.3 },
            ConvoyArm.E => setup with { BreakdownTruck = "Truck 2", BreakdownTick = 8 },
            ConvoyArm.G => setup with { StallAfter = 12 },
            ConvoyArm.H => setup with { DistrustRoads = true },
            ConvoyArm.I => setup with { HandOffWhenStranded = true },
            ConvoyArm.J => AllFixes(setup),
            ConvoyArm.K => AllFixes(setup with { BreakdownTruck = "Truck 2", BreakdownTick = 8 }),
            ConvoyArm.L => AllFixes(setup),
            ConvoyArm.S => AllFixes(setup) with { SeatModel = DefaultSeatModel },
            ConvoyArm.N => AllFixes(setup) with { SeatNetwork = ConvoyNetwork.Embedded },
            ConvoyArm.R => AllFixes(setup) with { SeatNetwork = ConvoyNetwork.EmbeddedTrial },
            ConvoyArm.W => AllFixes(setup) with { ShareDistrust = true },
            ConvoyArm.Q => AllFixes(setup) with { ShareDistrust = true, SeatNetwork = ConvoyNetwork.EmbeddedTrialShared },
            ConvoyArm.X => AllFixes(setup) with { LearnedJudge = ConvoyNetwork.EmbeddedWorld },
            ConvoyArm.Y => AllFixes(setup) with { LearnedJudge = ConvoyNetwork.EmbeddedWorld, LearnedJudgeThreshold = 4.720, LearnedJudgeCondemn = 5.785, LearnedJudgeCheckableOnly = true },
            ConvoyArm.Z => AllFixes(setup) with { LearnedJudge = ConvoyNetwork.EmbeddedWorldVaried, LearnedJudgeThreshold = 7.628, LearnedJudgeCondemn = 16.813, LearnedJudgeCheckableOnly = true },
            ConvoyArm.U => AllFixes(setup) with { Noise = SensorNoise.Registered },
            ConvoyArm.V => AllFixes(setup) with { Noise = SensorNoise.Registered, FusedCheck = true },
            ConvoyArm.F => AllFixes(setup) with { Noise = SensorNoise.Registered, LearnedJudge = ConvoyNetwork.EmbeddedWorldNoisy, LearnedJudgeThreshold = 10.82, LearnedJudgeCondemn = 12.534, LearnedJudgeCheckableOnly = true },
            ConvoyArm.P => AllFixes(setup) with { Noise = SensorNoise.Registered, LearnedJudge = ConvoyNetwork.EmbeddedJudgeNoisy, LearnedJudgeThreshold = 0.555544, LearnedJudgeCondemn = 0.955596, LearnedJudgeCheckableOnly = true },
            ConvoyArm.U2 => AllFixes(setup) with { Noise = SensorNoise.SealedRegistered },
            ConvoyArm.V2 => AllFixes(setup) with { Noise = SensorNoise.SealedRegistered, FusedCheck = true, FusedMemory = true },
            ConvoyArm.P2 => AllFixes(setup) with { Noise = SensorNoise.SealedRegistered, LearnedJudge = ConvoyNetwork.EmbeddedJudgeSealed, LearnedJudgeThreshold = 0.499305, LearnedJudgeCondemn = 0.972585, LearnedJudgeCheckableOnly = true },
            ConvoyArm.P3 => AllFixes(setup) with { Noise = SensorNoise.SealedRegistered, LearnedJudge = ConvoyNetwork.EmbeddedJudgeSealed, LearnedJudgeThreshold = 0.421863, LearnedJudgeCondemn = null, LearnedJudgeCheckableOnly = true },
            ConvoyArm.M => AllFixes(setup) with { SeatGuide = ConvoyNetwork.EmbeddedGuide, GuideMargin = 0.5 },
            ConvoyArm.MU => AllFixes(setup) with { Noise = SensorNoise.Registered, SeatGuide = ConvoyNetwork.EmbeddedGuideNoisy, GuideMargin = 0.9 },
            ConvoyArm.MK => AllFixes(setup with { BreakdownTruck = "Truck 2", BreakdownTick = 8 }) with { SeatGuide = ConvoyNetwork.EmbeddedGuide, GuideMargin = 0.5 },
            ConvoyArm.MN => AllFixes(setup) with { SeatGuide = ConvoyNetwork.Embedded, GuideMargin = 0.75 },
            _ => setup,
        };
    }
}
