using System.Text;
using BIT.Data.Sync;
using BIT.Data.Sync.Client;
using BIT.Data.Sync.EventArgs;
using BIT.Data.Sync.Imp;
using Microsoft.Extensions.DependencyInjection;

namespace Creatura.Cells.Sync;

/// <summary>
/// One signed entry as one SyncFramework delta: the operation is the entry's canonical JSON (UTF-8, uncompressed),
/// the identity is the cell that wrote it, and the delta id names the entry and its hash, so two conflicting entries
/// for one (cell, sequence) travel as two deltas.
/// </summary>
public static class LedgerDelta
{
    public static Delta From(SignedEntry entry)
    {
        var at = entry.Timestamp.ToUniversalTime();
        return new Delta(entry.Cell, Encoding.UTF8.GetBytes(entry.ToJson()))
        {
            DeltaId = $"{entry.Id}:{entry.Hash()[..16]}",
            Date = at.UtcDateTime,
            Epoch = at.ToUnixTimeMilliseconds(),
            Index = "-1",
        };
    }

    /// <summary>The entry a delta carries. Throws <see cref="FormatException"/> when its operation is not one.</summary>
    public static SignedEntry Entry(IDelta delta)
    {
        if (delta.Operation is not { Length: > 0 } bytes) throw new FormatException("a delta with no operation");
        try
        {
            return SignedEntry.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or NotSupportedException or ArgumentException or DecoderFallbackException)
        {
            throw new FormatException("a delta whose operation is not a signed entry", e);
        }
    }
}

/// <summary>
/// The delta processor of a cell: parses each delta's operation as a signed entry and merges it through
/// <see cref="WorkLedger.Merge"/>, which checks the signatures, the endorsement and the owner's trust. Nothing a
/// delta carries is ever executed. A delta's identity is the envelope, not the proof: a valid entry relayed under
/// another identity is accepted, an invalid one under its own cell's identity is not.
/// </summary>
public sealed class LedgerDeltaProcessor(WorkLedger ledger) : DeltaProcessorBase(new MemorySequenceService(new YearSequencePrefixStrategy()))
{
    private int _added, _duplicates, _conflicts;
    private readonly List<Rejection> _rejected = [];

    public WorkLedger Ledger { get; } = ledger;

    public override Task ProcessDeltasAsync(IEnumerable<IDelta> deltas, CancellationToken cancellationToken)
    {
        foreach (var delta in deltas)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var processing = new ProcessingDeltaEventArgs(delta);
            OnProcessingDelta(processing);
            if (!processing.CustomHandled)
            {
                SignedEntry entry;
                try
                {
                    entry = LedgerDelta.Entry(delta);
                }
                catch (FormatException e)
                {
                    _rejected.Add(new Rejection(delta.DeltaId ?? "(no delta id)", e.Message));
                    OnProcessedDelta(new ProcessedDeltaEventArgs(delta, false));
                    continue;
                }

                var merged = Ledger.Merge([entry]);
                _added += merged.Added;
                _duplicates += merged.Duplicates;
                _conflicts += merged.Conflicts;
                _rejected.AddRange(merged.Rejected);
            }
            OnProcessedDelta(new ProcessedDeltaEventArgs(delta, processing.CustomHandled));
        }
        return Task.CompletedTask;
    }

    /// <summary>What the deltas processed since the last call did to the ledger; resets the count.</summary>
    public MergeResult TakeResult()
    {
        var result = new MergeResult(_added, _duplicates, [.. _rejected], _conflicts);
        _added = _duplicates = _conflicts = 0;
        _rejected.Clear();
        return result;
    }
}

/// <summary>What one <see cref="LedgerSyncNode.SyncAsync"/> did: own entries staged, whether the push succeeded, and what the pull merged.</summary>
public sealed record LedgerSyncReport(int Staged, bool Pushed, string? Message, MergeResult Pulled);

/// <summary>
/// A cell's ledger as a SyncFramework client node. <see cref="SyncAsync"/> stages the cell's own new entries as
/// deltas in its outbox (<see cref="DeltaStore"/>), pushes them to the server node, and pulls what other cells pushed
/// through <see cref="LedgerDeltaProcessor"/>. The server is a relay: it stores and returns deltas and checks nothing,
/// so every cell verifies what it pulls. The caller saves the ledger to its store afterwards.
/// </summary>
public sealed class LedgerSyncNode : IClientSyncDatabase
{
    private readonly LedgerDeltaProcessor _processor;
    private long _stagedThrough = -1;

    public LedgerSyncNode(WorkLedger ledger, string cell, ISyncFrameworkClient client, IDeltaStore? outbox = null)
    {
        Ledger = ledger;
        Identity = cell;
        SyncFrameworkClient = client;
        DeltaStore = outbox ?? new MemoryDeltaStore();
        _processor = new LedgerDeltaProcessor(ledger);
    }

    public WorkLedger Ledger { get; }

    /// <summary>The cell id: the identity of the deltas this node pushes and the one the server leaves out when it answers a pull.</summary>
    public string Identity { get; }

    public IDeltaProcessor DeltaProcessor => _processor;

    public IDeltaStore DeltaStore { get; }

    public ISyncFrameworkClient SyncFrameworkClient { get; }

    /// <summary>Puts the cell's own entries that are not in the outbox yet into it, in sequence order; returns how many.</summary>
    public async Task<int> StageAsync(CancellationToken cancellationToken)
    {
        if (_stagedThrough < 0)
        {
            var staged = await DeltaStore.GetDeltasByIdentityAsync("", Identity, cancellationToken).ConfigureAwait(false);
            _stagedThrough = staged.Select(d => LedgerDelta.Entry(d).Sequence).DefaultIfEmpty(0).Max();
        }

        var fresh = Ledger.Entries.Where(e => e.Cell == Identity && e.Sequence > _stagedThrough).OrderBy(e => e.Sequence).ToList();
        if (fresh.Count == 0) return 0;
        await DeltaStore.SaveDeltasAsync(fresh.Select(e => (IDelta)LedgerDelta.From(e)).ToList(), cancellationToken).ConfigureAwait(false);
        _stagedThrough = fresh[^1].Sequence;
        return fresh.Count;
    }

    /// <summary>Pushes the staged entries the server has not had from this node.</summary>
    public async Task<PushOperationResponse> PushAsync(CancellationToken cancellationToken) =>
        await ISyncClientNodeExtensions.PushAsync(this, cancellationToken).ConfigureAwait(false);

    /// <summary>Pulls what other cells pushed since the last pull and merges it; the result says what was refused.</summary>
    public async Task<MergeResult> PullAsync(CancellationToken cancellationToken)
    {
        _processor.TakeResult();

        // MemoryDeltaStore (BIT.Data.Sync 2.34.0) sets an identity's push cursor to the pull's index when a pull comes
        // first, and the server's index is ahead of the outbox's own, so the cell's staged entries would never be
        // pushed (C12). Recording "nothing pushed yet" before the first pull keeps the two cursors apart; harmless once
        // the store no longer does it.
        if (string.IsNullOrEmpty(await DeltaStore.GetLastPushedDeltaAsync(Identity, cancellationToken).ConfigureAwait(false)))
        {
            await DeltaStore.SetLastPushedDeltaAsync("", Identity, cancellationToken).ConfigureAwait(false);
        }

        var response = await ISyncClientNodeExtensions.PullAsync(this, cancellationToken).ConfigureAwait(false);
        if (!response.Success) throw new IOException($"pull from the sync server failed: {response.Message}");
        return _processor.TakeResult();
    }

    /// <summary>Stage, push, pull.</summary>
    public async Task<LedgerSyncReport> SyncAsync(CancellationToken cancellationToken)
    {
        var staged = await StageAsync(cancellationToken).ConfigureAwait(false);
        var pushed = await PushAsync(cancellationToken).ConfigureAwait(false);
        var pulled = await PullAsync(cancellationToken).ConfigureAwait(false);
        return new LedgerSyncReport(staged, pushed.Success, pushed.Message, pulled);
    }
}
