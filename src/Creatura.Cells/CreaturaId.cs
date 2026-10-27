using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Creatura.Cells;

/// <summary>
/// The creature's own stable id: created once, kept in its <see cref="AgentRecord"/> as a part, and the same after a
/// move to another vessel, a change of seat or a change of host tool. "cr-" and 20 random hex digits.
/// </summary>
[JsonConverter(typeof(CreaturaIdConverter))]
public readonly record struct CreaturaId(string Value)
{
    /// <summary>The record part the id is kept under.</summary>
    public const string PartKey = "cells.creatura-id";

    public static CreaturaId New() => new("cr-" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(10)));

    /// <summary>The id kept in the record, or a new one set into it (the caller saves the record).</summary>
    public static CreaturaId OfRecord(AgentRecord record)
    {
        if (record.GetPart<string>(PartKey) is { Length: > 0 } kept) return new CreaturaId(kept);
        var id = New();
        record.SetPart(PartKey, id.Value);
        return id;
    }

    public override string ToString() => Value;

    private sealed class CreaturaIdConverter : JsonConverter<CreaturaId>
    {
        public override CreaturaId Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => new(reader.GetString() ?? "");

        public override void Write(Utf8JsonWriter writer, CreaturaId value, JsonSerializerOptions options) => writer.WriteStringValue(value.Value);
    }
}
