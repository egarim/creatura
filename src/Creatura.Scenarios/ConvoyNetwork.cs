using System.Collections.Concurrent;
using Creatura.Seats;

namespace Creatura.Scenarios;

/// <summary>
/// A trained policy network in a truck's seat (arm N, docs/design-trained-models.md, step 2). The network that ships
/// is <c>models/convoy-policy-j.json</c>, trained by example on arm J's planner and embedded in this assembly; any other
/// file from <c>tools/train/train_policy.py</c> can be named instead. A network is refused unless it was trained for the
/// convoy on a map with the same feature schema and the same action vocabulary as the map it is put in.
/// </summary>
public static class ConvoyNetwork
{
    /// <summary>The network embedded in this assembly, the default for arm N.</summary>
    public const string Embedded = "convoy-policy-j";

    /// <summary>Arm N's network trained further by trial (step 3), the default for arm R.</summary>
    public const string EmbeddedTrial = "convoy-policy-j-trial";

    private static readonly ConcurrentDictionary<string, PolicyNetwork> Loaded = new(StringComparer.Ordinal);

    /// <summary>
    /// An embedded network by its name, or a weights file by its path. An embedded one is read once; a file is read
    /// again when it changed, since the trial trainer rewrites it between rounds.
    /// </summary>
    public static PolicyNetwork Load(string nameOrPath)
    {
        var stream = typeof(ConvoyNetwork).Assembly.GetManifestResourceStream($"Creatura.Scenarios.{nameOrPath}.json");
        if (stream is null)
        {
            var key = $"{Path.GetFullPath(nameOrPath)}@{File.GetLastWriteTimeUtc(nameOrPath).Ticks}";
            return Loaded.GetOrAdd(key, _ => PolicyNetwork.Load(nameOrPath));
        }
        using (stream)
        {
            return Loaded.GetOrAdd(nameOrPath, _ =>
            {
                using var reader = new StreamReader(stream);
                return PolicyNetwork.Parse(reader.ReadToEnd(), $"models/{nameOrPath}.json");
            });
        }
    }

    /// <summary>The network as a truck's seat, after checking it fits the map; the features are the recorder's own encoder.</summary>
    /// <param name="sample">For training by trial: the generator to sample with; null plays the best visible action.</param>
    public static NetworkSeat Seat(PolicyNetwork network, RoadMap map, PhysicalMachine machine, Random? sample = null, double temperature = 1)
    {
        Fits(network, map);
        return new NetworkSeat(network, c => ConvoyFeatures.Encode(map, machine, c.Facts, c.Notes, c.Tick, c.MaxTicks), "convoy-network", sample, temperature);
    }

    /// <summary>Refuses a network trained for another world, another feature schema or another action vocabulary.</summary>
    public static void Fits(PolicyNetwork network, RoadMap map)
    {
        if (network.World != "convoy") throw new ArgumentException($"{network.Source} was trained for the world \"{network.World}\", not the convoy");
        var schema = ConvoyFeatures.Schema(map);
        if (network.SchemaHash != schema.Hash || !network.Features.SequenceEqual(schema.Names))
            throw new ArgumentException($"{network.Source} reads feature schema {network.SchemaHash}; the map {map.Name} gives {schema.Hash}");
        if (!network.Actions.SequenceEqual(ConvoyActions.Vocabulary(map)))
            throw new ArgumentException($"{network.Source} scores another action vocabulary than the map {map.Name}'s");
    }
}
