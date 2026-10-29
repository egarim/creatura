// The Creatura control layer on Semantic Kernel. Offline until a key is set: then the model answers, through the
// OpenAI chat API, at the base URL below. This file holds the NAME of the variable with the key, never the key.
//
//   dotnet run                                   # offline: breadth-first search in the model's seat
//   export MODEL_KEY_ENV=...; dotnet run         # a model in the seat (it costs whatever that model costs)
using System.ClientModel;
using Creatura;
using Creatura.Hosts.SemanticKernel;
using Microsoft.Extensions.AI;
using OpenAI;

const string KeyVariable = "MODEL_KEY_ENV";
const string BaseUrl = "MODEL_BASE_URL";
const string Model = "MODEL_NAME";

var key = Environment.GetEnvironmentVariable(KeyVariable);
ControlledKernelAgent agent;
if (string.IsNullOrEmpty(key))
{
    Console.WriteLine($"offline: no key in {KeyVariable}, so the planner sits in the model's seat. Set {KeyVariable} to use {Model}.");
    agent = ControlledKernelAgent.Offline();
}
else
{
    Console.WriteLine($"model: {Model} at {BaseUrl}");
    var client = new OpenAIClient(new ApiKeyCredential(key), new OpenAIClientOptions { Endpoint = new Uri(BaseUrl) });
    agent = new ControlledKernelAgent((_, _) => client.GetChatClient(Model).AsIChatClient(), Model);
}

// The toy world with one tool that lies: "apply-patch" reports success and changes nothing. The control layer checks
// every call against the facts, so the lie is caught and another route is taken.
var world = RestoreServiceDomain.NewWorld();
world.MakeLiar("apply-patch");
var run = await agent.RunAsync(world, maxTicks: 30);

Console.WriteLine($"{run.Agent}: goal {(run.GoalReached ? "reached" : "not reached")} in {run.Ticks} ticks, {run.Blocks} blocked");
foreach (var note in run.Notes) Console.WriteLine($"  · {note}");
foreach (var row in run.Trace.Where(r => r.Kind != TraceKind.Idle).Take(10))
    Console.WriteLine($"  t{row.Tick,-2} {row.Level,-8} {row.Action,-20} {row.Kind,-10} {row.Detail}");
