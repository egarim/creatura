# Creatura.Cells.Sync

The Creatura work ledger over [SyncFramework](https://github.com/egarim/SyncFramework): push to and pull from a ledger hub (`creatura-hub`, attached to each GitHub release). Every request is signed by its cell, and every entry pulled is verified before it is merged.

```sh
dotnet add package Creatura.Cells.Sync --prerelease
```

```csharp
using Creatura.Cells;
using Creatura.Cells.Sync;

var home = new CellHome();
using var owner = home.EnsureOwner(out _);
using var cell = home.Init(creatura: "alpha");
var ledger = new WorkLedger(owner.PublicKey);
home.Ledger.LoadInto(ledger);

var hub = new Uri("http://127.0.0.1:5181");          // where your creatura-hub listens
var report = await LedgerHub.SyncAsync(ledger, cell, hub, Path.Combine(home.Root, "hub-sync.json"),
    persist: home.Ledger.Save, CancellationToken.None);
Console.WriteLine($"sent {report.Sent}, pulled {report.Pulled.Added}, refused {report.Pulled.Rejected.Count}");
```

From the command line: `creatura cell sync --hub <url>`.

More: the [README](https://github.com/egarim/creatura#cells), "Cells".

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
