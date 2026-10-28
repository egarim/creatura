using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Creatura.Cells;

/// <summary>
/// A request to a ledger hub signed by a cell (docs/design-cells.md, "Who may read the hub"). The cell signs the
/// purpose, the method, the path and query, the time and the SHA-256 of the body; its endorsement travels with the
/// request, so the hub can check, with nothing but the owner keys it trusts, that an owner's cell sent exactly this
/// request within the last five minutes (C20).
/// </summary>
public static class HubRequest
{
    public const string EndorsementHeader = "X-Creatura-Endorsement";
    public const string TimeHeader = "X-Creatura-Time";
    public const string SignatureHeader = "X-Creatura-Signature";

    /// <summary>How far a request's time may be from the hub's clock, either way.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    private const string Purpose = "creatura hub request v1";

    public static byte[] SignedBytes(string method, string pathAndQuery, DateTimeOffset at, ReadOnlySpan<byte> body) =>
        Encoding.UTF8.GetBytes(string.Join('\n', Purpose, method.ToUpperInvariant(), pathAndQuery, CellJson.Stamp(at),
            Convert.ToHexStringLower(SHA256.HashData(body))));

    /// <summary>The three headers for a request. Throws when the cell is not endorsed.</summary>
    public static IReadOnlyList<KeyValuePair<string, string>> Sign(LocalCell cell, string method, string pathAndQuery, DateTimeOffset at, ReadOnlySpan<byte> body)
    {
        var endorsement = cell.Identity.Endorsement ?? throw new InvalidOperationException($"{cell.Id} is not endorsed, so it cannot sign hub requests");
        return
        [
            new(EndorsementHeader, Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(endorsement, CellJson.Options))),
            new(TimeHeader, CellJson.Stamp(at)),
            new(SignatureHeader, cell.Sign(SignedBytes(method, pathAndQuery, at, body))),
        ];
    }

    /// <summary>Why the request is refused, or null when an endorsed cell of a trusted owner signed exactly it within the window.</summary>
    public static string? Verify(IReadOnlyCollection<string> trustedOwners, string? endorsementHeader, string? timeHeader, string? signatureHeader,
        string method, string pathAndQuery, ReadOnlySpan<byte> body, DateTimeOffset now, out string? cell, Func<string, bool>? isRevoked = null)
    {
        cell = null;
        if (string.IsNullOrEmpty(endorsementHeader) || string.IsNullOrEmpty(timeHeader) || string.IsNullOrEmpty(signatureHeader)) return "the request is not signed";

        VesselEndorsement? endorsement;
        try
        {
            endorsement = JsonSerializer.Deserialize<VesselEndorsement>(Convert.FromBase64String(endorsementHeader), CellJson.Options);
        }
        catch (Exception e) when (e is FormatException or JsonException or NotSupportedException)
        {
            return "the endorsement header is not an endorsement";
        }
        if (endorsement is null) return "the endorsement header is not an endorsement";
        if (!trustedOwners.Contains(endorsement.OwnerPublicKey, StringComparer.Ordinal)) return "the cell is endorsed by an owner this hub does not trust";
        if (!endorsement.Verify()) return "the endorsement's signature does not verify";

        if (!DateTimeOffset.TryParseExact(timeHeader, "yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at))
            return "the time header is not a time";
        if ((now - at).Duration() > Window) return "the request was signed more than five minutes from the hub's clock";

        if (!CellKey.Verify(endorsement.CellPublicKey, SignedBytes(method, pathAndQuery, at, body), signatureHeader))
            return "the signature does not cover this request with the endorsed key";
        var signer = CellKey.CellIdOf(endorsement.CellPublicKey);
        if (isRevoked?.Invoke(signer) == true) return "the cell is revoked";
        cell = signer;
        return null;
    }
}
