using System.Text.Json.Nodes;

namespace Creatura.Cells.Tests;

/// <summary>Handing work from one session to the next (docs/design-handoff.md, slice H1): C38 to C41.</summary>
public sealed class HandoffTests
{
    private const string Creatura = HookCommand.DefaultCreatura;

    private static readonly FakeVessel Vessel = new("11111111-2222-3333-4444-555555555555", "air");

    private const string Folder = "/Users/owner/Projects/creatura";

    private static HookEnvironment Env()
    {
        // The real clock: these tests mix hook events with `creatura cell` commands, which use it, and the ledger orders by time.
        return new HookEnvironment
        {
            Vessel = Vessel,
            Mesh = () => null,
            Git = folder => folder.EndsWith("creatura", StringComparison.Ordinal) ? ("egarim/creatura", "main") : (null, null),
            Now = () => DateTimeOffset.UtcNow,
            LockWait = TimeSpan.FromSeconds(30),
        };
    }

    private static int Cell(TempHome home, StringWriter output, params string[] args) =>
        CellCommand.Run(["cell", args[0], "--home", home.Path, "--no-mesh", "--creatura", Creatura, .. args[1..]], output, output, Vessel);

    private static string Event(string name, string session, string cwd = Folder, params (string Key, object? Value)[] fields)
    {
        var o = new JsonObject { ["session_id"] = session, ["transcript_path"] = $"/tmp/{session}.jsonl", ["cwd"] = cwd, ["hook_event_name"] = name };
        foreach (var (key, value) in fields) o[key] = value is JsonNode node ? node : JsonValue.Create(value);
        return o.ToJsonString();
    }

    private static string Fire(TempHome home, HookEnvironment env, string json)
    {
        var said = new List<string>();
        HookCommand.Handle(json, new CellHome(home.Path), "folder", Creatura, env, said.Add);
        return string.Concat(said);
    }

    private static WorkLedger Ledger(TempHome home)
    {
        var cellHome = new CellHome(home.Path);
        using var owner = cellHome.LoadOwner()!;
        var ledger = new WorkLedger([owner.PublicKey]);
        Assert.Empty(cellHome.Ledger.LoadInto(ledger).Rejected);
        return ledger;
    }

    private static string Message(string printed) => printed.Length == 0 ? "" : (string)JsonNode.Parse(printed)!["systemMessage"]!;

    /// <summary>
    /// C38: a hand-off written with notes keeps them, from the terminal and from a file; a session that wrote its own
    /// hand-off ends with no second one, and its late turns are still recorded. Falsifier: notes lost or cut, a second
    /// HandOff, or a turn missing.
    /// </summary>
    [Fact]
    public void C38_AHandOffKeepsItsNotesAndASessionThatWroteItsOwnIsNotHandedOffTwice()
    {
        using var home = new TempHome();
        var output = new StringWriter();
        Assert.Equal(0, Cell(home, output, "init"));
        var env = Env();

        // A session claims, works a turn, writes its own hand-off with notes, and ends.
        Fire(home, env, Event("SessionStart", "s1", fields: [("source", "startup")]));
        Fire(home, env, Event("UserPromptSubmit", "s1", fields: [("prompt", "build the thing")]));
        Fire(home, env, Event("PostToolUse", "s1", fields: [("tool_name", "Edit"), ("tool_input", new JsonObject { ["file_path"] = "/a.cs" })]));
        Fire(home, env, Event("Stop", "s1", fields: [("stop_hook_active", false)]));
        Assert.Equal(0, Cell(home, output, "handoff", "--session", "s1", "--reason", "out of time", "--notes", "next: run the tests\nthen push"));
        Fire(home, env, Event("SessionEnd", "s1", fields: [("reason", "prompt_input_exit")]));

        var ledger = Ledger(home);
        Assert.Equal([EntryKind.Hello, EntryKind.Claim, EntryKind.Progress, EntryKind.HandOff], ledger.Entries.Select(e => e.Kind));
        var handOff = ledger.Entries[^1].Body<HandOffPayload>();
        Assert.Equal(("out of time", "next: run the tests\nthen push"), (handOff.Reason, handOff.Notes));
        Assert.Equal(ClaimState.HandedOff, ledger.StateOf(ledger.Entries[1].Id));

        // From a file, trimmed; and too long is refused before anything is written.
        var notes = Path.Combine(home.Path, "notes.md");
        File.WriteAllText(notes, "  the file's notes\r\nline two\r\n");
        Assert.Equal(0, Cell(home, output, "claim", "--goal", "second", "--folder", "/x"));
        Assert.Equal(0, Cell(home, output, "handoff", "--reason", "lunch", "--notes-file", notes));
        ledger = Ledger(home);
        Assert.Equal("the file's notes\nline two", ledger.Entries[^1].Body<HandOffPayload>().Notes);
        Assert.Equal(0, Cell(home, output, "claim", "--goal", "third"));
        var before = Ledger(home).Count;
        Assert.NotEqual(0, Cell(home, output, "handoff", "--reason", "x", "--notes", new string('n', HandOffPayload.MaxNotes + 1)));
        Assert.Equal(before, Ledger(home).Count);
        Assert.Contains("at most 4000", output.ToString());

        // `--path` prints the notes under the hand-off.
        output = new StringWriter();
        Assert.Equal(0, Cell(home, output, "ledger", "--path", ledger.Entries[1].Id));
        Assert.Contains("| next: run the tests", output.ToString());
        Assert.Contains("| then push", output.ToString());
    }

    /// <summary>
    /// C39: at a new session's start the hook shows the claims handed off in this folder or repository that nobody
    /// continued, with the reason and the notes' first line, to the person only; a resumed session is shown nothing;
    /// a continued claim is shown to no one after. Falsifier: a claim from elsewhere shown, a continued one shown again,
    /// anything reaching the model's context.
    /// </summary>
    [Fact]
    public void C39_TheHookShowsWhatWasHandedOffHereToThePersonUntilSomeoneContinuesIt()
    {
        using var home = new TempHome();
        var output = new StringWriter();
        Assert.Equal(0, Cell(home, output, "init"));
        var env = Env();

        Fire(home, env, Event("SessionStart", "s1", fields: [("source", "startup")]));
        Fire(home, env, Event("UserPromptSubmit", "s1", fields: [("prompt", "build the thing")]));
        Assert.Equal(0, Cell(home, output, "handoff", "--session", "s1", "--reason", "out of time", "--notes", "next: run the tests\nthen push"));
        Fire(home, env, Event("SessionEnd", "s1", fields: [("reason", "prompt_input_exit")]));
        var claim = Ledger(home).Entries.Single(e => e.Kind == EntryKind.Claim).Id;

        // Elsewhere (another folder, not a repository): nothing. A resumed session here: nothing.
        Assert.Equal("", Fire(home, env, Event("SessionStart", "s2", "/Users/owner/Projects/other", ("source", "startup"))));
        Assert.Equal("", Fire(home, env, Event("SessionStart", "s3", fields: [("source", "resume")])));

        // Here, a new session: one JSON object whose only field is the message for the person.
        var printed = Fire(home, env, Event("SessionStart", "s4", fields: [("source", "startup")]));
        var json = JsonNode.Parse(printed)!.AsObject();
        Assert.Equal(["systemMessage"], json.Select(p => p.Key));
        var message = (string)json["systemMessage"]!;
        Assert.Contains("handed off here", message);
        Assert.Contains("\"creatura on main\"", message);
        Assert.Contains(claim, message);
        Assert.Contains("out of time", message);
        Assert.Contains("notes: next: run the tests", message);
        Assert.DoesNotContain("then push", message);
        Assert.Contains($"continue {claim}", message);

        // The same claim, seen from the repository rather than the folder: still here.
        Assert.Contains(claim, Message(Fire(home, env, Event("SessionStart", "s5", "/Users/owner/elsewhere/creatura", ("source", "startup")))));

        // Continued by s4: shown to no one after.
        Assert.Equal("", Fire(home, env, Event("UserPromptSubmit", "s4", fields: [("prompt", $"continue {claim} where it left off")])));
        Assert.Equal("", Fire(home, env, Event("SessionStart", "s6", fields: [("source", "startup")])));
    }

    /// <summary>
    /// C40: a first prompt naming `continue <claim>` writes a claim that cites it and carries its goal; `cell claim
    /// --continues` does the same from a terminal, carrying repository, branch and folder unless given; continuing a
    /// released claim is refused. Falsifier: a claim without the citation or the goal, or a released claim continued.
    /// </summary>
    [Fact]
    public void C40_AClaimContinuesAHandedOffOneFromThePromptOrTheTerminalAndNeverAReleasedOne()
    {
        using var home = new TempHome();
        var output = new StringWriter();
        Assert.Equal(0, Cell(home, output, "init"));
        var env = Env();

        Fire(home, env, Event("SessionStart", "s1", fields: [("source", "startup")]));
        Fire(home, env, Event("UserPromptSubmit", "s1", fields: [("prompt", "build the thing")]));
        Fire(home, env, Event("SessionEnd", "s1", fields: [("reason", "prompt_input_exit")]));   // the automatic hand-off, no notes
        var first = Ledger(home).Entries.Single(e => e.Kind == EntryKind.Claim).Id;

        // From the prompt: the claim cites it and carries its goal; where is this session's.
        Assert.Contains(first, Message(Fire(home, env, Event("SessionStart", "s2", fields: [("source", "startup")]))));
        Fire(home, env, Event("UserPromptSubmit", "s2", fields: [("prompt", $"please continue {first}")]));
        var ledger = Ledger(home);
        var second = ledger.Entries.Where(e => e.Kind == EntryKind.Claim).Last();
        var payload = second.Body<ClaimPayload>();
        Assert.Equal((first, "creatura on main", "egarim/creatura", "main", Folder, "s2"), (payload.Continues, payload.Goal, payload.Repo, payload.Branch, payload.Folder, payload.Session));
        Assert.Equal([second.Id], ledger.ContinuedBy(first).Select(e => e.Id));

        // A prompt that names a claim the session was not shown writes an ordinary claim.
        Fire(home, env, Event("SessionStart", "s3", "/Users/owner/Projects/other", ("source", "startup")));
        Fire(home, env, Event("UserPromptSubmit", "s3", fields: [("prompt", $"continue {first}")]));
        Assert.Null(Ledger(home).Entries.Where(e => e.Kind == EntryKind.Claim).Last().Body<ClaimPayload>().Continues);

        // From the terminal: goal and where carried over unless given.
        Assert.Equal(0, Cell(home, output, "claim", "--goal", "cli work", "--repo", "egarim/x", "--branch", "dev", "--folder", "/x"));
        Assert.Equal(0, Cell(home, output, "handoff", "--reason", "lunch"));
        var cli = Ledger(home).Entries.Where(e => e.Kind == EntryKind.Claim).Last().Id;
        output = new StringWriter();
        Assert.Equal(0, Cell(home, output, "claim", "--continues", cli, "--branch", "dev-2"));
        Assert.Contains($"(continues {cli})", output.ToString());
        var carried = Ledger(home).Entries.Where(e => e.Kind == EntryKind.Claim).Last().Body<ClaimPayload>();
        Assert.Equal(("cli work", "egarim/x", "dev-2", "/x", cli), (carried.Goal, carried.Repo, carried.Branch, carried.Folder, carried.Continues));

        // A released claim cannot be continued; an unknown one neither.
        Assert.Equal(0, Cell(home, output, "release", "--evidence", "done"));
        var released = Ledger(home).Entries.Where(e => e.Kind == EntryKind.Claim).Last().Id;
        var count = Ledger(home).Count;
        output = new StringWriter();
        Assert.NotEqual(0, Cell(home, output, "claim", "--continues", released));
        Assert.Contains("released", output.ToString());
        Assert.NotEqual(0, Cell(home, output, "claim", "--continues", "cell-0000#9"));
        Assert.Equal(count, Ledger(home).Count);
    }

    /// <summary>
    /// C41: `cell ledger --path` of any claim in a chain prints the whole chain in ledger order; the stream's
    /// continuing claim names what it continues and the hand-off carries its notes. Falsifier: an entry of the chain
    /// missing, or the stream without the link or the notes.
    /// </summary>
    [Fact]
    public void C41_ThePathSpansTheChainAndTheStreamLinksIt()
    {
        using var home = new TempHome();
        var output = new StringWriter();
        Assert.Equal(0, Cell(home, output, "init"));

        Assert.Equal(0, Cell(home, output, "claim", "--goal", "the work", "--folder", "/w"));
        Assert.Equal(0, Cell(home, output, "progress", "--row", "step one"));
        Assert.Equal(0, Cell(home, output, "handoff", "--reason", "night", "--notes", "step two is next"));
        var first = Ledger(home).Entries.Single(e => e.Kind == EntryKind.Claim).Id;
        Assert.Equal(0, Cell(home, output, "claim", "--continues", first));
        Assert.Equal(0, Cell(home, output, "progress", "--row", "step two"));
        Assert.Equal(0, Cell(home, output, "handoff", "--reason", "night again", "--notes", "step three is next"));
        var second = Ledger(home).Entries.Where(e => e.Kind == EntryKind.Claim).Last().Id;
        Assert.Equal(0, Cell(home, output, "claim", "--continues", second));
        Assert.Equal(0, Cell(home, output, "release", "--evidence", "all three steps"));
        var third = Ledger(home).Entries.Where(e => e.Kind == EntryKind.Claim).Last().Id;

        var ledger = Ledger(home);
        Assert.Equal([first, second, third], ledger.ChainOf(second));
        Assert.Equal(ledger.ChainOf(first), ledger.ChainOf(third));
        Assert.Equal(ledger.Entries.Where(e => e.Kind != EntryKind.Hello).Select(e => e.Id), ledger.PathOfChain(third).Select(e => e.Id));
        Assert.Empty(ledger.ChainOf("cell-0000#9"));

        foreach (var id in new[] { first, second, third })
        {
            output = new StringWriter();
            Assert.Equal(0, Cell(home, output, "ledger", "--path", id));
            var text = output.ToString();
            Assert.Contains("a chain of 3 claims", text);
            foreach (var row in new[] { "step one", "step two", "all three steps", "| step two is next", "| step three is next", $"(continues {first})", $"(continues {second})" }) Assert.Contains(row, text);
        }

        output = new StringWriter();
        Assert.Equal(0, Cell(home, output, "ledger", "--path", first));
        Assert.Contains($"continued by {second}", output.ToString());
        output = new StringWriter();
        Assert.Equal(0, Cell(home, output, "ledger", "--open"));
        Assert.Contains($"(continued by {second})", output.ToString());

        var items = JsonNode.Parse(ActivityStream.Document(ledger))!["orderedItems"]!.AsArray().Select(n => n!.AsObject()).ToList();
        var continuing = items.Single(i => ((string?)i["creatura:continues"])?.EndsWith(first, StringComparison.Ordinal) == true);
        Assert.Equal("Create", (string?)continuing["type"]);
        Assert.Contains("continued 'the work'", (string?)continuing["summary"]);
        Assert.Equal(["step three is next", "step two is next"], items.Where(i => (string?)i["type"] == "Leave").Select(i => (string?)i["creatura:notes"]).Order());
    }
}
