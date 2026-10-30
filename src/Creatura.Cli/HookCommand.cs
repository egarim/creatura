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

    /// <summary>The offers shown to the person at this session's start; the first prompt may take one by its id.</summary>
    public List<string> Offered { get; set; } = [];

    /// <summary>
    /// How far into the session's transcript the ledger has recorded: its length at the claim and at each recorded Stop.
    /// SessionEnd reads on from here for turns whose Stop never landed (<see cref="HookTranscript"/>).
    /// </summary>
    public long TranscriptOffset { get; set; }

    public DateTimeOffset Started { get; set; }
}

/// <summary>
/// The parts of one hook event the ledger needs, and no more: no prompt text (only the offers it names and, with
/// <c>--goal prompt</c>, its scrubbed goal) and of a tool call only its name and the files it named. A hook that cannot
/// get the home's lock leaves one in <c>&lt;home&gt;/hooks-waiting/</c>; the next hook to get the lock records it at
/// <see cref="At"/>.
/// </summary>
public sealed record HookEvent
{
    public string Event { get; init; } = "";

    public string Session { get; init; } = "";

    public string Creatura { get; init; } = HookCommand.DefaultCreatura;

    /// <summary>When the event happened; set only on an event left waiting.</summary>
    public DateTimeOffset At { get; init; }

    public string? Cwd { get; init; }

    public string? Source { get; init; }

    public string? Model { get; init; }

    public string? Transcript { get; init; }

    public string? Reason { get; init; }

    public string? Tool { get; init; }

    public List<string> Files { get; init; } = [];

    public List<string> OffersNamed { get; init; } = [];

    public string? PromptGoal { get; init; }

    /// <summary>The mesh position, read at a session's start.</summary>
    public Position? Mesh { get; init; }
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

    /// <summary>How long a hook waits for the home's lock before it leaves its event for the next hook (it never blocks the session).</summary>
    public TimeSpan LockWait { get; init; } = TimeSpan.FromMilliseconds(500);
}

/// <summary>
/// <c>creatura cell hook</c>: one Claude Code hook event on stdin, at most one ledger entry out. It always exits 0,
/// whatever happens, and prints nothing, with one exception: at the start of a new session with a goal offered to this
/// cell or its vessel, it prints one JSON object whose only field is <c>systemMessage</c>, which Claude Code shows to
/// the person and does not add to the model's context (plain text would be: on <c>SessionStart</c> and
/// <c>UserPromptSubmit</c> anything else printed becomes context, and exit code 2 would block a prompt or a stop). The
/// person takes the offer by naming it in the first prompt (<c>offer of-…</c>); the claim then cites it
/// (docs/design-activity-stream.md, C29). Failures go to <c>&lt;home&gt;/hook.log</c> (owner-only), one line each,
/// without prompt text or tool data.
/// <para>
/// The mapping (docs/design-cells.md, "How host tools join"): SessionStart records the session; the first
/// UserPromptSubmit writes the Claim; PostToolUse counts tool calls; Stop writes one Progress per finished turn;
/// SessionEnd writes a Progress for each turn after the last recorded Stop, read from the transcript (Stop runs in the
/// background, and a session that exits at once, as <c>claude -p</c> does, ends before its last Stop lands), then a
/// HandOff (a session ending is not the goal being done; Release stays explicit). Events from subagents and any other
/// event write nothing.
/// </para>
/// </summary>
public static partial class HookCommand
{
    public const string DefaultCreatura = "claude-code";

    public const string ToolName = "Claude Code";

    /// <summary>The position used when the mesh is not read: the same one <c>creatura cell</c> commands use, so no new hello is written for it.</summary>
    public static readonly Position Offline = new(PositionKind.InProcess) { Channel = "creatura cell" };

    /// <summary>Entry point for <c>creatura cell hook ...</c>: never throws, returns 0, prints only an offer's message at a session's start.</summary>
    public static int Run(string[] args, TextReader stdin)
    {
        string? root = null;
        try
        {
            var options = Options(args);
            root = options.GetValueOrDefault("--home") ?? Environment.GetEnvironmentVariable("CREATURA_HOME");
            var json = stdin.ReadToEnd();
            Handle(json, new CellHome(root, LedgerStores.Factory(options.GetValueOrDefault("--store"))), options.GetValueOrDefault("--goal") ?? "folder", options.GetValueOrDefault("--creatura") ?? DefaultCreatura, new HookEnvironment(), Console.Out.Write);
        }
        catch (Exception e)
        {
            Log(root, "?", "?", e);
        }
        return 0;
    }

    /// <summary>
    /// Handles one event. Returns what it wrote to the ledger (for tests), never throws. <paramref name="say"/> gets the
    /// only thing the hook ever prints: an offer's message for the person, at a new session's start. When the home's
    /// lock stays held past <see cref="HookEnvironment.LockWait"/>, the event is left in <c>&lt;home&gt;/hooks-waiting/</c>
    /// as a <see cref="HookEvent"/> and recorded, at its own time, by the next hook that gets the lock.
    /// </summary>
    public static SignedEntry? Handle(string json, CellHome home, string goalMode, string creatura, HookEnvironment env, Action<string>? say = null)
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
            if (eventName is not ("SessionStart" or "UserPromptSubmit" or "PostToolUse" or "Stop" or "SessionEnd")) return null;

            var hook = Read(input, eventName, sessionId, goalMode, creatura);
            if (eventName == "SessionStart") hook = hook with { Mesh = env.Mesh() };  // outside the lock: it may take a moment

            using var held = LedgerLock.TryAcquire(home.Root, env.LockWait);
            if (held is null)
            {
                LeaveWaiting(home, hook with { At = env.Now() });
                Log(home.Root, eventName, sessionId, new TimeoutException("the home's lock stayed held; event left for the next hook to record"));
                return null;
            }

            Drain(home, env);
            var written = Apply(hook, home, env, say);
            Drain(home, env);                                                          // one left while this one held the lock
            return written;
        }
        catch (Exception e)
        {
            Log(home.Root, eventName, sessionId, e);
            return null;
        }
    }

    /// <summary>What an event means for the session and the ledger; the caller holds the home's lock.</summary>
    private static SignedEntry? Apply(HookEvent hook, CellHome home, HookEnvironment env, Action<string>? say)
    {
        if (hook.Event == "SessionStart") SavePosition(home, hook.Mesh ?? Offline);
        var loaded = LoadSession(home, hook.Session);
        if (loaded is null && hook.Event is "Stop" or "SessionEnd") return null;     // ended already, or began before the hooks: nothing to record against
        var session = loaded ?? NewSession(hook, env);
        var creatura = hook.Creatura;
        SignedEntry? written = null;

        switch (hook.Event)
        {
            case "SessionStart":
                session.Source = hook.Source ?? session.Source;
                session.Model = hook.Model ?? session.Model;
                if (hook.Cwd is { } cwd && cwd != session.Cwd) Locate(session, cwd, env);
                // A new conversation, not a resumed or compacted one, is told what the owner has offered here.
                if (say is not null && session.Source is not ("resume" or "compact") && Pending(home, creatura, env) is { Count: > 0 } offers)
                {
                    session.Offered = offers.Select(o => o.Id).ToList();
                    say(JsonSerializer.Serialize(new Dictionary<string, string> { ["systemMessage"] = OfferMessage(offers) }));
                }
                break;

            case "UserPromptSubmit":
                if (session.ClaimId is not null) break;
                var taken = TakenOffer(hook.OffersNamed, session, Pending(home, creatura, env));
                var goal = taken?.Goal ?? hook.PromptGoal ?? FolderGoal(session);
                written = Write(home, creatura, env, new ClaimPayload(goal)
                {
                    Repo = taken?.Repo ?? session.Repo,
                    Branch = taken?.Branch ?? session.Branch,
                    Folder = taken?.Folder ?? session.Cwd,
                    Tool = ToolName,
                    Session = hook.Session,
                    Offer = taken?.Id,
                });
                if (written is not null)
                {
                    session.ClaimId = written.Id;
                    session.TranscriptOffset = HookTranscript.Length(hook.Transcript);
                }
                break;

            case "PostToolUse":
                var tool = hook.Tool ?? "tool";
                session.Tools[tool] = session.Tools.GetValueOrDefault(tool) + 1;
                foreach (var file in hook.Files.Where(f => !session.Files.Contains(f))) session.Files.Add(file);
                break;

            case "Stop":
                if (session.ClaimId is not null)
                {
                    session.Turns++;
                    written = Write(home, creatura, env, new ProgressPayload(session.ClaimId, TurnRow(session)));
                    session.TranscriptOffset = HookTranscript.Length(hook.Transcript);
                }
                session.Tools.Clear();
                session.Files.Clear();
                break;

            case "SessionEnd":
                if (session.ClaimId is not null)
                {
                    var rows = new List<ProgressPayload>();
                    foreach (var turn in HookTranscript.TurnsAfter(hook.Transcript, session.TranscriptOffset))
                    {
                        session.Turns++;
                        rows.Add(new ProgressPayload(session.ClaimId, TurnRow(session.Turns, turn.Tools, turn.Files)));
                    }
                    var reason = hook.Reason ?? "other";
                    written = Write(home, creatura, env, rows, new HandOffPayload(session.ClaimId,
                        $"session ended ({reason}) after {session.Turns} turn{(session.Turns == 1 ? "" : "s")}"));
                }
                DeleteSession(home, hook.Session);
                return written;

            default:
                return null;
        }

        SaveSession(home, session);
        return written;
    }

    /// <summary>The fields of an event that <see cref="Apply"/> uses: the prompt only as the offers it names and, with <c>--goal prompt</c>, its scrubbed goal.</summary>
    private static HookEvent Read(JsonElement input, string eventName, string sessionId, string goalMode, string creatura)
    {
        var files = new List<string>();
        if (input.TryGetProperty("tool_input", out var toolInput) && toolInput.ValueKind == JsonValueKind.Object)
        {
            foreach (var name in new[] { "file_path", "notebook_path" })
            {
                if (Str(toolInput, name) is { Length: > 0 } file && !files.Contains(file)) files.Add(file);
            }
        }
        var prompt = Str(input, "prompt");
        return new HookEvent
        {
            Event = eventName,
            Session = sessionId,
            Creatura = creatura,
            Cwd = Str(input, "cwd"),
            Source = Str(input, "source"),
            Model = Str(input, "model"),
            Transcript = Str(input, "transcript_path"),
            Reason = Str(input, "reason"),
            Tool = Str(input, "tool_name"),
            Files = files,
            OffersNamed = string.IsNullOrEmpty(prompt) ? [] : OfferNamed().Matches(prompt).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            PromptGoal = goalMode == "prompt" ? Scrubber.GoalFromPrompt(prompt) : null,
        };
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"\boffer\s+([A-Za-z0-9][A-Za-z0-9_-]*)", System.Text.RegularExpressions.RegexOptions.IgnoreCase)]
    private static partial System.Text.RegularExpressions.Regex OfferNamed();

    private static string WaitingFolder(CellHome home) => Path.Combine(home.Root, "hooks-waiting");

    /// <summary>One event for a later hook: a file named by its time, written whole and then renamed, so a reader never sees half of it.</summary>
    private static void LeaveWaiting(CellHome home, HookEvent hook)
    {
        var name = $"{hook.At.UtcTicks.ToString("D19", CultureInfo.InvariantCulture)}-{Guid.NewGuid():N}.json";
        OwnerOnly.Replace(Path.Combine(WaitingFolder(home), name), JsonSerializer.SerializeToUtf8Bytes(hook, CellJson.Options));
    }

    /// <summary>Records the events left waiting, oldest first, each at its own time; the caller holds the home's lock.</summary>
    private static void Drain(CellHome home, HookEnvironment env)
    {
        var folder = WaitingFolder(home);
        if (!Directory.Exists(folder)) return;
        foreach (var path in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal).ToList())
        {
            HookEvent? waiting = null;
            try
            {
                waiting = JsonSerializer.Deserialize<HookEvent>(File.ReadAllBytes(path), CellJson.Options);
                if (waiting is not null) Apply(waiting, home, env with { Now = () => waiting.At }, say: null);
            }
            catch (Exception e)
            {
                Log(home.Root, waiting?.Event ?? "?", waiting?.Session ?? "?", e);              // recorded once or never, not retried forever
            }
            File.Delete(path);
        }
    }

    /// <summary>The message the person sees: each offer's goal and id, and how to take it. Nothing else of the ledger.</summary>
    public static string OfferMessage(IReadOnlyList<GoalOffer> offers)
    {
        var lines = offers.Select(o => $"- \"{o.Goal}\" (offer {o.Id}, open until {o.ExpiresAt.UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture)} UTC)");
        var noun = offers.Count == 1 ? "a goal" : $"{offers.Count} goals";
        return $"Creatura: the owner offered this vessel {noun}:\n{string.Join("\n", lines)}\nTo take one, name it in your first message, e.g. \"offer {offers[0].Id}\". Nothing is started unless you do.";
    }

    /// <summary>The offer the first prompt names (<c>offer of-…</c>), when it is one shown at this session's start and still waiting for this cell.</summary>
    private static GoalOffer? TakenOffer(IReadOnlyList<string> named, HookSession session, IReadOnlyList<GoalOffer> pending)
    {
        if (named.Count == 0 || session.Offered.Count == 0) return null;
        return pending.FirstOrDefault(o => session.Offered.Contains(o.Id) && named.Contains(o.Id, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>The offers waiting for this home's cell of <paramref name="creatura"/>; none when it is not endorsed.</summary>
    private static IReadOnlyList<GoalOffer> Pending(CellHome home, string creatura, HookEnvironment env)
    {
        using var cell = home.OpenCell(env.Vessel, LoadPosition(home), creatura);
        if (!cell.Identity.IsEndorsed) return [];
        using var owner = home.LoadOwner();
        var ledger = new WorkLedger(new[] { owner?.PublicKey, cell.Identity.Endorsement?.OwnerPublicKey }.OfType<string>().Distinct());
        home.ApplyRevocations(ledger);
        LedgerStores.LoadInto(home, home.Ledger, ledger);
        return ledger.PendingOffers(cell.Id, cell.Identity.Hardware.VesselId, env.Now());
    }

    /// <summary>"turn 3: Bash x5, Edit x2; 2 files touched" from the turn's counters, never their content.</summary>
    public static string TurnRow(HookSession session) => TurnRow(session.Turns, session.Tools, session.Files);

    public static string TurnRow(int turn, IReadOnlyDictionary<string, int> toolCalls, IReadOnlyCollection<string> touched)
    {
        var tools = toolCalls.Count == 0
            ? "no tool calls"
            : string.Join(", ", toolCalls.OrderByDescending(t => t.Value).ThenBy(t => t.Key, StringComparer.Ordinal).Select(t => $"{t.Key} x{t.Value}"));
        var files = touched.Count == 0 ? "" : $"; {touched.Count} file{(touched.Count == 1 ? "" : "s")} touched";
        return $"turn {turn}: {tools}{files}";
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

    private static SignedEntry? Write<T>(CellHome home, string creatura, HookEnvironment env, T payload) where T : IEntryPayload =>
        Write(home, creatura, env, [], payload);

    /// <summary>Appends <paramref name="first"/> and then <paramref name="payload"/> in one load and one save of the ledger; returns the last.</summary>
    private static SignedEntry? Write<T>(CellHome home, string creatura, HookEnvironment env, IReadOnlyList<ProgressPayload> first, T payload) where T : IEntryPayload
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
        home.ApplyRevocations(ledger);
        var store = home.Ledger;
        LedgerStores.LoadInto(home, store, ledger);
        CellCommand.SayHelloIfNeeded(ledger, cell);
        foreach (var row in first) ledger.Append(cell, row, env.Now());
        var entry = ledger.Append(cell, payload, env.Now());
        store.Save(ledger);
        return entry;
    }

    private static HookSession NewSession(HookEvent hook, HookEnvironment env)
    {
        var session = new HookSession { SessionId = hook.Session, Started = env.Now() };
        if (hook.Cwd is { } cwd) Locate(session, cwd, env);
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

/// <summary>
/// The turns of a Claude Code transcript (one JSON object per line) after a byte offset, read for tool names and the
/// files they named, never for what was said. A prompt from the person starts a turn, and a turn counts once the
/// assistant answered it; activity before the first prompt after the offset is a turn too (its prompt landed just
/// before the offset). Subagents' lines (<c>isSidechain</c>) and Claude Code's own (<c>isMeta</c>) are not turns.
/// </summary>
public static class HookTranscript
{
    public sealed record Turn(Dictionary<string, int> Tools, List<string> Files);

    /// <summary>The transcript's length in bytes, or 0 when there is none to read.</summary>
    public static long Length(string? path)
    {
        try
        {
            return path is not null && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>The answered turns after <paramref name="offset"/>, oldest first; none when the transcript cannot be read or is shorter than the offset.</summary>
    public static IReadOnlyList<Turn> TurnsAfter(string? path, long offset)
    {
        var turns = new List<Turn>();
        try
        {
            if (path is null || !File.Exists(path)) return turns;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (offset > stream.Length) return turns;                              // not the transcript the offset was taken from
            stream.Seek(offset, SeekOrigin.Begin);
            using var reader = new StreamReader(stream);
            Turn? current = null;
            var answered = false;
            while (reader.ReadLine() is { } line)
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;                                                      // a line cut by the offset, or one being written
                }
                using (document)
                {
                    var o = document.RootElement;
                    if (o.ValueKind != JsonValueKind.Object || True(o, "isSidechain") || True(o, "isMeta")) continue;
                    var content = o.TryGetProperty("message", out var message) && message.ValueKind == JsonValueKind.Object && message.TryGetProperty("content", out var c) ? c : default;
                    var type = o.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                    if (type == "user" && IsPrompt(content))
                    {
                        if (current is not null && answered) turns.Add(current);
                        current = new Turn(new(StringComparer.Ordinal), []);
                        answered = false;
                    }
                    else if (type == "assistant")
                    {
                        current ??= new Turn(new(StringComparer.Ordinal), []);
                        answered = true;
                        if (content.ValueKind != JsonValueKind.Array) continue;
                        foreach (var block in content.EnumerateArray())
                        {
                            if (block.ValueKind != JsonValueKind.Object || !block.TryGetProperty("type", out var kind) || kind.GetString() != "tool_use") continue;
                            var name = block.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "tool";
                            current.Tools[name] = current.Tools.GetValueOrDefault(name) + 1;
                            if (!block.TryGetProperty("input", out var input) || input.ValueKind != JsonValueKind.Object) continue;
                            foreach (var field in new[] { "file_path", "notebook_path" })
                            {
                                if (input.TryGetProperty(field, out var f) && f.ValueKind == JsonValueKind.String && f.GetString() is { Length: > 0 } file && !current.Files.Contains(file)) current.Files.Add(file);
                            }
                        }
                    }
                }
            }
            if (current is not null && answered) turns.Add(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // An unreadable transcript adds no turns; the HandOff is still written.
        }
        return turns;
    }

    private static bool True(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Text from the person: a string, or blocks none of which is a tool's result.</summary>
    private static bool IsPrompt(JsonElement content) => content.ValueKind switch
    {
        JsonValueKind.String => true,
        JsonValueKind.Array => content.EnumerateArray().All(b => b.ValueKind != JsonValueKind.Object || !b.TryGetProperty("type", out var k) || k.GetString() != "tool_result"),
        _ => false,
    };
}
