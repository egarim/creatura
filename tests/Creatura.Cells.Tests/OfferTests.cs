using System.Text.Json;
using System.Text.Json.Nodes;

namespace Creatura.Cells.Tests;

/// <summary>
/// The pre-registered tests of docs/design-activity-stream.md for goals offered to cells (C27 to C29), written before
/// the code. Each test's name is its id; its summary is the claim, and the assertions are the falsifier as the note
/// states it.
/// </summary>
public class OfferTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 29, 9, 0, 0, TimeSpan.Zero);

    private static (CellHome Home, OwnerKey Owner) NewHome(TempHome dir)
    {
        var home = new CellHome(dir.Path);
        var owner = home.EnsureOwner(out _);
        return (home, owner);
    }

    private static LocalCell NewCell(CellHome home, string raw, string creatura, string host) =>
        home.Endorse(home.OpenCell(new FakeVessel(raw, host), Position.InProcess(creatura), creatura));

    private static WorkLedger LedgerOf(string owner, params LocalCell[] cells)
    {
        var ledger = new WorkLedger(owner);
        foreach (var cell in cells) ledger.Append(cell, new HelloPayload(cell.Identity), T0);
        return ledger;
    }

    /// <summary>
    /// C27: an offer is shown only when its owner signature verifies against a trusted owner and it is addressed to
    /// this cell or vessel, unexpired, not withdrawn and not yet claimed. Falsifier: an offer shown that is tampered
    /// with, signed by another owner or only by a cell, addressed elsewhere, expired, withdrawn or taken.
    /// </summary>
    [Fact]
    public void C27_AnOfferIsShownOnlyWhenItIsTheOwnersForThisCellAndStillOpen()
    {
        using var dir = new TempHome();
        using var strangerDir = new TempHome();
        var (home, owner) = NewHome(dir);
        var (_, stranger) = NewHome(strangerDir);
        using var laptop = NewCell(home, "raw-laptop", "owner-cell", "laptop");
        using var surface = NewCell(home, "raw-surface", "claude-code", "SURFACE");
        using var other = NewCell(home, "raw-other", "claude-code", "other");
        var ledger = LedgerOf(owner.PublicKey, laptop, surface, other);
        var vessel = surface.Identity.Hardware.VesselId;
        IReadOnlyList<string> Pending(DateTimeOffset at) => ledger.PendingOffers(surface.Id, vessel, at).Select(o => o.Id).ToList();

        // The owner's offers, to the vessel and to the cell: both shown while open.
        var toVessel = owner.Offer(vessel, "rotate the logs", T0, TimeSpan.FromHours(24));
        var toCell = owner.Offer(surface.Id, "update the hooks", T0, TimeSpan.FromHours(24)) with { };
        ledger.Append(laptop, new OfferPayload(toVessel), T0.AddMinutes(1));
        ledger.Append(laptop, new OfferPayload(toCell), T0.AddMinutes(2));
        Assert.Equal(new[] { toVessel.Id, toCell.Id }.Order(), Pending(T0.AddHours(1)).Order());

        // Addressed elsewhere: another vessel's offer is not shown here.
        var elsewhere = owner.Offer(other.Identity.Hardware.VesselId, "not for the surface", T0, TimeSpan.FromHours(24));
        ledger.Append(laptop, new OfferPayload(elsewhere), T0.AddMinutes(3));
        Assert.DoesNotContain(elsewhere.Id, Pending(T0.AddHours(1)));

        // Tampered after signing, signed by another owner, or carrying no owner signature at all: the ledger refuses
        // the entry, though a trusted cell signed it, and nothing is shown.
        var tampered = owner.Offer(vessel, "the real goal", T0, TimeSpan.FromHours(24)) with { Goal = "a goal the owner never wrote" };
        var foreign = stranger.Offer(vessel, "a stranger's goal", T0, TimeSpan.FromHours(24));
        var unsigned = owner.Offer(vessel, "only a cell signed this", T0, TimeSpan.FromHours(24)) with { Signature = "" };
        foreach (var bad in new[] { tampered, foreign, unsigned })
        {
            var entry = SignedEntry.Sign(laptop, ledger.NextSequence(laptop.Id), new OfferPayload(bad), T0.AddMinutes(4));
            Assert.Single(ledger.Merge([entry]).Rejected);
            Assert.Throws<InvalidOperationException>(() => ledger.Append(laptop, new OfferPayload(bad), T0.AddMinutes(4)));
            Assert.DoesNotContain(bad.Id, Pending(T0.AddHours(1)));
        }

        // Expired: past its time it is no longer shown.
        Assert.Empty(Pending(T0.AddHours(25)));

        // Withdrawn by the owner: no longer shown. A withdrawal signed by another owner changes nothing.
        ledger.Append(laptop, new WithdrawPayload(owner.Withdraw(toCell.Id, T0.AddMinutes(5))), T0.AddMinutes(5));
        var foreignWithdrawal = SignedEntry.Sign(laptop, ledger.NextSequence(laptop.Id), new WithdrawPayload(stranger.Withdraw(toVessel.Id, T0.AddMinutes(6))), T0.AddMinutes(6));
        Assert.Single(ledger.Merge([foreignWithdrawal]).Rejected);
        Assert.Equal([toVessel.Id], Pending(T0.AddHours(1)));

        // Taken: once a claim cites it, it is shown to nobody.
        ledger.Append(surface, new ClaimPayload(toVessel.Goal) { Offer = toVessel.Id }, T0.AddMinutes(7));
        Assert.Empty(Pending(T0.AddHours(1)));
    }

    /// <summary>
    /// C28: a claim that cites an offer closes it in the stream; two claims on one offer are both kept and marked as a
    /// conflict. Falsifier: an open offer after its claim, a dropped claim, or a conflict not marked.
    /// </summary>
    [Fact]
    public void C28_AClaimClosesItsOfferAndTwoClaimsAreAMarkedConflict()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var laptop = NewCell(home, "raw-laptop", "owner-cell", "laptop");
        using var a = NewCell(home, "raw-surface", "claude-code", "SURFACE");
        using var b = NewCell(home, "raw-surface", "codex", "SURFACE");
        var ledger = LedgerOf(owner.PublicKey, laptop, a, b);
        var vessel = a.Identity.Hardware.VesselId;
        var offer = owner.Offer(vessel, "rotate the logs", T0, TimeSpan.FromHours(24));
        ledger.Append(laptop, new OfferPayload(offer), T0.AddMinutes(1));

        JsonObject OfferActivity() => Items(ActivityStream.Document(ledger)).Single(i => (string?)i["type"] == "Offer");
        Assert.Equal("open", (string?)OfferActivity()["creatura:state"]);
        Assert.Equal($"urn:creatura:owner:{owner.Fingerprint}", (string?)OfferActivity()["actor"]);

        var first = ledger.Append(a, new ClaimPayload(offer.Goal) { Offer = offer.Id }, T0.AddMinutes(2));
        Assert.Equal("taken", (string?)OfferActivity()["creatura:state"]);
        var claim = Items(ActivityStream.Document(ledger)).Single(i => (string?)i["id"] == $"urn:creatura:entry:{first.Id}");
        Assert.Equal($"urn:creatura:offer:{offer.Id}", (string?)claim["creatura:offer"]);
        Assert.Null(claim["creatura:conflict"]);

        // A second cell on the same vessel takes it too, before it heard of the first: both claims stay, both marked.
        var second = ledger.Append(b, new ClaimPayload(offer.Goal) { Offer = offer.Id }, T0.AddMinutes(3));
        var items = Items(ActivityStream.Document(ledger));
        Assert.Equal("conflict", (string?)items.Single(i => (string?)i["type"] == "Offer")["creatura:state"]);
        foreach (var taken in new[] { first, second })
        {
            var activity = items.Single(i => (string?)i["id"] == $"urn:creatura:entry:{taken.Id}");
            Assert.True((bool)activity["creatura:conflict"]!);
        }

        // Withdrawn reads as withdrawn, as an Undo by the owner.
        var later = owner.Offer(vessel, "clean the cache", T0, TimeSpan.FromHours(24));
        ledger.Append(laptop, new OfferPayload(later), T0.AddMinutes(4));
        ledger.Append(laptop, new WithdrawPayload(owner.Withdraw(later.Id, T0.AddMinutes(5))), T0.AddMinutes(5));
        items = Items(ActivityStream.Document(ledger));
        Assert.Equal("withdrawn", (string?)items.Single(i => (string?)i["type"] == "Offer" && i["object"] is JsonObject goal && (string?)goal["id"] == $"urn:creatura:offer:{later.Id}")["creatura:state"]);
        Assert.Contains(items, i => (string?)i["type"] == "Undo" && (string?)i["object"] == $"urn:creatura:offer:{later.Id}");
    }

    private static List<JsonObject> Items(string json) => JsonNode.Parse(json)!["orderedItems"]!.AsArray().Select(n => n!.AsObject()).ToList();

    /// <summary>
    /// C29: the hook keeps its promises: it always exits 0, prints nothing when there is no offer to show, and with one
    /// shows only that offer's goal and id, in the field for the person. Falsifier: another exit code, output with no
    /// offer, or anything else printed.
    /// </summary>
    [Fact]
    public void C29_TheHookShowsAnOfferOnlyToThePersonAndOtherwisePrintsNothing()
    {
        using var dir = new TempHome();
        var vessel = new FakeVessel("11111111-2222-3333-4444-555555555555", "air");
        var output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "init", "--home", dir.Path, "--no-mesh", "--creatura", HookCommand.DefaultCreatura], output, output, vessel));
        var clock = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 60);
        var env = new HookEnvironment { Vessel = vessel, Mesh = () => null, Git = _ => (null, null), Now = () => clock = clock.AddSeconds(1), LockWait = TimeSpan.FromSeconds(30) };
        string Event(string name, string session, params (string Key, string Value)[] fields)
        {
            var o = new JsonObject { ["session_id"] = session, ["cwd"] = "/Users/owner/Projects/creatura", ["hook_event_name"] = name };
            foreach (var (key, value) in fields) o[key] = value;
            return o.ToJsonString();
        }

        string Fire(string json)
        {
            var said = new List<string>();
            HookCommand.Handle(json, new CellHome(dir.Path), "folder", HookCommand.DefaultCreatura, env, said.Add);
            return string.Concat(said);
        }

        // No offer: nothing is printed at session start, as before.
        Assert.Equal("", Fire(Event("SessionStart", "s-1", ("source", "startup"))));

        // The owner offers this vessel a goal.
        var home = new CellHome(dir.Path);
        Assert.Equal(0, CellCommand.Run(["cell", "offer", "--home", dir.Path, "--no-mesh", "--creatura", HookCommand.DefaultCreatura, "--to", "air", "--goal", "rotate the logs"], output, output, vessel));
        using var owner = home.LoadOwner()!;
        var ledger = new WorkLedger(owner.PublicKey);
        home.Ledger.LoadInto(ledger);
        var offer = Assert.Single(ledger.OfferStates()).Offer;

        // At the next session start: exactly one JSON object, whose only field is the message for the person, which
        // carries the goal and the id and nothing else of the ledger.
        var printed = Fire(Event("SessionStart", "s-2", ("source", "startup")));
        var json = JsonNode.Parse(printed)!.AsObject();
        Assert.Equal(["systemMessage"], json.Select(p => p.Key));
        var message = (string)json["systemMessage"]!;
        Assert.Contains("rotate the logs", message, StringComparison.Ordinal);
        Assert.Contains(offer.Id, message, StringComparison.Ordinal);
        Assert.DoesNotContain(owner.PublicKey, printed, StringComparison.Ordinal);
        Assert.DoesNotContain(offer.Signature, printed, StringComparison.Ordinal);

        // A resumed session is not shown it again; the other events still print nothing.
        Assert.Equal("", Fire(Event("SessionStart", "s-3", ("source", "resume"))));
        Assert.Equal("", Fire(Event("Stop", "s-2")));

        // Taking it with the first prompt: the claim cites the offer and carries its goal; then it is shown to no one.
        Assert.Equal("", Fire(Event("UserPromptSubmit", "s-2", ("prompt", $"offer {offer.Id}, please"))));
        ledger = new WorkLedger(owner.PublicKey);
        home.Ledger.LoadInto(ledger);
        var claim = ledger.Entries.Single(e => e.Kind == EntryKind.Claim).Body<ClaimPayload>();
        Assert.Equal((offer.Id, "rotate the logs"), (claim.Offer, claim.Goal));
        Assert.Equal("", Fire(Event("SessionStart", "s-4", ("source", "startup"))));

        // The command itself always exits 0, with or without an offer, and on input it cannot read.
        foreach (var input in new[] { Event("SessionStart", "s-5", ("source", "startup")), "not json", "" })
        {
            Assert.Equal(0, HookCommand.Run(["cell", "hook", "--home", dir.Path], new StringReader(input)));
        }
    }
}
