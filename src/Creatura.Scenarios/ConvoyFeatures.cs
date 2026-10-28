using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Creatura.Scenarios;

/// <summary>The names of a feature vector's entries, in order, and a hash of them that a trained model is tied to.</summary>
public sealed record FeatureSchema(IReadOnlyList<string> Names, string Hash);

/// <summary>
/// The convoy's feature encoder (docs/design-trained-models.md, step 1): what a truck's seat reads, as numbers a network
/// can take. Every entry is between 0 and 1. It reads the same facts and notes the seat reads, and the truck's machine,
/// nothing the seat does not see. The schema depends on the map only (its places, villages and roads), so a model
/// trained on one map refuses another whose schema hash differs.
/// <list type="bullet">
/// <item><c>at.&lt;place&gt;</c>: where the truck is, one of them 1;</item>
/// <item><c>fuel</c>, <c>load</c>: the gauge's reading over the tank, the load over the capacity; <c>loaded</c>;</item>
/// <item><c>machine.capacity</c>, <c>machine.range</c>: the truck's capacity over 10 and its range over 20;</item>
/// <item>per village: <c>need</c> (believed need over demand), <c>served</c>, <c>mine</c>, <c>claimed</c> (by another), <c>quiet</c>;</item>
/// <item>per road: <c>open</c> (the sensor's report), <c>avoid</c> (distrusted, from the notes);</item>
/// <item><c>radio</c>, <c>all.served</c>, <c>stranded</c>, <c>time</c> (tick over the run's length).</item>
/// </list>
/// </summary>
public static class ConvoyFeatures
{
    public static FeatureSchema Schema(RoadMap map)
    {
        var names = new List<string>();
        names.AddRange(map.Nodes.Select(n => $"at.{n.Id}"));
        names.AddRange(["fuel", "load", "loaded", "machine.capacity", "machine.range"]);
        foreach (var v in map.Villages)
        {
            names.AddRange([$"need.{v.Id}", $"served.{v.Id}", $"mine.{v.Id}", $"claimed.{v.Id}", $"quiet.{v.Id}"]);
        }
        foreach (var r in map.Roads) names.AddRange([$"open.{r.Id}", $"avoid.{r.Id}"]);
        names.AddRange(["radio", "all.served", "stranded", "time"]);
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("convoy features v1\n" + string.Join('\n', names))))[..16];
        return new FeatureSchema(names, hash);
    }

    public static double[] Encode(RoadMap map, PhysicalMachine machine, IReadOnlySet<string> facts, IEnumerable<string> notes, int tick, int maxTicks)
    {
        var schema = Schema(map);
        var x = new double[schema.Names.Count];
        var index = schema.Names.Select((n, i) => (n, i)).ToDictionary(p => p.n, p => p.i, StringComparer.Ordinal);
        void Set(string name, double value) => x[index[name]] = Math.Clamp(value, 0, 1);

        var avoid = notes.Where(n => n.StartsWith("avoid:", StringComparison.Ordinal)).Select(n => n["avoid:".Length..]).ToHashSet(StringComparer.Ordinal);
        foreach (var fact in facts)
        {
            var parts = fact.Split(':');
            switch (parts[0])
            {
                case "at" when parts.Length == 2 && index.ContainsKey($"at.{parts[1]}"): Set($"at.{parts[1]}", 1); break;
                case "fuel" when parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var fuel):
                    Set("fuel", fuel / (double)Math.Max(1, machine.FuelCapacity)); break;
                case "load" when parts.Length == 2 && int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var load):
                    Set("load", load / (double)Math.Max(1, machine.Capacity)); break;
                case "loaded": Set("loaded", 1); break;
                case "need" when parts.Length == 3 && int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var need) && index.ContainsKey($"need.{parts[1]}"):
                    Set($"need.{parts[1]}", need / (double)Math.Max(1, map.Node(parts[1]).Demand)); break;
                case "served" when parts.Length == 2 && index.ContainsKey($"served.{parts[1]}"): Set($"served.{parts[1]}", 1); break;
                case "mine" when parts.Length == 2 && index.ContainsKey($"mine.{parts[1]}"): Set($"mine.{parts[1]}", 1); break;
                case "claimed" when parts.Length == 3 && index.ContainsKey($"claimed.{parts[1]}"): Set($"claimed.{parts[1]}", 1); break;
                case "quiet" when parts.Length == 3 && index.ContainsKey($"quiet.{parts[1]}"): Set($"quiet.{parts[1]}", 1); break;
                case "road" when parts.Length == 3 && parts[2] == "open" && index.ContainsKey($"open.{parts[1]}"): Set($"open.{parts[1]}", 1); break;
                case "radio": Set("radio", 1); break;
                case "all.served": Set("all.served", 1); break;
                case "stranded": Set("stranded", 1); break;
            }
        }
        foreach (var road in avoid.Where(r => index.ContainsKey($"avoid.{r}"))) Set($"avoid.{road}", 1);
        Set("machine.capacity", machine.Capacity / 10.0);
        Set("machine.range", machine.FuelCapacity / (double)Math.Max(1, machine.FuelPerStep) / 20.0);
        Set("time", tick / (double)Math.Max(1, maxTicks));
        return x;
    }
}

/// <summary>
/// The convoy's action vocabulary: every action any truck could take on a map, in a fixed order, so a network's output
/// has one entry per action and the actions not visible this tick are masked out.
/// </summary>
public static class ConvoyActions
{
    public static List<string> Vocabulary(RoadMap map) => TruckView.AllToolsOf(map, radio: true).Select(t => t.Id).ToList();

    /// <summary>The vocabulary indexes of the visible tools.</summary>
    public static IReadOnlyList<int> Mask(RoadMap map, IEnumerable<string> visible)
    {
        var vocabulary = Vocabulary(map);
        return visible.Select(id => vocabulary.IndexOf(id)).Where(i => i >= 0).Order().ToList();
    }
}
