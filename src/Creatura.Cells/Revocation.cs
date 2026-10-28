using System.Text.Json;

namespace Creatura.Cells;

/// <summary>
/// The owner's word that a cell is no longer theirs past a point: the cell, its public key, the highest sequence still
/// trusted, why, when, the owner's public key and the owner's signature over all of it (docs/design-cells.md,
/// "Replays and revocation"). Ledgers refuse the cell's entries past <see cref="UpToSequence"/> and the hub refuses the
/// cell entirely. It cannot be withdrawn: a cell that comes back gets a new key and a new endorsement.
/// </summary>
public sealed record Revocation(string Cell, string CellPublicKey, long UpToSequence, string Reason, DateTimeOffset IssuedAt, string OwnerPublicKey, string Signature)
{
    private const string Purpose = "creatura cell revocation v1";

    public byte[] SignedBytes() => CellJson.CanonicalBytes(JsonSerializer.SerializeToElement(new Dictionary<string, string>
    {
        ["purpose"] = Purpose,
        ["cell"] = Cell,
        ["cellPublicKey"] = CellPublicKey,
        ["upToSequence"] = UpToSequence.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["reason"] = Reason,
        ["issuedAt"] = CellJson.Stamp(IssuedAt),
        ["ownerPublicKey"] = OwnerPublicKey,
    }));

    /// <summary>True when the owner's signature verifies and the cell id is its key's.</summary>
    public bool Verify() => UpToSequence >= 0 && CellKey.CellIdOf(CellPublicKey) == Cell && CellKey.Verify(OwnerPublicKey, SignedBytes(), Signature);

    /// <summary>Of two revocations of one cell, the one every home keeps: the smaller sequence, then the smaller signature.</summary>
    public static Revocation Stronger(Revocation a, Revocation b) =>
        a.UpToSequence != b.UpToSequence ? (a.UpToSequence < b.UpToSequence ? a : b)
        : string.CompareOrdinal(a.Signature, b.Signature) <= 0 ? a : b;

    /// <summary>One revocation per cell, the stronger of any two.</summary>
    public static IReadOnlyList<Revocation> Merge(IEnumerable<Revocation> revocations) => revocations
        .GroupBy(r => r.Cell, StringComparer.Ordinal)
        .Select(g => g.Aggregate(Stronger))
        .OrderBy(r => r.Cell, StringComparer.Ordinal)
        .ToList();
}
