using System.Globalization;
using Creatura.Cells;

namespace Creatura.Scenarios;

/// <summary>
/// Turn-taking between the scheduler and one truck's loop. The loop calls <see cref="WaitForTurn"/> at the start of
/// every tick (through <see cref="ITickedWorld.BeginTick"/>); the scheduler calls <see cref="GiveTurnAsync"/> and gets
/// control back when the truck reaches its next tick or its loop ends. So exactly one truck acts at a time, in the
/// scheduler's order, and the run is the same for the same seed.
/// </summary>
internal sealed class TurnGate
{
    private readonly SemaphoreSlim _turn = new(0);
    private readonly SemaphoreSlim _yielded = new(0);

    public void WaitForTurn(int tick)
    {
        if (tick > 1) _yielded.Release();
        _turn.Wait();
    }

    /// <summary>Lets the truck take one tick. False when its loop ended instead of reaching the next tick.</summary>
    public async Task<bool> GiveTurnAsync(Task run)
    {
        _turn.Release();
        var yielded = _yielded.WaitAsync();
        return await Task.WhenAny(yielded, run).ConfigureAwait(false) == yielded;
    }
}

/// <summary>
/// One truck's view of the shared world: what its sensors report and what its radio has heard, as facts; its tools,
/// as the roads from where it stands, the depot's load and refuel, unloading at a village, claiming a village on the
/// ledger, waiting, and reporting the work done. Each truck's <see cref="ReferenceLoop"/> acts on its view and nothing
/// else. The radio is the truck's cell: what it does is written to its ledger and sent over the mesh, and what other
/// trucks wrote arrives at the start of its ticks.
/// </summary>
public sealed class TruckView : ITickedWorld
{
    public static readonly DateTimeOffset T0 = new(2026, 10, 28, 6, 0, 0, TimeSpan.Zero);

    private readonly ConvoyWorld _world;
    private readonly TruckState _truck;
    private readonly ConvoySetup _setup;
    private readonly LocalCell? _cell;
    private readonly WorkLedger? _ledger;
    private readonly ScenarioMesh? _mesh;
    private readonly IReadOnlyList<string> _others;
    private readonly IReadOnlyList<Tool> _all;
    private readonly Dictionary<string, (int Need, int Tick)> _believed = new(StringComparer.Ordinal);
    private readonly HashSet<string> _transient = new(StringComparer.Ordinal);
    private HashSet<string> _facts = new(StringComparer.Ordinal);
    private int _tick;
    private int _lastSaid;
    private bool _done;
    private bool _stranded;

    internal TurnGate Gate { get; } = new();

    public TruckView(ConvoyWorld world, TruckState truck, ConvoySetup setup, LocalCell? cell, WorkLedger? ledger, ScenarioMesh? mesh)
    {
        _world = world;
        _truck = truck;
        _setup = setup;
        _cell = cell;
        _ledger = ledger;
        _mesh = mesh;
        _others = setup.Trucks.Select(t => t.Name).Where(n => n != truck.Name).ToList();
        foreach (var village in world.Map.Villages) _believed[village.Id] = (village.Demand, 0);
        _all = AllToolsOf(world.Map, Radio);
        Refresh();
    }

    public string Truck => _truck.Name;

    /// <summary>The truck's ledger, when it has a radio.</summary>
    public WorkLedger? Ledger => _ledger;

    /// <summary>Before the first tick: the truck's hello, on its ledger and sent to the others.</summary>
    public void Introduce()
    {
        if (Radio) Say(new HelloPayload(_cell!.Identity));
    }

    /// <summary>True when the truck has a radio: a cell, a ledger and the mesh (every arm but C).</summary>
    public bool Radio => _ledger is not null;

    public int Ticks { get; private set; }

    public IReadOnlySet<string> Facts => _facts;

    public IReadOnlyList<Tool> AllTools => _all;

    public IReadOnlyList<Tool> AvailableTools => _all.Where(Offered).ToList();

    public bool Has(string fact) => _facts.Contains(fact);

    public Tool? Tool(string id) => _all.FirstOrDefault(t => t.Id == id);

    public bool EffectsLanded(Tool tool) => tool.Grants.All(Has);

    /// <summary>The start of a tick in this truck's loop: wait for its turn, take in what the mesh brought, and keep in touch.</summary>
    public void BeginTick(int tick)
    {
        Gate.WaitForTurn(tick);
        _tick = tick;
        _transient.Clear();
        if (Radio && !_truck.Broken)
        {
            var arrived = _mesh!.Arrived(_truck.Name, tick);
            if (arrived.Count > 0) _ledger!.Merge(arrived);
            LetGoOfLostTies();
            if (tick - _lastSaid >= 3) Say(new HeartbeatPayload(_truck.Load));
        }
        Refresh();
    }

    public ActionOutcome Act(string toolId, string? payload = null)
    {
        Ticks++;
        var outcome = Run(toolId, payload);
        Refresh();
        return outcome;
    }

    private ActionOutcome Run(string toolId, string? payload)
    {
        var parts = toolId.Split(':');
        switch (parts[0])
        {
            case "drive" when parts.Length == 3:
            {
                _truck.Target = payload is { } p && p.StartsWith("target=", StringComparison.Ordinal) ? p["target=".Length..] : null;
                var outcome = _world.Drive(_truck, toolId, parts[1], parts[2]);
                if (!outcome.Succeeded && outcome.Observation == "out of fuel" && _setup.HandOffWhenStranded && !_stranded)
                {
                    _stranded = true;
                    if (Radio && MyClaim() is { } held)
                    {
                        Say(new HandOffPayload(held.Id, $"stranded without fuel at {_truck.At}"));
                        _truck.Claim = null;
                        _world.Record(new ReplayEvent("release", $"{_truck.Name} is out of fuel at {_world.Name(_truck.At)} and hands {_world.Name(held.Village)} back.") { Truck = _truck.Name, Village = held.Village });
                    }
                }
                if (outcome.Succeeded && _truck.At == parts[2])
                {
                    if (_world.Map.Node(_truck.At).Kind == NodeKind.Village) Observe(_truck.At);
                    if (MyClaim() is { } claim) Say(new ProgressPayload(claim.Id, Row(claim.Village)));
                }
                return outcome;
            }
            case "load":
                return _world.Load(_truck, toolId);
            case "refuel":
                return _world.Refuel(_truck, toolId);
            case "unload" when parts.Length == 2:
            {
                var village = parts[1];
                var outcome = _world.Unload(_truck, toolId, village);
                if (_truck.At == village) Observe(village);
                if (outcome.Succeeded) _transient.Add($"delivered:{village}");
                if (Radio && !_truck.Broken) Report(village);
                return outcome;
            }
            case "claim" when parts.Length == 2:
                return Claim(toolId, parts[1]);
            case "wait":
                _truck.ActedThisTick = true;
                _transient.Add("waited");
                return new ActionOutcome(toolId, true, ["waited"], "waited");
            case "report-done":
                _truck.ActedThisTick = true;
                if (_truck.Broken) return new ActionOutcome(toolId, false, [], "broken down");
                if (!_believed.Values.All(b => b.Need == 0)) return new ActionOutcome(toolId, false, [], "villages still in need");
                _done = true;
                return new ActionOutcome(toolId, true, ["convoy.done"], "every village served, as far as this truck knows");
            default:
                return new ActionOutcome(toolId, false, [], "tool unavailable");
        }
    }

    private bool Offered(Tool tool)
    {
        var parts = tool.Id.Split(':');
        return parts[0] switch
        {
            "drive" => _world.Map.Road(parts[1]).Touches(_truck.At) && _world.Map.Road(parts[1]).Other(_truck.At) == parts[2],
            "load" or "refuel" => _truck.At == _world.Map.Depot,
            "unload" => _truck.At == parts[1],
            "claim" => Radio,
            _ => true,
        };
    }

    private ActionOutcome Claim(string toolId, string village)
    {
        _truck.ActedThisTick = true;
        if (!Radio || _truck.Broken) return new ActionOutcome(toolId, false, [], "no radio");
        if (MyClaim()?.Village == village) return new ActionOutcome(toolId, true, [$"mine:{village}"], "already claimed");
        if (MyClaim() is { } old) Say(new HandOffPayload(old.Id, $"switching to {village}"));

        var quiet = Owners().GetValueOrDefault(village);
        var takeover = quiet is { Open: true } && quiet.Truck != _truck.Name ? quiet.Id : null;
        var entry = Say(new ClaimPayload($"deliver:{village}") { Tool = _truck.Name, Session = takeover });
        _truck.Claim = village;
        var name = _world.Name(village);
        _world.Record(takeover is null
            ? new ReplayEvent("claim", $"{_truck.Name} claims the delivery to {name} on the ledger.") { Truck = _truck.Name, Village = village }
            : new ReplayEvent("takeover", quiet!.Quiet
                ? $"{_truck.Name} takes over {name}: {quiet.Truck} has been silent for {_tick - quiet.LastHeard} ticks."
                : $"{_truck.Name} takes over {name}: {quiet.Truck} has delivered nothing there for {_tick - quiet.LastWork} ticks.") { Truck = _truck.Name, Village = village });
        Refresh();
        return new ActionOutcome(toolId, true, [$"mine:{village}"], $"claimed {entry.Id}");
    }

    /// <summary>After an unload, or an arrival at a village with nothing left to do: what this truck now knows of it, on the ledger.</summary>
    private void Report(string village)
    {
        var need = _believed[village].Need;
        var claim = MyClaim();
        if (claim?.Village != village)
        {
            // Delivered without holding the claim (it was lost to another truck on the way): say so all the same.
            var entry = Say(new ClaimPayload($"deliver:{village}") { Tool = _truck.Name });
            claim = (village, entry.Id);
        }

        if (need == 0)
        {
            Say(new ReleasePayload(claim.Value.Id, Row(village) + " did=unload"));
            _truck.Claim = null;
            _world.Record(new ReplayEvent("release", $"{_truck.Name} reports {_world.Name(village)} served.") { Truck = _truck.Name, Village = village });
        }
        else
        {
            Say(new ProgressPayload(claim.Value.Id, Row(village) + " did=unload"));
        }
    }

    /// <summary>A claim this truck made that lost a tie to another truck's earlier claim: handed off, so the ledger says so.</summary>
    private void LetGoOfLostTies()
    {
        if (MyClaimEntry() is not { } mine) return;
        var village = VillageOf(mine);
        var owner = Owners().GetValueOrDefault(village);
        if (owner is null || owner.Truck == _truck.Name || owner.Open) return;
        Say(new HandOffPayload(mine.Id, $"{owner.Truck} claimed {village} first"));
        _truck.Claim = null;
        _world.Record(new ReplayEvent("release", $"{_truck.Name} leaves {_world.Name(village)} to {owner.Truck}, who claimed it first.") { Truck = _truck.Name, Village = village });
    }

    private string Row(string village) =>
        string.Create(CultureInfo.InvariantCulture, $"v={village} need={_believed[village].Need} at={_truck.At} fuel={_world.Gauge(_truck)} load={_truck.Load}");

    private void Observe(string village) => _believed[village] = (_world.Need[village], _tick);

    private SignedEntry Say<T>(T payload) where T : IEntryPayload
    {
        var entry = _ledger!.Append(_cell!, payload, T0.AddSeconds(_tick));
        _lastSaid = _tick;
        _mesh!.Publish(_truck.Name, entry, _tick, _others, _world.Record);
        return entry;
    }

    /// <summary>A claim on the ledger and who holds it: the truck, the claim's id, when it was made, when its truck was last heard, whether it went quiet.</summary>
    private sealed record Holder(string Truck, string Id, int Claimed, int LastHeard, bool Quiet)
    {
        /// <summary>Alive, but nothing delivered to the village for the stall period (only with <see cref="ConvoySetup.StallAfter"/>).</summary>
        public bool Stalled { get; init; }

        public int LastWork { get; init; }

        /// <summary>May be taken over: silent, or stalled.</summary>
        public bool Open => Quiet || Stalled;
    }

    private static string VillageOf(SignedEntry claim) => claim.Body<ClaimPayload>().Goal.Replace("deliver:", "", StringComparison.Ordinal);

    private static int TickOf(SignedEntry entry) => (int)(entry.Timestamp - T0).TotalSeconds;

    /// <summary>
    /// For each village, the claim that holds it: among the active claims not superseded by a takeover, the earliest,
    /// ties to the truck whose name sorts first, never to a cell id. A claim whose truck has been silent for the
    /// takeover period is quiet, and may be taken over.
    /// </summary>
    private Dictionary<string, Holder> Owners()
    {
        var owners = new Dictionary<string, Holder>(StringComparer.Ordinal);
        if (!Radio) return owners;
        var entries = _ledger!.Entries;
        var heard = entries.GroupBy(e => e.Cell).ToDictionary(g => g.Key, g => g.Max(TickOf), StringComparer.Ordinal);
        var claims = entries.Where(e => e.Kind == EntryKind.Claim && e.Body<ClaimPayload>().Goal.StartsWith("deliver:", StringComparison.Ordinal)).ToList();
        var superseded = claims.Select(c => c.Body<ClaimPayload>().Session).OfType<string>().ToHashSet(StringComparer.Ordinal);
        // The last delivery each cell reported to each village: what the stall rule counts as progress.
        var delivered = new Dictionary<(string Cell, string Village), int>();
        foreach (var e in entries.Where(e => e.Kind is EntryKind.Progress or EntryKind.Release))
        {
            var row = e.Kind == EntryKind.Progress ? e.Body<ProgressPayload>().Row : e.Body<ReleasePayload>().Evidence;
            if (!row.Contains("did=unload", StringComparison.Ordinal)) continue;
            var village = row.Split(' ').FirstOrDefault(p => p.StartsWith("v=", StringComparison.Ordinal))?[2..];
            if (village is not null) delivered[(e.Cell, village)] = Math.Max(delivered.GetValueOrDefault((e.Cell, village)), TickOf(e));
        }

        foreach (var group in claims.Where(c => !superseded.Contains(c.Id) && _ledger.StateOf(c.Id) == ClaimState.Active).GroupBy(VillageOf))
        {
            var holders = group.Select(c =>
            {
                var last = heard.GetValueOrDefault(c.Cell);
                var work = Math.Max(TickOf(c), delivered.GetValueOrDefault((c.Cell, group.Key)));
                return new Holder(c.Body<ClaimPayload>().Tool ?? c.Cell, c.Id, TickOf(c), last, _tick - last >= _setup.TakeoverAfter)
                {
                    LastWork = work,
                    Stalled = _setup.StallAfter is { } stall && _tick - work >= stall,
                };
            }).ToList();
            var live = holders.Where(h => !h.Open).OrderBy(h => h.Claimed).ThenBy(h => h.Truck, StringComparer.Ordinal).FirstOrDefault();
            owners[group.Key] = live ?? holders.OrderBy(h => h.Claimed).ThenBy(h => h.Truck, StringComparer.Ordinal).First();
        }
        return owners;
    }

    private SignedEntry? MyClaimEntry() =>
        _ledger?.Entries.LastOrDefault(e => e.Kind == EntryKind.Claim && e.Cell == _cell!.Id && _ledger.StateOf(e.Id) == ClaimState.Active
            && e.Body<ClaimPayload>().Goal.StartsWith("deliver:", StringComparison.Ordinal));

    private (string Village, string Id)? MyClaim() =>
        MyClaimEntry() is { } mine && Owners().GetValueOrDefault(VillageOf(mine))?.Id == mine.Id ? (VillageOf(mine), mine.Id) : null;

    /// <summary>What the ledger's delivery reports say each village still needs, newer reports winning and, within a tick, the lower.</summary>
    private void TakeInReports()
    {
        if (!Radio) return;
        foreach (var entry in _ledger!.Entries.Where(e => e.Kind is EntryKind.Progress or EntryKind.Release))
        {
            var row = entry.Kind == EntryKind.Progress ? entry.Body<ProgressPayload>().Row : entry.Body<ReleasePayload>().Evidence;
            var fields = row.Split(' ').Select(p => p.Split('=')).Where(p => p.Length == 2).ToDictionary(p => p[0], p => p[1], StringComparer.Ordinal);
            if (!fields.TryGetValue("v", out var village) || !fields.TryGetValue("need", out var text) || !_believed.ContainsKey(village)) continue;
            var tick = TickOf(entry);
            if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var need)) continue;

            // A newer report wins. Two reports of one tick come in the ledger's order, which ties on random cell ids,
            // so the lower need is kept: needs only go down, so the lower is the later.
            var (held, heldTick) = _believed[village];
            if (tick > heldTick || (tick == heldTick && need < held)) _believed[village] = (need, tick);
        }
    }

    private void Refresh()
    {
        TakeInReports();
        var facts = new HashSet<string>(StringComparer.Ordinal)
        {
            $"at:{_truck.At}",
            $"fuel:{_world.Gauge(_truck)}",
            $"load:{_truck.Load}",
            $"truck:{_truck.Name}",
        };
        if (_truck.Load > 0) facts.Add("loaded");
        if (_world.Gauge(_truck) >= _truck.Spec.Machine.FuelCapacity) facts.Add("fuel:full");
        if (Radio) facts.Add("radio");
        foreach (var road in _world.Map.Roads.Where(r => _world.SensedOpen(r.Id))) facts.Add($"road:{road.Id}:open");
        foreach (var (village, (need, _)) in _believed)
        {
            facts.Add($"need:{village}:{need}");
            if (need == 0) facts.Add($"served:{village}");
        }
        if (_believed.Values.All(b => b.Need == 0)) facts.Add("all.served");

        if (Radio)
        {
            var mine = MyClaim();
            if (mine is { } m) facts.Add($"mine:{m.Village}");
            foreach (var (village, holder) in Owners())
            {
                if (holder.Truck == _truck.Name) continue;
                facts.Add(holder.Open ? $"quiet:{village}:{holder.Truck}" : $"claimed:{village}:{holder.Truck}");
            }
        }
        if (_done) facts.Add("convoy.done");
        if (_stranded) facts.Add("stranded");
        facts.UnionWith(_transient);
        _facts = facts;
    }

    /// <summary>Every tool any truck could have on this map.</summary>
    private static IReadOnlyList<Tool> AllToolsOf(RoadMap map, bool radio)
    {
        var tools = new List<Tool>();
        foreach (var road in map.Roads)
        {
            tools.Add(new Tool($"drive:{road.Id}:{road.B}", [$"at:{road.A}"], [$"at:{road.B}"], "write"));
            tools.Add(new Tool($"drive:{road.Id}:{road.A}", [$"at:{road.B}"], [$"at:{road.A}"], "write"));
        }
        tools.Add(new Tool("load", [$"at:{map.Depot}"], ["loaded"], "write"));
        tools.Add(new Tool("refuel", [$"at:{map.Depot}"], ["fuel:full"], "write"));
        foreach (var village in map.Villages)
        {
            tools.Add(new Tool($"unload:{village.Id}", [$"at:{village.Id}", "loaded"], [$"delivered:{village.Id}"], "write"));
            if (radio) tools.Add(new Tool($"claim:{village.Id}", [], [$"mine:{village.Id}"], "contact"));
        }
        tools.Add(new Tool("wait", [], ["waited"], "verify"));
        tools.Add(new Tool("report-done", ["all.served"], ["convoy.done"], "contact"));
        return tools.OrderBy(t => t.Id, StringComparer.Ordinal).ToList();
    }
}
