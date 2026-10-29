# Creatura.Scenarios

Several Creatura agents driving machines in one shared world. The convoy: three trucks, each a creatura with its own cell and ledger, deliver supplies to four villages after a flood and coordinate over an in-memory mesh. The world lies: a flooded road reports itself open, and a fuel gauge reads high. Everything runs offline, and a seed replays byte for byte.

```sh
dotnet add package Creatura.Scenarios --prerelease
```

```csharp
using Creatura.Scenarios;

var run = await new ConvoyScenario(ConvoySetup.For(ConvoyArm.J, seed: 0)).RunAsync();
foreach (var line in run.Summary.Outcome) Console.WriteLine(line);

var m = run.Summary.Measures;
Console.WriteLine($"delivered {m.Delivered} of {m.Demand}, lies believed {m.LiesBelieved}, ledgers identical {m.LedgersIdentical}");

var battery = await ConvoyBattery.RunAsync(seeds: 50);   // every default arm over the same 50 seeds
```

The trained policy networks the arms use are embedded in the package.

More: the [README](https://github.com/egarim/creatura#the-convoy-machines-acting-together), "The convoy".

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
