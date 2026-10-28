using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Creatura.Cells;

/// <summary>What a Claude Code session has done so far, kept between its hooks in <c>&lt;home&gt;/sessions/&lt;id&gt;.json</c>.</summary>
public sealed class HookSession
{
    public string SessionId { get; set; } = "";

    public string? Cwd { get; set; }

    public string? Repo { get; set; }

    public string? Branch { get; set; }

    public string? Source { get; set; }

    public string? Model { get; set; }

    /// <summary>The claim this session wrote at its first prompt, once it has.</summary>
    public string? ClaimId { get; set; }

    public int Turns { get; set; }

    /// <summary>Tool calls in the current turn, by tool name.</summary>
    public Dictionary<string, int> Tools { get; set; } = new(StringComparer.Ordinal);

    /// <summary>Distinct files the current turn's tool calls named.</summary>
    public List<string> Files { get; set; } = [];

    public DateTimeOffset Started { get; set; }
}

/// <summary>Everything a hook reads from the outside world, so tests can replace it.</summary>
public sealed record HookEnvironment
{
    public IVesselSource? Vessel { get; init; }

    /// <summary>The mesh position, read once per session start; null when the mesh is not available.</summary>
    public Func<Position?> Mesh { get; init; } = () => Position.FromTailscaleStatus(new PlatformVesselSource().MeshStatusJson());

    /// <summary>(repo, branch) of a folder, or nulls when it is not a git work tree.</summary>
    public Func<string, (string? Repo, string? Branch)> Git { get; init; } = HookCommand.ReadGit;

    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.UtcNow;

    /// <summary>How long a hook waits for the home's lock before it drops its event (it never blocks the session).</summary>
    public TimeSpan LockWait { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// <c>creatura cell hook</c>: one Claude Code hook event on stdin, at most one ledger entry out. It prints nothing and
/// always exits 0, whatever happens: on <c>SessionStart</c> and <c>UserPromptSubmit</c> anything printed would be added
/// to the model's context, and exit code 2 would block a prompt or a stop. Failures go to <c>&lt;home&gt;/hook.log</c>
/// (owner-only), one line each, without prompt text or tool data.
/// <para>
/// The mapping (docs/design-cells.md, "How host tools join"): SessionStart records the session; the first
/// UserPromptSubmit writes the Claim; PostToolUse counts tool calls; Stop writes one Progress per finished turn;
/// SessionEnd writes a HandOff (a session ending is not the goal being done; Release stays explicit). Events from
/// subagents and any other event write nothing.
/// </para>
/// </summary>
public static class HookCommand
{
    public const string DefaultCreatura = "claude-code";

    public const string ToolName = "Claude Code";

    /// <summary>The position used when the mesh is not read: the same one <c>creatura cell</c> commands use, so no new hello is written for it.</summary>
    public static readonly Position Offline = new(PositionKind.InProcess) { Channel = "creatura cell" };

    /// <summary>Entry point for <c>creatura cell hook ...</c>: never throws, never prints, returns 0.</summary>
    public static int Run(string[] args, TextReader stdin)
    {
        string? root = null;
        try
        {
            var options = Options(args);
            root = options.GetValueOrDefault("--home") ?? Environment.GetEnvironmentVariable("CREATURA_HOME");
            var json = stdin.ReadToEnd();
            Handle(json, new CellHome(root, LedgerStores.Factory(options.GetValueOrDefault("--store"))), options.GetValueOrDefault("--goal") ?? "folder", options.GetValueOrDefault("--creatura") ?? DefaultCreatura, new HookEnvironment());
        }
        catch (Exception e)
        {
            Log(root, "?", "?", e);
        }
        return 0;
    }

    /// <summary>Handles one event. Returns what it wrote to the ledger (for tests), never throws.</summary>
    public static SignedEntry? Handle(string json, CellHome home, string goalMode, string creatura, HookEnvironment env)
    {
        string eventName = "?", sessionId = "?";
        try
        {
            using var document = JsonDocument.Parse(json);
            var input = document.RootElement;
            if (input.ValueKind != JsonValueKind.Object) return null;
            eventName = Str(input, "hook_event_name") ?? "?";
            sessionId = Str(input, "session_id") ?? "?";
            if (input.TryGetProperty("agent_id", out _)) return null;                 // a subagent: its parent's turn covers it
            if (sessionId == "?" || !home.IsInitialized) return null;                  // nothing to file under, or no cell yet
            if (eventName == "Stop" && input.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True) return null;

            Position? mesh = null;
            if (eventName == "SessionStart") mesh = env.Mesh();                       // outside the lock: it may take a moment

            using var held = LedgerLock.TryAcquire(home.Root, env.LockWait);
            if (held is null)
            {
                Log(home.Root, eventName, sessionId, new TimeoutException("the home's lock stayed held; event dropped"));
                return null;
            }

            if (eventName == "SessionStart") SavePosition(home, mesh ?? Offline);
            var session = LoadSession(home, sessionId) ?? NewSession(input, sessionId, env);
            SignedEntry? written = null;

            switch (eventName)
            {
                case "SessionStart":
                    session.Source = Str(input, "source") ?? session.Source;
                    session.Model = Str(input, "model") ?? session.Model;
                    if (Str(input, "cwd") is { } cwd && cwd != session.Cwd) Locate(session, cwd, env);
                    break;

                case "UserPromptSubmit":
                    if (session.ClaimId is not null) break;
                    var goal = goalMode == "prompt" ? Scrubber.GoalFromPrompt(Str(input, "prompt")) : null;
                    goal ??= FolderGoal(session);
                    written = Write(home, creatura, env, new ClaimPayload(goal)
                    {
                        Repo = session.Repo,
                        Branch = session.Branch,
                        Folder = session.Cwd,
                        Tool = ToolName,
                        Session = sessionId,
                    });
                    if (written is not null) session.ClaimId = written.Id;
                    break;

                case "PostToolUse":
                    var tool = Str(input, "tool_name") ?? "tool";
                    session.Tools[tool] = session.Tools.GetValueOrDefault(tool) + 1;
                    if (input.TryGetProperty("tool_input", out var toolInput) && toolInput.ValueKind == JsonValueKind.Object)
                    {
                        foreach (var name in new[] { "file_path", "notebook_path" })
                        {
                            if (Str(toolInput, name) is { Length: > 0 } file && !session.Files.Contains(file)) session.Files.Add(file);
                        }
                    }
                    break;

                case "Stop":
                    if (session.ClaimId is not null)
                    {
                        session.Turns++;
                        written = Write(home, creatura, env, new ProgressPayload(session.ClaimId, TurnRow(session)));
                    }
                    session.Tools.Clear();
                    session.Files.Clear();
                    break;

                case "SessionEnd":
                    if (session.ClaimId is not null)
                    {
                        var reason = Str(input, "reason") ?? "other";
                        written = Write(home, creatura, env, new HandOffPayload(session.ClaimId,
                            $"session ended ({reason}) after {session.Turns} turn{(session.Turns == 1 ? "" : "s")}"));
                    }
                    DeleteSession(home, sessionId);
                    return written;

                default:
                    return null;
            }

            SaveSession(home, session);
            return written;
        }
        catch (Exception e)
        {
            Log(home.Root, eventName, sessionId, e);
            return null;
        }
    }

    /// <summary>"turn 3: Bash x5, Edit x2; 2 files touched" from the turn's counters, never their content.</summary>
    public static string TurnRow(HookSession session)
    {
        var tools = session.Tools.Count == 0
            ? "no tool calls"
            : string.Join(", ", session.Tools.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key} x{t.Value}"));
        var files = session.Files.Count == 0 ? "" : $"; {session.Files.Count} file{(session.Files.Count == 1 ? "" : "s")} touched";
        return $"turn {session.Turns}: {tools}{files}";
    }

    public static string FolderGoal(HookSession session)
    {
        var folder = string.IsNullOrEmpty(session.Cwd) ? "a session" : Path.GetFileName(session.Cwd.TrimEnd('/', '\\')) is { Length: > 0 } name ? name : session.Cwd;
        return session.Branch is null ? folder : $"{folder} on {session.Branch}";
    }

    /// <summary>(owner/name or a credential-free URL, branch) of a folder, read with git; nulls when it is not a work tree.</summary>
    public static (string? Repo, string? Branch) ReadGit(string folder)
    {
        if (!Directory.Exists(folder)) return (null, null);
        var branch = Git(folder, "rev-parse --abbrev-ref HEAD");
        if (branch is null) return (null, null);
        return (Scrubber.RepoFromRemote(Git(folder, "remote get-url origin")), branch == "HEAD" ? null : branch);
    }

    private static string? Git(string folder, string arguments)
    {
        try
        {
            var start = new ProcessStartInfo("git", $"-C \"{folder}\" {arguments}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            using var process = Process.Start(start);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(2000)) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            return process.ExitCode == 0 && output.Trim() is { Length: > 0 } value ? value : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    private static SignedEntry? Write<T>(CellHome home, string creatura, HookEnvironment env, T payload) where T : IEntryPayload
    {
        using var cell = home.OpenCell(env.Vessel, LoadPosition(home), creatura);
        if (!cell.Identity.IsEndorsed)
        {
            Log(home.Root, T.Kind.ToString(), "-", new InvalidOperationException($"the {creatura} cell is not endorsed: run `creatura cell init --creatura {creatura}`"));
            return null;
        }
        using var owner = home.LoadOwner();
        var trusted = new[] { owner?.PublicKey, cell.Identity.Endorsement?.OwnerPublicKey }.OfType<string>().Distinct();
        var ledger = new WorkLedger(trusted);
        var store = home.Ledger;
        LedgerStores.LoadInto(home, store, ledger);
        CellCommand.SayHelloIfNeeded(ledger, cell);
        var entry = ledger.Append(cell, payload, env.Now());
        store.Save(ledger);
        return entry;
    }

    private static HookSession NewSession(JsonElement input, string sessionId, HookEnvironment env)
    {
        var session = new HookSession { SessionId = sessionId, Started = env.Now() };
        if (Str(input, "cwd") is { } cwd) Locate(session, cwd, env);
        return session;
    }

    private static void Locate(HookSession session, string cwd, HookEnvironment env)
    {
        session.Cwd = cwd;
        (session.Repo, session.Branch) = env.Git(cwd);
    }

    private static string SessionPath(CellHome home, string sessionId)
    {
        var safe = sessionId.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_') && sessionId.Length <= 80
            ? sessionId
            : Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sessionId)))[..32];
        return Path.Combine(home.Root, "sessions", safe + ".json");
    }

    private static HookSession? LoadSession(CellHome home, string sessionId)
    {
        var path = SessionPath(home, sessionId);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<HookSession>(File.ReadAllText(path), CellJson.Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void SaveSession(CellHome home, HookSession session)
    {
        OwnerOnly.CreateDirectory(Path.Combine(home.Root, "sessions"));
        OwnerOnly.Replace(SessionPath(home, session.SessionId), JsonSerializer.SerializeToUtf8Bytes(session, CellJson.Options));
    }

    private static void DeleteSession(CellHome home, string sessionId)
    {
        var path = SessionPath(home, sessionId);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string PositionPath(CellHome home) => Path.Combine(home.Root, "position.json");

    private static void SavePosition(CellHome home, Position position) =>
        OwnerOnly.Replace(PositionPath(home), JsonSerializer.SerializeToUtf8Bytes(position, CellJson.Options));

    private static Position LoadPosition(CellHome home)
    {
        try
        {
            return File.Exists(PositionPath(home)) ? JsonSerializer.Deserialize<Position>(File.ReadAllText(PositionPath(home)), CellJson.Options) ?? Offline : Offline;
        }
        catch (JsonException)
        {
            return Offline;
        }
    }

    private static string? Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static Dictionary<string, string?> Options(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 2; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) continue;
            options[args[i]] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : null;
        }
        return options;
    }

    /// <summary>One line to <c>&lt;home&gt;/hook.log</c>: time, event, session, exception type and message. Never throws.</summary>
    private static void Log(string? root, string eventName, string sessionId, Exception e)
    {
        try
        {
            var home = Path.GetFullPath(root ?? CellHome.DefaultRoot);
            if (!Directory.Exists(home)) return;                                   // no home: nowhere to log, and nothing to say
            var path = Path.Combine(home, "hook.log");
            var line = $"{DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)} {eventName} {sessionId} {e.GetType().Name}: {Scrubber.Scrub(e.Message).ReplaceLineEndings(" ")}\n";
            if (!File.Exists(path))
            {
                OwnerOnly.WriteNew(path, System.Text.Encoding.UTF8.GetBytes(line));
                return;
            }
            File.AppendAllText(path, line);
        }
        catch (Exception)
        {
            // A hook must never fail the session it watches: a log that cannot be written is dropped.
        }
    }
}
