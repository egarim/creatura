using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Creatura.Cells;

/// <summary>The things a cell can say in the work ledger (docs/design-cells.md, "The work ledger").</summary>
public enum EntryKind
{
    /// <summary>This cell exists: creatura id, vessel, position, public key.</summary>
    Hello,

    /// <summary>I am working on this: goal, repository and branch or folder, host tool and session.</summary>
    Claim,

    /// <summary>A monstrum: a trace row, a stress level, a blocked or escalated step.</summary>
    Progress,

    /// <summary>I am stopping; this is where the work stands and why.</summary>
    HandOff,

    /// <summary>Done, with the evidence the goal cites.</summary>
    Release,

    /// <summary>Still alive, with the vessel's load.</summary>
    Heartbeat,

    /// <summary>A file shared with other cells: its hashes, its sealed label and its key wrapped for each recipient; the bytes are at the hub.</summary>
    Artifact,

    /// <summary>Something this cell saw in the world that the others may act on: a subject and the evidence. Refers to no claim.</summary>
    Warning,
}

/// <summary>A payload of one entry kind. The kind is the type's, so a payload cannot be filed under another.</summary>
public interface IEntryPayload
{
    static abstract EntryKind Kind { get; }
}

public sealed record HelloPayload(CellIdentity Cell) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Hello;
}

/// <summary>A claim on work. <see cref="Goal"/> is required; where the work happens is said with whichever of the rest apply.</summary>
public sealed record ClaimPayload(string Goal) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Claim;

    public string? Repo { get; init; }

    public string? Branch { get; init; }

    public string? Folder { get; init; }

    /// <summary>The host tool doing the work (claude-code, codex, a creatura's own loop).</summary>
    public string? Tool { get; init; }

    /// <summary>The host tool's own session id, so its history can be found.</summary>
    public string? Session { get; init; }
}

/// <summary>A step of a claim's work. <see cref="Claim"/> is the claim entry's id (<see cref="SignedEntry.Id"/>).</summary>
public sealed record ProgressPayload(string Claim, string Row) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Progress;

    /// <summary>The cell's stress, 0 to 1, when it reports one (Levin's stress bus).</summary>
    public double? Stress { get; init; }

    public bool? Blocked { get; init; }
}

public sealed record HandOffPayload(string Claim, string Reason) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.HandOff;
}

public sealed record ReleasePayload(string Claim, string Evidence) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Release;
}

public sealed record HeartbeatPayload(double? Load) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Heartbeat;
}

/// <summary>
/// A warning to the other cells: what it is about (<see cref="Subject"/>, such as <c>road:r12</c>) and what this cell saw
/// (<see cref="Evidence"/>). The cells that read it decide what to do with it; it changes no claim.
/// </summary>
public sealed record WarningPayload(string Subject, string Evidence) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Warning;
}

/// <summary>A file's key, wrapped for one recipient cell: the ephemeral public key of the agreement and the sealed key (nonce, ciphertext and tag, base64).</summary>
public sealed record WrappedKey(string Cell, string Ephemeral, string Sealed);

/// <summary>
/// A shared file (docs/design-shared-files.md): the SHA-256 of its ciphertext, by which the hub stores it, the
/// ciphertext's size, the SHA-256 of the plain bytes, the name and content type sealed with the file's key
/// (<see cref="Label"/>), and that key wrapped for each recipient. No name, no content, no key in the clear.
/// </summary>
public sealed record ArtifactPayload(string Ciphertext, long Size, string Plain, string Label, IReadOnlyList<WrappedKey> Keys) : IEntryPayload
{
    public static EntryKind Kind => EntryKind.Artifact;

    /// <summary>The claim the file belongs to, when it belongs to one.</summary>
    public string? Claim { get; init; }
}

/// <summary>
/// One signed entry of the work ledger: the cell that wrote it, that cell's own sequence (from 1), when, the kind and
/// its payload, the owner's endorsement of the cell's key, and the cell's signature over the canonical JSON of the
/// first five (<see cref="SignedBytes"/>). Self-verifying: everything needed to check it travels with it, so a ledger
/// can take entries in any order. Two entries are equal when their canonical JSON (<see cref="ToJson"/>) is.
/// </summary>
public sealed record SignedEntry
{
    public required string Cell { get; init; }

    public required long Sequence { get; init; }

    public required DateTimeOffset Timestamp { get; init; }

    public required EntryKind Kind { get; init; }

    public required JsonElement Payload { get; init; }

    public required VesselEndorsement Endorsement { get; init; }

    public required string Signature { get; init; }

    /// <summary>"cell#sequence": unique in a ledger; how Progress, HandOff and Release name the claim they are about.</summary>
    [JsonIgnore]
    public string Id => $"{Cell}#{Sequence}";

    /// <summary>The bytes the cell signs: cell, kind, payload, sequence, timestamp and a version, canonical JSON.</summary>
    public byte[] SignedBytes() => SignedBytes(Cell, Sequence, Timestamp, Kind, Payload);

    internal static byte[] SignedBytes(string cell, long sequence, DateTimeOffset timestamp, EntryKind kind, JsonElement payload)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("cell", cell);
            writer.WriteString("kind", kind.ToString());
            writer.WritePropertyName("payload");
            CellJson.Write(writer, payload);
            writer.WriteNumber("sequence", sequence);
            writer.WriteString("timestamp", CellJson.Stamp(timestamp));
            writer.WriteString("v", "creatura entry v1");
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    /// <summary>Signs a payload as the cell's entry number <paramref name="sequence"/>.</summary>
    public static SignedEntry Sign<T>(LocalCell cell, long sequence, T payload, DateTimeOffset at) where T : IEntryPayload
    {
        var endorsement = cell.Identity.Endorsement ?? throw new InvalidOperationException($"{cell.Id} is not endorsed: run `creatura cell init`");
        var element = CellJson.ToElement(payload);
        var timestamp = at.ToUniversalTime();
        return new SignedEntry
        {
            Cell = cell.Id,
            Sequence = sequence,
            Timestamp = timestamp,
            Kind = T.Kind,
            Payload = element,
            Endorsement = endorsement,
            Signature = cell.Sign(SignedBytes(cell.Id, sequence, timestamp, T.Kind, element)),
        };
    }

    /// <summary>The payload as its type. Throws when the entry is of another kind.</summary>
    public T Body<T>() where T : IEntryPayload =>
        Kind == T.Kind
            ? Payload.Deserialize<T>(CellJson.Options) ?? throw new FormatException($"{Id} has an empty payload")
            : throw new InvalidOperationException($"{Id} is a {Kind}, not a {T.Kind}");

    /// <summary>The claim a Progress, HandOff, Release or Artifact entry is about; null for the other kinds.</summary>
    [JsonIgnore]
    public string? ClaimRef => Kind is EntryKind.Progress or EntryKind.HandOff or EntryKind.Release or EntryKind.Artifact
        && Payload.ValueKind == JsonValueKind.Object && Payload.TryGetProperty("claim", out var claim) && claim.ValueKind == JsonValueKind.String ? claim.GetString() : null;

    /// <summary>The entry as canonical JSON, one line: what the ledger file holds and what equality compares.</summary>
    public string ToJson() => CellJson.Canonical(CellJson.ToElement(this));

    /// <summary>SHA-256 of <see cref="ToJson"/>, hex: the tie-break between two different entries claiming one (cell, sequence).</summary>
    public string Hash() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(ToJson())));

    public static SignedEntry Parse(string json) =>
        JsonSerializer.Deserialize<SignedEntry>(json, CellJson.Options) ?? throw new FormatException("not a signed entry");

    public bool Equals(SignedEntry? other) => other is not null && string.Equals(ToJson(), other.ToJson(), StringComparison.Ordinal);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(ToJson());

    public override string ToString() => $"{Id} {Kind} {CellJson.Stamp(Timestamp)}";
}
