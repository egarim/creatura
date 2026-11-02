using System.Text.Json.Nodes;
using Creatura.Cli;

namespace Creatura.Cells.Tests;

/// <summary>Codex sessions as cells (docs/design-handoff.md, slice H2): C42 to C44.</summary>
public sealed class CodexTests
{
    private const string Creatura = HookCommand.CodexCreatura;

    private static readonly FakeVessel Vessel = new("11111111-2222-3333-4444-555555555555", "air");

    private const string Folder = "/Users/owner/Projects/creatura";

    private static HookEnvironment Env() => new()
    {
        Vessel = Vessel,
        Mesh = () => null,
        Git = folder => folder.EndsWith("creatura", StringComparison.Ordinal) ? ("egarim/creatura", "main") : (null, null),
        Now = () => DateTimeOffset.UtcNow,
        LockWait = TimeSpan.FromSeconds(30),
    };

    private static void Init(TempHome home)
    {
        var output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "init", "--home", home.Path, "--no-mesh", "--creatura", Creatura], output, output, Vessel));
    }

    private static string Event(string name, string session, string transcript, params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject { ["session_id"] = session, ["transcript_path"] = transcript, ["cwd"] = Folder, ["hook_event_name"] = name, ["model"] = "gpt-5-codex", ["permission_mode"] = "default" };
        foreach (var (key, value) in fields) o[key] = value is JsonNode node ? node : JsonValue.Create(value);
        return o.ToJsonString();
    }

    private static WorkLedger Ledger(TempHome home)
    {
        var cellHome = new CellHome(home.Path);
        using var owner = cellHome.LoadOwner()!;
        var ledger = new WorkLedger([owner.PublicKey]);
        Assert.Empty(cellHome.Ledger.LoadInto(ledger).Rejected);
        return ledger;
    }

    /// <summary>A Codex rollout with the given turns: each a turn_context, its tool calls, and a task_complete.</summary>
    private static string Rollout(string path, string threadId, params (string Tool, string? Text)[][] turns)
    {
        var lines = new List<string> { new JsonObject { ["timestamp"] = "2026-11-02T10:00:00.000Z", ["type"] = "session_meta", ["payload"] = new JsonObject { ["id"] = threadId, ["cwd"] = Folder, ["cli_version"] = "0.166.0" } }.ToJsonString() };
        var n = 0;
        foreach (var turn in turns)
        {
            n++;
            lines.Add(new JsonObject { ["type"] = "turn_context", ["payload"] = new JsonObject { ["turn_id"] = $"t{n}", ["cwd"] = Folder } }.ToJsonString());
            lines.Add(new JsonObject { ["type"] = "event_msg", ["payload"] = new JsonObject { ["type"] = "user_message", ["message"] = "the prompt, never read" } }.ToJsonString());
            foreach (var (tool, text) in turn)
            {
                lines.Add(tool == "apply_patch"
                    ? new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "custom_tool_call", ["name"] = tool, ["input"] = text, ["call_id"] = $"c{n}" } }.ToJsonString()
                    : new JsonObject { ["type"] = "response_item", ["payload"] = new JsonObject { ["type"] = "function_call", ["name"] = tool, ["arguments"] = text ?? "{}", ["call_id"] = $"c{n}" } }.ToJsonString());
            }
            lines.Add(new JsonObject { ["type"] = "event_msg", ["payload"] = new JsonObject { ["type"] = "task_complete", ["turn_id"] = $"t{n}", ["last_agent_message"] = "done, never read" } }.ToJsonString());
        }
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    private const string Patch = "*** Begin Patch\n*** Update File: src/a.cs\n@@\n-x\n+y\n*** Add File: docs/b.md\n+hello\n*** End Patch\n";

    /// <summary>
    /// C42: a Codex session through its hooks is a claim of the Codex tool with a progress row per turn and a hand-off at
    /// its end; the turns whose Stop never landed are read from its rollout, with the tools and the files its patches
    /// named, never a word of the conversation; the files a hook's apply_patch names reach the row. Falsifier: another
    /// tool name, a turn or a file missing, or a word of the prompt or the answer in the ledger.
    /// </summary>
    [Fact]
    public void C42_ACodexSessionThroughItsHooksIsAClaimWithItsTurnsReadFromTheRollout()
    {
        using var home = new TempHome();
        Init(home);
        var env = Env();
        var rollout = Path.Combine(home.Path, "rollout-2026-11-02T10-00-00-abc.jsonl");
        Rollout(rollout, "abc", [("exec_command", """{"cmd":"dotnet test","workdir":"/x"}""")]);
        const string s = "abc";

        HookCommand.Handle(Event("SessionStart", s, rollout, ("source", "startup")), new CellHome(home.Path), "folder", Creatura, env);
        HookCommand.Handle(Event("UserPromptSubmit", s, rollout, ("prompt", "fix the build"), ("turn_id", "t1")), new CellHome(home.Path), "folder", Creatura, env);
        HookCommand.Handle(Event("PostToolUse", s, rollout, ("tool_name", "apply_patch"), ("tool_input", new JsonObject { ["input"] = Patch }), ("tool_response", new JsonObject()), ("tool_use_id", "u1"), ("turn_id", "t1")), new CellHome(home.Path), "folder", Creatura, env);
        HookCommand.Handle(Event("PostToolUse", s, rollout, ("tool_name", "exec_command"), ("tool_input", new JsonObject { ["cmd"] = "dotnet test" }), ("tool_response", new JsonObject()), ("tool_use_id", "u2"), ("turn_id", "t1")), new CellHome(home.Path), "folder", Creatura, env);
        HookCommand.Handle(Event("Stop", s, rollout, ("stop_hook_active", false), ("last_assistant_message", "fixed"), ("turn_id", "t1")), new CellHome(home.Path), "folder", Creatura, env);

        // Two more turns land in the rollout after the last recorded Stop, then the session ends.
        Rollout(rollout, "abc", [("exec_command", null)], [("apply_patch", Patch), ("exec_command", null)], [("exec_command", null), ("exec_command", null)]);
        HookCommand.Handle(Event("SessionEnd", s, rollout, ("reason", "other")), new CellHome(home.Path), "folder", Creatura, env);

        var ledger = Ledger(home);
        Assert.Equal([EntryKind.Hello, EntryKind.Claim, EntryKind.Progress, EntryKind.Progress, EntryKind.Progress, EntryKind.HandOff], ledger.Entries.Select(e => e.Kind));
        var claim = ledger.Entries[1].Body<ClaimPayload>();
        Assert.Equal(("Codex", "abc", "egarim/creatura", "creatura on main"), (claim.Tool, claim.Session, claim.Repo, claim.Goal));
        var rows = ledger.Entries.Where(e => e.Kind == EntryKind.Progress).Select(e => e.Body<ProgressPayload>().Row).ToList();
        Assert.Contains("apply_patch", rows[0]);
        Assert.Contains("exec_command", rows[0]);
        Assert.Contains("2 files touched", rows[0]);                     // src/a.cs and docs/b.md, from the hook's patch
        Assert.StartsWith("turn 2:", rows[1]);
        Assert.Contains("apply_patch x1", rows[1]);
        Assert.Contains("exec_command x1", rows[1]);
        Assert.Contains("2 files touched", rows[1]);                     // from the rollout's patch
        Assert.StartsWith("turn 3:", rows[2]);
        Assert.Contains("exec_command x2", rows[2]);
        Assert.Contains("after 3 turns", ledger.Entries[^1].Body<HandOffPayload>().Reason);
        var text = File.ReadAllText(new CellHome(home.Path).Ledger.Path);
        Assert.DoesNotContain("fix the build", text);
        Assert.DoesNotContain("never read", text);
        Assert.DoesNotContain("dotnet test", text);
    }

    /// <summary>
    /// C43: the older Codex's notify, one JSON argument per finished turn: the first writes the claim (the Codex tool, the
    /// thread as the session, the folder from the event), each writes a progress row per turn the rollout shows since the
    /// last, and nothing ends the session; an event of another kind writes nothing. Falsifier: anything else.
    /// </summary>
    [Fact]
    public void C43_TheOlderCodexNotifyWritesAClaimAndATurnEachTimeAndNoHandOff()
    {
        using var home = new TempHome();
        Init(home);
        var env = Env();
        var sessions = Directory.CreateDirectory(Path.Combine(home.Path, "sessions", "2026", "11", "02")).FullName;
        var rollout = Rollout(Path.Combine(sessions, "rollout-2026-11-02T10-00-00-0199aaaa-bbbb-cccc-dddd-eeeeeeeeeeee.jsonl"), "0199aaaa-bbbb-cccc-dddd-eeeeeeeeeeee",
            [("exec_command", null), ("apply_patch", Patch)]);
        var root = Path.Combine(home.Path, "sessions");
        string Notify(string type = "agent-turn-complete") => new JsonObject
        {
            ["type"] = type, ["thread-id"] = "0199aaaa-bbbb-cccc-dddd-eeeeeeeeeeee", ["turn-id"] = "t", ["cwd"] = Folder,
            ["input-messages"] = new JsonArray("the prompt, never read"), ["last-assistant-message"] = "the answer, never read",
        }.ToJsonString();

        Assert.Null(HookCommand.Notify(Notify("something-else"), new CellHome(home.Path), Creatura, env, root));
        Assert.NotNull(HookCommand.Notify(Notify(), new CellHome(home.Path), Creatura, env, root));
        var ledger = Ledger(home);
        Assert.Equal([EntryKind.Hello, EntryKind.Claim, EntryKind.Progress], ledger.Entries.Select(e => e.Kind));
        var claim = ledger.Entries[1].Body<ClaimPayload>();
        Assert.Equal(("Codex", "0199aaaa-bbbb-cccc-dddd-eeeeeeeeeeee", Folder, "egarim/creatura"), (claim.Tool, claim.Session, claim.Folder, claim.Repo));
        var row = ledger.Entries[2].Body<ProgressPayload>().Row;
        Assert.StartsWith("turn 1:", row);
        Assert.Contains("apply_patch x1", row);
        Assert.Contains("2 files touched", row);

        // A second turn, then a notify with nothing new in the rollout: a turn each, no hand-off.
        Rollout(rollout, "0199aaaa-bbbb-cccc-dddd-eeeeeeeeeeee", [("exec_command", null), ("apply_patch", Patch)], [("exec_command", null)]);
        HookCommand.Notify(Notify(), new CellHome(home.Path), Creatura, env, root);
        HookCommand.Notify(Notify(), new CellHome(home.Path), Creatura, env, root);
        ledger = Ledger(home);
        Assert.Equal([EntryKind.Hello, EntryKind.Claim, EntryKind.Progress, EntryKind.Progress, EntryKind.Progress], ledger.Entries.Select(e => e.Kind));
        Assert.StartsWith("turn 2:", ledger.Entries[3].Body<ProgressPayload>().Row);
        Assert.StartsWith("turn 3:", ledger.Entries[4].Body<ProgressPayload>().Row);
        Assert.Equal(ClaimState.Active, ledger.StateOf(ledger.Entries[1].Id));
        Assert.DoesNotContain("never read", File.ReadAllText(new CellHome(home.Path).Ledger.Path));

        // The command itself: the event as the last argument, exit 0, nothing printed; a thread with no rollout still counts its turn.
        var noRollout = new JsonObject { ["type"] = "agent-turn-complete", ["thread-id"] = "ffffffff-0000-0000-0000-000000000000", ["turn-id"] = "t", ["cwd"] = Folder, ["input-messages"] = new JsonArray(), ["last-assistant-message"] = "x" }.ToJsonString();
        Assert.Equal(0, HookCommand.Run(["cell", "hook", "--home", home.Path, "--creatura", Creatura, "--notify", noRollout], new StringReader("")));
        Assert.NotNull(HookCommand.Notify(noRollout, new CellHome(home.Path), Creatura, env, root));
        ledger = Ledger(home);
        Assert.Equal(2, ledger.Entries.Count(e => e.Kind == EntryKind.Claim));
        Assert.Equal("turn 1: no tool calls", ledger.Entries[^1].Body<ProgressPayload>().Row);
    }

    /// <summary>C44: the doctor says whether Codex reaches the ledger: through its hooks file, through its notify line, or not at all. Falsifier: a wrong or missing line.</summary>
    [Fact]
    public void C44_TheDoctorChecksCodexHooksOrItsNotifyLine()
    {
        using var temp = new TempHome();
        var program = Path.Combine(temp.Path, "creatura");
        File.WriteAllText(program, "");
        var hooks = Path.Combine(temp.Path, "hooks.json");
        var config = Path.Combine(temp.Path, "config.toml");
        string Said()
        {
            var output = new StringWriter();
            DoctorCommand.Run(["doctor", "--offline"], output, new DoctorEnvironment
            {
                Home = Path.Combine(temp.Path, "no-home"), Offline = true, Sdks = () => "10.0.103 [/x]", ClaudeSettings = Path.Combine(temp.Path, "none.json"),
                LaunchAgents = temp.Path, CodexHooks = hooks, CodexConfig = config,
            });
            return output.ToString();
        }

        Assert.Contains("Codex: no hooks, so its sessions do not reach the ledger", Said());
        File.WriteAllText(config, "model = \"gpt-5-codex\"\nnotify = [\"creatura\", \"cell\", \"hook\", \"--creatura\", \"codex\", \"--notify\"]\n");
        Assert.Contains("Codex: its notify command calls creatura cell hook", Said());
        File.WriteAllText(hooks, "{\"hooks\":{\"Stop\":[{\"hooks\":[{\"type\":\"command\",\"command\":\"" + program.Replace("\\", "\\\\") + " cell hook --creatura codex\"}]}]}}");
        Assert.Contains("Codex: hooks on Stop, calling", Said());
        File.WriteAllText(hooks, """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"/nowhere/creatura cell hook --creatura codex"}]}]}}""");
        Assert.Contains("Codex: the hooks call /nowhere/creatura, which is not there", Said());
    }
}
