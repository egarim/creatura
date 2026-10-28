using System.Text.Json.Serialization;

namespace Creatura.Cells;

/// <summary>
/// Who a cell is: the creatura (stable across vessels), the vessel's hardware (its id hashed), where it is reachable,
/// its public key and the owner's endorsement of that key on that vessel. A cell is one creatura on one vessel; its
/// id is named by its key (<see cref="CellKey.CellIdOf"/>). <see cref="Model"/> is the Core's model half of the vessel,
/// when a host states it.
/// </summary>
public sealed record CellIdentity(CreaturaId Creatura, Hardware Hardware, Position Position, string PublicKey)
{
    public VesselEndorsement? Endorsement { get; init; }

    /// <summary>The public half of the cell's encryption key (<see cref="CellCipher"/>): what files are sealed to. Null in a hello from before shared files.</summary>
    public string? EncryptionKey { get; init; }

        /// <summary>The model the creatura runs on here (the Core's <see cref="Vessel"/>), when known.</summary>
    public Vessel? Model { get; init; }

    [JsonIgnore]
    public string Cell => CellKey.CellIdOf(PublicKey);

    [JsonIgnore]
    public string Fingerprint => CellKey.FingerprintOf(PublicKey);

    /// <summary>True when the endorsement verifies and names this key, this creatura and this vessel.</summary>
    [JsonIgnore]
    public bool IsEndorsed => Endorsement is { } e
        && e.Covers(PublicKey)
        && e.VesselId == Hardware.VesselId
        && e.Creatura == Creatura.Value;

    public override string ToString() =>
        $"{Cell}: creatura {Creatura} on vessel {Hardware.VesselId} ({Hardware.Render()}), {Position.Render()}, key {Fingerprint}, "
        + (IsEndorsed ? $"endorsed by {Endorsement!.OwnerFingerprint}" : "not endorsed");
}

/// <summary>
/// The cell running here: its identity and the private key that signs for it. Made by <see cref="CellHome.OpenCell"/>
/// (or <see cref="Create"/> for a caller that keeps its own files); the key never leaves this object.
/// </summary>
public sealed class LocalCell : IDisposable
{
    private readonly CellKey _key;
    private readonly CellCipher? _cipher;

    private LocalCell(CellIdentity identity, CellKey key, string name, CellCipher? cipher = null)
    {
        Name = name;
        if (identity.PublicKey != key.PublicKey) throw new ArgumentException("the identity's public key is not this key's", nameof(identity));
        if (cipher is not null && identity.EncryptionKey != cipher.PublicKey) throw new ArgumentException("the identity's encryption key is not this key's", nameof(identity));
        Identity = identity;
        _key = key;
        _cipher = cipher;
    }

    public CellIdentity Identity { get; private set; }

    public string Id => Identity.Cell;

    /// <summary>The creatura's name in its home: the key its record is kept under.</summary>
    public string Name { get; }

    public static LocalCell Create(CreaturaId creatura, Hardware hardware, Position position, CellKey key, VesselEndorsement? endorsement = null, string name = "creatura",
        CellCipher? cipher = null) =>
        new(new CellIdentity(creatura, hardware, position, key.PublicKey) { Endorsement = endorsement, EncryptionKey = cipher?.PublicKey }, key, name, cipher);

    /// <summary>The key agreed between this cell's encryption key and another public key (<see cref="CellCipher.Agree"/>). Throws when the cell has none.</summary>
    public byte[] Agree(string otherPublicKey, byte[] context) =>
        (_cipher ?? throw new SharedFileException(SharedFileProblem.NoEncryptionKey, $"{Id} has no encryption key")).Agree(otherPublicKey, context);

    /// <summary>Takes the owner's endorsement. Refuses one that does not cover this key on this vessel for this creatura.</summary>
    public LocalCell Endorsed(VesselEndorsement endorsement)
    {
        var endorsed = Identity with { Endorsement = endorsement };
        if (!endorsed.IsEndorsed) throw new ArgumentException("the endorsement does not cover this cell's key, vessel and creatura", nameof(endorsement));
        Identity = endorsed;
        return this;
    }

    /// <summary>The cell where it is reachable now; the key and the endorsement stay.</summary>
    public LocalCell At(Position position)
    {
        Identity = Identity with { Position = position };
        return this;
    }

    public string Sign(ReadOnlySpan<byte> data) => _key.Sign(data);

    public void Dispose()
    {
        _key.Dispose();
        _cipher?.Dispose();
    }

    public override string ToString() => Identity.ToString();
}
