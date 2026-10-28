namespace Creatura;

/// <summary>
/// A Kegan-derived stage (Bach, Lex Fridman Podcast #392, 00:02:12 / 00:22:11), used here as a safety interlock rather than a score.
/// Bach insists stages are descriptive and give no score (00:22:11), and this
/// code takes him at his word: the level only caps what an agent is allowed to do on its own.
/// </summary>
public sealed record Stage(int Level, string Name, double FreedomCap, bool MayDeploy, bool MayEditSelfModel);

public static class Stages
{
    public static readonly Stage[] All =
    [
        new(1, "reactive survival", 0.35, false, false),
        new(2, "personal self", 0.50, false, false),
        new(3, "social self", 0.65, false, false),
        new(4, "rational agency", 0.80, true, false),
        new(5, "self-authoring", 1.00, true, true),
    ];

    public static Stage At(int level) => All[Math.Clamp(level, 1, All.Length) - 1];

    /// <summary>The ceiling on freedom: plasticity may ask for more, the stage refuses.</summary>
    public static double Cap(int level) => At(level).FreedomCap;
}
