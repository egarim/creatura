using System.Globalization;
using System.Text.Json;

namespace Creatura.Cells;

/// <summary>
/// A cell's reputation after a shared job (docs/design-ratings.md, R2): the rating the experiment's owner computed
/// for it (μ, σ, how many jobs so far) and what the job showed (shipped, how many false claims, the contract kept),
/// the owner's public key and the owner's signature over all of it. It travels as a <see cref="EntryKind.Reputation"/>
/// entry of one of the owner's cells, and a ledger takes it only when the owner signature inside verifies by an owner
/// it trusts, the cell that carries it is not the one rated, and that cell holds no claim: a worker never rates.
/// </summary>
public sealed record CellReputation(string Cell, string Job, double Mu, double Sigma, int Games, bool Shipped, int FalseClaims, bool ContractKept,
    DateTimeOffset IssuedAt, string OwnerPublicKey, string Signature)
{
    private const string Purpose = "creatura cell reputation v1";

    /// <summary>What a leaderboard sorts by: μ − 3σ, a conservative estimate.</summary>
    public double Ordinal => Mu - 3 * Sigma;

    public string OwnerFingerprint => CellKey.FingerprintOf(OwnerPublicKey);

    public byte[] SignedBytes() => CellJson.CanonicalBytes(JsonSerializer.SerializeToElement(new Dictionary<string, string>
    {
        ["purpose"] = Purpose,
        ["cell"] = Cell,
        ["job"] = Job,
        ["mu"] = Mu.ToString("R", CultureInfo.InvariantCulture),
        ["sigma"] = Sigma.ToString("R", CultureInfo.InvariantCulture),
        ["games"] = Games.ToString(CultureInfo.InvariantCulture),
        ["shipped"] = Shipped ? "true" : "false",
        ["falseClaims"] = FalseClaims.ToString(CultureInfo.InvariantCulture),
        ["contractKept"] = ContractKept ? "true" : "false",
        ["issuedAt"] = CellJson.Stamp(IssuedAt),
        ["ownerPublicKey"] = OwnerPublicKey,
    }));

    /// <summary>True when it names a cell and a job, its numbers are sane, and the owner's signature verifies.</summary>
    public bool Verify() =>
        !string.IsNullOrWhiteSpace(Cell) && !string.IsNullOrWhiteSpace(Job) && double.IsFinite(Mu) && double.IsFinite(Sigma) && Sigma > 0 && Games > 0 && FalseClaims >= 0
        && !string.IsNullOrEmpty(Signature) && CellKey.Verify(OwnerPublicKey, SignedBytes(), Signature);
}

public sealed record ReputationPayload(CellReputation Reputation) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Reputation;
}

public static class ReputationLedger
{
    /// <summary>
    /// The cell's latest reputation in this ledger (every entry here is from an owner the ledger trusts), or null; with
    /// <paramref name="before"/>, only the entries written before that time, so two homes that hold the same entries up
    /// to a proposal's time read the same reputation for it.
    /// </summary>
    public static CellReputation? ReputationOf(this WorkLedger ledger, string cell, DateTimeOffset? before = null) => ledger.Entries
        .Where(e => e.Kind == EntryKind.Reputation && (before is null || e.Timestamp < before))
        .Select(e => e.Body<ReputationPayload>().Reputation)
        .LastOrDefault(r => r.Cell == cell);

    /// <summary>Every rated cell's latest reputation, best first.</summary>
    public static IReadOnlyList<CellReputation> Reputations(this WorkLedger ledger) => ledger.Entries
        .Where(e => e.Kind == EntryKind.Reputation)
        .Select(e => e.Body<ReputationPayload>().Reputation)
        .GroupBy(r => r.Cell, StringComparer.Ordinal)
        .Select(g => g.Last())
        .OrderByDescending(r => r.Ordinal)
        .ThenBy(r => r.Cell, StringComparer.Ordinal)
        .ToList();
}
