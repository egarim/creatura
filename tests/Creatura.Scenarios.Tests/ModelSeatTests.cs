using System.Text.Json.Nodes;
using Creatura.Seats;

namespace Creatura.Scenarios.Tests;

/// <summary>
/// Arm S with a fake model: a transport that answers from a script, behind a catalog of its own, so nothing here
/// touches the network or the process-wide catalog. It checks the plumbing (the briefing reaches the model, its
/// answers drive the trucks, no answer means waiting, the replay names the model and counts what it used), not
/// whether a model drives well; that is what a run of arm S on a real model measures.
/// </summary>
public sealed class ModelSeatTests
{
    /// <summary>Answers with the first visible tool from a preference list, or with no tool every fifth call.</summary>
    private sealed class ScriptedModel : IModelTransport
    {
        private static readonly string[] Preference = ["report-done", "unload:", "load", "claim:", "drive:", "wait"];
        public Provider Provider => Provider.Ollama;
        public List<ModelRequest> Requests { get; } = [];

        public Task<ModelReply> CompleteAsync(ModelRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var prompt = string.Join("\n", request.Messages.Select(m => m.Content));
            string? tool = null;
            if (Requests.Count % 5 != 0)
            {
                foreach (var start in Preference)
                {
                    // The prompt lists actions as "  - <id>: requires [...] -> grants [...]".
                    var hit = System.Text.RegularExpressions.Regex.Match(prompt, $@"(?m)^\s*-\s*({System.Text.RegularExpressions.Regex.Escape(start)}\S*?):\s+requires");
                    if (hit.Success) { tool = hit.Groups[1].Value; break; }
                }
            }
            var answer = new JsonObject { ["tool"] = tool, ["why"] = "scripted" }.ToJsonString();
            return Task.FromResult(new ModelReply(answer, null, [], 120, 8, null, null, 0.01, null, null, null, null, "stop", 200, null));
        }
    }

    private const string Catalog = """
        {"models":[{"name":"scripted","provider":"ollama","baseUrl":"http://localhost:11434","model":"scripted-7b","keyEnv":null,
                    "nativeTools":false,"reasoning":false,"inputPerMillion":0,"outputPerMillion":0}]}
        """;

    private static async Task<(ConvoyRun Run, ScriptedModel Model)> RunWithFake(int maxTicks = 20)
    {
        var model = new ScriptedModel();
        var client = new ModelClient(transports: [model]) { RetryDelay = TimeSpan.Zero };
        var setup = ConvoySetup.For(ConvoyArm.S, 1) with { SeatModel = "scripted", MaxTicks = maxTicks };
        var run = await new ConvoyScenario(setup, client, catalog: ModelCatalog.Parse(Catalog, "test")).RunAsync();
        return (run, model);
    }

    [Fact]
    public async Task TheModelIsBriefedAndItsAnswersDriveTheTrucks()
    {
        var (run, model) = await RunWithFake();
        Assert.Empty(run.Errors);
        Assert.NotEmpty(model.Requests);
        var first = string.Join("\n", model.Requests[0].Messages.Select(m => m.Content));
        Assert.Contains("How a delivery goes", first);
        Assert.Contains("You drive Truck", first);
        Assert.Contains("r01 depot-j1", first);
        Assert.Contains(run.Ticks.SelectMany(t => t.Events), e => e.Kind is "claim" or "load" or "drive");
    }

    [Fact]
    public async Task TheReplayNamesTheModelAndCountsWhatItUsed()
    {
        var (run, model) = await RunWithFake();
        Assert.All(run.Header.Trucks, t =>
        {
            Assert.Contains("scripted-7b", t.Agent!.Model);
            Assert.Contains("SeatFactory.Model(\"scripted\")", t.Agent.CSharp);
            Assert.Contains("language model", t.Agent.Seat);
        });
        Assert.Contains("scripted", run.Header.ArmText);
        var seat = Assert.IsType<SeatUse>(run.Summary.Seat);
        Assert.Equal(model.Requests.Count, seat.Calls);
        Assert.Equal(120L * seat.Calls, seat.PromptTokens);
        Assert.Equal(0m, seat.Cost);
    }

    [Fact]
    public async Task NoAnswerMeansWaiting()
    {
        var seat = new ConvoyModelSeat(new FixedPolicy(null), RoadMap.FloodedValley(), new PhysicalMachine("truck") { Capacity = 4, FuelCapacity = 8 }, "Truck 2");
        var tools = new[] { new Tool("wait", [], ["waited"], "verify"), new Tool("load", ["at:depot"], ["loaded"], "write") };
        var context = new PolicyContext { Facts = new HashSet<string> { "at:depot" }, Tools = tools, Goal = _ => false, GoalText = "", Tick = 1, MaxTicks = 10, Freedom = 1 };
        Assert.Equal("wait", (await seat.NextAsync(context)).Tool);
        var chosen = new ConvoyModelSeat(new FixedPolicy("load"), RoadMap.FloodedValley(), new PhysicalMachine("truck") { Capacity = 4, FuelCapacity = 8 }, "Truck 2");
        Assert.Equal("load", (await chosen.NextAsync(context)).Tool);
    }

    [Fact]
    public void TheOfflineArmsHaveNoModelAndTheModelArmIsNotInTheDefaultBattery()
    {
        Assert.Null(ConvoySetup.For(ConvoyArm.J, 0).SeatModel);
        Assert.Equal(ConvoySetup.DefaultSeatModel, ConvoySetup.For(ConvoyArm.S, 0).SeatModel);
        Assert.DoesNotContain(ConvoyArm.S, ConvoyBattery.DefaultArms);
    }

    private sealed class FixedPolicy(string? tool) : IPolicy
    {
        public string Name => "fixed";
        public Task<PolicySuggestion> NextAsync(PolicyContext context, CancellationToken cancellationToken = default) => Task.FromResult(new PolicySuggestion(tool, "fixed"));
    }
}
