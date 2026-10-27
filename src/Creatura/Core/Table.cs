namespace Creatura;

/// <summary>
/// Renders a fixed-width table. Small, exacting, and the sort of thing that looks trivial until the
/// alignment rules bite: every column is as wide as its widest cell (capped), cells are left aligned,
/// overlong cells are truncated with an ellipsis, and no line ever ends in whitespace.
///
/// The rules are pinned by the tests; the table is what the demo prints.
/// </summary>
public static class Table
{
    /// <summary>Default cap on a column, so one long cell cannot push the table off the screen.</summary>
    public const int DefaultMaxWidth = 40;

    public const string Ellipsis = "…";

    public static string Render(
        IReadOnlyList<string> headers,
        IReadOnlyList<IReadOnlyList<string>> rows,
        int maxColumnWidth = DefaultMaxWidth)
    {
        if (headers.Count == 0)
        {
            return string.Empty;
        }

        if (maxColumnWidth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(maxColumnWidth), "must be at least 1");
        }

        foreach (var row in rows)
        {
            if (row.Count != headers.Count)
            {
                throw new ArgumentException($"row has {row.Count} cells but there are {headers.Count} columns", nameof(rows));
            }
        }

        var widths = new int[headers.Count];
        for (var column = 0; column < headers.Count; column++)
        {
            var widest = DisplayWidth(headers[column]);
            foreach (var row in rows)
            {
                widest = Math.Max(widest, DisplayWidth(row[column]));
            }

            widths[column] = Math.Min(widest, maxColumnWidth);
        }

        var lines = new List<string> { Line(headers, widths) };
        lines.Add(new string('-', lines[0].Length));
        foreach (var row in rows)
        {
            lines.Add(Line(row, widths));
        }

        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Characters, not bytes: the ellipsis is one character even though it is three bytes.</summary>
    private static int DisplayWidth(string value) => value.Length;

    private static string Line(IReadOnlyList<string> cells, int[] widths)
    {
        var parts = new string[cells.Count];
        for (var column = 0; column < cells.Count; column++)
        {
            parts[column] = Fit(cells[column], widths[column]);
        }

        // Two spaces between columns; the final cell is never padded, so lines do not end in whitespace.
        return string.Join("  ", parts).TrimEnd();
    }

    private static string Fit(string value, int width)
    {
        if (value.Length > width)
        {
            value = width == 1 ? Ellipsis : value[..(width - 1)] + Ellipsis;
        }

        return value.PadRight(width);
    }
}
