using System.Globalization;

namespace Creatura.Seats;

/// <summary>
/// What a <see cref="GuidedSeat"/> asks of its guide: the best of the visible actions and how sure it is, and how
/// surprising the last step was.
/// </summary>
public interface ISeatGuide
{
    string Name { get; }

    /// <summary>The action vocabulary the guide scores, in its order.</summary>
    IReadOnlyList<string> Actions { get; }

    /// <summary>
    /// The best of <paramref name="visible"/> (indices into <see cref="Actions"/>, at least one) for what the vessel sees,
    /// and the relative margin between the two best, from 0 (a tie) to 1 (only one choice, or no contest).
    /// </summary>
    (int Best, double Margin) Rank(IReadOnlyList<double> features, IReadOnlyList<int> visible);

    /// <summary>How surprising <paramref name="after"/> was after <paramref name="action"/> from <paramref name="before"/>; null when the guide has no measure of it.</summary>
    double? Surprise(IReadOnlyList<double> before, int action, IReadOnlyList<double> after);
}

/// <summary>
/// A JEPA and a latent subgoal head as a guide (docs/design-trained-models.md, step 8): each visible action is scored by
/// the squared distance between the JEPA's predicted next embedding and the subgoal, the nearest is best, and the
/// relative margin is (d₂ − d₁) / d₂. Its surprise is the JEPA's latent surprise.
/// </summary>
public sealed class LatentGuide : ISeatGuide
{
    private readonly JepaModel _jepa;
    private readonly LatentGoal _goal;

    public LatentGuide(JepaModel jepa, LatentGoal goal)
    {
        goal.Fits(jepa);
        _jepa = jepa;
        _goal = goal;
    }

    public string Name => $"jepa-guide({Path.GetFileNameWithoutExtension(_goal.Source)})";

    public IReadOnlyList<string> Actions => _jepa.Actions;

    public JepaModel Jepa => _jepa;

    public LatentGoal Goal => _goal;

    public (int Best, double Margin) Rank(IReadOnlyList<double> features, IReadOnlyList<int> visible)
    {
        var z = _jepa.Encode(features);
        var g = _goal.Predict(z);
        int best = -1;
        double first = double.PositiveInfinity, second = double.PositiveInfinity;
        foreach (var a in visible)
        {
            var p = _jepa.Predict(z, a);
            var d = 0.0;
            for (var i = 0; i < p.Length; i++) d += (p[i] - g[i]) * (p[i] - g[i]);
            if (d < first) { second = first; first = d; best = a; }
            else if (d < second) second = d;
        }
        var margin = double.IsPositiveInfinity(second) ? 1 : second > 0 ? (second - first) / second : 0;
        return (best, margin);
    }

    public double? Surprise(IReadOnlyList<double> before, int action, IReadOnlyList<double> after) => _jepa.LatentSurprise(before, action, after);
}

/// <summary>
/// A policy network as a guide, the control of step 8: the best visible action by its score, and the relative margin
/// between the two best softmax shares among the visible actions. It has no measure of surprise.
/// </summary>
public sealed class NetworkGuide(PolicyNetwork network) : ISeatGuide
{
    public string Name => $"network-guide({Path.GetFileNameWithoutExtension(network.Source)})";

    public PolicyNetwork Network => network;

    public IReadOnlyList<string> Actions => network.Actions;

    public (int Best, double Margin) Rank(IReadOnlyList<double> features, IReadOnlyList<int> visible)
    {
        var input = new double[network.InputCount];
        for (var i = 0; i < features.Count; i++) input[i] = features[i];
        foreach (var a in visible) input[features.Count + a] = 1;
        var scores = network.Scores(input);
        int best = -1;
        double first = double.NegativeInfinity, second = double.NegativeInfinity;
        foreach (var a in visible)
        {
            if (scores[a] > first) { second = first; first = scores[a]; best = a; }
            else if (scores[a] > second) second = scores[a];
        }
        // Shares of the two best are proportional to exp(score), so their relative margin is 1 − exp(second − first).
        return (best, double.IsNegativeInfinity(second) ? 1 : 1 - Math.Exp(second - first));
    }

    public double? Surprise(IReadOnlyList<double> before, int action, IReadOnlyList<double> after) => null;
}

/// <summary>A seat behind a counter: every call to it is counted, and nothing else changes.</summary>
public sealed class CountingSeat(IPolicy inner) : IPolicy
{
    public string Name => inner.Name;

    public IPolicy Inner => inner;

    public int Calls { get; private set; }

    public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        Calls++;
        return inner.NextAsync(context, cancellationToken);
    }
}

/// <summary>
/// A seat that calls another, expensive seat only when its guide is unsure (docs/design-trained-models.md, step 8).
/// Each decision the guide ranks the visible actions. With one visible action there is nothing to choose. Otherwise it
/// calls the inner seat when the guide's margin is below <paramref name="margin"/>, or when the guide's surprise at the
/// previous step was above <paramref name="surprise"/>, and plays the guide's best action without a call otherwise.
/// The previous step is told to it through <see cref="Observe"/>, with what the vessel saw just before and just after
/// the action, as a world set records it. Plain arithmetic and no random draws, so a run replays exactly.
/// </summary>
/// <param name="encode">The world's features for a decision, in the guide's order.</param>
/// <param name="surprise">The surprise trigger's threshold; null: no surprise trigger.</param>
public sealed class GuidedSeat(IPolicy inner, ISeatGuide guide, Func<PolicyContext, IReadOnlyList<double>> encode, double margin, double? surprise) : IPolicy
{
    private bool _surprised;

    public string Name => $"{guide.Name} over {inner.Name}";

    public ISeatGuide Guide => guide;

    public int Decisions { get; private set; }

    /// <summary>Calls to the inner seat because the guide's margin was too small.</summary>
    public int MarginCalls { get; private set; }

    /// <summary>Calls to the inner seat because the previous step surprised the guide.</summary>
    public int SurpriseCalls { get; private set; }

    /// <summary>Calls to the inner seat because no visible action was in the guide's vocabulary.</summary>
    public int OtherCalls { get; private set; }

    public int Calls => MarginCalls + SurpriseCalls + OtherCalls;

    /// <summary>Of the calls, those where the inner seat chose other than the guide's best.</summary>
    public int Disagreements { get; private set; }

    /// <summary>After an action ran: what the vessel saw just before it and just after it. Above the threshold, the next decision calls the inner seat.</summary>
    public void Observe(IReadOnlyList<double> before, string tool, IReadOnlyList<double> after)
    {
        var action = -1;
        for (var a = 0; a < guide.Actions.Count; a++) if (guide.Actions[a] == tool) action = a;
        _surprised = action >= 0 && surprise is { } limit && guide.Surprise(before, action, after) > limit;
    }

    public async Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default)
    {
        Decisions++;
        var features = encode(context);
        var surprised = _surprised;
        _surprised = false;
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var a = 0; a < guide.Actions.Count; a++) index[guide.Actions[a]] = a;
        var visible = context.Tools.Select(t => index.TryGetValue(t.Id, out var a) ? a : -1).Where(a => a >= 0).Distinct().Order().ToList();
        if (visible.Count == 0)
        {
            OtherCalls++;
            return await inner.NextAsync(context, cancellationToken).ConfigureAwait(false);
        }

        var (best, sure) = guide.Rank(features, visible);
        if (visible.Count > 1 && (surprised || sure < margin))
        {
            if (surprised) SurpriseCalls++;
            else MarginCalls++;
            var suggestion = await inner.NextAsync(context, cancellationToken).ConfigureAwait(false);
            if (suggestion.Tool != guide.Actions[best]) Disagreements++;
            return suggestion;
        }
        return new PolicySuggestion(guide.Actions[best], string.Create(CultureInfo.InvariantCulture, $"guide: {guide.Actions[best]} (margin {sure:0.00})"));
    }
}
