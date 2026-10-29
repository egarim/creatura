# Creatura.Hosts.AgentFramework

Runs Creatura's control layer on Microsoft Agent Framework. The framework owns the model call and the function-calling loop; the control layer rides it as middleware. It offers the model only the tools it trusts, checks every tool's effect against the world's facts, and quarantines a tool that lies.

```sh
dotnet add package Creatura.Hosts.AgentFramework --prerelease
```

```csharp
using Creatura;
using Creatura.Hosts.AgentFramework;

var world = RestoreServiceDomain.NewWorld();
world.MakeLiar("apply-patch");                       // reports success once, changes nothing

var agent = ControlledAgent.Offline();               // the built-in planner in the model's seat: nothing leaves the machine
var run = await agent.RunAsync(world, 30);
Console.WriteLine($"{run.Agent}: goal {run.GoalReached} in {run.Ticks} ticks, {run.Escalations} escalations");
```

A model in the seat is any `IChatClient`, for example OpenAI's (`using Microsoft.Extensions.AI; using OpenAI;`):

```csharp
var openAi = new OpenAIClient(Environment.GetEnvironmentVariable("OPENAI_API_KEY"));
var live = new ControlledAgent((_, _) => openAi.GetChatClient("gpt-4.1-mini").AsIChatClient(), "gpt-4.1-mini");
```

More: the [README](https://github.com/egarim/creatura#hosts), "Hosts", and [samples/AgentFrameworkHost](https://github.com/egarim/creatura/tree/main/samples/AgentFrameworkHost).

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
