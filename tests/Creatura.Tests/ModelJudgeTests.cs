using System.Net;
using Creatura.Hotz;
using Creatura.Seats;

namespace Creatura.Tests;

/// <summary>
/// The model judge without the network (migration step 15, second commit): one prompt layout, four arms that differ
/// only in what the model may know beyond the claim, the answer clamped by the schedule, every prompt asked once
/// through the cache, the memory built from judged routes as the memory experiment built it.
/// </summary>
public class ModelJudgeTests
{
    private static readonly Claim PayFee = new(3, "pay-fee", true, null, ["fee.paid"], ["deed.checked"]);

    private static ModelJudge Judge(SeatStubHandler handler, string arm = "weights", IJudgeMemory? memory = null, IJudgeCache? cache = null) =>
        new(SeatFixtures.Json("judge-stub"), SeatFixtures.Client(handler), null, arm, memory, cache) { World = "permit" };

    [Fact]
    public void TheArmsDifferOnlyInWhatTheModelMayKnowBeyondTheClaim()
    {
        var memory = new JudgeMemory();
        var route = new Route("permit", "bach", new Dictionary<string, bool>(),
            [new Claim(1, "pay-fee", true, null, ["fee.paid"], ["fee.paid"]), new Claim(2, "pay-fee", true, null, ["fee.paid"], []), new Claim(3, "pay-fee", false, null, ["fee.paid"], [])],
            [new Judgement(1, "pay-fee", Verdict.Supported, ["fee.paid"], [], true, "registry"), new Judgement(2, "pay-fee", Verdict.Contradicted, [], ["fee.paid"], true, "registry"), new Judgement(3, "pay-fee", Verdict.HonestFailure, [], [], false, "registry")],
            []);
        memory.Add("permit-01-bach", "permit", route);
        var entry = memory.Recall("permit", "pay-fee")!;
        Assert.Equal((3, 1, 1, 1), (entry.Cases, entry.Landed, entry.Contradicted, entry.Failed));
        Assert.Equal(["fee.paid"], entry.Gained);
        Assert.Equal(["fee.paid"], entry.Missing);
        Assert.Null(memory.Recall("flood", "pay-fee"));

        var handler = SeatStubHandler.OpenAi("Supported");
        var weights = Judge(handler).Prompt(PayFee);
        var spec = Judge(handler, "spec").Prompt(PayFee);
        var remembered = Judge(handler, "memory", memory).Prompt(PayFee);
        var rememberedSpec = Judge(handler, "memory-spec", memory).Prompt(PayFee);
        var forgotten = Judge(handler, "memory").Prompt(PayFee);

        Assert.StartsWith("Case file: permit\nAction: pay-fee\nTool reported: success\nRegistry after the action: deed.checked\n", weights);
        Assert.EndsWith("Answer:", weights);
        Assert.DoesNotContain("Declared", weights);
        Assert.DoesNotContain("Memory", weights);
        Assert.Contains("Declared: this action produces fee.paid.", spec);
        Assert.Contains("Memory of this action in 3 past cases: when it landed (1 times) the registry gained fee.paid [permit-01-bach]; it reported success while fee.paid was missing 1 times and was judged Contradicted [permit-01-bach]; it reported failure 1 times.", remembered);
        Assert.Contains("Learned from 3 past cases [permit-01-bach, permit-01-bach]: this action produces fee.paid.", rememberedSpec);
        Assert.Contains("Memory: no past case for this action.", forgotten);
        Assert.Throws<ArgumentException>(() => Judge(handler, "oracle"));
    }

    [Fact]
    public void TheClosureClaimIsWordedAsOne()
    {
        var closure = Claim.Closure(9, true, ["permit.issued"], new HashSet<string> { "permit.issued" });
        Assert.Contains("Action: closure (the agent reports whether the case is done)", Judge(SeatStubHandler.OpenAi("Supported")).Prompt(closure));
    }

    [Theory]
    [InlineData("Contradicted.", "Contradicted", 0)]
    [InlineData("  supported\n", "Supported", 0)]
    [InlineData("I think it is Supported", "I think it is Supported", 1)]
    [InlineData("", "", 1)]
    public void TheAnswerIsTheFirstWordWhenItIsInTheSetAndInvalidOtherwise(string raw, string expected, int invalid)
    {
        var judge = Judge(SeatStubHandler.OpenAi(raw));
        Assert.Equal(expected, judge.Answer(PayFee, null));
        Assert.Equal(invalid, judge.Invalid);
        Assert.Equal(invalid, judge.Telemetry.Invalid);
        Assert.Equal(invalid == 0 ? Enum.Parse<Verdict>(expected) : Verdict.Unchecked, new Schedule(judge).Compare(PayFee, null));
    }

    [Fact]
    public void TheCacheAsksEachPromptOnceAndADifferentWorldIsADifferentPrompt()
    {
        var handler = SeatStubHandler.OpenAi("Contradicted");
        var cache = new InMemoryJudgeCache();
        var judge = Judge(handler, cache: cache);

        Assert.Equal("Contradicted", judge.Answer(PayFee, null));
        Assert.Equal("Contradicted", judge.Answer(PayFee, null));
        Assert.Single(handler.Requests);
        Assert.Equal((1, 1, 1), (judge.Calls, judge.CacheHits, judge.UniquePrompts));
        Assert.Equal(1, cache.Count);

        judge.World = "flood";
        judge.Answer(PayFee, null);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Equal(2, judge.UniquePrompts);
        Assert.Contains("max_tokens\":8", handler.LastBody);
        Assert.Contains(ModelJudge.System.Split('\n')[0], handler.LastBody);
    }

    [Fact]
    public void AFailedCallIsNotCachedAndCountsAsInvalid()
    {
        var handler = new SeatStubHandler((HttpStatusCode.InternalServerError, "down"));
        var cache = new InMemoryJudgeCache();
        var judge = Judge(handler, cache: cache);
        Assert.Equal("", judge.Answer(PayFee, null));
        Assert.Equal(0, cache.Count);
        Assert.Equal(1, judge.Telemetry.Failed);
        Assert.Equal(1, judge.Invalid);
        Assert.Equal(Verdict.Unchecked, new Schedule(judge).Compare(PayFee, null));
    }

    [Fact]
    public void TheFileCacheRoundTrips()
    {
        var path = Path.Combine(Path.GetTempPath(), "creatura-judge-" + Guid.NewGuid().ToString("N"), "cache-stub.json");
        try
        {
            var cache = new JsonFileJudgeCache(path);
            cache.Set("ABC", "Supported");
            cache.Save();
            var again = new JsonFileJudgeCache(path);
            Assert.True(again.TryGet("ABC", out var answer));
            Assert.Equal("Supported", answer);
            Assert.Equal(1, again.Count);
        }
        finally
        {
            Directory.Delete(Path.GetDirectoryName(path)!, recursive: true);
        }
    }
}
