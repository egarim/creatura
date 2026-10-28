using System.Security.Cryptography;
using System.Text.Json;

namespace Creatura.Cells;

/// <summary>One stay of a creatura on a vessel, with the cell key it had there: a move is a new stay, not a new agent.</summary>
public sealed record VesselStay(string VesselId, string KeyFingerprint, DateTimeOffset Since);

/// <summary>
/// Where a creatura's cell files live on this vessel, by default <c>~/.creatura</c>:
/// <code>
/// owner/owner.key          the owner's private key (owner-only)    owner/vessel.salt    the salt vessel ids are hashed with
/// cells/&lt;vessel&gt;/&lt;creatura&gt;.key                 the cell's private key on that vessel (owner-only)
/// cells/&lt;vessel&gt;/&lt;creatura&gt;.endorsement.json    the owner's endorsement of that key
/// records/&lt;creatura&gt;.json                        the creatura's <see cref="AgentRecord"/> (its id, its vessel history)
/// ledger.json                                    the work ledger (owner-only)
/// </code>
/// Keys sit under their vessel's id, so a home carried to another vessel opens with a new key there (C3).
/// </summary>
/// <summary>
/// What a vessel without the owner key sends to the owner's vessel to be endorsed: public values only (vessel id,
/// creatura id, the cell's public key and id, the creatura's name, and the hardware in words for the owner to read).
/// </summary>
public sealed record EndorsementRequest(string VesselId, string Creatura, string CellPublicKey, string Cell, string Name, string Hardware);

public sealed class CellHome
{
    public const string VesselsPartKey = "cells.vessels";

    /// <param name="root">The home directory (default <see cref="DefaultRoot"/>).</param>
    /// <param name="ledgerStore">How the home keeps its ledger; the JSON file when null (another store, such as SQLite, lives in its own project).</param>
    public CellHome(string? root = null, Func<CellHome, ILedgerStore>? ledgerStore = null)
    {
        Root = Path.GetFullPath(root ?? DefaultRoot);
        _ledgerStore = ledgerStore;
    }

    private readonly Func<CellHome, ILedgerStore>? _ledgerStore;

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".creatura");

    public string Root { get; }

    public string OwnerDirectory => Path.Combine(Root, "owner");

    public string CellsDirectory => Path.Combine(Root, "cells");

    public string RecordsDirectory => Path.Combine(Root, "records");

    public string OwnerKeyPath => Path.Combine(OwnerDirectory, "owner.key");

    public string SaltPath => Path.Combine(OwnerDirectory, "vessel.salt");

    public string LedgerPath => Path.Combine(Root, "ledger.json");

    /// <summary>The revocations this home applies: owner-only JSON, one per cell (<see cref="Revocation.Merge"/>).</summary>
    public string RevocationsPath => Path.Combine(Root, "revocations.json");

    public IReadOnlyList<Revocation> LoadRevocations() => File.Exists(RevocationsPath)
        ? JsonSerializer.Deserialize<List<Revocation>>(File.ReadAllText(RevocationsPath), CellJson.Options) ?? []
        : [];

    /// <summary>The home's revocations merged with <paramref name="more"/> and written back; returns the merged list.</summary>
    public IReadOnlyList<Revocation> SaveRevocations(IEnumerable<Revocation> more)
    {
        var merged = Revocation.Merge(LoadRevocations().Concat(more));
        if (merged.Count == 0) return merged;
        OwnerOnly.CreateDirectory(Root);
        OwnerOnly.Replace(RevocationsPath, JsonSerializer.SerializeToUtf8Bytes(merged, new JsonSerializerOptions(CellJson.Options) { WriteIndented = true }));
        return merged;
    }

    /// <summary>Applies the home's revocations to a ledger before anything is loaded into it; returns how many it took.</summary>
    public int ApplyRevocations(WorkLedger ledger) => LoadRevocations().Count(ledger.Revoke);

    /// <summary>The ledger file of the JSON store, whichever store the home uses: a SQLite store imports it once.</summary>
    public JsonLedgerStore JsonLedger => new(LedgerPath);

    public ILedgerStore Ledger => _ledgerStore?.Invoke(this) ?? JsonLedger;

    /// <summary>True once <see cref="EnsureOwner"/> has run here: there is a salt to hash vessel ids with.</summary>
    public bool IsInitialized => File.Exists(SaltPath);

    public IRecordStore Records
    {
        get
        {
            OwnerOnly.CreateDirectory(Root);
            OwnerOnly.CreateDirectory(RecordsDirectory);
            return new JsonRecordStore(RecordsDirectory);
        }
    }

    public string KeyPathFor(string vesselId, string creatura) => Path.Combine(CellsDirectory, vesselId, SafeName(creatura) + ".key");

    public string EndorsementPathFor(string vesselId, string creatura) => Path.Combine(CellsDirectory, vesselId, SafeName(creatura) + ".endorsement.json");

    public string EncryptionKeyPathFor(string vesselId, string creatura) => Path.Combine(CellsDirectory, vesselId, SafeName(creatura) + ".encryption.key");

    /// <summary>Where this home keeps the ciphertext of files it fetched, by hash.</summary>
    public string FilesDirectory => Path.Combine(Root, "files");

    /// <summary>The owner key, or null when this home has none (a vessel the owner endorses from elsewhere).</summary>
    public OwnerKey? LoadOwner() => File.Exists(OwnerKeyPath) ? OwnerKey.Load(OwnerKeyPath) : null;

    /// <summary>The owner key and the vessel salt, created (owner-only) when absent. Idempotent.</summary>
    public OwnerKey EnsureOwner(out bool created)
    {
        OwnerOnly.CreateDirectory(Root);
        OwnerOnly.CreateDirectory(OwnerDirectory);
        if (!File.Exists(SaltPath)) OwnerOnly.WriteNew(SaltPath, RandomNumberGenerator.GetBytes(32));
        return OwnerKey.LoadOrCreate(OwnerKeyPath, out created);
    }

    /// <summary>The vessel salt alone, created when absent: a vessel the owner endorses from elsewhere has no owner key.</summary>
    public void EnsureSalt()
    {
        OwnerOnly.CreateDirectory(Root);
        OwnerOnly.CreateDirectory(OwnerDirectory);
        if (!File.Exists(SaltPath)) OwnerOnly.WriteNew(SaltPath, RandomNumberGenerator.GetBytes(32));
    }

    /// <summary>
    /// Step one of joining a vessel the owner key is not on: the salt ensured, the cell opened, and a request holding
    /// only public values for the owner to endorse (<see cref="EndorseRequest"/>).
    /// </summary>
    public EndorsementRequest Request(IVesselSource? source = null, Position? position = null, string creatura = "creatura")
    {
        EnsureSalt();
        using var cell = OpenCell(source, position, creatura);
        var id = cell.Identity;
        return new EndorsementRequest(id.Hardware.VesselId, id.Creatura.Value, id.PublicKey, cell.Id, creatura, id.Hardware.Render());
    }

    /// <summary>Step two, on the owner's vessel: this home's owner key endorses the requested cell key. Throws when the request does not hold together.</summary>
    public VesselEndorsement EndorseRequest(EndorsementRequest request, DateTimeOffset? now = null)
    {
        if (CellKey.CellIdOf(request.CellPublicKey) != request.Cell) throw new InvalidOperationException("the request's cell id is not its key's");
        if (string.IsNullOrWhiteSpace(request.VesselId) || string.IsNullOrWhiteSpace(request.Creatura)) throw new InvalidOperationException("the request names no vessel or no creatura");
        using var owner = LoadOwner() ?? throw new InvalidOperationException($"no owner key under {Root}: endorse on the owner's vessel");
        return owner.Endorse(request.VesselId, new CreaturaId(request.Creatura), request.CellPublicKey, now ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Step three, back on the new vessel: the endorsement kept beside the cell's key when it covers this cell (its
    /// key, vessel and creatura) and its signature verifies. Throws otherwise, and keeps nothing.
    /// </summary>
    public LocalCell Accept(VesselEndorsement endorsement, IVesselSource? source = null, Position? position = null, string creatura = "creatura")
    {
        var cell = OpenCell(source, position, creatura);
        var id = cell.Identity;
        string? problem = !endorsement.Verify() ? "its signature does not verify"
            : endorsement.CellPublicKey != id.PublicKey ? "it endorses another cell key"
            : endorsement.VesselId != id.Hardware.VesselId ? "it names another vessel"
            : endorsement.Creatura != id.Creatura.Value ? "it names another creatura"
            : null;
        if (problem is not null)
        {
            cell.Dispose();
            throw new InvalidOperationException($"the endorsement does not cover {cell.Id}: {problem}");
        }

        OwnerOnly.Replace(EndorsementPathFor(id.Hardware.VesselId, cell.Name),
            JsonSerializer.SerializeToUtf8Bytes(endorsement, new JsonSerializerOptions(CellJson.Options) { WriteIndented = true }));
        return cell.Endorsed(endorsement);
    }

    public byte[] LoadSalt() => IsInitialized
        ? File.ReadAllBytes(SaltPath)
        : throw new InvalidOperationException($"no vessel salt under {Root}: run `creatura cell init` first");

    /// <summary>
    /// The creatura's cell on the vessel the source describes: the record loaded (or created with a new creatura id),
    /// the hardware probed, the cell key for this vessel loaded or generated, the endorsement read when one covers it,
    /// and a new <see cref="VesselStay"/> added to the record when the vessel or the key changed.
    /// </summary>
    public LocalCell OpenCell(IVesselSource? source = null, Position? position = null, string creatura = "creatura", DateTimeOffset? now = null)
    {
        var probe = new VesselProbe(LoadSalt(), source);
        var hardware = probe.Probe();
        position ??= probe.ProbeMesh() ?? Position.InProcess(creatura);

        var records = Records;
        var record = records.Load(creatura) ?? new AgentRecord();
        var id = CreaturaId.OfRecord(record);

        OwnerOnly.CreateDirectory(CellsDirectory);
        OwnerOnly.CreateDirectory(Path.Combine(CellsDirectory, hardware.VesselId));
        var key = CellKey.LoadOrCreate(KeyPathFor(hardware.VesselId, creatura), out _);
        var cipher = CellCipher.LoadOrCreate(EncryptionKeyPathFor(hardware.VesselId, creatura));
        var cell = LocalCell.Create(id, hardware, position, key, name: creatura, cipher: cipher);

        var endorsementPath = EndorsementPathFor(hardware.VesselId, creatura);
        if (File.Exists(endorsementPath))
        {
            var endorsement = JsonSerializer.Deserialize<VesselEndorsement>(File.ReadAllText(endorsementPath), CellJson.Options);
            if (endorsement is not null && (cell.Identity with { Endorsement = endorsement }).IsEndorsed) cell.Endorsed(endorsement);
        }

        var history = VesselHistoryOf(record).ToList();
        if (history.Count == 0 || history[^1].VesselId != hardware.VesselId || history[^1].KeyFingerprint != key.Fingerprint)
        {
            history.Add(new VesselStay(hardware.VesselId, key.Fingerprint, (now ?? DateTimeOffset.UtcNow).ToUniversalTime()));
            record.SetPart(VesselsPartKey, history);
        }
        records.Save(creatura, record);
        return cell;
    }

    /// <summary>
    /// The cell endorsed by this home's owner key, the endorsement written beside the cell's key. A cell already
    /// endorsed by this owner is returned as it is. Throws when this home has no owner key.
    /// </summary>
    public LocalCell Endorse(LocalCell cell, DateTimeOffset? now = null)
    {
        using var owner = LoadOwner() ?? throw new InvalidOperationException($"no owner key under {Root}: run `creatura cell init` on the owner's vessel");
        if (cell.Identity.IsEndorsed && cell.Identity.Endorsement!.OwnerPublicKey == owner.PublicKey) return cell;

        var identity = cell.Identity;
        var endorsement = owner.Endorse(identity.Hardware.VesselId, identity.Creatura, identity.PublicKey, now ?? DateTimeOffset.UtcNow);
        OwnerOnly.Replace(EndorsementPathFor(identity.Hardware.VesselId, cell.Name),
            JsonSerializer.SerializeToUtf8Bytes(endorsement, new JsonSerializerOptions(CellJson.Options) { WriteIndented = true }));
        return cell.Endorsed(endorsement);
    }

    /// <summary>The owner key ensured, the cell opened and endorsed: what <c>creatura cell init</c> does. Idempotent.</summary>
    public LocalCell Init(IVesselSource? source = null, Position? position = null, string creatura = "creatura", DateTimeOffset? now = null)
    {
        EnsureOwner(out _).Dispose();
        return Endorse(OpenCell(source, position, creatura, now), now);
    }

    public static IReadOnlyList<VesselStay> VesselHistoryOf(AgentRecord record) => record.GetPart<List<VesselStay>>(VesselsPartKey) ?? [];

    private static string SafeName(string creatura)
    {
        if (string.IsNullOrWhiteSpace(creatura) || creatura.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')) || creatura.StartsWith('.'))
        {
            throw new ArgumentException("a creatura's name is letters, digits, '-', '_' and '.', not starting with '.'", nameof(creatura));
        }
        return creatura;
    }
}
