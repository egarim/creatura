using System.Text;
using BIT.Data.Sync;
using BIT.Data.Sync.Client;
using BIT.Data.Sync.EventArgs;
using BIT.Data.Sync.Imp;

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

/// <summary>What one <see cref="LedgerSyncNode.SyncAsync"/> did: entries sent, whether the push succeeded, and what the pull merged.</summary>
public sealed record LedgerSyncReport(int Sent, bool Pushed, string? Message, MergeResult Pulled)
{
    /// <summary>Revocations from the hub this ledger did not apply before.</summary>
    public int RevocationsTaken { get; init; }
}

/// <summary>
/// Where a home's sync with one hub stands: the cursor of what it has pushed (per cell, the highest sequence with no
/// gap below it) and the hub's index of the last delta it pulled. Kept between runs so a sync sends and asks for only
/// what is new (C16).
/// </summary>
public sealed record LedgerSyncState
{
    public Dictionary<string, long> Pushed { get; init; } = new(StringComparer.Ordinal);

    public string Pulled { get; init; } = "";

    public LedgerCursor PushedCursor => new(Pushed);

    public static LedgerSyncState Load(string path) =>
        File.Exists(path) ? System.Text.Json.JsonSerializer.Deserialize<LedgerSyncState>(File.ReadAllText(path), CellJson.Options) ?? new() : new();

    public void Save(string path) =>
        OwnerOnly.Replace(path, System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(this, new System.Text.Json.JsonSerializerOptions(CellJson.Options) { WriteIndented = true }));
}

/// <summary>
/// A ledger as a client of a SyncFramework server node. <see cref="SyncAsync"/> pushes every entry the ledger holds
/// that this node has not pushed (its own and the ones it relays), then pulls what the server has that this node has
/// not seen, through <see cref="LedgerDeltaProcessor"/>. The server is a relay and every client verifies what it pulls.
/// <see cref="State"/> holds the two cursors; the caller saves it, and the ledger, afterwards.
/// </summary>
public sealed class LedgerSyncNode
{
    private readonly LedgerDeltaProcessor _processor;

    public LedgerSyncNode(WorkLedger ledger, string identity, ISyncFrameworkClient client, LedgerSyncState? state = null)
    {
        Ledger = ledger;
        Identity = identity;
        Client = client;
        State = state ?? new LedgerSyncState();
        _processor = new LedgerDeltaProcessor(ledger);
    }

    public WorkLedger Ledger { get; }

    /// <summary>The identity of this node's deltas: the server leaves deltas with this identity out when it answers this node's pull.</summary>
    public string Identity { get; }

    public ISyncFrameworkClient Client { get; }

    public LedgerSyncState State { get; private set; }

    /// <summary>Pushes what the server has not had from this node. Returns how many entries were sent and the server's answer.</summary>
    public async Task<(int Sent, PushOperationResponse Response)> PushAsync(CancellationToken cancellationToken)
    {
        var cursor = Ledger.Cursor;
        var pending = Ledger.Since(State.PushedCursor);
        if (pending.Count == 0) return (0, new PushOperationResponse { Success = true, Message = "Nothing to send" });

        var deltas = pending.Select(e =>
        {
            var delta = LedgerDelta.From(e);
            delta.Identity = Identity;
            return (IDelta)delta;
        }).ToList();
        var response = await Client.PushAsync(deltas, cancellationToken).ConfigureAwait(false)
            ?? new PushOperationResponse { Success = false, Message = "the sync client returned no answer to the push" };
        if (response.Success) State = State with { Pushed = new Dictionary<string, long>(cursor.HighWater, StringComparer.Ordinal) };
        return (pending.Count, response);
    }

    /// <summary>Pulls what other nodes pushed since this node's last pull and merges it; the result says what was refused.</summary>
    public async Task<MergeResult> PullAsync(CancellationToken cancellationToken)
    {
        _processor.TakeResult();
        var start = string.IsNullOrEmpty(State.Pulled) ? "-1" : State.Pulled;
        var response = await Client.FetchAsync(start, Identity, cancellationToken).ConfigureAwait(false);
        if (response is null || !response.Success) throw new IOException($"pull from the sync server failed: {response?.Message ?? "no answer"}");
        if (response.Deltas.Count > 0)
        {
            await _processor.ProcessDeltasAsync(response.Deltas, cancellationToken).ConfigureAwait(false);
            State = State with { Pulled = response.Deltas.Select(d => d.Index).Max(StringComparer.Ordinal)! };
        }
        return _processor.TakeResult();
    }

    /// <summary>Push, then pull; afterwards nothing the ledger holds is pushed again.</summary>
    public async Task<LedgerSyncReport> SyncAsync(CancellationToken cancellationToken)
    {
        var (sent, pushed) = await PushAsync(cancellationToken).ConfigureAwait(false);
        var pulled = await PullAsync(cancellationToken).ConfigureAwait(false);

        // After a push that landed and a pull, the hub holds everything this ledger holds: what this node pushed, and
        // what it just pulled from the hub. Nothing of it needs sending back.
        if (pushed.Success) State = State with { Pushed = new Dictionary<string, long>(Ledger.Cursor.HighWater, StringComparer.Ordinal) };
        return new LedgerSyncReport(sent, pushed.Success, pushed.Message, pulled);
    }
}

/// <summary>
/// Signs every request to a hub as its cell (<see cref="HubRequest"/>): the endorsement, the time and the signature
/// over the method, path and query, time and body. The hub answers nothing else (C20).
/// </summary>
public sealed class HubSigningHandler(LocalCell cell, TimeProvider? clock = null) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var body = request.Content is null ? [] : await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        var at = (clock ?? TimeProvider.System).GetUtcNow();
        foreach (var (name, value) in HubRequest.Sign(cell, request.Method.Method, request.RequestUri!.PathAndQuery, at, body))
        {
            request.Headers.Remove(name);
            request.Headers.TryAddWithoutValidation(name, value);
        }
        return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
    }
}

/// <summary>
/// The ledger hub's address as SyncFramework sees it: the server node every cell pushes to and pulls from, served
/// at <c>/sync/</c> by <c>Creatura.Cells.Hub</c>.
/// </summary>
public static class LedgerHub
{
    public const string NodeId = "ledger";

    /// <summary>
    /// The HTTP client for a hub, every request signed as <paramref name="cell"/>. <paramref name="hub"/> is its base
    /// address, with or without <c>/sync/</c>. <paramref name="inner"/> replaces the network handler (tests).
    /// </summary>
    public static ISyncFrameworkClient Http(Uri hub, LocalCell cell, TimeSpan? timeout = null, TimeProvider? clock = null, HttpMessageHandler? inner = null)
    {
        var text = hub.ToString().TrimEnd('/');
        if (!text.EndsWith("/sync", StringComparison.OrdinalIgnoreCase)) text += "/sync";
        var handler = new HubSigningHandler(cell, clock) { InnerHandler = inner ?? new HttpClientHandler() };
        return new SyncFrameworkHttpClient(new HttpClient(handler) { BaseAddress = new Uri(text + "/"), Timeout = timeout ?? TimeSpan.FromSeconds(30) }, NodeId);
    }

    /// <summary>
    /// One sync of a ledger with a hub over HTTP as <paramref name="cell"/>, the state read from and written to
    /// <paramref name="statePath"/>: what the command calls, so it needs no SyncFramework type of its own.
    /// </summary>
    /// <param name="persist">Saves the ledger after the sync and before the cursors are saved, so a crash in between pulls again rather than losing what was pulled.</param>
    public static async Task<LedgerSyncReport> SyncAsync(WorkLedger ledger, LocalCell cell, Uri hub, string statePath, Action<WorkLedger> persist, CancellationToken cancellationToken)
    {
        // Revocations first, so entries a revocation refuses are neither pushed nor taken.
        var theirs = await ExchangeRevocationsAsync(hub, cell, [.. ledger.Revocations], cancellationToken).ConfigureAwait(false);
        var taken = theirs.Count(ledger.Revoke);

        var node = new LedgerSyncNode(ledger, cell.Id, Http(hub, cell), LedgerSyncState.Load(statePath));
        var report = await node.SyncAsync(cancellationToken).ConfigureAwait(false);
        persist(ledger);
        node.State.Save(statePath);
        return report with { RevocationsTaken = taken };
    }

    /// <summary>A signed HTTP client at the hub's root, for the endpoints beside <c>/sync</c>.</summary>
    private static HttpClient RootClient(Uri hub, LocalCell cell, TimeProvider? clock, HttpMessageHandler? inner, TimeSpan timeout)
    {
        var root = hub.ToString().TrimEnd('/');
        if (root.EndsWith("/sync", StringComparison.OrdinalIgnoreCase)) root = root[..^"/sync".Length];
        return new HttpClient(new HubSigningHandler(cell, clock) { InnerHandler = inner ?? new HttpClientHandler() }) { BaseAddress = new Uri(root + "/"), Timeout = timeout };
    }

    /// <summary>
    /// Uploads a sealed file's ciphertext, filed under its SHA-256 (or under <paramref name="name"/>, which the hub
    /// refuses unless the bytes hash to it). Throws <see cref="SharedFileException"/> when the hub refuses the bytes.
    /// </summary>
    public static async Task PutFileAsync(Uri hub, LocalCell cell, byte[] ciphertext, CancellationToken cancellationToken, string? name = null, TimeProvider? clock = null)
    {
        using var http = RootClient(hub, cell, clock, null, TimeSpan.FromMinutes(5));
        using var content = new ByteArrayContent(ciphertext);
        using var response = await http.PutAsync($"files/{name ?? SharedFiles.Hash(ciphertext)}", content, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge) throw new SharedFileException(SharedFileProblem.TooLarge, "the hub refused the file: over its size limit");
        if (response.StatusCode == System.Net.HttpStatusCode.BadRequest) throw new SharedFileException(SharedFileProblem.Altered, "the hub refused the file: its bytes do not hash to its name");
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Downloads a file's ciphertext by its SHA-256. Throws <see cref="SharedFileException"/> when it expired or was never there.</summary>
    public static async Task<byte[]> GetFileAsync(Uri hub, LocalCell cell, string hash, CancellationToken cancellationToken, TimeProvider? clock = null)
    {
        using var http = RootClient(hub, cell, clock, null, TimeSpan.FromMinutes(5));
        using var response = await http.GetAsync($"files/{hash}", cancellationToken).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.Gone) throw new SharedFileException(SharedFileProblem.Expired, $"the hub kept {hash} for its retention period and has deleted it");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new SharedFileException(SharedFileProblem.NotFound, $"the hub has never had {hash}");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hands the hub this home's revocations and returns the hub's. The caller applies them through
    /// <see cref="WorkLedger.Revoke"/>, which keeps only those an owner it trusts signed.
    /// </summary>
    public static async Task<IReadOnlyList<Revocation>> ExchangeRevocationsAsync(Uri hub, LocalCell cell, IReadOnlyCollection<Revocation> mine, CancellationToken cancellationToken,
        TimeProvider? clock = null, HttpMessageHandler? inner = null)
    {
        using var http = RootClient(hub, cell, clock, inner, TimeSpan.FromSeconds(30));
        if (mine.Count > 0)
        {
            using var content = new StringContent(System.Text.Json.JsonSerializer.Serialize(mine, CellJson.Options), System.Text.Encoding.UTF8, "application/json");
            using var posted = await http.PostAsync("revocations", content, cancellationToken).ConfigureAwait(false);
            posted.EnsureSuccessStatusCode();
        }
        var text = await http.GetStringAsync("revocations", cancellationToken).ConfigureAwait(false);
        return System.Text.Json.JsonSerializer.Deserialize<List<Revocation>>(text, CellJson.Options) ?? [];
    }
}
