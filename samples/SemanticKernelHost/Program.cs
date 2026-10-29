using System.ClientModel;
using Creatura;
using Creatura.Hosts.SemanticKernel;
using Creatura.Seats;
using Creatura.Worlds;
using Microsoft.Extensions.AI;
using OpenAI;

// The control layer on Semantic Kernel, against the toy suite's four barriers. Offline by default: the planner answers
// in the model's seat (PlannerChatClient), so nothing leaves the machine.
//
//   dotnet run --project samples/SemanticKernelHost                              # offline, breadth-first search in the model's seat
//   dotnet run --project samples/SemanticKernelHost -- --scenario "patch lies"   # one scenario, with its trace
//   dotnet run --project samples/SemanticKernelHost -- --compare                 # beside the Levin, Bach and hybrid designs
//   dotnet run --project samples/SemanticKernelHost -- --seat deepseek           # a model in the seat (opt in; see below)
//
// --seat takes a name from Creatura.Seats' ModelCatalog (models.json beside the executable, else the compiled table) whose
// provider speaks the OpenAI chat API; `deepseek` means DeepSeek's catalog row, honouring DEEPSEEK_MODEL. The base URL comes
// from the catalog and the key from the environment variable the row names; neither is written here.

string? seat = null;
string? scenarioFilter = null;
var trace = false;
var compare = false;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seat" when i + 1 < args.Length:
            seat = args[++i];
            break;
        case "--scenario" when i + 1 < args.Length:
            scenarioFilter = args[++i];
            trace = true;
            break;
        case "--trace":
            trace = true;
            break;
        case "--compare":
            compare = true;
            break;
        default:
            Console.WriteLine($"unknown argument: {args[i]}");
            return;
    }
}

ControlledKernelAgent controlled;
string policy;
if (seat is null)
{
    controlled = ControlledKernelAgent.Offline();
    policy = "bfs";
}
else
{
    ModelEndpoint endpoint;
    try
    {
        endpoint = seat.Equals("deepseek", StringComparison.OrdinalIgnoreCase) ? SeatFactory.DeepSeekEndpoint() : SeatFactory.Endpoint(seat);
    }
    catch (ArgumentException e)
    {
        Console.WriteLine(e.Message);
        return;
    }

    if (endpoint.Provider != Provider.OpenAiCompatible)
    {
        Console.WriteLine($"{endpoint.Name} is a {Providers.AsRow(endpoint.Provider)} seat; this host speaks the OpenAI chat API. Pick an OpenAI-compatible row, or run without --seat.");
        return;
    }

    if (SeatFactory.MissingKey(endpoint) is { } missing)
    {
        Console.WriteLine($"no key in {missing}: set it and retry, or run without --seat.");
        return;
    }

    // Semantic Kernel's chat-client path; the OpenAI client posts to {base}/chat/completions, the same URL the Seats transport uses.
    var key = endpoint.ApiKey() ?? "unused";
    var openAi = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(endpoint.BaseUrl.TrimEnd('/')) });
    policy = $"{endpoint.Name}:{endpoint.Model}";
    controlled = new ControlledKernelAgent((_, _) => openAi.GetChatClient(endpoint.Model).AsIChatClient(), policy);
}

var scenarios = new (string Name, Func<World> Build)[]
{
    ("clean", RestoreServiceDomain.NewWorld),
    ("patch revoked", () => { var w = RestoreServiceDomain.NewWorld(); w.Revoke("apply-patch"); return w; }),
    ("patch lies", () => { var w = RestoreServiceDomain.NewWorld(); w.MakeLiar("apply-patch"); return w; }),
    ("release denied", () => { var w = RestoreServiceDomain.NewWorld(); w.Revoke("deploy"); return w; }),
};

var selected = scenarios.Where(s => scenarioFilter is null || s.Name.Contains(scenarioFilter, StringComparison.OrdinalIgnoreCase)).ToList();
if (selected.Count == 0)
{
    Console.WriteLine($"no scenario matching '{scenarioFilter}'. available: {string.Join(", ", scenarios.Select(s => s.Name))}");
    return;
}

Console.WriteLine($"policy: {policy}  framework: {ControlledKernelAgent.Framework}");
Console.WriteLine();
Console.WriteLine($"{"scenario",-16}{"agent",-18}{"goal",-6}{"ticks",-7}{"escalations",-13}{"blocks",-7}");
Console.WriteLine(new string('-', 67));

var runs = new List<(string Scenario, AgentRun Run)>();
foreach (var scenario in selected)
{
    // The designs run the reference loop with the planner as their seat; the host runs the kernel's loop with the same planner as its model.
    var agents = compare
        ? new IAgent[] { AgentCatalog.Make("levin", new PlannerPolicy()), AgentCatalog.Make("bach", new PlannerPolicy()), AgentCatalog.Make("hybrid", new PlannerPolicy()), controlled }
        : [controlled];

    foreach (var agent in agents)
    {
        var run = await agent.RunAsync(scenario.Build(), 30);
        runs.Add((scenario.Name, run));
        Console.WriteLine($"{scenario.Name,-16}{run.Agent,-18}{(run.GoalReached ? "yes" : "no"),-6}{run.Ticks,-7}{run.Escalations,-13}{run.Blocks,-7}");
    }
}

foreach (var (name, run) in runs)
{
    Console.WriteLine();
    Console.WriteLine($"{name} / {run.Agent}");
    foreach (var note in run.Notes)
    {
        Console.WriteLine($"  · {note}");
    }

    if (!trace)
    {
        continue;
    }

    Console.WriteLine();
    Console.WriteLine($"  {"tick",-5}{"level",-10}{"stress",-8}{"free",-7}{"action",-20}detail");
    foreach (var e in run.Trace)
    {
        Console.WriteLine($"  {e.Tick,-5}{e.Level,-10}{e.Stress,-8:F2}{e.Freedom,-7:F2}{e.Action,-20}{e.Detail}");
    }
}
