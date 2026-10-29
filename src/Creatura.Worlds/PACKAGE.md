# Creatura.Worlds

The bench's worlds for Creatura: sample worlds with barriers you switch on (tools that fail, lie or change the rules), their manifests, controls, and the catalogs that build agents and extensions by name. No package dependencies and no file IO, so a browser can host it.

```sh
dotnet add package Creatura.Worlds --prerelease
```

Build a design by its catalog name and run it on a sample world, offline:

```csharp
using Creatura;
using Creatura.Worlds;

var permit = WorldCatalog.Permit;                    // a permit office whose tools fail, lie and change the rules
var world = permit.Create(BarrierSet.All(permit));   // every barrier switched on

IAgent agent = AgentCatalog.Make("peterson", new PlannerPolicy(), permit.Task);
var run = await agent.RunAsync(world);
Console.WriteLine($"{run.Agent}: goal {run.GoalReached} in {run.Ticks} ticks");

Console.WriteLine(string.Join(", ", AgentCatalog.Names));   // levin, bach, hybrid, peterson, orienting, ...
Console.WriteLine(string.Join(", ", WorldCatalog.Names));   // restore, permit, irrigation, livestock, flood, ...
```

More: the [README](https://github.com/egarim/creatura#readme).

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
