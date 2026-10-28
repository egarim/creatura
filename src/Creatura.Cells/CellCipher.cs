using System.Security.Cryptography;
using System.Text;

namespace Creatura.Cells;

/// <summary>
/// A cell's encryption key pair, ECDH on P-256 (docs/design-shared-files.md). Generated on the vessel like the
/// signing key and kept beside it, owner-only, as PKCS#8 PEM; the private half never leaves this object. The public
/// half travels in the cell's hello, which the cell signs, so it needs no endorsement of its own.
/// </summary>
public sealed class CellCipher : IDisposable
{
    private readonly ECDiffieHellman _key;

    private CellCipher(ECDiffieHellman key)
    {
        _key = key;
        PublicKey = Convert.ToBase64String(key.ExportSubjectPublicKeyInfo());
    }

    /// <summary>The public key: base64 of the DER SubjectPublicKeyInfo.</summary>
    public string PublicKey { get; }

    public static CellCipher Generate() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    public static CellCipher LoadOrCreate(string path)
    {
        if (!File.Exists(path))
        {
            var created = Generate();
            OwnerOnly.WriteNew(path, Encoding.ASCII.GetBytes(created._key.ExportPkcs8PrivateKeyPem()));
            return created;
        }
        var key = ECDiffieHellman.Create();
        key.ImportFromPem(File.ReadAllText(path));
        if (key.KeySize != 256) throw new CryptographicException("not a P-256 key");
        return new CellCipher(key);
    }

    /// <summary>The 32-byte key agreed with another public key: SHA-256 over <paramref name="context"/> and the shared secret.</summary>
    public byte[] Agree(string otherPublicKey, byte[] context) => Agree(_key, otherPublicKey, context);

    internal static byte[] Agree(ECDiffieHellman mine, string otherPublicKey, byte[] context)
    {
        using var other = ECDiffieHellman.Create();
        other.ImportSubjectPublicKeyInfo(Convert.FromBase64String(otherPublicKey), out _);
        if (other.KeySize != 256) throw new CryptographicException("not a P-256 key");
        return mine.DeriveKeyFromHash(other.PublicKey, HashAlgorithmName.SHA256, context, null);
    }

    public void Dispose() => _key.Dispose();
}
