# Creatura

The framework core: one agent loop, a control layer between the model and its tools, and a contract for worlds that can lie. No package dependencies.

```sh
dotnet add package Creatura --prerelease
```

A creatura is `ReferenceLoop` with a regulator in it. This one runs offline on the toy "restore the service" world, with one tool made to lie once. The control layer catches the lie and quarantines the tool.

```csharp
using Creatura;

var world = RestoreServiceDomain.NewWorld();
world.MakeLiar("apply-patch");                       // reports success once, changes nothing

IAgent plain = new ReferenceLoop(new PlainRegulator(), name: "plain loop");
var run = await plain.RunAsync(world, maxTicks: 40);
Console.WriteLine($"{run.Agent}: goal {run.GoalReached} in {run.Ticks} ticks");
foreach (var row in run.Trace) Console.WriteLine($"t{row.Tick} {row.Level} {row.Action} {row.Kind} {row.Detail}");

// A design is a regulator. Swap one in; the loop does not change.
IAgent bach = new ReferenceLoop(new SelfModelRegulator(Identity.Bach, stage: 3), name: "Bach");

sealed class PlainRegulator : IRegulator
{
    public Identity Identity { get; } = new("operator", "you");
}
```

The designs in the box: `SelfModelRegulator` (Bach), `ColonyRegulator` (Levin), `HybridRegulator`, `MapKeeperRegulator` (Peterson), `OrientingRegulator` (Sokolov), and the critic with no hands, `HotzAgent`.

More: the [README](https://github.com/egarim/creatura#readme) and its Quick start.

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
