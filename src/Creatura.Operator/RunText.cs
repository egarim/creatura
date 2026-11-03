using System.Globalization;
using System.Text;

namespace Creatura.Operator;

/// <summary>
/// The Run view as plain text: what <c>creatura-console run --print</c> writes, for a terminal with no screen to draw on,
/// for an agent reading a run, and for the tests. The same model and the same columns as the screen.
/// </summary>
public static class RunText
{
    public static string MarkText(RowMark mark) => mark switch
    {
        RowMark.Blocked => "BLOCKED",
        RowMark.Refused => "REFUSED",
        RowMark.Flagged => "FLAG",
        _ => "",
    };

    /// <summary>The header in a line or two: the scenario, the arm and what it is, the seed, how far the run is.</summary>
    public static string Title(RunModel model)
    {
        var h = model.Header;
        if (h is null) return model.Lines == 0 ? "waiting for the run's first line" : "a replay with no header";
        var ic = CultureInfo.InvariantCulture;
        var title = string.Create(ic, $"{h.Scenario}, arm {h.Arm}") + (h.ArmText.Length > 0 ? $" ({h.ArmText})" : "")
            + (h.Scenario == "convoy" || h.Seed != 0 ? string.Create(ic, $", seed {h.Seed}") : "")
            + string.Create(ic, $": tick {model.Tick}") + (h.MaxTicks > 0 ? string.Create(ic, $" of {h.MaxTicks}") : "");
        return model.Ended ? title + ", ended" : title;
    }

    /// <summary>The counts a person scans for: rows, and the blocked, refused and flagged ones.</summary>
    public static string Counts(RunModel model, string? cell = null) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{model.RowsOf(cell).Count()} rows, {model.Count(RowMark.Blocked, cell)} blocked, {model.Count(RowMark.Refused, cell)} refused, {model.Count(RowMark.Flagged, cell)} flagged");

    public static string Render(RunModel model, string? cell = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine(Title(model));
        if (model.Header is { Goal.Length: > 0 } h) sb.AppendLine("goal: " + h.Goal);
        sb.AppendLine((cell is null ? "every cell" : "cell " + cell) + ": " + Counts(model, cell));
        sb.AppendLine();
        foreach (var row in model.RowsOf(cell))
        {
            sb.Append(string.Create(CultureInfo.InvariantCulture, $"{row.Tick,4}  {row.Cell,-10} {row.Act,-28} {row.Answer}"));
            if (row.Verdict.Length > 0) sb.Append("  [verdict " + row.Verdict + "]");
            if (row.Mark != RowMark.None) sb.Append("  " + MarkText(row.Mark));
            sb.AppendLine();
            if (row.Observation.Length > 0) sb.AppendLine("        saw: " + row.Observation);
            if (row.Offer.Length > 0) sb.AppendLine("        offer: " + row.Offer);
            if (row.Decision.Length > 0) sb.AppendLine("        decision: " + row.Decision);
        }

        if (model.Ended)
        {
            sb.AppendLine();
            foreach (var line in model.Outcome) sb.AppendLine(line);
        }

        return sb.ToString();
    }
}
