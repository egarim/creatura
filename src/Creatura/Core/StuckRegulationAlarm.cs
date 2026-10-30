namespace Creatura;

/// <summary>
/// Stuck regulation, not high stress: acting with no effect on the gap (Bach, 01:56:31). The alarm reads the
/// task's progress once per tick and counts the ticks in a row that ended no further along than the last; at
/// <see cref="Patience"/> stale ticks it fires. A vetoed alarm is <see cref="Reset"/> and counts again from
/// zero, against the same last progress. Lived inside the hybrid's loop; a component now, so any regulator or
/// join may carry one.
/// </summary>
public sealed class StuckRegulationAlarm
{
    private double _lastProgress = -1.0;

    /// <param name="patience">How many stale ticks in a row fire the alarm. The hybrid's is the quarantine cooldown plus two.</param>
    public StuckRegulationAlarm(int patience) => Patience = patience;

    public int Patience { get; }

    /// <summary>Ticks in a row with no progress, this one included.</summary>
    public int StaleTicks { get; private set; }

    public bool Fired => StaleTicks >= Patience;

    /// <summary>Read this tick's progress. True when the alarm fires.</summary>
    public bool Observe(double progress)
    {
        StaleTicks = progress <= _lastProgress ? StaleTicks + 1 : 0;
        _lastProgress = progress;
        return Fired;
    }

    /// <summary>A vetoed alarm starts counting again; what counted as the last progress stands.</summary>
    public void Reset() => StaleTicks = 0;

    /// <summary>The detail the hybrid's alarm row carries.</summary>
    public string Detail(double freedom) => string.Create(TraceText.Culture, $"{StaleTicks} ticks with no progress at {freedom:P0} freedom");
}
