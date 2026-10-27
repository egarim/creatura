using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Creatura.Cells;

/// <summary>
/// A signing key pair, ECDSA P-256, generated on the vessel. The private half is kept in a PKCS#8 PEM file only its
/// owner can read (<see cref="OwnerOnly"/>) and never leaves this object; the public half is the base64 of its
/// SubjectPublicKeyInfo and travels in every entry. Signatures are IEEE P1363 (r ‖ s), base64.
/// </summary>
public sealed class CellKey : IDisposable
{
    private readonly ECDsa _key;

    private CellKey(ECDsa key)
    {
        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The public key: base64 of the DER SubjectPublicKeyInfo.</summary>
    public string PublicKey { get; }

    public string Fingerprint => FingerprintOf(PublicKey);

    /// <summary>The cell id this key signs as (<see cref="CellIdOf"/>).</summary>
    public string CellId => CellIdOf(PublicKey);

    public static CellKey Generate() => new(ECDsa.Create(ECCurve.NamedCurves.nistP256));

    public static CellKey Load(string path)
    {
        var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(path));
        if (key.KeySize != 256) throw new CryptographicException("not a P-256 key");
        return new CellKey(key);
    }

    /// <summary>The key in the file, or a new one written there (owner-only) when the file does not exist.</summary>
    public static CellKey LoadOrCreate(string path, out bool created)
    {
        created = !File.Exists(path);
        if (!created) return Load(path);
        var key = Generate();
        key.Save(path);
        return key;
    }

    /// <summary>Writes the private key to a new owner-only file. Refuses to overwrite one.</summary>
    public void Save(string path) => OwnerOnly.WriteNew(path, Encoding.ASCII.GetBytes(_key.ExportPkcs8PrivateKeyPem()));

    public string Sign(ReadOnlySpan<byte> data) =>
        Convert.ToBase64String(_key.SignData(data, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

    /// <summary>True when the signature is the public key's over the data. False, never a throw, for malformed input.</summary>
    public static bool Verify(string publicKey, ReadOnlySpan<byte> data, string signature)
    {
        try
        {
            using var key = ECDsa.Create();
            key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(publicKey), out _);
            return key.KeySize == 256
                && key.VerifyData(data, Convert.FromBase64String(signature), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        }
        catch (Exception e) when (e is FormatException or CryptographicException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>"SHA256:" and the first 32 hex digits of the SHA-256 of the public key's DER bytes.</summary>
    public static string FingerprintOf(string publicKey) => "SHA256:" + Hash(publicKey)[..32];

    /// <summary>"cell-" and the first 16 hex digits of the same hash: a cell is named by its key.</summary>
    public static string CellIdOf(string publicKey) => "cell-" + Hash(publicKey)[..16];

    private static string Hash(string publicKey)
    {
        byte[] der;
        try { der = Convert.FromBase64String(publicKey); }
        catch (FormatException) { der = Encoding.UTF8.GetBytes(publicKey); }
        return Convert.ToHexStringLower(SHA256.HashData(der));
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// The owner's key: the root of trust for the ledger (decision 1 of docs/design-cells.md). Same mechanics as a
/// <see cref="CellKey"/>; it signs a <see cref="VesselEndorsement"/> for each cell the owner admits, and a ledger
/// accepts only entries whose cell carries one from an owner it trusts.
/// </summary>
public sealed class OwnerKey : IDisposable
{
    private readonly CellKey _key;

    private OwnerKey(CellKey key) => _key = key;

    public string PublicKey => _key.PublicKey;

    public string Fingerprint => _key.Fingerprint;

    public static OwnerKey Load(string path) => new(CellKey.Load(path));

    public static OwnerKey LoadOrCreate(string path, out bool created) => new(CellKey.LoadOrCreate(path, out created));

    /// <summary>Signs that this cell key, of this creatura, on this vessel, is the owner's.</summary>
    public VesselEndorsement Endorse(string vesselId, CreaturaId creatura, string cellPublicKey, DateTimeOffset at)
    {
        var unsigned = new VesselEndorsement(vesselId, creatura.Value, cellPublicKey, PublicKey, at.ToUniversalTime(), "");
        return unsigned with { Signature = _key.Sign(unsigned.SignedBytes()) };
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>
/// The owner's word that a cell key on a vessel is theirs: vessel id, creatura id, the cell's public key, the owner's
/// public key, when, and the owner's signature over all five. It binds the key to the vessel, so a key carried to
/// another vessel is not covered there (C3).
/// </summary>
public sealed record VesselEndorsement(string VesselId, string Creatura, string CellPublicKey, string OwnerPublicKey, DateTimeOffset IssuedAt, string Signature)
{
    private const string Purpose = "creatura vessel endorsement v1";

    /// <summary>The bytes the owner signs: the five fields and a purpose string, canonical JSON.</summary>
    public byte[] SignedBytes() => CellJson.CanonicalBytes(JsonSerializer.SerializeToElement(new Dictionary<string, string>
    {
        ["purpose"] = Purpose,
        ["vesselId"] = VesselId,
        ["creatura"] = Creatura,
        ["cellPublicKey"] = CellPublicKey,
        ["ownerPublicKey"] = OwnerPublicKey,
        ["issuedAt"] = CellJson.Stamp(IssuedAt),
    }));

    /// <summary>True when the owner's signature verifies.</summary>
    public bool Verify() => CellKey.Verify(OwnerPublicKey, SignedBytes(), Signature);

    /// <summary>True when the signature verifies and it is this cell key it endorses.</summary>
    public bool Covers(string cellPublicKey) => string.Equals(CellPublicKey, cellPublicKey, StringComparison.Ordinal) && Verify();

    [JsonIgnore]
    public string OwnerFingerprint => CellKey.FingerprintOf(OwnerPublicKey);
}
