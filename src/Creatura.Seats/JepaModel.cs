using System.Text.Json;

namespace Creatura.Seats;

/// <summary>
/// A world model that predicts in a representation it learned, as written by <c>tools/train/train_jepa.py</c> (format
/// <c>creatura.network/1</c>, kind <c>jepa</c>): an encoder from the features a vessel sees to an embedding, a target
/// encoder (the encoder's moving average when it was trained), and a predictor from the embedding and an action to the
/// next embedding. It decides nothing. Its <see cref="LatentSurprise"/> is how far the target encoder's embedding of
/// what was observed lies from the predicted one: the squared distance, as the trainer computes it. Unlike a
/// <see cref="WorldModel"/>'s surprise it is not summed over the features, so a feature the model learned to treat as
/// unpredictable weighs less.
/// </summary>
public sealed class JepaModel
{
    public required string World { get; init; }

    public required string Map { get; init; }

    public required string SchemaHash { get; init; }

    public required IReadOnlyList<string> Features { get; init; }

    public required IReadOnlyList<string> Actions { get; init; }

    /// <summary>The size of the embedding.</summary>
    public required int Embedding { get; init; }

    public required IReadOnlyList<NetworkLayer> Encoder { get; init; }

    public required IReadOnlyList<NetworkLayer> TargetEncoder { get; init; }

    public required IReadOnlyList<NetworkLayer> Predictor { get; init; }

    public JsonElement? Training { get; init; }

    public string Source { get; init; } = "";

    public static JepaModel Load(string path) => Parse(File.ReadAllText(path), path);

    public static JepaModel Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatException($"{source}: no \"{name}\"");
        if (Text("format") != PolicyNetwork.Format) throw new FormatException($"{source}: format is {Text("format")}, expected {PolicyNetwork.Format}");
        if (Text("kind") != "jepa") throw new FormatException($"{source}: kind is {Text("kind")}, expected jepa");
        if (Text("inputs") != "features+action") throw new FormatException($"{source}: inputs \"{Text("inputs")}\" is not one this model knows");
        static List<string> Strings(JsonElement e) => e.EnumerateArray().Select(s => s.GetString()!).ToList();
        static double[] Numbers(JsonElement e) => e.EnumerateArray().Select(n => n.GetDouble()).ToArray();
        List<NetworkLayer> Network(string name) => root.TryGetProperty(name, out var n)
            ? n.EnumerateArray().Select(l => new NetworkLayer(
                l.GetProperty("weights").EnumerateArray().Select(Numbers).ToList(), Numbers(l.GetProperty("bias")), l.GetProperty("activation").GetString()!)).ToList()
            : throw new FormatException($"{source}: no \"{name}\"");

        var model = new JepaModel
        {
            World = Text("world"),
            Map = Text("map"),
            SchemaHash = Text("schemaHash"),
            Features = Strings(root.GetProperty("features")),
            Actions = Strings(root.GetProperty("actions")),
            Embedding = root.TryGetProperty("embedding", out var e) && e.ValueKind == JsonValueKind.Number ? e.GetInt32() : throw new FormatException($"{source}: no \"embedding\""),
            Encoder = Network("encoder"),
            TargetEncoder = Network("targetEncoder"),
            Predictor = Network("predictor"),
            Training = root.TryGetProperty("training", out var t) ? t.Clone() : null,
            Source = source,
        };
        model.Check();
        return model;
    }

    private void Check()
    {
        Check("encoder", Encoder, Features.Count);
        Check("targetEncoder", TargetEncoder, Features.Count);
        Check("predictor", Predictor, Embedding + Actions.Count);
    }

    private void Check(string name, IReadOnlyList<NetworkLayer> layers, int inputs)
    {
        if (layers.Count == 0) throw new FormatException($"{Source}: the {name} has no layers");
        if (layers[0].Inputs != inputs) throw new FormatException($"{Source}: the {name}'s first layer takes {layers[0].Inputs} inputs, expected {inputs}");
        if (layers[^1].Outputs != Embedding) throw new FormatException($"{Source}: the {name} gives {layers[^1].Outputs} values for an embedding of {Embedding}");
        for (var i = 0; i < layers.Count; i++)
        {
            var layer = layers[i];
            if (layer.Weights.Count != layer.Outputs || layer.Weights.Any(row => row.Length != layer.Inputs))
                throw new FormatException($"{Source}: the {name}'s layer {i} weights are not {layer.Outputs} rows of {layer.Inputs}");
            if (i > 0 && layer.Inputs != layers[i - 1].Outputs) throw new FormatException($"{Source}: the {name}'s layer {i} takes {layer.Inputs}, layer {i - 1} gives {layers[i - 1].Outputs}");
            var expected = i < layers.Count - 1 ? "relu" : "none";
            if (layer.Activation != expected) throw new FormatException($"{Source}: the {name}'s layer {i} activation is \"{layer.Activation}\", expected {expected}");
        }
    }

    /// <summary>The embedding of what a vessel sees, by the encoder (the side a prediction starts from).</summary>
    public double[] Encode(IReadOnlyList<double> features) => Run(Encoder, Checked(features));

    /// <summary>The embedding of what a vessel sees, by the target encoder (the side a prediction is measured against).</summary>
    public double[] EncodeTarget(IReadOnlyList<double> features) => Run(TargetEncoder, Checked(features));

    /// <summary>The embedding the model expects after <paramref name="action"/> (an index into <see cref="Actions"/>).</summary>
    public double[] Predict(IReadOnlyList<double> embedding, int action)
    {
        if (embedding.Count != Embedding) throw new ArgumentException($"the model's embedding has {Embedding} numbers, not {embedding.Count}", nameof(embedding));
        if (action < 0 || action >= Actions.Count) throw new ArgumentOutOfRangeException(nameof(action));
        var x = new double[Embedding + Actions.Count];
        for (var i = 0; i < Embedding; i++) x[i] = embedding[i];
        x[Embedding + action] = 1;
        return Run(Predictor, x);
    }

    /// <summary>How surprising <paramref name="observed"/> is after <paramref name="action"/> from <paramref name="before"/>, in the learned representation: the squared distance between the predicted and the target-encoded next embedding.</summary>
    public double LatentSurprise(IReadOnlyList<double> before, int action, IReadOnlyList<double> observed)
    {
        var predicted = Predict(Encode(before), action);
        var target = EncodeTarget(observed);
        var total = 0.0;
        for (var i = 0; i < predicted.Length; i++) total += (predicted[i] - target[i]) * (predicted[i] - target[i]);
        return total;
    }

    private double[] Checked(IReadOnlyList<double> features)
    {
        if (features.Count != Features.Count) throw new ArgumentException($"the model takes {Features.Count} features, not {features.Count}", nameof(features));
        return features.ToArray();
    }

    private static double[] Run(IReadOnlyList<NetworkLayer> layers, double[] x)
    {
        foreach (var layer in layers)
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
