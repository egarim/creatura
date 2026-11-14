using Creatura.Cells.Sqlite;

namespace Creatura.Cells;

/// <summary>
/// The <c>--store</c> option: any name registered with <see cref="LedgerStoreProviders"/> (issue #88). Built in:
/// <c>json</c> (the default, <c>&lt;home&gt;/ledger.json</c>) and <c>sqlite</c> (<c>&lt;home&gt;/ledger.db</c>). The
/// first SQLite run imports the JSON ledger, checking every signature; after that the JSON file is no longer written,
/// so a home moved to SQLite stays on it.
/// </summary>
internal static class LedgerStores
{
    static LedgerStores() => SqliteLedgerStore.Register();

    /// <summary>The factory for a <c>--store</c> name; null for the default JSON store. Unknown names throw, listing the known ones.</summary>
    public static Func<CellHome, ILedgerStore>? Factory(string? name)
    {
        if (name is null || string.Equals(name, "json", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            return LedgerStoreProviders.Resolve(name);
        }
        catch (ArgumentException)
        {
            throw new ArgumentException($"--store is one of {string.Join(", ", LedgerStoreProviders.Names)}, not {name}");
        }
    }

    /// <summary>The stored ledger merged into <paramref name="ledger"/>; an empty SQLite store first imports ledger.json.</summary>
    public static MergeResult LoadInto(CellHome home, ILedgerStore store, WorkLedger ledger)
    {
        var imported = store is SqliteLedgerStore sqlite ? sqlite.ImportIfEmpty(home.JsonLedger, ledger.TrustedOwners) : null;
        var loaded = store.LoadInto(ledger);
        return imported is null ? loaded : loaded with { Rejected = [.. imported.Rejected, .. loaded.Rejected] };
    }
}
