# Creatura.Seats

Puts a language model in a Creatura agent's decision seat, or in the critic's judge seat, with no SDK behind it. Transports for Ollama, OpenAI-compatible APIs (OpenAI, DeepSeek, Azure OpenAI v1) and Azure AI Foundry over an injected `HttpClient`, a model catalog by name, a call log and a scoreboard. No package dependencies.

```sh
dotnet add package Creatura.Seats --prerelease
```

```csharp
using Creatura;
using Creatura.Seats;
using Creatura.Worlds;

// "deepseek-chat" is a row of the model catalog (models.json beside the program, else the compiled table).
// The key is read from the environment variable the row names, at the first call.
var seat = SeatFactory.Model("deepseek-chat", log: new CallLog("runs/calls.jsonl"));

var permit = WorldCatalog.Permit;
var run = await AgentCatalog.Make("bach", seat, permit.Task).RunAsync(permit.Create(BarrierSet.All(permit)));
Console.WriteLine($"{run.Agent}: goal {run.GoalReached} in {run.Ticks} ticks");
```

The model may only name a tool it was shown; a hidden tool is refused, never obeyed. Keys never reach an error text or a log line.

More: the [README](https://github.com/egarim/creatura#the-seats), "The seats".

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
