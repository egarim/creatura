# Creatura.Ratings

Ratings for Creatura (docs/design-ratings.md): a Weng-Lin rating, the open TrueSkill-style model, per seat, read from the rows the bench, the web-world matrices and the two-creatures runs already write. Three leaderboards, one per axis (the model in the seat, the design in the loop, the prompt the seat was given), each with its uncertainty and a plain verdict: the order is settled, or these pairs need more runs.

```sh
dotnet add package Creatura.Ratings --prerelease
```

```csharp
using Creatura.Ratings;

var runs = Sources.Read(["runs/ux", "research/batteries", "experiments/two-creatures/records"]);
foreach (var board in Leaderboard.All(runs)) Console.Write(Leaderboard.Render(board));
```

Or from the tool: `creatura rate <path>...` walks the paths for `scoreboard.csv`, `results.csv`, `report.json` and `summary.json`; `--by model|design|prompt` picks one board, `--json` prints the numbers.

A rating is recomputed from every row each time, in a fixed order: the rows are the record, the rating is a reading of them. A seat is rated only against the seats that ran the same job with the other axes held fixed; one that met nobody keeps the prior and the board says so.
