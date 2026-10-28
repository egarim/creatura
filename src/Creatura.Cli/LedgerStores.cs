using Creatura.Cells.Sqlite;

namespace Creatura.Cells;

/// <summary>
/// The <c>--store</c> option: <c>json</c> (the default, <c>&lt;home&gt;/ledger.json</c>) or <c>sqlite</c>
/// (<c>&lt;home&gt;/ledger.db</c>). The first SQLite run imports the JSON ledger, checking every signature; after that the
/// JSON file is no longer written, so a home moved to SQLite stays on it.
/// </summary>
internal static class LedgerStores
{
    public static Func<CellHome, ILedgerStore>? Factory(string? name) => name switch
    {
        null or "json" => null,
        "sqlite" => SqliteLedgerStore.For,
        _ => throw new ArgumentException($"--store is json or sqlite, not {name}"),
    };

    /// <summary>The stored ledger merged into <paramref name="ledger"/>; an empty SQLite store first imports ledger.json.</summary>
    public static MergeResult LoadInto(CellHome home, ILedgerStore store, WorkLedger ledger)
    {
        var imported = store is SqliteLedgerStore sqlite ? sqlite.ImportIfEmpty(home.JsonLedger, ledger.TrustedOwners) : null;
        var loaded = store.LoadInto(ledger);
        return imported is null ? loaded : loaded with { Rejected = [.. imported.Rejected, .. loaded.Rejected] };
    }
}
