using System.Text.Json;
using System.Text.Json.Serialization;

namespace Creatura.Graph;

/// <summary>
/// Knowledge as a portable document: the nodes and relationships one caller may read, the relations they mean, and
/// where it came from. This is the export contract, and it is deliberately provider-neutral — a visualizer, another
/// agent on another model, and a future SQLite provider all read the same document, which is what makes knowledge
/// belong to the ecosystem rather than to the model that happened to discover it. An export is filtered by the access
/// policy before it is written, because an export that respected no policy would be the easiest leak in the system.
/// </summary>
public sealed record GraphSnapshot
{
    /// <summary>What this document is, in the shape the trained networks' files use (<c>creatura.network/1</c>).</summary>
    public const string FormatName = "creatura.graph/1";

    /// <summary>
    /// How the document is written and read: camelCase, enums by name, nulls left out. camelCase rather than the
    /// Core's PascalCase because this file is an interchange contract — a visualizer, another agent and a future
    /// persistent provider all read it — and it matches the other interchange file in the repository,
    /// <c>models.json</c>.
    /// </summary>
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Which document this is. A reader that does not recognise the format says so instead of guessing.</summary>
    public string Format { get; init; } = FormatName;

    public int Schema { get; init; } = GraphSchema.Current;

    public DateTimeOffset Exported { get; init; }

    /// <summary>Who exported it, so a snapshot that turns up somewhere unexpected says where it came from.</summary>
    public string ExportedBy { get; init; } = "";

    /// <summary>The place the snapshot was taken from. Knowledge that crosses this boundary has been shared on purpose.</summary>
    public GraphScope Scope { get; init; } = GraphScope.Default;

    /// <summary>The relations in force, so a reader can tell an exclusive relation from a free one without the schema that produced it.</summary>
    public IReadOnlyList<RelationType> Relations { get; init; } = [];

    public IReadOnlyList<GraphNode> Nodes { get; init; } = [];

    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    public static GraphSnapshot Parse(string json)
    {
        var snapshot = JsonSerializer.Deserialize<GraphSnapshot>(json, Json)
                       ?? throw new FormatException("not a graph snapshot");
        if (!string.Equals(snapshot.Format, FormatName, StringComparison.Ordinal))
            throw new FormatException($"'{snapshot.Format}' is not {FormatName}");
        if (snapshot.Schema > GraphSchema.Current)
            throw new FormatException($"graph snapshot schema {snapshot.Schema} is newer than {GraphSchema.Current}");
        return snapshot;
    }

    public static GraphSnapshot FromFile(string path) => Parse(File.ReadAllText(path));

    public void WriteTo(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, ToJson());
    }
}

/// <summary>What an import did, including everything it turned down and why. An import that reported only its successes would hide the policy working.</summary>
public sealed record GraphImport(int Added, int Revised, IReadOnlyList<GraphMutation> Refused)
{
    public static readonly GraphImport Nothing = new(0, 0, []);

    public int Taken => Added + Revised;

    /// <summary>Whether anything in the document failed to arrive. An import that reported only its successes would hide the policy working.</summary>
    public bool Lossy => Refused.Count > 0;
}
