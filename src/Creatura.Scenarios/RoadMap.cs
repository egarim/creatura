namespace Creatura.Scenarios;

public enum NodeKind { Depot, Junction, Village }

/// <summary>A place on the map, with where it is drawn and, for a village, how many units of supplies it needs.</summary>
public sealed record MapNode(string Id, string Name, double X, double Y, NodeKind Kind, int Demand = 0);

/// <summary>A road between two places. Every road takes one tick to drive.</summary>
public sealed record MapRoad(string Id, string A, string B)
{
    public bool Touches(string node) => node == A || node == B;

    public string Other(string node) => node == A ? B : node == B ? A : throw new ArgumentException($"{Id} does not touch {node}");
}

/// <summary>
/// The scenario's map: a graph of places and roads. Static knowledge every truck carries, like a paper map; which
/// roads are open is something the trucks only sense, and their sensors can lie.
/// </summary>
public sealed class RoadMap
{
    public RoadMap(string name, IEnumerable<MapNode> nodes, IEnumerable<MapRoad> roads)
    {
        Name = name;
        Nodes = nodes.ToList();
        Roads = roads.OrderBy(r => r.Id, StringComparer.Ordinal).ToList();
        Depot = Nodes.Single(n => n.Kind == NodeKind.Depot).Id;
    }

    public string Name { get; }

    public IReadOnlyList<MapNode> Nodes { get; }

    public IReadOnlyList<MapRoad> Roads { get; }

    public string Depot { get; }

    public IReadOnlyList<MapNode> Villages => Nodes.Where(n => n.Kind == NodeKind.Village).OrderBy(n => n.Id, StringComparer.Ordinal).ToList();

    public MapNode Node(string id) => Nodes.Single(n => n.Id == id);

    public MapRoad Road(string id) => Roads.Single(r => r.Id == id);

    public IEnumerable<MapRoad> RoadsFrom(string node) => Roads.Where(r => r.Touches(node));

    /// <summary>The shortest path by roads that <paramref name="usable"/> allows, as roads in order; empty when already there, null when there is none. Ties go by road id.</summary>
    public IReadOnlyList<MapRoad>? Path(string from, string to, Func<MapRoad, bool> usable)
    {
        if (from == to) return [];
        var previous = new Dictionary<string, MapRoad>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal) { from };
        var queue = new Queue<string>([from]);
        while (queue.Count > 0)
        {
            var here = queue.Dequeue();
            foreach (var road in RoadsFrom(here).Where(usable))
            {
                var next = road.Other(here);
                if (!seen.Add(next)) continue;
                previous[next] = road;
                if (next == to)
                {
                    var path = new List<MapRoad>();
                    for (var at = to; at != from; at = previous[at].Other(at)) path.Add(previous[at]);
                    path.Reverse();
                    return path;
                }
                queue.Enqueue(next);
            }
        }
        return null;
    }

    public int? Distance(string from, string to, Func<MapRoad, bool> usable) => Path(from, to, usable)?.Count;

    /// <summary>
    /// The convoy's map: a depot, three crossings and four villages cut off by a flood, joined by twelve roads.
    /// Demand: Alba 8, Brisa 7, Ceiba 9, Dalia 6, 30 units against 15 of capacity, so every truck makes two trips.
    /// </summary>
    public static RoadMap FloodedValley() => new("the flooded valley",
    [
        new("depot", "Depot", 0, 0, NodeKind.Depot),
        new("j1", "North crossing", 2, 1.2, NodeKind.Junction),
        new("j2", "South crossing", 2, -1.2, NodeKind.Junction),
        new("j3", "East crossing", 4, 0, NodeKind.Junction),
        new("alba", "Alba", 2.5, 3, NodeKind.Village, 8),
        new("brisa", "Brisa", 5.5, 2, NodeKind.Village, 7),
        new("ceiba", "Ceiba", 5.5, -2, NodeKind.Village, 9),
        new("dalia", "Dalia", 2.5, -3, NodeKind.Village, 6),
    ],
    [
        new("r01", "depot", "j1"), new("r02", "depot", "j2"), new("r03", "j1", "j2"), new("r04", "j1", "alba"),
        new("r05", "j1", "j3"), new("r06", "j2", "j3"), new("r07", "j2", "dalia"), new("r08", "j3", "brisa"),
        new("r09", "j3", "ceiba"), new("r10", "alba", "brisa"), new("r11", "ceiba", "dalia"), new("r12", "depot", "alba"),
    ]);
}
