using System.Security.Cryptography;
using System.Text;

namespace Creatura.Cells;

public enum ClaimState
{
    /// <summary>Claimed, neither handed off nor released: someone is working on it.</summary>
    Active,

    /// <summary>Its cell stopped and said where the work stands; nobody is on it, and it is not done.</summary>
    HandedOff,

    /// <summary>Done, with evidence.</summary>
    Released,
}

/// <summary>An entry a merge refused, and why.</summary>
public sealed record Rejection(string EntryId, string Reason);

/// <summary>
/// What a merge did: entries added, duplicates ignored, entries rejected, and conflicts (two different entries for
/// one (cell, sequence), settled by keeping the one with the smaller <see cref="SignedEntry.Hash"/>, so every ledger
/// settles the same way).
/// </summary>
public sealed record MergeResult(int Added, int Duplicates, IReadOnlyList<Rejection> Rejected, int Conflicts);

/// <summary>A claim and where it stands: whose, on which vessel, its goal, its state and its latest entry.</summary>
public sealed record WorkItem(SignedEntry Claim, ClaimPayload Goal, ClaimState State, SignedEntry Latest)
{
    public string Cell => Claim.Cell;

    public string Creatura => Claim.Endorsement.Creatura;

    public string VesselId => Claim.Endorsement.VesselId;
}

/// <summary>
/// For each cell, the highest sequence n such that its entries 1..n are all held: what a peer has to send to catch
/// this ledger up (<see cref="WorkLedger.Since"/>). A gap stops the count, so a lost entry is asked for again.
/// </summary>
public sealed record LedgerCursor(IReadOnlyDictionary<string, long> HighWater)
{
    public static readonly LedgerCursor Empty = new(new Dictionary<string, long>(StringComparer.Ordinal));

    public long Of(string cell) => HighWater.TryGetValue(cell, out var n) ? n : 0;

    public bool Covers(SignedEntry entry) => entry.Sequence <= Of(entry.Cell);
}

/// <summary>
/// The append-only log of signed entries that says who is working on what. Every cell keeps it whole (decision 2).
/// <see cref="Merge"/> is a union by (cell, sequence) that admits only entries that verify and whose cell an owner
/// this ledger trusts has endorsed; it converges whatever order entries arrive in, with no leader and no consensus.
/// <see cref="Entries"/> are ordered by (timestamp, cell, sequence).
/// </summary>
public sealed class WorkLedger
{
    private readonly HashSet<string> _owners;
    private readonly Dictionary<(string Cell, long Sequence), SignedEntry> _entries = [];
    private readonly Dictionary<string, Revocation> _revoked = new(StringComparer.Ordinal);
    private IReadOnlyList<SignedEntry>? _ordered;

    public WorkLedger(params IEnumerable<string> trustedOwnerKeys) => _owners = new HashSet<string>(trustedOwnerKeys, StringComparer.Ordinal);

    public IReadOnlyCollection<string> TrustedOwners => _owners;

    public int Count => _entries.Count;

    /// <summary>The revocations this ledger applies, one per cell.</summary>
    public IReadOnlyCollection<Revocation> Revocations => _revoked.Values;

    /// <summary>
    /// Applies a revocation signed by an owner this ledger trusts: the cell's entries past its sequence are dropped now
    /// and refused from now on. Of two revocations of one cell the stronger is kept (<see cref="Revocation.Stronger"/>),
    /// so ledgers end the same whatever order revocations and entries arrive in. Returns false when it changed nothing
    /// or is not to be trusted.
    /// </summary>
    public bool Revoke(Revocation revocation)
    {
        if (!_owners.Contains(revocation.OwnerPublicKey) || !revocation.Verify()) return false;
        if (_revoked.TryGetValue(revocation.Cell, out var held) && ReferenceEquals(Revocation.Stronger(held, revocation), held)) return false;
        _revoked[revocation.Cell] = revocation;
        foreach (var key in _entries.Keys.Where(k => k.Cell == revocation.Cell && k.Sequence > revocation.UpToSequence).ToList()) _entries.Remove(key);
        _ordered = null;
        return true;
    }

    /// <summary>True when an owner this ledger trusts has revoked the cell.</summary>
    public bool IsRevoked(string cell) => _revoked.ContainsKey(cell);

    /// <summary>The entry this ledger holds for (cell, sequence), or null.</summary>
    public SignedEntry? Get(string cell, long sequence) => _entries.GetValueOrDefault((cell, sequence));

    public IReadOnlyList<SignedEntry> Entries => _ordered ??= _entries.Values
        .OrderBy(e => e.Timestamp).ThenBy(e => e.Cell, StringComparer.Ordinal).ThenBy(e => e.Sequence).ToList();

    /// <summary>The cell's next sequence number: one past the highest it has in this ledger.</summary>
    public long NextSequence(string cell) => _entries.Keys.Where(k => k.Cell == cell).Select(k => k.Sequence).DefaultIfEmpty(0).Max() + 1;

    /// <summary>Signs the payload as the cell's next entry and adds it. Throws when the ledger would reject it (an unendorsed or untrusted cell).</summary>
    public SignedEntry Append<T>(LocalCell cell, T payload, DateTimeOffset? at = null) where T : IEntryPayload
    {
        var entry = SignedEntry.Sign(cell, NextSequence(cell.Id), payload, at ?? DateTimeOffset.UtcNow);
        if (Verify(entry) is { } reason) throw new InvalidOperationException($"{cell.Id} cannot write here: {reason}");
        Add(entry);
        return entry;
    }

    public MergeResult Merge(IEnumerable<SignedEntry> entries) => Merge(entries, checkSignatures: true);

    /// <summary>
    /// Merges entries that a store verified when they entered it: every check of <see cref="Merge"/> except the two
    /// signature checks, which are what a load costs. Only for the owner's own store (an owner-only file the owner
    /// alone writes); anything that arrives from a peer, a relay or a file edited by hand goes through
    /// <see cref="Merge"/>.
    /// </summary>
    public MergeResult MergeVerified(IEnumerable<SignedEntry> entries) => Merge(entries, checkSignatures: false);

    private MergeResult Merge(IEnumerable<SignedEntry> entries, bool checkSignatures)
    {
        int added = 0, duplicates = 0, conflicts = 0;
        var rejected = new List<Rejection>();
        foreach (var entry in entries)
        {
            if (Verify(entry, checkSignatures) is { } reason)
            {
                rejected.Add(new Rejection(entry.Id, reason));
                continue;
            }

            if (_entries.TryGetValue((entry.Cell, entry.Sequence), out var held))
            {
                if (held.Equals(entry))
                {
                    duplicates++;
                    continue;
                }
                conflicts++;
                if (string.CompareOrdinal(entry.Hash(), held.Hash()) >= 0) continue;
                Add(entry);
                continue;
            }

            Add(entry);
            added++;
        }
        return new MergeResult(added, duplicates, rejected, conflicts);
    }

    /// <summary>Why the entry would be rejected, or null when it verifies and its cell is endorsed by a trusted owner.</summary>
    public string? Verify(SignedEntry entry) => Verify(entry, checkSignatures: true);

    private string? Verify(SignedEntry entry, bool checkSignatures)
    {
        if (entry.Sequence < 1) return "a sequence below 1";
        if (entry.Endorsement is not { } endorsement) return "no endorsement";
        if (!_owners.Contains(endorsement.OwnerPublicKey)) return "endorsed by an owner this ledger does not trust";
        if (_revoked.TryGetValue(entry.Cell, out var revocation) && entry.Sequence > revocation.UpToSequence)
            return $"its cell was revoked after sequence {revocation.UpToSequence}";
        if (checkSignatures && !endorsement.Verify()) return "the endorsement's signature does not verify";
        if (CellKey.CellIdOf(endorsement.CellPublicKey) != entry.Cell) return "the entry's cell is not the endorsed key";
        if (checkSignatures && !CellKey.Verify(endorsement.CellPublicKey, entry.SignedBytes(), entry.Signature)) return "the signature does not verify";

        try
        {
            switch (entry.Kind)
            {
                case EntryKind.Hello:
                    var hello = entry.Body<HelloPayload>().Cell;
                    if (hello.PublicKey != endorsement.CellPublicKey) return "a hello for another key";
                    if (hello.Hardware.VesselId != endorsement.VesselId) return "a hello from a vessel the endorsement does not name";
                    if (hello.Creatura.Value != endorsement.Creatura) return "a hello for another creatura";
                    break;
                case EntryKind.Claim:
                    if (string.IsNullOrWhiteSpace(entry.Body<ClaimPayload>().Goal)) return "a claim with no goal";
                    break;
                case EntryKind.Progress: _ = entry.Body<ProgressPayload>(); break;
                case EntryKind.HandOff: _ = entry.Body<HandOffPayload>(); break;
                case EntryKind.Release: _ = entry.Body<ReleasePayload>(); break;
                case EntryKind.Heartbeat: _ = entry.Body<HeartbeatPayload>(); break;
                case EntryKind.Artifact:
                    var artifact = entry.Body<ArtifactPayload>();
                    if (!SharedFiles.IsHash(artifact.Ciphertext) || !SharedFiles.IsHash(artifact.Plain)) return "an artifact whose hashes are not SHA-256";
                    if (artifact.Size <= 0) return "an artifact with no bytes";
                    if (artifact.Keys is not { Count: > 0 } || artifact.Keys.Any(k => string.IsNullOrEmpty(k.Cell))) return "an artifact no cell can open";
                    break;
                case EntryKind.Warning:
                    if (string.IsNullOrWhiteSpace(entry.Body<WarningPayload>().Subject)) return "a warning about nothing";
                    break;
                case EntryKind.Offer:
                    // The cell signature around it is not enough: the owner's own signature inside must verify, by an owner this ledger trusts.
                    var offer = entry.Body<OfferPayload>().Offer;
                    if (!_owners.Contains(offer.OwnerPublicKey)) return "an offer from an owner this ledger does not trust";
                    if (checkSignatures && !offer.Verify()) return "an offer whose owner signature does not verify";
                    if (string.IsNullOrWhiteSpace(offer.Goal) || offer.ExpiresAt <= offer.IssuedAt) return "an offer with no goal or no time to take it";
                    break;
                case EntryKind.Withdraw:
                    var withdrawal = entry.Body<WithdrawPayload>().Withdrawal;
                    if (!_owners.Contains(withdrawal.OwnerPublicKey)) return "a withdrawal from an owner this ledger does not trust";
                    if (checkSignatures && !withdrawal.Verify()) return "a withdrawal whose owner signature does not verify";
                    break;
                default: return "an unknown kind";
            }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or FormatException or InvalidOperationException or NotSupportedException)
        {
            return "a payload that is not its kind's";
        }
        return null;
    }

    public ClaimState StateOf(string claimId)
    {
        var closing = Entries.Where(e => e.ClaimRef == claimId && e.Kind is EntryKind.Release or EntryKind.HandOff).ToList();
        if (closing.Any(e => e.Kind == EntryKind.Release)) return ClaimState.Released;
        return closing.Count > 0 ? ClaimState.HandedOff : ClaimState.Active;
    }

    /// <summary>Claims not released (active or handed off): the work that is not done.</summary>
    public IReadOnlyList<SignedEntry> OpenClaims() =>
        Entries.Where(e => e.Kind == EntryKind.Claim && StateOf(e.Id) != ClaimState.Released).ToList();

    /// <summary>Every claim with its state and latest entry; <paramref name="activeOnly"/> keeps the ones someone is on.</summary>
    public IReadOnlyList<WorkItem> WhoIsWorkingOn(bool activeOnly = true) => Entries
        .Where(e => e.Kind == EntryKind.Claim)
        .Select(c => new WorkItem(c, c.Body<ClaimPayload>(), StateOf(c.Id), PathOf(c.Id)[^1]))
        .Where(w => !activeOnly || w.State == ClaimState.Active)
        .ToList();

    /// <summary>The path of the work: the claim and every entry about it, in ledger order. Empty for an unknown claim.</summary>
    public IReadOnlyList<SignedEntry> PathOf(string claimId) =>
        Entries.Where(e => (e.Kind == EntryKind.Claim && e.Id == claimId) || e.ClaimRef == claimId).ToList();

    /// <summary>The cell's latest claim that is still active, or null.</summary>
    public SignedEntry? LatestActiveClaimOf(string cell) =>
        Entries.LastOrDefault(e => e.Kind == EntryKind.Claim && e.Cell == cell && StateOf(e.Id) == ClaimState.Active);

    /// <summary>The cell's latest hello, or null when it has not said one here.</summary>
    public SignedEntry? HelloOf(string cell) => Entries.LastOrDefault(e => e.Kind == EntryKind.Hello && e.Cell == cell);

    public LedgerCursor Cursor
    {
        get
        {
            var high = new Dictionary<string, long>(StringComparer.Ordinal);
            foreach (var cell in _entries.Keys.Select(k => k.Cell).Distinct())
            {
                long n = 0;
                while (_entries.ContainsKey((cell, n + 1))) n++;
                high[cell] = n;
            }
            return new LedgerCursor(high);
        }
    }

    /// <summary>What a ledger at <paramref name="cursor"/> lacks from this one.</summary>
    public IReadOnlyList<SignedEntry> Since(LedgerCursor cursor) => Entries.Where(e => !cursor.Covers(e)).ToList();

    /// <summary>SHA-256 over the entries' canonical JSON in ledger order, hex: two ledgers with the same digest hold the same entries.</summary>
    public string Digest()
    {
        var text = string.Join("\n", Entries.Select(e => e.ToJson()));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private void Add(SignedEntry entry)
    {
        _entries[(entry.Cell, entry.Sequence)] = entry;
        _ordered = null;
    }
}

/// <summary>
/// Where a cell keeps its ledger between runs. <see cref="LoadInto"/> merges what is stored into a ledger and says
/// what it refused; <see cref="Save"/> stores what the ledger holds. A store holds only entries a ledger admitted.
/// </summary>
public interface ILedgerStore
{
    string Path { get; }

    MergeResult LoadInto(WorkLedger ledger);

    void Save(WorkLedger ledger);
}

/// <summary>
/// The ledger on disk: one owner-only JSON file, <c>{"schema":1,"entries":[...]}</c> with one canonical entry per
/// line, written whole through a rename. Loading merges into a ledger, so a file edited by hand is checked like any
/// other arrival.
/// </summary>
public sealed class JsonLedgerStore(string path) : ILedgerStore
{
    public const int Schema = 1;

    public string Path { get; } = System.IO.Path.GetFullPath(path);

    public IReadOnlyList<SignedEntry> Load()
    {
        if (!File.Exists(Path)) return [];
        using var document = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Path));
        var root = document.RootElement;
        if (root.TryGetProperty("schema", out var schema) && schema.GetInt32() > Schema) throw new FormatException($"ledger schema {schema.GetInt32()} is newer than {Schema}");
        return root.GetProperty("entries").EnumerateArray().Select(e => SignedEntry.Parse(e.GetRawText())).ToList();
    }

    /// <summary>The stored entries merged into the ledger.</summary>
    public MergeResult LoadInto(WorkLedger ledger) => ledger.Merge(Load());

    public void Save(WorkLedger ledger)
    {
        var text = new StringBuilder();
        text.Append("{\"schema\":").Append(Schema).Append(",\"entries\":[");
        var first = true;
        foreach (var entry in ledger.Entries)
        {
            text.Append(first ? "\n" : ",\n").Append(entry.ToJson());
            first = false;
        }
        text.Append("\n]}\n");
        OwnerOnly.Replace(Path, Encoding.UTF8.GetBytes(text.ToString()));
    }
}
