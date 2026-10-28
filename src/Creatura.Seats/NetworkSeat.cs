using System.Globalization;
using System.Text.Json;

namespace Creatura.Seats;

/// <summary>One layer of a network: its weights (one row per output), its bias, and its activation ("relu" or "none").</summary>
public sealed record NetworkLayer(IReadOnlyList<double[]> Weights, double[] Bias, string Activation)
{
    public int Inputs => Weights.Count == 0 ? 0 : Weights[0].Length;

    public int Outputs => Bias.Length;
}

/// <summary>
/// A trained policy network as written by <c>tools/train/train_policy.py</c> (format <c>creatura.network/1</c>): the
/// world and map it was trained for, the hash of its feature schema, the action vocabulary its outputs score, its layers,
/// and how it was trained. Plain JSON and plain arithmetic, so no package is needed to run it.
/// </summary>
public sealed class PolicyNetwork
{
    public const string Format = "creatura.network/1";

    public required string World { get; init; }

    public required string Map { get; init; }

    public required string SchemaHash { get; init; }

    /// <summary>What the input is made of: "features+visible" is the world's features, then one bit per action that is visible.</summary>
    public required string Inputs { get; init; }

    public required IReadOnlyList<string> Features { get; init; }

    public required IReadOnlyList<string> Actions { get; init; }

    public required IReadOnlyList<NetworkLayer> Layers { get; init; }

    /// <summary>How it was trained (data, seeds, accuracy), as the trainer wrote it.</summary>
    public JsonElement? Training { get; init; }

    /// <summary>Where it was read from, for messages.</summary>
    public string Source { get; init; } = "";

    public static PolicyNetwork Load(string path) => Parse(File.ReadAllText(path), path);

    public static PolicyNetwork Parse(string json, string source)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        string Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()!
            : throw new FormatException($"{source}: no \"{name}\"");
        if (Text("format") != Format) throw new FormatException($"{source}: format is {Text("format")}, expected {Format}");
        if (Text("kind") != "policy") throw new FormatException($"{source}: kind is {Text("kind")}, expected policy");
        static List<string> Strings(JsonElement e) => e.EnumerateArray().Select(s => s.GetString()!).ToList();
        static double[] Numbers(JsonElement e) => e.EnumerateArray().Select(n => n.GetDouble()).ToArray();

        var layers = root.GetProperty("layers").EnumerateArray().Select(l => new NetworkLayer(
            l.GetProperty("weights").EnumerateArray().Select(Numbers).ToList(), Numbers(l.GetProperty("bias")), l.GetProperty("activation").GetString()!)).ToList();
        var network = new PolicyNetwork
        {
            World = Text("world"),
            Map = Text("map"),
            SchemaHash = Text("schemaHash"),
            Inputs = Text("inputs"),
            Features = Strings(root.GetProperty("features")),
            Actions = Strings(root.GetProperty("actions")),
            Layers = layers,
            Training = root.TryGetProperty("training", out var t) ? t.Clone() : null,
            Source = source,
        };
        network.Check();
        return network;
    }

    /// <summary>The layers fit together, the first takes the inputs the network says it takes, and the last scores every action.</summary>
    private void Check()
    {
        if (Layers.Count == 0) throw new FormatException($"{Source}: no layers");
        if (Inputs != "features+visible") throw new FormatException($"{Source}: inputs \"{Inputs}\" is not one this seat knows");
        if (Layers[0].Inputs != InputCount) throw new FormatException($"{Source}: the first layer takes {Layers[0].Inputs} inputs, the network names {InputCount}");
        for (var i = 0; i < Layers.Count; i++)
        {
            var layer = Layers[i];
            if (layer.Weights.Count != layer.Outputs || layer.Weights.Any(row => row.Length != layer.Inputs))
                throw new FormatException($"{Source}: layer {i}'s weights are not {layer.Outputs} rows of {layer.Inputs}");
            if (i > 0 && layer.Inputs != Layers[i - 1].Outputs) throw new FormatException($"{Source}: layer {i} takes {layer.Inputs}, layer {i - 1} gives {Layers[i - 1].Outputs}");
            if (layer.Activation is not ("relu" or "none")) throw new FormatException($"{Source}: layer {i}'s activation \"{layer.Activation}\" is unknown");
        }
        if (Layers[^1].Outputs != Actions.Count) throw new FormatException($"{Source}: the last layer gives {Layers[^1].Outputs} scores for {Actions.Count} actions");
    }

    public int InputCount => Features.Count + Actions.Count;

    /// <summary>One score per action, from the input; the arithmetic the trainer did, in the same order.</summary>
    public double[] Scores(IReadOnlyList<double> input)
    {
        if (input.Count != InputCount) throw new ArgumentException($"the network takes {InputCount} inputs, not {input.Count}", nameof(input));
        var x = input.ToArray();
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

/// <summary>
/// A trained network in the seat (docs/design-trained-models.md, step 2). It encodes what the seat is given with the
/// world's encoder, adds one bit per action for whether it is visible, scores every action, and plays the visible one
/// with the highest score (the lowest index on a tie). No language model, no randomness: the same situation gives the
/// same action, so a seed still replays byte for byte. When none of the visible tools is in its vocabulary it waits,
/// or gives no tool when waiting is not offered either.
/// <para>For training by trial (step 3) it can sample instead: each visible action with its softmax share of the scores
/// divided by <paramref name="temperature"/>, drawn from <paramref name="sample"/>, which the caller seeds so a run can
/// be made again exactly.</para>
/// </summary>
/// <param name="encode">The world's features for a decision, in the order of <see cref="PolicyNetwork.Features"/>.</param>
/// <param name="sample">The generator to sample with; null plays the best visible action.</param>
public sealed class NetworkSeat(PolicyNetwork network, Func<PolicyContext, IReadOnlyList<double>> encode, string name = "policy-network",
    Random? sample = null, double temperature = 1) : IPolicy
{
    public string Name => name;

    public PolicyNetwork Network => network;

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default) => Task.FromResult(Next(context));

    public PolicySuggestion Next(PolicyContext context)
    {
        var features = encode(context);
        if (features.Count != network.Features.Count)
            throw new InvalidOperationException($"the encoder gave {features.Count} features, {network.Source} takes {network.Features.Count}");
        var visible = context.Tools.Select(t => t.Id).ToHashSet(StringComparer.Ordinal);
        var input = new double[network.InputCount];
        for (var i = 0; i < features.Count; i++) input[i] = features[i];
        for (var a = 0; a < network.Actions.Count; a++) input[features.Count + a] = visible.Contains(network.Actions[a]) ? 1 : 0;

        var scores = network.Scores(input);
        if (sample is not null) return Sampled(scores, input, features.Count, visible);
        var best = -1;
        for (var a = 0; a < scores.Length; a++)
        {
            if (input[features.Count + a] > 0 && (best < 0 || scores[a] > scores[best])) best = a;
        }
        if (best < 0)
        {
            return visible.Contains("wait")
                ? new PolicySuggestion("wait", "network: no visible action is in its vocabulary")
                : new PolicySuggestion(null, "network: no visible action is in its vocabulary");
        }

        // How sure it was: the softmax share of the best score among the visible actions.
        var top = scores[best];
        var total = 0.0;
        for (var a = 0; a < scores.Length; a++)
        {
            if (input[features.Count + a] > 0) total += Math.Exp(scores[a] - top);
        }
        return new PolicySuggestion(network.Actions[best], string.Create(CultureInfo.InvariantCulture, $"network: {network.Actions[best]} ({1 / total:P0} of the visible choices)"));
    }

    private PolicySuggestion Sampled(double[] scores, double[] input, int offset, HashSet<string> visible)
    {
        var shown = Enumerable.Range(0, scores.Length).Where(a => input[offset + a] > 0).ToList();
        if (shown.Count == 0)
            return visible.Contains("wait") ? new PolicySuggestion("wait", "network: no visible action is in its vocabulary") : new PolicySuggestion(null, "network: no visible action is in its vocabulary");
        var top = shown.Max(a => scores[a]);
        var weights = shown.Select(a => Math.Exp((scores[a] - top) / temperature)).ToList();
        var draw = sample!.NextDouble() * weights.Sum();
        var pick = shown[^1];
        for (var i = 0; i < shown.Count; i++)
        {
            draw -= weights[i];
            if (draw < 0) { pick = shown[i]; break; }
        }
        var share = weights[shown.IndexOf(pick)] / weights.Sum();
        return new PolicySuggestion(network.Actions[pick], string.Create(CultureInfo.InvariantCulture, $"network, sampled at {temperature}: {network.Actions[pick]} ({share:P0})"));
    }
}
