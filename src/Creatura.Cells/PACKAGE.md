# Creatura.Cells

Identity and a shared work ledger for Creatura agents. Each agent gets a cell: a key bound to its vessel (the machine it runs on) and endorsed by an owner key. Every ledger entry is signed by its cell and verified on merge, so the ledger says who is working on what. No package dependencies: ECDSA P-256, SHA-256 and JSON come with .NET.

```sh
dotnet add package Creatura.Cells --prerelease
```

```csharp
using Creatura.Cells;

var home = new CellHome("my-home");                  // default: ~/.creatura, or $CREATURA_HOME
using var owner = home.EnsureOwner(out _);           // the owner key that endorses each vessel's cell
using var cell = home.Init(creatura: "alpha");       // this vessel's cell, endorsed

var ledger = new WorkLedger(owner.PublicKey);        // trusts what this owner's cells sign
home.Ledger.LoadInto(ledger);
var claim = ledger.Append(cell, new ClaimPayload("restore the service"));
ledger.Append(cell, new ProgressPayload(claim.Id, "patched; waiting for the health check"));
ledger.Append(cell, new ReleasePayload(claim.Id, "alert resolved"));
home.Ledger.Save(ledger);

foreach (var item in ledger.WhoIsWorkingOn(activeOnly: false))
    Console.WriteLine($"{item.Creatura} on {item.VesselId}: {item.Goal.Goal} ({item.State})");
```

Private keys, the vessel salt and the ledger are written owner-only. The raw hardware identifier is never written or printed. The `creatura` tool (`dotnet tool install -g Creatura.Cli --prerelease`) does all of this from the command line.

More: the [README](https://github.com/egarim/creatura#cells), "Cells".

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
