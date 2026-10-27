using System.Text.Json;
using System.Text.Json.Serialization;
using Creatura.Hotz;

namespace Creatura;

/// <summary>
/// What a creature earns across cases and what an exam freezes: one record per key, kept by an
/// <see cref="IRecordStore"/>. The Core's own carry is typed (skills, recent errors, stage, predictions, coherence,
/// effect memory, dreams, territory, interrupts); what a regulator alone understands goes in <see cref="Parts"/>
/// by key, as JSON, so a store never needs to know the regulator. Quarantine is deliberately not persisted:
/// distrust is a hypothesis with a cooldown, and a new case starts with none. The <see cref="ReferenceLoop"/>
/// writes one after every sleep (<see cref="AgentRun.Record"/>) and, given a store, loads it before the run;
/// <see cref="RecordCarryExtension"/> reads and writes the Core carry through the same shape.
/// </summary>
public sealed class AgentRecord
{
    /// <summary>The shape as written today. A store refuses a record from a newer schema rather than misread it.</summary>
    public const int CurrentSchema = 1;

    public string Key { get; set; } = "";

    public int Schema { get; set; } = CurrentSchema;

    /// <summary>How many cases the record has been carried through.</summary>
    public int Cases { get; set; }

    /// <summary>The highest stage earned; 0 when none was carried.</summary>
    public int StageLevel { get; set; }

    public Dictionary<string, SkillBelief> Skills { get; set; } = new(StringComparer.Ordinal);

    public List<string> RecentErrors { get; set; } = [];

    public PredictionSnapshot? Predictions { get; set; }

    public CoherenceSnapshot? Coherence { get; set; }

    public EffectMemorySnapshot? Effects { get; set; }

    public DreamSnapshot? Dreams { get; set; }

    public TerritorySnapshot? Territory { get; set; }

    public InterruptSnapshot? Interrupts { get; set; }

    /// <summary>Regulator-private parts by key, as JSON. Written and read with <see cref="SetPart{T}"/> and <see cref="GetPart{T}"/>.</summary>
    public Dictionary<string, JsonElement> Parts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>The JSON options every store and part uses: indented, enums by name, nulls left out.</summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    public void SetPart<T>(string key, T value) => Parts[key] = JsonSerializer.SerializeToElement(value, Json);

    /// <summary>The part under a key, or default when there is none.</summary>
    public T? GetPart<T>(string key) => Parts.TryGetValue(key, out var element) ? element.Deserialize<T>(Json) : default;

    public bool HasPart(string key) => Parts.ContainsKey(key);

    /// <summary>A deep copy, through JSON: the stores hand out copies so no caller aliases what is kept.</summary>
    public AgentRecord Clone() => Parse(JsonSerializer.Serialize(this, Json));

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static AgentRecord Parse(string json)
    {
        var record = JsonSerializer.Deserialize<AgentRecord>(json, Json) ?? throw new FormatException("not an agent record");
        if (record.Schema > CurrentSchema) throw new FormatException($"record schema {record.Schema} is newer than {CurrentSchema}");
        return record;
    }
}

/// <summary>Where records live. One record per key; a load of a key never saved is null.</summary>
public interface IRecordStore
{
    AgentRecord? Load(string key);

    void Save(string key, AgentRecord record);
}

/// <summary>A store in memory, for tests and for a record extension that was given no other. Hands out copies.</summary>
public sealed class InMemoryRecordStore : IRecordStore
{
    private readonly Dictionary<string, string> _records = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> Keys => _records.Keys;

    public AgentRecord? Load(string key) => _records.TryGetValue(key, out var json) ? AgentRecord.Parse(json) : null;

    public void Save(string key, AgentRecord record)
    {
        record.Key = key;
        _records[key] = record.ToJson();
    }
}

/// <summary>A store on disk: one JSON file per key under a directory, written whole on every save.</summary>
public sealed class JsonRecordStore : IRecordStore
{
    public JsonRecordStore(string directory) => Directory = directory;

    public string Directory { get; }

    /// <summary>The file a key is kept in: the key with every character a file name cannot hold replaced by <c>_</c>, plus <c>.json</c>.</summary>
    public string PathFor(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var name = new string(key.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return Path.Combine(Directory, name + ".json");
    }

    public AgentRecord? Load(string key)
    {
        var path = PathFor(key);
        return File.Exists(path) ? AgentRecord.Parse(File.ReadAllText(path)) : null;
    }

    public void Save(string key, AgentRecord record)
    {
        record.Key = key;
        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllText(PathFor(key), record.ToJson());
    }
}
