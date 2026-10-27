using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Creatura;

public enum TraceKind
{
    Acted,
    Escalated,
    Blocked,
    Idle,
    Consolidated,
}

/// <summary>One line of the run, with the numbers that make the architecture visible.</summary>
public sealed record TraceEvent(
    int Tick,
    string Level,
    string SetPoint,
    double Stress,
    double Freedom,
    string Action,
    TraceKind Kind,
    string Detail)
{
    /// <summary>The typed verdict behind a cell row, when the writer set one. Readers fall back to the detail text for rows without it.</summary>
    public ActionVerdict? Verdict { get; init; }

    /// <summary>The typed hand-off reason behind an escalated row, when the writer set one.</summary>
    public HandOffReason? Reason { get; init; }
}

/// <summary>The result of one agent against one world. Same shape for all three designs so they compare.</summary>
public sealed record AgentRun(
    string Agent,
    bool GoalReached,
    int Ticks,
    IReadOnlyList<TraceEvent> Trace,
    IReadOnlyList<string> Notes)
{
    public int Escalations => Trace.Count(t => t.Kind == TraceKind.Escalated);

    public int Blocks => Trace.Count(t => t.Kind == TraceKind.Blocked);

    /// <summary>
    /// The run as JSON lines, one object per line: the header first (agent, goal, ticks), then every trace
    /// row in order, then every note in order. The golden traces under tests/golden are written with this;
    /// a trace row is one line, so a diff names the first row that moved.
    /// </summary>
    public void WriteJsonl(string path)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        using var stream = File.Create(path);
        WriteJsonl(stream);
    }

    public void WriteJsonl(Stream stream)
    {
        var options = new JsonWriterOptions { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        using var writer = new Utf8JsonWriter(stream, options);
        writer.WriteStartObject();
        writer.WriteString("agent", Agent);
        writer.WriteBoolean("goalReached", GoalReached);
        writer.WriteNumber("ticks", Ticks);
        writer.WriteEndObject();
        NewLine(writer, stream);
        foreach (var e in Trace)
        {
            writer.WriteStartObject();
            writer.WriteNumber("tick", e.Tick);
            writer.WriteString("level", e.Level);
            writer.WriteString("setPoint", e.SetPoint);
            writer.WriteNumber("stress", e.Stress);
            writer.WriteNumber("freedom", e.Freedom);
            writer.WriteString("action", e.Action);
            writer.WriteString("kind", e.Kind.ToString());
            writer.WriteString("detail", e.Detail);
            if (e.Verdict is { } verdict) writer.WriteString("verdict", verdict.ToString());
            if (e.Reason is { } reason) writer.WriteString("reason", reason.ToString());
            writer.WriteEndObject();
            NewLine(writer, stream);
        }

        foreach (var note in Notes)
        {
            writer.WriteStartObject();
            writer.WriteString("note", note);
            writer.WriteEndObject();
            NewLine(writer, stream);
        }

        static void NewLine(Utf8JsonWriter writer, Stream stream)
        {
            writer.Flush();
            stream.WriteByte((byte)'\n');
            writer.Reset(stream);
        }
    }

    /// <summary>Read a run written by <see cref="WriteJsonl(string)"/>. Rows and notes come back in file order.</summary>
    public static AgentRun ReadJsonl(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8);
        return ReadJsonl(reader);
    }

    public static AgentRun ReadJsonl(TextReader reader)
    {
        string? agent = null;
        var goalReached = false;
        var ticks = 0;
        var trace = new List<TraceEvent>();
        var notes = new List<string>();
        var line = 0;
        while (reader.ReadLine() is { } text)
        {
            line++;
            if (string.IsNullOrWhiteSpace(text)) continue;
            using var doc = JsonDocument.Parse(text);
            var o = doc.RootElement;
            if (o.TryGetProperty("note", out var note))
            {
                notes.Add(note.GetString() ?? "");
            }
            else if (o.TryGetProperty("tick", out var tick))
            {
                trace.Add(new TraceEvent(
                    tick.GetInt32(),
                    o.GetProperty("level").GetString() ?? "",
                    o.GetProperty("setPoint").GetString() ?? "",
                    o.GetProperty("stress").GetDouble(),
                    o.GetProperty("freedom").GetDouble(),
                    o.GetProperty("action").GetString() ?? "",
                    Enum.Parse<TraceKind>(o.GetProperty("kind").GetString() ?? ""),
                    o.GetProperty("detail").GetString() ?? "")
                {
                    Verdict = o.TryGetProperty("verdict", out var verdict) ? Enum.Parse<ActionVerdict>(verdict.GetString() ?? "") : null,
                    Reason = o.TryGetProperty("reason", out var reason) ? Enum.Parse<HandOffReason>(reason.GetString() ?? "") : null,
                });
            }
            else if (o.TryGetProperty("agent", out var name) && agent is null)
            {
                agent = name.GetString() ?? "";
                goalReached = o.GetProperty("goalReached").GetBoolean();
                ticks = o.GetProperty("ticks").GetInt32();
            }
            else
            {
                throw new FormatException($"line {line}: not a run header, a trace row or a note");
            }
        }

        return new AgentRun(agent ?? throw new FormatException("no run header"), goalReached, ticks, trace, notes);
    }
}

public interface IAgent
{
    string Name { get; }

    /// <summary>
    /// Async because a policy may be a model call. The architecture does not care which; the loop, the
    /// set points and the audit are identical for BFS and for DeepSeek.
    /// </summary>
    Task<AgentRun> RunAsync(IWorld world, int maxTicks = 40, CancellationToken cancellationToken = default);
}
