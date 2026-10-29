using Creatura.Seats;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// Step 7 of docs/design-trained-models.md, a world model in a learned representation: the C# model repeats the
/// trainer's arithmetic for both models (T13d) and refuses files of another kind.
/// </summary>
public sealed class JepaModelTests
{
    private static JepaModel Model => JepaModel.Load(Path.Combine(Repo, "models", "convoy-jepa-varied.json"));

    /// <summary>T13d: the latent surprise the C# model computes for a fixed input is the one the Python trainer computed for it.</summary>
    [Fact]
    public void TheLatentSurpriseIsTheTrainersArithmetic()
    {
        var model = Model;
        var before = Enumerable.Range(0, model.Features.Count).Select(i => (i % 7) / 6.0).ToArray();
        var after = Enumerable.Range(0, model.Features.Count).Select(i => ((i * 3) % 5) / 4.0).ToArray();
        // From tools/train/train_jepa.py's latent_surprise, encode and predict_next on models/convoy-jepa-varied.json.
        Assert.Equal(85.9984981945168, model.LatentSurprise(before, 5, after), 9);
        Assert.Equal(0.07209678697904129, model.Encode(before)[0], 12);
        Assert.Equal(0.08210587373888675, model.Predict(model.Encode(before), 5)[0], 12);
    }

    [Fact]
    public void BothFilesLoadForTheConvoyAndOtherKindsAreRefused()
    {
        foreach (var name in new[] { "convoy-jepa-varied.json", "convoy-jepa-noisy.json" })
        {
            var model = JepaModel.Load(Path.Combine(Repo, "models", name));
            Assert.Equal("convoy", model.World);
            Assert.Equal(ConvoyFeatures.Schema(RoadMap.FloodedValley()).Hash, model.SchemaHash);
            Assert.Equal(model.Embedding, model.Encode(new double[model.Features.Count]).Length);
        }
        Assert.Throws<FormatException>(() => JepaModel.Load(Path.Combine(Repo, "models", "convoy-world-varied.json")));
        Assert.Throws<FormatException>(() => WorldModel.Load(Path.Combine(Repo, "models", "convoy-jepa-varied.json")));
        Assert.Throws<ArgumentException>(() => Model.Encode(new double[3]));
        Assert.Throws<ArgumentOutOfRangeException>(() => Model.Predict(new double[Model.Embedding], Model.Actions.Count));
    }

    /// <summary>The same for the noisy world's model, whose embedding is 64 numbers.</summary>
    [Fact]
    public void TheNoisyModelIsTheTrainersArithmeticToo()
    {
        var model = JepaModel.Load(Path.Combine(Repo, "models", "convoy-jepa-noisy.json"));
        Assert.Equal(64, model.Embedding);
        var before = Enumerable.Range(0, model.Features.Count).Select(i => (i % 7) / 6.0).ToArray();
        var after = Enumerable.Range(0, model.Features.Count).Select(i => ((i * 3) % 5) / 4.0).ToArray();
        // From tools/train/train_jepa.py on models/convoy-jepa-noisy.json.
        Assert.Equal(226.06305231021395, model.LatentSurprise(before, 5, after), 9);
        Assert.Equal(-0.38778544803867016, model.Encode(before)[0], 12);
        Assert.Equal(-0.23339349965677844, model.Predict(model.Encode(before), 5)[0], 12);
    }

    private static string Repo
    {
        get
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Creatura.slnx"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("the repository root was not found");
        }
    }
}
