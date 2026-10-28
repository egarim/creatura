using System.Text.Json;
using System.Text.Json.Serialization;

namespace Creatura.Scenarios;

/// <summary>A place, as a viewer draws it.</summary>
public sealed record ReplayNode(string Id, string Name, double X, double Y, string Kind, int Demand);

public sealed record ReplayRoad(string Id, string A, string B);

/// <summary>A truck as the run starts: its cell, its design and its machine.</summary>
public sealed record ReplayTruck(string Name, string Cell, string Design, int Capacity, int FuelCapacity, string Start);

/// <summary>The first line of a replay: what the run is, the map and the trucks (docs/replay-format.md).</summary>
public sealed record ReplayHeader(string Scenario, int Seed, string Arm, string ArmText, int MaxTicks,
    IReadOnlyList<ReplayNode> Nodes, IReadOnlyList<ReplayRoad> Roads, IReadOnlyList<ReplayTruck> Trucks, IReadOnlyList<string> Faults)
{
    public string Format { get; init; } = "creatura.replay/1";
}

/// <summary>
/// One truck at the end of a tick: where it is, its true fuel and what its gauge says, its load, its state
/// ("acting", "finished", "broken", "stranded") and the village it has claimed.
/// </summary>
public sealed record TruckFrame(string Name, string At, int Fuel, int FuelBelieved, int Load, string State, string? Claim);

/// <summary>
/// Something that happened in a tick, with a sentence for the story. Kinds: drive, blocked, lie, caught, refused, load,
/// refuel, unload, wasted, claim, takeover, release, distrust, message, lost, delayed, closure, breakdown, out-of-fuel,
/// done, stopped.
/// </summary>
public sealed record ReplayEvent(string Kind, string Text)
{
    public string? Truck { get; init; }
    public string? Road { get; init; }
    public string? Village { get; init; }
    public string? From { get; init; }
    public string? To { get; init; }

    /// <summary>The control layer's verdict on an action: Landed, Lied, HonestFailure.</summary>
    public string? Verdict { get; init; }
}

/// <summary>One tick of a replay: every truck, every village's remaining need, the closed roads, and the events.</summary>
public sealed record ReplayTick(int Tick, IReadOnlyList<TruckFrame> Trucks, IReadOnlyDictionary<string, int> Needs, IReadOnlyList<string> Closed, IReadOnlyList<ReplayEvent> Events);

/// <summary>
/// What the judge read from the world (D6). <see cref="Delivered"/> counts only what reached a village; a truck's own
/// report counts for nothing.
/// </summary>
public sealed record ConvoyMeasures(int Delivered, int Demand, int WastedTrips, int Stranded, int LiesBelieved, int LyingRoadRepeats,
    int TicksToServe, bool LedgersIdentical, int IdleWhileUnserved, int MessagesSent, int MessagesLost)
{
    public bool AllServed => Delivered >= Demand;
}

/// <summary>The last line of a replay.</summary>
public sealed record ReplaySummary(ConvoyMeasures Measures, IReadOnlyList<string> Outcome);

/// <summary>
/// How a UI follows a run: live, tick by tick, or from a replay file read back. The scenario calls it on its own
/// thread, between ticks; an observer that is slow slows the run, nothing else.
/// </summary>
public interface IScenarioObserver
{
    void Start(ReplayHeader header);

    void Tick(ReplayTick tick);

    void End(ReplaySummary summary);
}

/// <summary>
/// Writes a replay as JSON lines: <c>{"type":"header",...}</c>, one <c>{"type":"tick",...}</c> per tick, then
/// <c>{"type":"end",...}</c>. camelCase names, nulls left out.
/// </summary>
public sealed class JsonlReplayWriter(TextWriter output) : IScenarioObserver
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public void Start(ReplayHeader header) => Line("header", header);

    public void Tick(ReplayTick tick) => Line("tick", tick);

    public void End(ReplaySummary summary)
    {
        Line("end", summary);
        output.Flush();
    }

    private void Line<T>(string type, T value)
    {
        var node = JsonSerializer.SerializeToNode(value, Options)!.AsObject();
        var line = new System.Text.Json.Nodes.JsonObject { ["type"] = type };
        foreach (var (key, item) in node.ToList())
        {
            node.Remove(key);
            line[key] = item;
        }
        output.WriteLine(line.ToJsonString(Options));
    }
}
