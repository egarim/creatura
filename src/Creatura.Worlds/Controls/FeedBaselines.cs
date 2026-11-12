using Creatura;
using Creatura.Worlds.Samples;

namespace Creatura.Worlds;

/// <summary>
/// The engagement baseline of the virtual TikTok (docs/design-axi.md): among the kinds the loop lets it see, always
/// play the one with the highest predicted stay time. It trusts the predictions completely and never asks the user.
/// </summary>
public class MaxStaytimePolicy(TikTokWorld world) : IPolicy
{
    public virtual string Name => "max-staytime";

    protected TikTokWorld World { get; } = world;

    public virtual Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        var best = context.Tools
            .Select(t => (Tool: t, Prediction: TikTokWorld.KindOf(t.Id) is { } k ? World.Predicted(k) : null))
            .Where(x => x.Prediction is not null)
            .OrderByDescending(x => x.Prediction!.StaySeconds)
            .ThenBy(x => x.Tool.Id, StringComparer.Ordinal)
            .FirstOrDefault();
        return Task.FromResult(best.Tool is null
            ? new PolicySuggestion(context.Tools.Any(t => t.Id == "end-session") ? "end-session" : null, "nothing to play")
            : new PolicySuggestion(best.Tool.Id, $"highest predicted stay {best.Prediction!.StaySeconds:F1} s"));
    }
}

/// <summary>
/// The same baseline with the rule one creator says he found on his phone (docs/design-axi.md): when the user is
/// skimming, play live as a last resort.
/// </summary>
public sealed class LastResortPolicy(TikTokWorld world) : MaxStaytimePolicy(world)
{
    public override string Name => "last-resort";

    public override Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default) =>
        World.IsSkimming && context.Tools.Any(t => t.Id == "play-live")
            ? Task.FromResult(new PolicySuggestion("play-live", "the user is skimming: live, as a last resort"))
            : base.NextAsync(context, cancellationToken);
}
