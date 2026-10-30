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

    /// <summary>The lie judge's second attempt (sealed noise, memory, loop data), the one arm P2 reads.</summary>
    public const string EmbeddedJudgeSealed = "convoy-judge-sealed";

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
            return new ActionScorer("lie probability", (b, a, f, m) => judge.Score(b, a, f, judge.Memory.Count == 0 ? null : m), judge.Memory.Count > 0);
        }
        var world = LoadWorld(nameOrPath, map);
        return new ActionScorer("surprise", (b, a, f, _) => world.Surprise(b, a, f));
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

    /// <summary>The network that copies DeepSeek from its runs on seeds 50 to 99 (step 10), arm MS's guide.</summary>
    public const string EmbeddedCopyDeepSeek = "convoy-copy-deepseek-50";

    /// <summary>Arm M's guide (step 8): the latent subgoal head over <c>convoy-jepa-varied</c>, embedded in this assembly.</summary>
    public const string EmbeddedGuide = "convoy-jepa-varied-goal";

    /// <summary>Arm MU's guide: the latent subgoal head over <c>convoy-jepa-noisy</c>.</summary>
    public const string EmbeddedGuideNoisy = "convoy-jepa-noisy-goal";

    private static readonly ConcurrentDictionary<string, ISeatGuide> LoadedGuides = new(StringComparer.Ordinal);

    /// <summary>
    /// A guide for step 8's seat, by an embedded name or a path: a latent subgoal head (kind <c>goal</c>) with the JEPA it
    /// names (embedded, or the file beside it), or a policy network. Each is checked against the map's schema and vocabulary.
    /// </summary>
    public static ISeatGuide LoadGuide(string nameOrPath, RoadMap map) => LoadedGuides.GetOrAdd(nameOrPath, key =>
    {
        var (json, source) = Text(key);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        if (doc.RootElement.TryGetProperty("kind", out var kind) && kind.GetString() == "policy")
        {
            var network = Load(key);
            Fits(network, map);
            return new NetworkGuide(network);
        }
        var goal = LatentGoal.Parse(json, source);
        var jepaName = Path.GetFileNameWithoutExtension(goal.Jepa);
        var (jepaJson, jepaSource) = Resource(jepaName) ?? (File.ReadAllText(Path.Combine(Path.GetDirectoryName(key) ?? "", goal.Jepa)), Path.Combine(Path.GetDirectoryName(key) ?? "", goal.Jepa));
        var jepa = JepaModel.Parse(jepaJson, jepaSource);
        var schema = ConvoyFeatures.Schema(map);
        if (jepa.World != "convoy" || jepa.SchemaHash != schema.Hash || !jepa.Features.SequenceEqual(schema.Names) || !jepa.Actions.SequenceEqual(ConvoyActions.Vocabulary(map)))
            throw new ArgumentException($"{jepa.Source} was trained for another world, schema or action vocabulary than the map {map.Name}'s");
        return new LatentGuide(jepa, goal);
    });

    /// <summary>The surprise trigger's threshold named <paramref name="name"/> in a latent guide's goal file; null when none is asked for.</summary>
    public static double? GuideSurprise(ISeatGuide guide, string? name) => name is null ? null
        : guide is LatentGuide latent && latent.Goal.SurpriseThresholds.TryGetValue(name, out var t) ? t
        : throw new ArgumentException($"{guide.Name} has no surprise threshold \"{name}\"");

    private static (string Json, string Source)? Resource(string name)
    {
        using var stream = typeof(ConvoyNetwork).Assembly.GetManifestResourceStream($"Creatura.Scenarios.{name}.json");
        return stream is null ? null : (new StreamReader(stream).ReadToEnd(), $"models/{name}.json");
    }

    private static (string Json, string Source) Text(string nameOrPath) => Resource(nameOrPath) ?? (File.ReadAllText(nameOrPath), nameOrPath);

    /// <summary>
    /// Refuses a network trained on the battery's seeds by the owner's exception (step 9: the DeepSeek copy, marked
    /// <c>"batteryException": true</c>) on those seeds, so no loop measure on seeds 0 to 49 is scored with it.
    /// </summary>
    public static void CheckSeed(PolicyNetwork network, int seed)
    {
        if (seed < ConvoyDatasets.FirstTrainingSeed && network.Training is { } training
            && training.TryGetProperty("batteryException", out var exception) && exception.ValueKind == System.Text.Json.JsonValueKind.True)
            throw new ArgumentException($"{network.Source} was trained on the battery's seeds by an exception and never runs on seeds 0 to {ConvoyDatasets.FirstTrainingSeed - 1} (seed {seed})");
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

/// <summary>How a learned judge scores an action from what was seen before it, the action and what was seen after: higher is more like a lie.</summary>
/// <param name="Memory">Whether it reads the truck's memory of its own actions (step 6's second attempt): see <see cref="ActionMemory"/>.</param>
internal sealed record ActionScorer(string Kind, Func<IReadOnlyList<double>, int, IReadOnlyList<double>, IReadOnlyList<double>, double> Score, bool Memory = false);

/// <summary>
/// A truck's memory of its own actions, the two inputs of step 6's second attempt: <c>again</c>, 1 when its previous
/// action was this same action and was reported a success; <c>tries</c>, its earlier tries of this same action in the
/// run, over 5, at most 1. Computed from what the truck did and was told, nothing else, the same way in the loop and in
/// an exported set.
/// </summary>
public sealed class ActionMemory
{
    public static readonly string[] Names = ["again", "tries"];

    private readonly Dictionary<string, int> _tries = new(StringComparer.Ordinal);
    private (string Tool, bool Reported)? _previous;

    /// <summary>The memory inputs for <paramref name="tool"/>, before it is recorded.</summary>
    public double[] For(string tool) =>
        [_previous is { } p && p.Tool == tool && p.Reported ? 1 : 0, Math.Min(_tries.GetValueOrDefault(tool) / 5.0, 1)];

    /// <summary>Whether the truck's previous action was <paramref name="tool"/>, reported a success.</summary>
    public bool Again(string tool) => _previous is { } p && p.Tool == tool && p.Reported;

    /// <summary>After an action ran: what it was and whether it reported success.</summary>
    public void Record(string tool, bool reported)
    {
        _tries[tool] = _tries.GetValueOrDefault(tool) + 1;
        _previous = (tool, reported);
    }
}
