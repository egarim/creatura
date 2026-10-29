# Creatura.Hosts.SemanticKernel

Runs Creatura's control layer on Semantic Kernel. Semantic Kernel owns the function-calling loop; the control layer rides its filters. The model only sees the tools the layer trusts, it reads the layer's verdict instead of a tool's own claim, and a lying tool is quarantined.

```sh
dotnet add package Creatura.Hosts.SemanticKernel --prerelease
```

```csharp
using Creatura;
using Creatura.Hosts.SemanticKernel;

var world = RestoreServiceDomain.NewWorld();
world.MakeLiar("apply-patch");                       // reports success once, changes nothing

var agent = ControlledKernelAgent.Offline();         // the built-in planner in the model's seat: nothing leaves the machine
var run = await agent.RunAsync(world, 30);
Console.WriteLine($"{run.Agent}: goal {run.GoalReached} in {run.Ticks} ticks, {run.Escalations} escalations");
```

A model in the seat is any `IChatClient`. For OpenAI, also add `Microsoft.SemanticKernel.Connectors.OpenAI`, then (`using Microsoft.Extensions.AI; using OpenAI;`):

```csharp
var openAi = new OpenAIClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
var live = new ControlledKernelAgent((_, _) => openAi.GetChatClient("gpt-4.1-mini").AsIChatClient(), "gpt-4.1-mini");
```

The classic `IChatCompletionService` connectors run their loop inside the connector, so they cannot give the layer its tick; any `IChatClient` can.

More: the [README](https://github.com/egarim/creatura#hosts), "Hosts", and [samples/SemanticKernelHost](https://github.com/egarim/creatura/tree/main/samples/SemanticKernelHost).

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
