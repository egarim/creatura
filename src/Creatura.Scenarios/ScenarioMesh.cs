using Creatura.Cells;

namespace Creatura.Scenarios;

/// <summary>
/// The scenario's in-memory mesh between the trucks' ledgers: every signed entry a truck writes is sent to every other
/// truck, and each copy may be lost or arrive one to three ticks late. Every choice comes from the seed, in the order
/// the entries are sent, and trucks send in turn order, so a seed always loses the same messages. A copy that arrives
/// is merged by its receiver, which checks its signature like any other arrival.
/// </summary>
public sealed class ScenarioMesh(int seed, double loss, double delayChance)
{
    private readonly Random _random = new(seed);
    private readonly List<(int At, string To, SignedEntry Entry)> _inFlight = [];

    public int Sent { get; private set; }

    public int Lost { get; private set; }

    /// <summary>Sends an entry from one truck to the others; <paramref name="tell"/> receives the events worth a line in the story.</summary>
    public void Publish(string from, SignedEntry entry, int tick, IEnumerable<string> to, Action<ReplayEvent> tell)
    {
        var told = entry.Kind is EntryKind.Claim or EntryKind.Release or EntryKind.HandOff;
        foreach (var recipient in to.Order(StringComparer.Ordinal))
        {
            Sent++;
            if (_random.NextDouble() < loss)
            {
                Lost++;
                if (told) tell(new ReplayEvent("lost", $"The {Describe(entry)} from {from} to {recipient} is lost on the mesh.") { Truck = from, To = recipient });
                continue;
            }

            var late = _random.NextDouble() < delayChance ? _random.Next(1, 4) : 0;
            _inFlight.Add((tick + 1 + late, recipient, entry));
            if (told)
            {
                tell(late > 0
                    ? new ReplayEvent("delayed", $"The {Describe(entry)} from {from} to {recipient} will arrive {late} tick{(late == 1 ? "" : "s")} late.") { Truck = from, To = recipient }
                    : new ReplayEvent("message", $"{from} tells {recipient}: {Describe(entry)}.") { Truck = from, To = recipient });
            }
        }
    }

    /// <summary>The entries that have arrived for a truck by this tick, in the order they were sent.</summary>
    public IReadOnlyList<SignedEntry> Arrived(string truck, int tick)
    {
        var arrived = _inFlight.Where(m => m.To == truck && m.At <= tick).ToList();
        _inFlight.RemoveAll(m => m.To == truck && m.At <= tick);
        return arrived.Select(m => m.Entry).ToList();
    }

    private static string Describe(SignedEntry entry) => entry.Kind switch
    {
        EntryKind.Claim => $"claim on {entry.Body<ClaimPayload>().Goal.Replace("deliver:", "", StringComparison.Ordinal)}",
        EntryKind.Release => "delivery report",
        EntryKind.HandOff => "hand-off",
        EntryKind.Warning => $"warning about {entry.Body<WarningPayload>().Subject.Replace("road:", "road ", StringComparison.Ordinal)}",
        _ => entry.Kind.ToString().ToLowerInvariant(),
    };
}
