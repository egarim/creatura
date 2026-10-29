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

    /// <summary>Arm N's network trained by trial on arm W's setup (step 3's second attempt), the default for arm Q.</summary>
    public const string EmbeddedTrialShared = "convoy-policy-w-trial";

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

    /// <summary>The world model arm X's learned judge reads (docs/design-trained-models.md, step 5), embedded in this assembly.</summary>
    public const string EmbeddedWorld = "convoy-world-j";

    /// <summary>The world model trained on varied driving (step 5), the one arm Z's learned judge reads.</summary>
    public const string EmbeddedWorldVaried = "convoy-world-varied";

    /// <summary>The world model retrained on noisy driving (step 6), the one arm F's learned judge reads.</summary>
    public const string EmbeddedWorldNoisy = "convoy-world-noisy";

    /// <summary>The lie judge trained on the noisy world's truth (step 6), the one arm P's learned judge reads.</summary>
    public const string EmbeddedJudgeNoisy = "convoy-judge-noisy";

    private static readonly ConcurrentDictionary<string, WorldModel> LoadedWorlds = new(StringComparer.Ordinal);

    /// <summary>An embedded world model by its name, or a weights file by its path, checked against the map's schema and vocabulary.</summary>
    public static WorldModel LoadWorld(string nameOrPath, RoadMap map) => LoadedWorlds.GetOrAdd(nameOrPath, key =>
    {
        using var stream = typeof(ConvoyNetwork).Assembly.GetManifestResourceStream($"Creatura.Scenarios.{key}.json");
        var model = stream is null ? WorldModel.Load(key) : WorldModel.Parse(new StreamReader(stream).ReadToEnd(), $"models/{key}.json");
        var schema = ConvoyFeatures.Schema(map);
        if (model.World != "convoy" || model.SchemaHash != schema.Hash || !model.Features.SequenceEqual(schema.Names) || !model.Actions.SequenceEqual(ConvoyActions.Vocabulary(map)))
            throw new ArgumentException($"{model.Source} was trained for another world, schema or action vocabulary than the map {map.Name}'s");
        return model;
    });

    private static readonly ConcurrentDictionary<string, LieJudge> LoadedJudges = new(StringComparer.Ordinal);

    /// <summary>
    /// What a learned judge reads (steps 5 and 6), by an embedded name or a path: a world model, scored by its surprise,
    /// or a lie judge, scored by its probability of a lie. Either is checked against the map's schema and vocabulary.
    /// </summary>
    internal static ActionScorer LoadScorer(string nameOrPath, RoadMap map)
    {
        using var stream = typeof(ConvoyNetwork).Assembly.GetManifestResourceStream($"Creatura.Scenarios.{nameOrPath}.json");
        var json = stream is null ? File.ReadAllText(nameOrPath) : new StreamReader(stream).ReadToEnd();
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "judge")
        {
            var judge = LoadJudge(nameOrPath, map);
            return new ActionScorer("lie probability", judge.Score);
        }
        var world = LoadWorld(nameOrPath, map);
        return new ActionScorer("surprise", world.Surprise);
    }

    /// <summary>An embedded lie judge by its name, or a weights file by its path, checked against the map's schema and vocabulary.</summary>
    public static LieJudge LoadJudge(string nameOrPath, RoadMap map) => LoadedJudges.GetOrAdd(nameOrPath, key =>
    {
        using var stream = typeof(ConvoyNetwork).Assembly.GetManifestResourceStream($"Creatura.Scenarios.{key}.json");
        var judge = stream is null ? LieJudge.Load(key) : LieJudge.Parse(new StreamReader(stream).ReadToEnd(), $"models/{key}.json");
        var schema = ConvoyFeatures.Schema(map);
        if (judge.World != "convoy" || judge.SchemaHash != schema.Hash || !judge.Features.SequenceEqual(schema.Names) || !judge.Actions.SequenceEqual(ConvoyActions.Vocabulary(map)))
            throw new ArgumentException($"{judge.Source} was trained for another world, schema or action vocabulary than the map {map.Name}'s");
        return judge;
    });

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

/// <summary>How a learned judge scores an action from what was seen before it, the action and what was seen after: higher is more like a lie.</summary>
internal sealed record ActionScorer(string Kind, Func<IReadOnlyList<double>, int, IReadOnlyList<double>, double> Score);
