using System.Text.Json;

namespace Creatura.Seats;

/// <summary>
/// A learned lie judge as written by <c>tools/train/train_landed.py</c> (format <c>creatura.network/1</c>, kind
/// <c>judge</c>): given the features a vessel saw before an action, the action, and the features it saw after, the
/// probability that the action lied (reported success while nothing landed). Unlike a <see cref="WorldModel"/>, it is
/// trained on labels: in a simulator the world says what each action really came to.
/// </summary>
public sealed class LieJudge
{
    public required string World { get; init; }

    public required string Map { get; init; }

    public required string SchemaHash { get; init; }

    public required IReadOnlyList<string> Features { get; init; }

    public required IReadOnlyList<string> Actions { get; init; }

    /// <summary>The memory inputs it reads after the features (step 6's second attempt: "again", "tries"); none for the first judge.</summary>
    public IReadOnlyList<string> Memory { get; init; } = [];

    public required IReadOnlyList<NetworkLayer> Layers { get; init; }

    public JsonElement? Training { get; init; }

    public string Source { get; init; } = "";

    public static LieJudge Load(string path) => Parse(File.ReadAllText(path), path);

    public static LieJudge Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatException($"{source}: no \"{name}\"");
        if (Text("format") != PolicyNetwork.Format) throw new FormatException($"{source}: format is {Text("format")}, expected {PolicyNetwork.Format}");
        if (Text("kind") != "judge") throw new FormatException($"{source}: kind is {Text("kind")}, expected judge");
        var inputs = Text("inputs");
        if (inputs is not ("features+action+features" or "features+action+features+memory")) throw new FormatException($"{source}: inputs \"{inputs}\" is not one this judge knows");
        static List<string> Strings(JsonElement e) => e.EnumerateArray().Select(s => s.GetString()!).ToList();
        static double[] Numbers(JsonElement e) => e.EnumerateArray().Select(n => n.GetDouble()).ToArray();

        var judge = new LieJudge
        {
            World = Text("world"),
            Map = Text("map"),
            SchemaHash = Text("schemaHash"),
            Features = Strings(root.GetProperty("features")),
            Actions = Strings(root.GetProperty("actions")),
            Memory = inputs.EndsWith("+memory", StringComparison.Ordinal) ? Strings(root.GetProperty("memory")) : [],
            Layers = root.GetProperty("layers").EnumerateArray().Select(l => new NetworkLayer(
                l.GetProperty("weights").EnumerateArray().Select(Numbers).ToList(), Numbers(l.GetProperty("bias")), l.GetProperty("activation").GetString()!)).ToList(),
            Training = root.TryGetProperty("training", out var t) ? t.Clone() : null,
            Source = source,
        };
        judge.Check();
        return judge;
    }

    private void Check()
    {
        if (Layers.Count == 0) throw new FormatException($"{Source}: no layers");
        var inputs = 2 * Features.Count + Actions.Count + Memory.Count;
        if (Layers[0].Inputs != inputs) throw new FormatException($"{Source}: the first layer takes {Layers[0].Inputs} inputs, the judge names {inputs}");
        if (Layers[^1].Outputs != 1) throw new FormatException($"{Source}: the last layer gives {Layers[^1].Outputs} values, not one probability");
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

    /// <summary>The probability that <paramref name="action"/> (an index into <see cref="Actions"/>) lied, from what was seen before and after it, and with memory inputs when the judge reads them.</summary>
    public double Score(IReadOnlyList<double> before, int action, IReadOnlyList<double> after, IReadOnlyList<double>? memory = null)
    {
        if (before.Count != Features.Count || after.Count != Features.Count) throw new ArgumentException($"the judge takes {Features.Count} features before and after");
        if (action < 0 || action >= Actions.Count) throw new ArgumentOutOfRangeException(nameof(action));
        if ((memory?.Count ?? 0) != Memory.Count) throw new ArgumentException($"the judge reads {Memory.Count} memory inputs, not {memory?.Count ?? 0}", nameof(memory));
        var x = new double[2 * Features.Count + Actions.Count + Memory.Count];
        for (var i = 0; i < before.Count; i++) x[i] = before[i];
        x[Features.Count + action] = 1;
        for (var i = 0; i < after.Count; i++) x[Features.Count + Actions.Count + i] = after[i];
        for (var i = 0; i < Memory.Count; i++) x[2 * Features.Count + Actions.Count + i] = memory![i];
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
        return x[0];
    }
}
