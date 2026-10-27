using Creatura;

namespace Creatura.Tests;

/// <summary>
/// The table renderer, pinned exactly. These assertions are the contract the `table` rebuild task is graded
/// against, so they spell out alignment, truncation and whitespace instead of checking "contains".
/// </summary>
public class TableTests
{
    [Fact]
    public void ColumnsAreAsWideAsTheWidestCellAndLeftAligned()
    {
        var rendered = Table.Render(
            ["agent", "ticks"],
            [
                ["levin", "5"],
                ["hybrid", "12"],
            ]);

        var lines = rendered.Split(Environment.NewLine);
        Assert.Equal("agent   ticks", lines[0]);
        Assert.Equal("-------------", lines[1]);
        Assert.Equal("levin   5", lines[2]);
        Assert.Equal("hybrid  12", lines[3]);
    }

    [Fact]
    public void TheSeparatorIsAsLongAsTheHeaderLine()
    {
        var rendered = Table.Render(["a", "bb"], [["ccc", "d"]]).Split(Environment.NewLine);

        Assert.Equal(rendered[0].Length, rendered[1].Length);
        Assert.All(rendered[1], c => Assert.Equal('-', c));
    }

    [Fact]
    public void OverlongCellsAreTruncatedWithAnEllipsis()
    {
        var rendered = Table.Render(["name", "note"], [["x", "abcdefghij"]], maxColumnWidth: 5).Split(Environment.NewLine);

        // Column width is min(widest, cap) = 5, so the cell keeps four characters and one ellipsis.
        Assert.Contains("abcd" + Table.Ellipsis, rendered[2]);
        Assert.DoesNotContain("abcde", rendered[2]);
    }

    [Fact]
    public void AColumnCappedToOneCharacterIsJustTheEllipsis()
    {
        var rendered = Table.Render(["h"], [["abcdef"]], maxColumnWidth: 1).Split(Environment.NewLine);

        Assert.Equal(Table.Ellipsis, rendered[2]);
    }

    [Fact]
    public void NoLineEndsInWhitespace()
    {
        var rendered = Table.Render(["a", "bb"], [["1", "2"]]);

        Assert.All(rendered.Split(Environment.NewLine), line => Assert.Equal(line.TrimEnd(), line));
    }

    [Fact]
    public void RowsMustMatchTheHeaderWidth()
    {
        Assert.Throws<ArgumentException>(() => Table.Render(["a", "b"], [["only-one"]]));
    }

    [Fact]
    public void AnEmptyHeaderListRendersNothing()
    {
        Assert.Equal(string.Empty, Table.Render([], []));
    }

    [Fact]
    public void AMaxWidthBelowOneIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Table.Render(["a"], [["b"]], maxColumnWidth: 0));
    }
}
