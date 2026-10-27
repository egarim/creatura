namespace Creatura;

/// <summary>
/// Consequence-modelling evidence. An agent only advances when it predicts its own effects, not when it
/// reports confidence — the interlock from "we would game it, before we have the necessary amount of
/// wisdom" (00:21:15).
/// </summary>
public sealed class PredictionTracker
{
    private readonly Queue<bool> _recent = new();

    public int Total { get; private set; }

    public int Correct { get; private set; }

    public double Accuracy => Total == 0 ? 0.0 : (double)Correct / Total;

    public double RecentAccuracy => _recent.Count == 0 ? 0.0 : (double)_recent.Count(x => x) / _recent.Count;

    public void Record(bool predictedCorrectly)
    {
        Total++;
        if (predictedCorrectly)
        {
            Correct++;
        }

        _recent.Enqueue(predictedCorrectly);
        while (_recent.Count > 8)
        {
            _recent.Dequeue();
        }
    }

    /// <summary>Four recent predictions at 75%+ is enough to earn the next stage.</summary>
    public bool EarnedNextStage => _recent.Count >= 4 && RecentAccuracy >= 0.75;

    /// <summary>The last eight outcomes, oldest first: what a record carried across cases has to hold.</summary>
    public IReadOnlyList<bool> Recent => _recent.ToList();

    /// <summary>Start from a carried record instead of from nothing. Used by an extension that keeps the record across cases.</summary>
    public void Seed(int correct, int total, IEnumerable<bool> recent)
    {
        Correct = correct;
        Total = total;
        _recent.Clear();
        foreach (var r in recent.TakeLast(8)) _recent.Enqueue(r);
    }
}
