using System.Text.Json;

namespace Creatura.Seats;

/// <summary>
/// A learned world model as written by <c>tools/train/train_world.py</c> (format <c>creatura.network/1</c>, kind
/// <c>world</c>): given the features a vessel sees and an action, the features it should see after, each between 0 and
/// 1. It decides nothing. Its <see cref="Surprise"/> at what was then observed is how far the world did not do what
/// the model expected: the cross-entropy summed over the features, as the trainer computes it.
/// </summary>
public sealed class WorldModel
{
    /// <summary>The clamp on a predicted probability before its logarithm, the trainer's own.</summary>
    public const double Epsilon = 1e-6;

    public required string World { get; init; }

    public required string Map { get; init; }

    public required string SchemaHash { get; init; }

    public required IReadOnlyList<string> Features { get; init; }

    public required IReadOnlyList<string> Actions { get; init; }

    public required IReadOnlyList<NetworkLayer> Layers { get; init; }

    public JsonElement? Training { get; init; }

    public string Source { get; init; } = "";

    public static WorldModel Load(string path) => Parse(File.ReadAllText(path), path);

    public static WorldModel Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatException($"{source}: no \"{name}\"");
        if (Text("format") != PolicyNetwork.Format) throw new FormatException($"{source}: format is {Text("format")}, expected {PolicyNetwork.Format}");
        if (Text("kind") != "world") throw new FormatException($"{source}: kind is {Text("kind")}, expected world");
        if (Text("inputs") != "features+action") throw new FormatException($"{source}: inputs \"{Text("inputs")}\" is not one this model knows");
        static List<string> Strings(JsonElement e) => e.EnumerateArray().Select(s => s.GetString()!).ToList();
        static double[] Numbers(JsonElement e) => e.EnumerateArray().Select(n => n.GetDouble()).ToArray();

        var model = new WorldModel
        {
            World = Text("world"),
            Map = Text("map"),
            SchemaHash = Text("schemaHash"),
            Features = Strings(root.GetProperty("features")),
            Actions = Strings(root.GetProperty("actions")),
            Layers = root.GetProperty("layers").EnumerateArray().Select(l => new NetworkLayer(
                l.GetProperty("weights").EnumerateArray().Select(Numbers).ToList(), Numbers(l.GetProperty("bias")), l.GetProperty("activation").GetString()!)).ToList(),
            Training = root.TryGetProperty("training", out var t) ? t.Clone() : null,
            Source = source,
        };
        model.Check();
        return model;
    }

    private void Check()
    {
        if (Layers.Count == 0) throw new FormatException($"{Source}: no layers");
        if (Layers[0].Inputs != Features.Count + Actions.Count) throw new FormatException($"{Source}: the first layer takes {Layers[0].Inputs} inputs, the model names {Features.Count + Actions.Count}");
        if (Layers[^1].Outputs != Features.Count) throw new FormatException($"{Source}: the last layer gives {Layers[^1].Outputs} values for {Features.Count} features");
        for (var i = 0; i < Layers.Count; i++)
        {
            var layer = Layers[i];
            if (layer.Weights.Count != layer.Outputs || layer.Weights.Any(row => row.Length != layer.Inputs))
                throw new FormatException($"{Source}: layer {i}'s weights are not {layer.Outputs} rows of {layer.Inputs}");
            if (i > 0 && layer.Inputs != Layers[i - 1].Outputs) throw new FormatException($"{Source}: layer {i} takes {layer.Inputs}, layer {i - 1} gives {Layers[i - 1].Outputs}");
            var expected = i < Layers.Count - 1 ? "relu" : "sigmoid";
            if (layer.Activation != expected) throw new FormatException($"{Source}: layer {i}'s activation is \"{layer.Activation}\", expected {expected}");
        }
    }

    /// <summary>The features the model expects after <paramref name="action"/> (an index into <see cref="Actions"/>), each 0..1.</summary>
    public double[] Predict(IReadOnlyList<double> features, int action)
    {
        if (features.Count != Features.Count) throw new ArgumentException($"the model takes {Features.Count} features, not {features.Count}", nameof(features));
        if (action < 0 || action >= Actions.Count) throw new ArgumentOutOfRangeException(nameof(action));
        var x = new double[Features.Count + Actions.Count];
        for (var i = 0; i < features.Count; i++) x[i] = features[i];
        x[Features.Count + action] = 1;
        foreach (var layer in Layers)
        {
            var y = new double[layer.Outputs];
            for (var o = 0; o < y.Length; o++)
            {
                var row = layer.Weights[o];
                var sum = 0.0;
                for (var i = 0; i < row.Length; i++) sum += x[i] * row[i];
                sum += layer.Bias[o];
                y[o] = layer.Activation == "relu" ? Math.Max(sum, 0) : 1 / (1 + Math.Exp(-sum));
            }
            x = y;
        }
        return x;
    }

    /// <summary>How surprising <paramref name="observed"/> is after <paramref name="action"/> from <paramref name="before"/>: the cross-entropy summed over the features.</summary>
    public double Surprise(IReadOnlyList<double> before, int action, IReadOnlyList<double> observed)
    {
        var p = Predict(before, action);
        var total = 0.0;
        for (var i = 0; i < p.Length; i++)
        {
            var q = Math.Clamp(p[i], Epsilon, 1 - Epsilon);
            total -= observed[i] * Math.Log(q) + (1 - observed[i]) * Math.Log(1 - q);
        }
        return total;
    }
}
