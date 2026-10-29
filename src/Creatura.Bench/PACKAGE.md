# Creatura.Bench

Where a Creatura design is trained and tested: oracles that judge a run by evidence the agent cannot author, pre-registered batteries of hypotheses, the trainer and the exam over the record store, and result rows. No package dependencies.

```sh
dotnet add package Creatura.Bench --prerelease
```

Train a design case after case, then examine its record without changing it:

```csharp
using Creatura;
using Creatura.Bench;

var store = new JsonRecordStore("records");
var training = await Trainer.RunAsync("bach", Curriculum.Licence(10), store, "bach");   // releases from case 2
var exam = await Exam.RunAsync(SokolovBattery.Battery, store, "bach");                   // never writes to "bach"
Console.WriteLine(exam.Report.ToMarkdown());
```

A design that starts every case from nothing refuses the release every time. A carried record earns it by case 2.

More: the [README](https://github.com/egarim/creatura#the-bench), "The bench".

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
