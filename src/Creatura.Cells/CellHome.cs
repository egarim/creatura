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
public sealed class CellHome
{
    public const string VesselsPartKey = "cells.vessels";

    public CellHome(string? root = null) => Root = Path.GetFullPath(root ?? DefaultRoot);

    public static string DefaultRoot => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".creatura");

    public string Root { get; }

    public string OwnerDirectory => Path.Combine(Root, "owner");

    public string CellsDirectory => Path.Combine(Root, "cells");

    public string RecordsDirectory => Path.Combine(Root, "records");

    public string OwnerKeyPath => Path.Combine(OwnerDirectory, "owner.key");

    public string SaltPath => Path.Combine(OwnerDirectory, "vessel.salt");

    public string LedgerPath => Path.Combine(Root, "ledger.json");

    public JsonLedgerStore Ledger => new(LedgerPath);

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
        var cell = LocalCell.Create(id, hardware, position, key, name: creatura);

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
