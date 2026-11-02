using System.Text.Json;
using Creatura.Ratings;

namespace Creatura.Tests;

/// <summary>Memory credit (docs/design-ratings.md, slice R4): G12 and G13, no record store.</summary>
public class MemoryCreditTests
{
    /// <summary>G12: every retrieval of a memory is counted, and every run that retrieved it is credited to it as ended well or not.</summary>
    [Fact]
    public void G12_EveryRetrievalIsCountedAndEveryRunCreditedToWhatItRetrieved()
    {
        var credit = new MemoryCredit();
        Assert.Equal(MemoryStanding.Unmet, credit.StandingOf("a"));
        credit.Credit(["a", "b", "a"], endedWell: true);     // a retrieved once in this run, however often it was listed
        credit.Credit(["a"], endedWell: false);
        credit.Credit([], endedWell: false);                  // a run that retrieved nothing still counts toward the base rate
        Assert.Equal((3, 1), (credit.Runs, credit.Good));
        Assert.Equal(1.0 / 3, credit.BaseRate, 9);
        Assert.Equal((2, 1), (credit.UseOf("a")!.Shown, credit.UseOf("a")!.Good));
        Assert.Equal((1, 1), (credit.UseOf("b")!.Shown, credit.UseOf("b")!.Good));
        Assert.Null(credit.UseOf("c"));
        Assert.Equal(MemoryStanding.Promoted, credit.StandingOf("a"));   // 1 of 2, above a base rate of 1 in 3

        // It lives in a record part: through JSON and back, nothing is lost.
        var json = JsonSerializer.Serialize(credit, AgentRecord.Json);
        var back = JsonSerializer.Deserialize<MemoryCredit>(json, AgentRecord.Json)!;
        Assert.Equal((3, 1, 2), (back.Runs, back.Good, back.Memories.Count));
        Assert.Equal(0.5, back.UseOf("a")!.Rate, 9);
        Assert.DoesNotContain("\"Rate\"", json);
    }

    /// <summary>G13: a memory retrieved often and never followed by a good run is pruned; one followed by good runs more than its persona's base rate is promoted, shown first.</summary>
    [Fact]
    public void G13_ThePrunedAreLeftOutAndThePromotedShownFirst()
    {
        var credit = new MemoryCredit();
        // Four runs: "lucky" is shown in the two good ones, "dud" in every one, "once" in one good run, "new" never.
        credit.Credit(["dud", "lucky"], endedWell: true);
        credit.Credit(["dud"], endedWell: false);
        credit.Credit(["dud", "lucky", "once"], endedWell: true);
        credit.Credit(["dud"], endedWell: false);
        Assert.Equal(0.5, credit.BaseRate, 9);
        Assert.Equal(MemoryStanding.Promoted, credit.StandingOf("lucky"));   // 2 of 2, above the base rate
        Assert.Equal(MemoryStanding.Kept, credit.StandingOf("dud"));         // 2 of 4: at the base rate, not above it
        Assert.Equal(MemoryStanding.Kept, credit.StandingOf("once"));        // 1 of 1, but shown fewer than PromoteAfter times
        Assert.Equal(MemoryStanding.Unmet, credit.StandingOf("new"));
        Assert.Equal(["lucky", "dud", "once", "new"], credit.Order(["dud", "once", "lucky", "new"]));

        // Retrieved PruneAfter times and never followed by a good run: pruned, and left out of the order.
        for (var i = 0; i < MemoryCredit.PruneAfter; i++) credit.Credit(["dud", "never"], endedWell: false);
        Assert.Equal(MemoryStanding.Pruned, credit.StandingOf("never"));
        Assert.Equal(MemoryStanding.Kept, credit.StandingOf("dud"));         // 2 of 7, the base rate itself
        Assert.Equal(["lucky", "dud", "once", "new"], credit.Order(["never", "dud", "once", "lucky", "new"]));
        Assert.Empty(credit.Order([]));
        var render = credit.Render();
        Assert.Contains("pruned", render);
        Assert.Contains("promoted", render);
        Assert.StartsWith("  7 runs, 2 ended well (base rate 0.29)", render);
    }
}
