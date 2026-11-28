// Vendored from Sentiora (C:\Users\joche\source\repos\st-agent, commit 302d71f),
// clients/dotnet/Sentiora.Client/Protocol.cs. Copied unchanged apart from this comment; the namespace stays
// Sentiora.Client. This copy is to be replaced by a PackageReference once Sentiora publishes one (PACKAGE.md).


namespace Sentiora.Client;

/// <summary>One JSON-RPC error returned by the runtime, with the method that produced it.</summary>
public sealed class SentioraException : Exception
{
    public SentioraException(string message, string? method = null, int? jsonRpcCode = null, Exception? inner = null)
        : base(message, inner)
    {
        Method = method;
        JsonRpcCode = jsonRpcCode;
    }

    public string? Method { get; }
    public int? JsonRpcCode { get; }
}

/// <summary>What <c>initialize</c> reports. <c>Stream</c> is the capability version for terminal.stream.</summary>
public sealed record InitializeResult
{
    public int ProtocolVersion { get; init; }
    public string Platform { get; init; } = "";
    public string Arch { get; init; } = "";
    public bool ConptyDll { get; init; }
    public int Stream { get; init; }
    public int Recording { get; init; }
}

public sealed record Cursor
{
    public int X { get; init; }
    public int Y { get; init; }
}

public sealed record ExitInfo
{
    public int ExitCode { get; init; }
}

/// <summary>
/// The full observation. Objective facts (state, pid, revision, running, exit, geometry)
/// and the rendered screen are deliberately in one object because that is what the runtime
/// returns; a consumer that wants to treat them as different kinds of evidence must split
/// them itself rather than expecting the runtime to.
/// </summary>
public sealed record Observation
{
    public string SessionId { get; init; } = "";
    public string? Owner { get; init; }
    public int? Pid { get; init; }
    public string State { get; init; } = "";
    public long Revision { get; init; }
    public bool Running { get; init; }
    public ExitInfo? Exit { get; init; }
    public int Cols { get; init; }
    public int Rows { get; init; }
    public int WriteErrors { get; init; }
    public int RenderErrors { get; init; }
    public Cursor Cursor { get; init; } = new();
    public string Screen { get; init; } = "";
    public long IdleMilliseconds { get; init; }
}

public sealed record StreamEvent
{
    public long Seq { get; init; }
    public string Kind { get; init; } = "";
    /// <summary>Base64 of the child's raw bytes, for kind == "output" | "input".</summary>
    public string? Data { get; init; }
    public ExitInfo? Exit { get; init; }
}

public sealed record SnapshotInfo
{
    public string Screen { get; init; } = "";
    public int Cols { get; init; }
    public int Rows { get; init; }
    public Cursor Cursor { get; init; } = new();
}

public sealed record StreamFrame
{
    public List<StreamEvent> Events { get; init; } = new();
    public long NextSequence { get; init; }
    public long LatestSequence { get; init; }
    public bool Resync { get; init; }
    public bool Truncated { get; init; }
    public bool Running { get; init; }
    public ExitInfo? Exit { get; init; }
    public SnapshotInfo? Snapshot { get; init; }
}

public sealed record CreateSessionRequest
{
    public string Command { get; init; } = "";
    public IReadOnlyList<string> Args { get; init; } = Array.Empty<string>();
    public int Cols { get; init; } = 120;
    public int Rows { get; init; } = 40;
    public string? Cwd { get; init; }
    /// <summary>Explicit only: a session never inherits the host environment.</summary>
    public IReadOnlyDictionary<string, string>? Env { get; init; }
    public bool? ConptyDll { get; init; }
    public bool? RecordInput { get; init; }
    public long? RecordBytes { get; init; }
}

/// <summary>A live session. Owner is the capability for type/resize/close; never log it.</summary>
public sealed record SessionHandle
{
    public required string SessionId { get; init; }
    public required string Owner { get; init; }
    public int? Pid { get; init; }
    public int Cols { get; init; }
    public int Rows { get; init; }
}

