using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Creatura.Cells;

public enum SharedFileProblem
{
    /// <summary>This cell is not among the file's recipients, or its wrapped key does not open with this cell's key.</summary>
    NotARecipient,

    /// <summary>The bytes are not the ones the entry names: changed at the hub, on disk, or filed under another hash.</summary>
    Altered,

    /// <summary>Over the size limit (<see cref="SharedFiles.MaxBytes"/>, or the hub's).</summary>
    TooLarge,

    /// <summary>The hub kept the file for its retention period and has deleted it; the entry remains.</summary>
    Expired,

    /// <summary>The hub has never had the file.</summary>
    NotFound,

    /// <summary>The cell has no encryption key, so nothing can be sealed to it or opened by it.</summary>
    NoEncryptionKey,
}

public sealed class SharedFileException(SharedFileProblem problem, string message) : Exception(message)
{
    public SharedFileProblem Problem { get; } = problem;
}

/// <summary>A cell a file can be sealed to: its id and the public half of its encryption key.</summary>
public sealed record Recipient(string Cell, string EncryptionKey);

/// <summary>A sealed file: the bytes for the hub, and the payload of the Artifact entry that lets its recipients open them.</summary>
public sealed record SealedFile(byte[] Ciphertext, ArtifactPayload Payload);

/// <summary>A file opened by one of its recipients.</summary>
public sealed record OpenedFile(string Name, string ContentType, byte[] Content);

/// <summary>
/// Sealing and opening shared files (docs/design-shared-files.md). A fresh 256-bit key per file (D1) encrypts the
/// bytes with AES-256-GCM, and, separately, a small label holding the name and content type (D6), so a recipient can
/// list a file without downloading it and the hub learns neither. The key is wrapped for each recipient: ECDH between
/// a fresh ephemeral key and the recipient's encryption key, SHA-256 over a context naming the recipient and the
/// ciphertext, then AES-GCM. Everything comes with .NET, so the cells stay package-free.
/// </summary>
public static class SharedFiles
{
    /// <summary>The largest file a cell shares (D3): 64 MB.</summary>
    public const int MaxBytes = 64 * 1024 * 1024;

    private static readonly byte[] Magic = "CRF1"u8.ToArray();
    private const int NonceBytes = 12;
    private const int TagBytes = 16;

    /// <summary>Lowercase hex SHA-256, as the entry and the hub name files.</summary>
    public static string Hash(ReadOnlySpan<byte> bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    public static bool IsHash(string? value) => value is { Length: 64 } && value.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');

    /// <summary>
    /// The cells a file is sealed to: by default every cell this ledger has a hello with an encryption key from, minus
    /// revoked cells (D2), and always the sharer. <paramref name="only"/> narrows it to those cells (and the sharer);
    /// a named cell this ledger knows no encryption key for is an error.
    /// </summary>
    public static IReadOnlyList<Recipient> Recipients(WorkLedger ledger, LocalCell sharer, IEnumerable<string>? only = null)
    {
        var known = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var hello in ledger.Entries.Where(e => e.Kind == EntryKind.Hello))
        {
            if (ledger.IsRevoked(hello.Cell)) continue;
            if (hello.Body<HelloPayload>().Cell.EncryptionKey is { Length: > 0 } key) known[hello.Cell] = key;
        }
        known[sharer.Id] = sharer.Identity.EncryptionKey ?? throw new SharedFileException(SharedFileProblem.NoEncryptionKey, $"{sharer.Id} has no encryption key");

        if (only is not null)
        {
            var named = only.ToHashSet(StringComparer.Ordinal);
            foreach (var cell in named)
            {
                if (!known.ContainsKey(cell)) throw new SharedFileException(SharedFileProblem.NoEncryptionKey, $"no encryption key known for {cell}: it has not said a hello with one here, or it is revoked");
            }
            named.Add(sharer.Id);
            return known.Where(k => named.Contains(k.Key)).OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new Recipient(k.Key, k.Value)).ToList();
        }
        return known.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => new Recipient(k.Key, k.Value)).ToList();
    }

    public static SealedFile Seal(byte[] content, string name, string contentType, IReadOnlyList<Recipient> recipients)
    {
        if (content.Length > MaxBytes) throw new SharedFileException(SharedFileProblem.TooLarge, string.Create(TraceText.Culture, $"{name} is {content.Length:N0} bytes; the limit is {MaxBytes:N0}"));
        if (recipients.Count == 0) throw new SharedFileException(SharedFileProblem.NotARecipient, "a file needs at least one recipient");

        var fileKey = RandomNumberGenerator.GetBytes(32);
        try
        {
            var ciphertext = new byte[Magic.Length + NonceBytes + TagBytes + content.Length];
            Magic.CopyTo(ciphertext, 0);
            var nonce = ciphertext.AsSpan(Magic.Length, NonceBytes);
            RandomNumberGenerator.Fill(nonce);
            using (var aes = new AesGcm(fileKey, TagBytes))
            {
                aes.Encrypt(nonce, content, ciphertext.AsSpan(Magic.Length + NonceBytes + TagBytes), ciphertext.AsSpan(Magic.Length + NonceBytes, TagBytes), Magic);
            }
            var cipherHash = Hash(ciphertext);

            var label = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["name"] = name, ["type"] = contentType });
            var keys = recipients.Select(r => Wrap(fileKey, r, cipherHash)).ToList();
            var payload = new ArtifactPayload(cipherHash, ciphertext.Length, Hash(content), Seal(fileKey, label, LabelContext(cipherHash)), keys);
            return new SealedFile(ciphertext, payload);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    /// <summary>The file's name and content type, for a recipient, without its bytes.</summary>
    public static (string Name, string ContentType) Label(ArtifactPayload payload, LocalCell me)
    {
        var fileKey = Unwrap(payload, me);
        try
        {
            return ReadLabel(payload, fileKey);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    /// <summary>Opens a file for a recipient: the ciphertext checked against the entry, the key unwrapped, the bytes decrypted and checked.</summary>
    public static OpenedFile Open(byte[] ciphertext, ArtifactPayload payload, LocalCell me)
    {
        if (ciphertext.Length != payload.Size || Hash(ciphertext) != payload.Ciphertext)
            throw new SharedFileException(SharedFileProblem.Altered, "the file was altered: its bytes are not the ones the entry names");
        if (ciphertext.Length < Magic.Length + NonceBytes + TagBytes || !ciphertext.AsSpan(0, Magic.Length).SequenceEqual(Magic))
            throw new SharedFileException(SharedFileProblem.Altered, "the file was altered: it is not a sealed file");

        var fileKey = Unwrap(payload, me);
        try
        {
            var (name, type) = ReadLabel(payload, fileKey);
            var content = new byte[ciphertext.Length - Magic.Length - NonceBytes - TagBytes];
            try
            {
                using var aes = new AesGcm(fileKey, TagBytes);
                aes.Decrypt(ciphertext.AsSpan(Magic.Length, NonceBytes), ciphertext.AsSpan(Magic.Length + NonceBytes + TagBytes),
                    ciphertext.AsSpan(Magic.Length + NonceBytes, TagBytes), content, Magic);
            }
            catch (AuthenticationTagMismatchException)
            {
                throw new SharedFileException(SharedFileProblem.Altered, "the file was altered: it does not decrypt");
            }
            if (Hash(content) != payload.Plain) throw new SharedFileException(SharedFileProblem.Altered, "the file was altered: its content is not the one the entry names");
            return new OpenedFile(name, type, content);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(fileKey);
        }
    }

    private static WrappedKey Wrap(byte[] fileKey, Recipient recipient, string cipherHash)
    {
        using var ephemeral = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        var wrapping = CellCipher.Agree(ephemeral, recipient.EncryptionKey, WrapContext(recipient.Cell, cipherHash));
        try
        {
            return new WrappedKey(recipient.Cell, Convert.ToBase64String(ephemeral.ExportSubjectPublicKeyInfo()), Seal(wrapping, fileKey, Encoding.UTF8.GetBytes(recipient.Cell)));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapping);
        }
    }

    private static byte[] Unwrap(ArtifactPayload payload, LocalCell me)
    {
        var wrapped = payload.Keys.FirstOrDefault(k => k.Cell == me.Id)
            ?? throw new SharedFileException(SharedFileProblem.NotARecipient, $"{me.Id} is not a recipient of this file");
        byte[] wrapping;
        try
        {
            wrapping = me.Agree(wrapped.Ephemeral, WrapContext(me.Id, payload.Ciphertext));
        }
        catch (Exception e) when (e is CryptographicException or FormatException)
        {
            throw new SharedFileException(SharedFileProblem.NotARecipient, $"{me.Id}'s wrapped key is malformed");
        }
        try
        {
            return Open(wrapping, wrapped.Sealed, Encoding.UTF8.GetBytes(me.Id))
                ?? throw new SharedFileException(SharedFileProblem.NotARecipient, $"the key wrapped for {me.Id} does not open with its encryption key");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(wrapping);
        }
    }

    private static (string Name, string ContentType) ReadLabel(ArtifactPayload payload, byte[] fileKey)
    {
        var label = Open(fileKey, payload.Label, LabelContext(payload.Ciphertext))
            ?? throw new SharedFileException(SharedFileProblem.Altered, "the file's label was altered");
        var fields = JsonSerializer.Deserialize<Dictionary<string, string>>(label) ?? [];
        return (fields.GetValueOrDefault("name") ?? "file", fields.GetValueOrDefault("type") ?? "application/octet-stream");
    }

    private static byte[] WrapContext(string cell, string cipherHash) => Encoding.UTF8.GetBytes($"creatura file key v1\n{cell}\n{cipherHash}\n");

    private static byte[] LabelContext(string cipherHash) => Encoding.UTF8.GetBytes($"creatura file label v1\n{cipherHash}");

    /// <summary>AES-256-GCM with a fresh nonce: base64 of nonce, ciphertext and tag.</summary>
    private static string Seal(byte[] key, byte[] plain, byte[] context)
    {
        var output = new byte[NonceBytes + plain.Length + TagBytes];
        var nonce = output.AsSpan(0, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, output.AsSpan(NonceBytes, plain.Length), output.AsSpan(NonceBytes + plain.Length, TagBytes), context);
        return Convert.ToBase64String(output);
    }

    /// <summary>The plain bytes of <see cref="Seal(byte[], byte[], byte[])"/>, or null when they do not authenticate.</summary>
    private static byte[]? Open(byte[] key, string sealedText, byte[] context)
    {
        byte[] input;
        try
        {
            input = Convert.FromBase64String(sealedText);
        }
        catch (FormatException)
        {
            return null;
        }
        if (input.Length < NonceBytes + TagBytes) return null;
        var plain = new byte[input.Length - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(input.AsSpan(0, NonceBytes), input.AsSpan(NonceBytes, plain.Length), input.AsSpan(NonceBytes + plain.Length, TagBytes), plain, context);
            return plain;
        }
        catch (AuthenticationTagMismatchException)
        {
            return null;
        }
    }
}
