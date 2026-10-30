using System.Text.Json;

namespace Creatura.Seats;

/// <summary>
/// A latent subgoal head as written by <c>tools/train/train_goal.py</c> (format <c>creatura.network/1</c>, kind
/// <c>goal</c>): from a <see cref="JepaModel"/>'s embedding of what a vessel sees, the embedding the inner seat's choice
/// usually leads to. A <see cref="GuidedSeat"/> plays the visible action whose predicted next embedding is nearest it.
/// The file also carries the guide's surprise thresholds: percentiles of the JEPA's latent surprise at landed actions.
/// </summary>
public sealed class LatentGoal
{
    public required string World { get; init; }

    public required string SchemaHash { get; init; }

    /// <summary>The file name of the JEPA whose embedding it reads, such as <c>convoy-jepa-varied.json</c>.</summary>
    public required string Jepa { get; init; }

    public required int Embedding { get; init; }

    /// <summary>The surprise thresholds by name (<c>p99</c>, <c>p999</c>).</summary>
    public required IReadOnlyDictionary<string, double> SurpriseThresholds { get; init; }

    public required IReadOnlyList<NetworkLayer> Layers { get; init; }

    public JsonElement? Training { get; init; }

    public string Source { get; init; } = "";

    public static LatentGoal Load(string path) => Parse(File.ReadAllText(path), path);

    public static LatentGoal Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatException($"{source}: no \"{name}\"");
        if (Text("format") != PolicyNetwork.Format) throw new FormatException($"{source}: format is {Text("format")}, expected {PolicyNetwork.Format}");
        if (Text("kind") != "goal") throw new FormatException($"{source}: kind is {Text("kind")}, expected goal");
        static double[] Numbers(JsonElement e) => e.EnumerateArray().Select(n => n.GetDouble()).ToArray();
        var goal = new LatentGoal
        {
            World = Text("world"),
            SchemaHash = Text("schemaHash"),
            Jepa = Text("jepa"),
            Embedding = root.TryGetProperty("embedding", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : throw new FormatException($"{source}: no \"embedding\""),
            SurpriseThresholds = root.GetProperty("surpriseThresholds").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble(), StringComparer.Ordinal),
            Layers = root.GetProperty("layers").EnumerateArray().Select(l => new NetworkLayer(
                l.GetProperty("weights").EnumerateArray().Select(Numbers).ToList(), Numbers(l.GetProperty("bias")), l.GetProperty("activation").GetString()!)).ToList(),
            Training = root.TryGetProperty("training", out var t) ? t.Clone() : null,
            Source = source,
        };
        goal.Check();
        return goal;
    }

    private void Check()
    {
        if (Layers.Count == 0) throw new FormatException($"{Source}: no layers");
        if (Layers[0].Inputs != Embedding || Layers[^1].Outputs != Embedding) throw new FormatException($"{Source}: the layers do not map an embedding of {Embedding} to one of {Embedding}");
        for (var i = 0; i < Layers.Count; i++)
        {
            var layer = Layers[i];
            if (layer.Weights.Count != layer.Outputs || layer.Weights.Any(row => row.Length != layer.Inputs))
                throw new FormatException($"{Source}: layer {i}'s weights are not {layer.Outputs} rows of {layer.Inputs}");
            if (i > 0 && layer.Inputs != Layers[i - 1].Outputs) throw new FormatException($"{Source}: layer {i} takes {layer.Inputs}, layer {i - 1} gives {Layers[i - 1].Outputs}");
            var expected = i < Layers.Count - 1 ? "relu" : "none";
            if (layer.Activation != expected) throw new FormatException($"{Source}: layer {i}'s activation is \"{layer.Activation}\", expected {expected}");
        }
    }

    /// <summary>Refuses a JEPA this head was not trained over.</summary>
    public void Fits(JepaModel jepa)
    {
        if (jepa.World != World || jepa.SchemaHash != SchemaHash || jepa.Embedding != Embedding)
            throw new ArgumentException($"{Source} reads {Jepa}'s embedding; {jepa.Source} is another model");
    }

    /// <summary>The subgoal embedding for <paramref name="embedding"/>.</summary>
    public double[] Predict(IReadOnlyList<double> embedding)
    {
        if (embedding.Count != Embedding) throw new ArgumentException($"the head takes an embedding of {Embedding}, not {embedding.Count}", nameof(embedding));
        var x = embedding.ToArray();
        foreach (var layer in Layers)
        {
            var y = new double[layer.Outputs];
            for (var o = 0; o < y.Length; o++)
            {
                var row = layer.Weights[o];
                var sum = 0.0;
                for (var i = 0; i < row.Length; i++) sum += x[i] * row[i];
                sum += layer.Bias[o];
                y[o] = layer.Activation == "relu" ? Math.Max(sum, 0) : sum;
            }
            x = y;
        }
        return x;
    }
}
