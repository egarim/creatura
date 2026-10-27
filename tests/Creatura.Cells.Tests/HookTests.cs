using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Creatura.Cells.Tests;

/// <summary>Slice 2: the Claude Code hook command (docs/design-cells.md, "How host tools join").</summary>
public sealed class HookTests
{
    private const string Creatura = HookCommand.DefaultCreatura;

    private static readonly FakeVessel Vessel = new("11111111-2222-3333-4444-555555555555", "air");

    private static HookEnvironment Env(DateTimeOffset? start = null, TimeSpan? lockWait = null)
    {
        var clock = start ?? new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);
        return new HookEnvironment
        {
            Vessel = Vessel,
            Mesh = () => null,
            Git = folder => folder.EndsWith("creatura", StringComparison.Ordinal) ? ("egarim/creatura", "main") : (null, null),
            Now = () => clock = clock.AddSeconds(1),
            LockWait = lockWait ?? TimeSpan.FromSeconds(30),
        };
    }

    private static void Init(TempHome home)
    {
        var output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "init", "--home", home.Path, "--no-mesh", "--creatura", Creatura], output, output, Vessel));
    }

    private static string Event(string name, string session, params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject
        {
            ["session_id"] = session,
            ["transcript_path"] = $"/tmp/{session}.jsonl",
            ["cwd"] = "/Users/owner/Projects/creatura",
            ["hook_event_name"] = name,
            ["permission_mode"] = "auto",
        };
        foreach (var (key, value) in fields) o[key] = value is JsonNode node ? node : JsonValue.Create(value);
        return o.ToJsonString();
    }

    private static WorkLedger Ledger(TempHome home)
    {
        var cellHome = new CellHome(home.Path);
        using var owner = cellHome.LoadOwner()!;
        var ledger = new WorkLedger([owner.PublicKey]);
        var result = cellHome.Ledger.LoadInto(ledger);
        Assert.Empty(result.Rejected);
        return ledger;
    }

    private static SignedEntry? Fire(TempHome home, string json, string goal = "folder", HookEnvironment? env = null) =>
        HookCommand.Handle(json, new CellHome(home.Path), goal, Creatura, env ?? Env());

    [Fact]
    public void AReplayedSessionWritesClaimProgressProgressAndHandOff()
    {
        using var home = new TempHome();
        Init(home);
        var env = Env();
        const string s = "6f1c0d2a-session";

        HookCommand.Handle(Event("SessionStart", s, ("source", "startup"), ("model", "claude-opus-5-5")), new CellHome(home.Path), "folder", Creatura, env);
        HookCommand.Handle(Event("UserPromptSubmit", s, ("prompt", "build slice 2")), new CellHome(home.Path), "folder", Creatura, env);
        foreach (var (tool, file) in new[] { ("Read", "/a.cs"), ("Edit", "/a.cs"), ("Bash", (string?)null), ("Bash", null) })
        {
            var input = file is null ? new JsonObject { ["command"] = "dotnet test" } : new JsonObject { ["file_path"] = file };
            HookCommand.Handle(Event("PostToolUse", s, ("tool_name", tool), ("tool_input", input), ("tool_response", new JsonObject())), new CellHome(home.Path), "folder", Creatura, env);
        }
        HookCommand.Handle(Event("Stop", s, ("stop_hook_active", false), ("last_assistant_message", "done")), new CellHome(home.Path), "folder", Creatura, env);
        HookCommand.Handle(Event("UserPromptSubmit", s, ("prompt", "now the tests")), new CellHome(home.Path), "folder", Creatura, env);
        foreach (var tool in new[] { "Write", "Bash", "Bash" })
        {
            HookCommand.Handle(Event("PostToolUse", s, ("tool_name", tool), ("tool_input", new JsonObject { ["file_path"] = "/b.cs" })), new CellHome(home.Path), "folder", Creatura, env);
        }
        HookCommand.Handle(Event("Stop", s, ("stop_hook_active", false)), new CellHome(home.Path), "folder", Creatura, env);
        HookCommand.Handle(Event("UserPromptSubmit", s, ("prompt", "thanks")), new CellHome(home.Path), "folder", Creatura, env);
        HookCommand.Handle(Event("SessionEnd", s, ("reason", "prompt_input_exit")), new CellHome(home.Path), "folder", Creatura, env);

        var ledger = Ledger(home);
        Assert.Equal([EntryKind.Hello, EntryKind.Claim, EntryKind.Progress, EntryKind.Progress, EntryKind.HandOff], ledger.Entries.Select(e => e.Kind));

        var claim = ledger.Entries[1].Body<ClaimPayload>();
        Assert.Equal("creatura on main", claim.Goal);
        Assert.Equal("egarim/creatura", claim.Repo);
        Assert.Equal("main", claim.Branch);
        Assert.Equal("/Users/owner/Projects/creatura", claim.Folder);
        Assert.Equal("Claude Code", claim.Tool);
        Assert.Equal(s, claim.Session);

        Assert.Equal("turn 1: Bash x2, Edit x1, Read x1; 1 file touched", ledger.Entries[2].Body<ProgressPayload>().Row);
        Assert.Equal("turn 2: Bash x2, Write x1; 1 file touched", ledger.Entries[3].Body<ProgressPayload>().Row);
        Assert.Equal("session ended (prompt_input_exit) after 2 turns", ledger.Entries[4].Body<HandOffPayload>().Reason);
        Assert.Equal(ClaimState.HandedOff, ledger.StateOf(ledger.Entries[1].Id));
        Assert.False(File.Exists(Path.Combine(home.Path, "sessions", s + ".json")));

        // Nothing the session said or did reached the ledger file: no prompt text, no tool input.
        var text = File.ReadAllText(new CellHome(home.Path).LedgerPath);
        foreach (var said in new[] { "build slice 2", "now the tests", "thanks", "dotnet test", "/a.cs", "/b.cs", "done" }) Assert.DoesNotContain(said, text);
    }

    [Fact]
    public void WithGoalPromptTheClaimIsTheFirstLineScrubbedAndTruncated()
    {
        using var home = new TempHome();
        Init(home);
        var prompt = "deploy with key sk-abc123def456ghi789 and password=hunter2 to https://bob:pw@example.com/x " + new string('z', 80) + "\nsecond line ignored";
        var entry = Fire(home, Event("UserPromptSubmit", "s-prompt", ("prompt", prompt)), goal: "prompt");

        var goal = entry!.Body<ClaimPayload>().Goal;
        Assert.DoesNotContain("sk-abc", goal);
        Assert.DoesNotContain("hunter2", goal);
        Assert.DoesNotContain("bob:pw", goal);
        Assert.DoesNotContain("second line", goal);
        Assert.True(goal.Length <= Scrubber.MaxGoal);
        Assert.StartsWith("deploy with key [redacted]", goal);
    }

    [Theory]
    [InlineData("use sk-proj-AbCdEf1234567890 now", "use [redacted] now")]
    [InlineData("curl -H 'Authorization: Bearer abc.def-123'", "curl -H 'Authorization: Bearer [redacted]'")]
    [InlineData("api_key: 9f8e7d6c", "api_key=[redacted]")]
    [InlineData("TOKEN=xyz", "TOKEN=[redacted]")]
    [InlineData("clone https://joche:ghp_secret@github.com/egarim/x", "clone https://[redacted]@github.com/egarim/x")]
    [InlineData("hash 0123456789abcdef0123456789abcdef here", "hash [redacted] here")]
    [InlineData("edit /Users/owner/Projects/creatura/src/Creatura.Cells/Scrubber.cs", "edit /Users/owner/Projects/creatura/src/Creatura.Cells/Scrubber.cs")]
    [InlineData("a normal sentence about the permit office", "a normal sentence about the permit office")]
    public void TheScrubberRedactsSecretShapesAndKeepsPlainText(string input, string expected) =>
        Assert.Equal(expected, Scrubber.Scrub(input));

    [Fact]
    public void ThePrivateKeyBlockAndPastedContentNeverReachAGoal()
    {
        Assert.Equal("fix [redacted] please", Scrubber.Scrub("fix -----BEGIN EC PRIVATE KEY-----\nMHcCAQEE\n-----END EC PRIVATE KEY----- please").ReplaceLineEndings(" "));
        Assert.Equal("review this", Scrubber.GoalFromPrompt("<pasted_content id=\"a1\">\nsk-live-0123456789abcdef\n</pasted_content id=\"a1\">\nreview this"));
        Assert.Null(Scrubber.GoalFromPrompt("<pasted_content id=\"a1\">only a paste</pasted_content id=\"a1\">"));
    }

    [Theory]
    [InlineData("git@github.com:egarim/creatura.git", "egarim/creatura")]
    [InlineData("https://github.com/egarim/biological-agents.git", "egarim/biological-agents")]
    [InlineData("https://joche:ghp_x@github.com/egarim/creatura", "egarim/creatura")]
    [InlineData("https://gitlab.example.com/team/sub/repo.git", "team/sub/repo")]
    public void ARemoteBecomesOwnerSlashName(string remote, string expected) => Assert.Equal(expected, Scrubber.RepoFromRemote(remote));

    [Fact]
    public void SubagentEventsStopHookContinuationsAndUnknownEventsWriteNothing()
    {
        using var home = new TempHome();
        Init(home);
        Fire(home, Event("UserPromptSubmit", "s-sub", ("prompt", "go")));
        var before = Ledger(home).Count;

        Assert.Null(Fire(home, Event("Stop", "s-sub", ("agent_id", "agent-1"), ("agent_type", "Explore"))));
        Assert.Null(Fire(home, Event("Stop", "s-sub", ("stop_hook_active", true))));
        Assert.Null(Fire(home, Event("Notification", "s-sub", ("message", "hi"))));
        Assert.Null(Fire(home, "not json at all"));
        Assert.Null(Fire(home, "[1,2,3]"));
        Assert.Equal(before, Ledger(home).Count);
    }

    [Fact]
    public void BeforeInitAHookWritesNothingAndCreatesNothing()
    {
        using var home = new TempHome();
        Assert.Null(Fire(home, Event("UserPromptSubmit", "s-early", ("prompt", "go"))));
        Assert.Empty(Directory.EnumerateFileSystemEntries(home.Path));
    }

    [Fact]
    public void ProgressReleaseAndHandOffCanNameASessionsClaim()
    {
        using var home = new TempHome();
        Init(home);
        Fire(home, Event("UserPromptSubmit", "s-one", ("prompt", "first")));
        Fire(home, Event("UserPromptSubmit", "s-two", ("prompt", "second")));
        var output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "release", "--home", home.Path, "--no-mesh", "--creatura", Creatura, "--session", "s-one", "--evidence", "merged"], output, output, Vessel));

        var ledger = Ledger(home);
        var one = ledger.WhoIsWorkingOn(activeOnly: false).Single(w => w.Goal.Session == "s-one");
        var two = ledger.WhoIsWorkingOn(activeOnly: false).Single(w => w.Goal.Session == "s-two");
        Assert.Equal(ClaimState.Released, one.State);
        Assert.Equal(ClaimState.Active, two.State);
    }

    [Fact]
    public async Task ParallelSessionsLoseNoEntryAndShareOneSequence()
    {
        using var home = new TempHome();
        Init(home);
        const int sessions = 8, turns = 25;
        for (var i = 0; i < sessions; i++) Fire(home, Event("UserPromptSubmit", $"par-{i}", ("prompt", "go")));

        await Task.WhenAll(Enumerable.Range(0, sessions).Select(i => Task.Run(() =>
        {
            var env = Env(new DateTimeOffset(2026, 10, 28, 10, i, 0, TimeSpan.Zero));
            for (var t = 0; t < turns; t++)
            {
                Assert.NotNull(HookCommand.Handle(Event("Stop", $"par-{i}", ("stop_hook_active", false)), new CellHome(home.Path), "folder", Creatura, env));
            }
        })));

        var ledger = Ledger(home);
        Assert.Equal(1 + sessions + sessions * turns, ledger.Count);
        var sequences = ledger.Entries.Select(e => e.Sequence).OrderBy(n => n).ToList();
        Assert.Equal(Enumerable.Range(1, ledger.Count).Select(n => (long)n), sequences);
        Assert.Equal(sessions, ledger.Entries.Where(e => e.Kind == EntryKind.Progress).Select(e => e.ClaimRef).Distinct().Count());
    }

    [Fact]
    public void TheRealProcessPrintsNothingAndExitsZeroWhateverItIsGiven()
    {
        using var home = new TempHome();
        Init(home);
        var missing = Path.Combine(home.Path, "does-not-exist");
        var valid = Event("UserPromptSubmit", "s-proc", ("prompt", "hello"));

        foreach (var (stdin, dir) in new[] { ("garbage {{{", home.Path), ("", home.Path), (valid, missing), (valid, home.Path) })
        {
            var (code, stdout, stderr) = RunCli(stdin, "cell", "hook", "--home", dir);
            Assert.Equal(0, code);
            Assert.Equal("", stdout);
            Assert.Equal("", stderr);
        }
        Assert.False(Directory.Exists(missing));

        if (!OperatingSystem.IsWindows())
        {
            var readOnly = new TempHome();
            try
            {
                Init(readOnly);
                File.SetUnixFileMode(readOnly.Path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
                var (code, stdout, stderr) = RunCli(valid, "cell", "hook", "--home", readOnly.Path);
                Assert.Equal(0, code);
                Assert.Equal("", stdout);
                Assert.Equal("", stderr);
            }
            finally
            {
                File.SetUnixFileMode(readOnly.Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
                readOnly.Dispose();
            }
        }
    }

    [Fact]
    public void InstallMergesIntoSettingsKeepsEveryKeyIsIdempotentAndUninstallRestoresTheFile()
    {
        using var home = new TempHome();
        var settings = Path.Combine(home.Path, "settings.json");
        var original = """
            {
              "env": { "SECRET_LOOKING": "value-that-must-not-print" },
              "theme": "dark",
              "hooks": { "Stop": [ { "hooks": [ { "type": "command", "command": "say done" } ] } ] }
            }
            """;
        File.WriteAllText(settings, original);
        var block = HooksSetup.HooksBlock("/home/me/.creatura/bin/creatura", "/home/me/.creatura");
        var now = new DateTimeOffset(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

        var dry = HooksSetup.Install(settings, block, dryRun: true, now);
        Assert.Contains("keys kept unchanged (2): env, theme", dry);
        Assert.Contains("every existing key byte-for-byte the same after the merge: yes", dry);
        Assert.DoesNotContain("value-that-must-not-print", dry);
        Assert.Equal(original, File.ReadAllText(settings));

        var report = HooksSetup.Install(settings, block, dryRun: false, now);
        Assert.Contains("backed up to", report);
        Assert.Equal(original, File.ReadAllText(settings + ".bak-20261028090000"));
        var installed = JsonNode.Parse(File.ReadAllText(settings))!.AsObject();
        Assert.Equal("value-that-must-not-print", installed["env"]!["SECRET_LOOKING"]!.GetValue<string>());
        Assert.Equal(2, installed["hooks"]!["Stop"]!.AsArray().Count);                                   // theirs kept, ours added
        Assert.True(installed["hooks"]!["SessionEnd"]![0]!["hooks"]![0]!["timeout"]!.GetValue<int>() == 5);
        Assert.True(installed["hooks"]!["PostToolUse"]![0]!["hooks"]![0]!["async"]!.GetValue<bool>());

        Assert.Contains("added: nothing (already installed)", HooksSetup.Install(settings, block, dryRun: false, now.AddMinutes(1)));

        HooksSetup.Uninstall(settings, block, dryRun: false, now.AddMinutes(2));
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(File.ReadAllText(settings))));
    }

    [Fact]
    public void TheHookCommandLineCannotFailOrTalk()
    {
        var line = HooksSetup.HookCommandLine("/Users/owner/.creatura/bin/creatura", "/Users/owner/.creatura", Creatura, "folder");
        Assert.Equal("/Users/owner/.creatura/bin/creatura cell hook --home /Users/owner/.creatura --creatura claude-code --goal folder >/dev/null 2>&1 || true", line);
        Assert.Equal("'/tmp/a b/creatura'", HooksSetup.ShellQuote("/tmp/a b/creatura"));
    }

    private static (int Code, string Stdout, string Stderr) RunCli(string stdin, params string[] args)
    {
        var dotnet = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") is { Length: > 0 } host ? host : "dotnet";
        var start = new ProcessStartInfo(dotnet, [Path.Combine(AppContext.BaseDirectory, "Creatura.Cli.dll"), .. args])
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start)!;
        process.StandardInput.Write(stdin);
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        Assert.True(process.WaitForExit(30_000));
        return (process.ExitCode, stdout.Result, stderr.Result);
    }
}
