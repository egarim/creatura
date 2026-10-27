using Creatura;

namespace Creatura.Tests;

/// <summary>
/// Identity and the vessel (migration step 4). The vessel replaced the self-model's free-text body: it is
/// rendered as a <c>vessel:</c> line only when something set it, and an identity seeds it into the self-model it
/// creates. (That a seat's biology fills an empty vessel without overwriting a stated one is pinned in the research
/// repository, beside the extension that knows the models.)
/// </summary>
public class IdentityTests
{
    [Fact]
    public void TheVesselLineIsRenderedOnlyWhenSet()
    {
        var self = new SelfModel();
        Assert.DoesNotContain("vessel:", self.Render(), StringComparison.Ordinal);
        Assert.DoesNotContain("body:", self.Render(), StringComparison.Ordinal);

        self.Vessel = new Vessel { Model = "deepseek-chat", Protocol = "json", ContextWindow = 64_000, SecondsPerCall = 2.14 };
        Assert.Contains($"role: operator for human{Environment.NewLine}vessel: deepseek-chat (json, ctx 64,000, 2.1s/call){Environment.NewLine}stage:", self.Render(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheVesselSaysOnlyWhatIsKnown()
    {
        Assert.Equal("unknown model", new Vessel().Render());
        Assert.Equal("unknown model on macbook-air, macOS, mesh 100.100.1.2", new Vessel { Machine = "macbook-air", Os = "macOS", Network = "mesh 100.100.1.2" }.Render());
        Assert.Equal("qwen3:8b (tools, reasoning, 31 tokens/s) on ubuntu-box", new Vessel { Model = "qwen3:8b", Protocol = "tools", Reasoning = true, TokensPerSecond = 31.4, Machine = "ubuntu-box" }.Render());
        Assert.Equal("gpt (json, $0.27 in / $1.10 out per million tokens)", new Vessel { Model = "gpt", Protocol = "json", InputPerMillion = 0.27m, OutputPerMillion = 1.10m }.Render());
    }

    [Fact]
    public void AnIdentitySeedsTheSelfModelItCreates()
    {
        var self = Identity.Bach.NewSelfModel(stageLevel: 3);
        Assert.Equal("service operator", self.Role);
        Assert.Equal("joche", self.Principal);
        Assert.Equal(3, self.Stage.Level);
        Assert.Equal(["restore the service to health"], self.OpenCommitments);
        Assert.Null(self.Vessel);
        Assert.Null(Identity.Bach.Scope);

        var vesselled = (Identity.Bach with { Vessel = new Vessel { Model = "planner", Machine = "tests" } }).NewSelfModel();
        Assert.Contains("vessel: planner on tests", vesselled.Render(), StringComparison.Ordinal);
    }
}
