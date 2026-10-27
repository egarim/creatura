using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace Creatura.Cells;

/// <summary>A cell on a network, by its id.</summary>
public readonly record struct CellAddress(string Cell)
{
    public override string ToString() => Cell;
}

/// <summary>
/// The replaceable part (docs/design-cells.md, "The cell network"). Delivery is at least once; entries are
/// idempotent by (cell, sequence), so duplicates are harmless and a cell that was away catches up with
/// <see cref="SyncAsync"/>. This slice ships <see cref="InProcessCellNetwork"/>; the mesh and the relay come later.
/// </summary>
public interface ICellNetwork
{
    /// <summary>Broadcast to every cell.</summary>
    Task PublishAsync(SignedEntry entry, CancellationToken ct);

    /// <summary>To one cell.</summary>
    Task SendAsync(CellAddress to, SignedEntry entry, CancellationToken ct);

    /// <summary>Everything addressed to this cell or broadcast.</summary>
    IAsyncEnumerable<SignedEntry> ReceiveAsync(CancellationToken ct);

    /// <summary>Catch up after being away: what the peer holds beyond the cursor.</summary>
    Task<IReadOnlyList<SignedEntry>> SyncAsync(CellAddress peer, LedgerCursor since, CancellationToken ct);
}

/// <summary>A peer that cannot be reached: offline, or never joined.</summary>
public sealed class CellUnreachableException(CellAddress cell) : IOException($"{cell} is not reachable");

/// <summary>
/// The cells of one process. <see cref="Join"/> gives each cell its own <see cref="InProcessCellNetwork"/>, backed by
/// its ledger for <see cref="ICellNetwork.SyncAsync"/>. Given a <see cref="Random"/>, the hub delivers some entries
/// twice, which is what at least once allows.
/// </summary>
public sealed class InProcessCellHub(Random? duplicates = null)
{
    private readonly Dictionary<string, InProcessCellNetwork> _cells = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    public InProcessCellNetwork Join(CellAddress address, WorkLedger ledger)
    {
        lock (_gate)
        {
            if (_cells.ContainsKey(address.Cell)) throw new InvalidOperationException($"{address} has already joined");
            var network = new InProcessCellNetwork(this, address, ledger);
            _cells[address.Cell] = network;
            return network;
        }
    }

    internal void Deliver(InProcessCellNetwork from, CellAddress? to, SignedEntry entry)
    {
        List<InProcessCellNetwork> targets;
        lock (_gate)
        {
            if (to is { } one)
            {
                if (!_cells.TryGetValue(one.Cell, out var target)) throw new CellUnreachableException(one);
                targets = [target];
            }
            else
            {
                targets = _cells.Values.Where(c => c != from).ToList();
            }
        }

        foreach (var target in targets)
        {
            // An offline cell misses what is sent while it is away; that is what SyncAsync is for.
            if (!target.Online) continue;
            target.Inbox(entry);
            bool twice;
            lock (_gate) twice = duplicates is not null && duplicates.Next(3) == 0;
            if (twice) target.Inbox(entry);
        }
    }

    internal WorkLedger LedgerOf(CellAddress peer)
    {
        lock (_gate)
        {
            return _cells.TryGetValue(peer.Cell, out var cell) && cell.Online ? cell.Ledger : throw new CellUnreachableException(peer);
        }
    }
}

/// <summary>
/// One cell's view of an <see cref="InProcessCellHub"/>: an unbounded channel as its inbox, its ledger served to peers
/// that sync. <see cref="Online"/> false takes it off the network: it neither sends nor receives nor serves.
/// </summary>
public sealed class InProcessCellNetwork : ICellNetwork
{
    private readonly InProcessCellHub _hub;
    private readonly Channel<SignedEntry> _inbox = Channel.CreateUnbounded<SignedEntry>();

    internal InProcessCellNetwork(InProcessCellHub hub, CellAddress address, WorkLedger ledger)
    {
        _hub = hub;
        Address = address;
        Ledger = ledger;
    }

    public CellAddress Address { get; }

    internal WorkLedger Ledger { get; }

    public bool Online { get; set; } = true;

    public Task PublishAsync(SignedEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureOnline();
        _hub.Deliver(this, null, entry);
        return Task.CompletedTask;
    }

    public Task SendAsync(CellAddress to, SignedEntry entry, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureOnline();
        _hub.Deliver(this, to, entry);
        return Task.CompletedTask;
    }

    public async IAsyncEnumerable<SignedEntry> ReceiveAsync([EnumeratorCancellation] CancellationToken ct)
    {
        await foreach (var entry in _inbox.Reader.ReadAllAsync(ct)) yield return entry;
    }

    public Task<IReadOnlyList<SignedEntry>> SyncAsync(CellAddress peer, LedgerCursor since, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        EnsureOnline();
        return Task.FromResult(_hub.LedgerOf(peer).Since(since));
    }

    /// <summary>What has arrived and not been read yet, without waiting: how a test or a polling cell takes its inbox.</summary>
    public IReadOnlyList<SignedEntry> Drain()
    {
        var entries = new List<SignedEntry>();
        while (_inbox.Reader.TryRead(out var entry)) entries.Add(entry);
        return entries;
    }

    internal void Inbox(SignedEntry entry) => _inbox.Writer.TryWrite(entry);

    private void EnsureOnline()
    {
        if (!Online) throw new CellUnreachableException(Address);
    }
}
