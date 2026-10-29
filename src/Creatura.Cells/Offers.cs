using System.Security.Cryptography;
using System.Text.Json;

namespace Creatura.Cells;

/// <summary>
/// A goal the owner offers a cell (docs/design-activity-stream.md, "Offering a goal"): an id, to whom (a cell id or a
/// vessel id: any cell on that vessel), the goal and optionally where, when it was made and when it lapses, the owner's
/// public key and the owner's signature over all of it. It travels as an <see cref="EntryKind.Offer"/> entry of one
/// of the owner's cells, and a ledger takes it only when the owner signature inside verifies and the owner is one it
/// trusts: the cell signature around it is not enough.
/// </summary>
public sealed record GoalOffer(string Id, string To, string Goal, DateTimeOffset IssuedAt, DateTimeOffset ExpiresAt, string OwnerPublicKey, string Signature)
{
    private const string Purpose = "creatura goal offer v1";

    public string? Repo { get; init; }

    public string? Branch { get; init; }

    public string? Folder { get; init; }

    public string OwnerFingerprint => CellKey.FingerprintOf(OwnerPublicKey);

    public static string NewId() => "of-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(5));

    public byte[] SignedBytes() => CellJson.CanonicalBytes(JsonSerializer.SerializeToElement(new Dictionary<string, string?>
    {
        ["purpose"] = Purpose,
        ["id"] = Id,
        ["to"] = To,
        ["goal"] = Goal,
        ["repo"] = Repo,
        ["branch"] = Branch,
        ["folder"] = Folder,
        ["issuedAt"] = CellJson.Stamp(IssuedAt),
        ["expiresAt"] = CellJson.Stamp(ExpiresAt),
        ["ownerPublicKey"] = OwnerPublicKey,
    }));

    /// <summary>True when it says something, lapses after it was made, and the owner's signature verifies.</summary>
    public bool Verify() =>
        !string.IsNullOrWhiteSpace(Id) && !string.IsNullOrWhiteSpace(To) && !string.IsNullOrWhiteSpace(Goal) && ExpiresAt > IssuedAt
        && !string.IsNullOrEmpty(Signature) && CellKey.Verify(OwnerPublicKey, SignedBytes(), Signature);
}

/// <summary>The owner taking an offer back: which offer, when, and the owner's signature. Only the offer's own owner can withdraw it.</summary>
public sealed record GoalWithdrawal(string Offer, DateTimeOffset IssuedAt, string OwnerPublicKey, string Signature)
{
    private const string Purpose = "creatura goal withdrawal v1";

    public byte[] SignedBytes() => CellJson.CanonicalBytes(JsonSerializer.SerializeToElement(new Dictionary<string, string>
    {
        ["purpose"] = Purpose,
        ["offer"] = Offer,
        ["issuedAt"] = CellJson.Stamp(IssuedAt),
        ["ownerPublicKey"] = OwnerPublicKey,
    }));

    public bool Verify() => !string.IsNullOrWhiteSpace(Offer) && !string.IsNullOrEmpty(Signature) && CellKey.Verify(OwnerPublicKey, SignedBytes(), Signature);
}

public sealed record OfferPayload(GoalOffer Offer) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Offer;
}

public sealed record WithdrawPayload(GoalWithdrawal Withdrawal) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Withdraw;
}

/// <summary>Where an offer stands in a ledger. Expiry is not a state here, since it depends on the clock: readers compare <see cref="GoalOffer.ExpiresAt"/> with theirs.</summary>
public enum OfferStatus
{
    Open,
    Taken,

    /// <summary>Two or more claims cite it: both stand, and the stream marks them.</summary>
    Conflict,
    Withdrawn,
}

/// <summary>An offer, the entry that carries it, where it stands, the claims that took it and the cells that declined it.</summary>
public sealed record OfferState(GoalOffer Offer, SignedEntry Entry, OfferStatus Status, IReadOnlyList<SignedEntry> Claims, IReadOnlyList<string> DeclinedBy);

public static class OfferLedger
{
    /// <summary>Every offer in the ledger, oldest first, with where it stands.</summary>
    public static IReadOnlyList<OfferState> OfferStates(this WorkLedger ledger)
    {
        var entries = ledger.Entries;
        var withdrawn = entries.Where(e => e.Kind == EntryKind.Withdraw).Select(e => e.Body<WithdrawPayload>().Withdrawal)
            .Select(w => (w.Offer, w.OwnerPublicKey)).ToHashSet();
        var claims = entries.Where(e => e.Kind == EntryKind.Claim && e.Body<ClaimPayload>().Offer is not null)
            .ToLookup(e => e.Body<ClaimPayload>().Offer!, StringComparer.Ordinal);
        var declines = entries.Where(e => e.Kind == EntryKind.HandOff && e.Body<HandOffPayload>().Offer is not null)
            .ToLookup(e => e.Body<HandOffPayload>().Offer!, StringComparer.Ordinal);

        var states = new List<OfferState>();
        foreach (var entry in entries.Where(e => e.Kind == EntryKind.Offer))
        {
            var offer = entry.Body<OfferPayload>().Offer;
            var taken = claims[offer.Id].ToList();
            var status = taken.Count > 1 ? OfferStatus.Conflict
                : taken.Count == 1 ? OfferStatus.Taken
                : withdrawn.Contains((offer.Id, offer.OwnerPublicKey)) ? OfferStatus.Withdrawn
                : OfferStatus.Open;
            states.Add(new OfferState(offer, entry, status, taken, declines[offer.Id].Select(d => d.Cell).Distinct().ToList()));
        }

        return states;
    }

    /// <summary>
    /// The offers waiting for this cell at <paramref name="now"/>: addressed to it or to its vessel, open, not lapsed,
    /// and not declined by it. Every offer here has passed the ledger's check of the owner's signature and trust.
    /// </summary>
    public static IReadOnlyList<GoalOffer> PendingOffers(this WorkLedger ledger, string cell, string vesselId, DateTimeOffset now) =>
        ledger.OfferStates()
            .Where(s => s.Status == OfferStatus.Open && (s.Offer.To == cell || s.Offer.To == vesselId) && s.Offer.ExpiresAt > now && !s.DeclinedBy.Contains(cell))
            .Select(s => s.Offer).ToList();

    /// <summary>The offer with this id, in any state, or null.</summary>
    public static OfferState? OfferState(this WorkLedger ledger, string id) => ledger.OfferStates().FirstOrDefault(s => s.Offer.Id == id);
}
