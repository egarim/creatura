# Creatura.Cells.Sqlite

The Creatura work ledger in SQLite (`SqliteLedgerStore`), in place of the default JSON file. A save writes only the entries that are new.

```sh
dotnet add package Creatura.Cells.Sqlite --prerelease
```

```csharp
using Creatura.Cells;
using Creatura.Cells.Sqlite;

var home = new CellHome("my-home", SqliteLedgerStore.For);   // the ledger in <home>/ledger.db instead of ledger.json
using var owner = home.EnsureOwner(out _);
using var cell = home.Init(creatura: "alpha");

var ledger = new WorkLedger(owner.PublicKey);
home.Ledger.LoadInto(ledger);                        // every entry is verified as it is merged
ledger.Append(cell, new ClaimPayload("index the archive"));
home.Ledger.Save(ledger);                            // writes only the entries that are new
Console.WriteLine($"{ledger.Count} entries in {home.Ledger.Path}");
```

From the command line: `creatura cell <command> --store sqlite`.

More: the [README](https://github.com/egarim/creatura#cells), "Cells".

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
