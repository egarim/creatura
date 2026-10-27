using System.Text;
using Creatura;

namespace Creatura.Cells.Tests;

/// <summary>
/// The five pre-registered tests of docs/design-cells.md ("What gets built first"), written before the code. Each
/// test's name is its id; its summary is the claim, and the assertions are the falsifier as the design states it.
/// </summary>
public class PreRegisteredTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    /// <summary>One owner, one home, one creatura per fake vessel, every cell endorsed by the owner.</summary>
    private static (CellHome Home, OwnerKey Owner) NewHome(TempHome dir)
    {
        var home = new CellHome(dir.Path);
        var owner = home.EnsureOwner(out _);
        return (home, owner);
    }

    private static LocalCell NewCell(CellHome home, string raw, string creatura) =>
        home.Endorse(home.OpenCell(new FakeVessel(raw), Position.InProcess(creatura), creatura));

    /// <summary>
    /// C1: two cells on one in-process network that each claim work end with identical ledgers, whatever order the
    /// entries arrive in. Falsifier: any difference after both have synced.
    /// </summary>
    [Fact]
    public async Task C1_TwoCellsThatEachClaimEndWithIdenticalLedgersWhateverTheOrder()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");

        var ledgerA = new WorkLedger(owner.PublicKey);
        var ledgerB = new WorkLedger(owner.PublicKey);
        var hub = new InProcessCellHub(duplicates: new Random(7));
        var netA = hub.Join(new CellAddress(a.Id), ledgerA);
        var netB = hub.Join(new CellAddress(b.Id), ledgerB);

        var written = new List<SignedEntry>();
        async Task Say<T>(LocalCell cell, WorkLedger ledger, ICellNetwork net, T payload, int minute) where T : IEntryPayload
        {
            var entry = ledger.Append(cell, payload, T0.AddMinutes(minute));
            written.Add(entry);
            await net.PublishAsync(entry, CancellationToken.None);
        }

        await Say(a, ledgerA, netA, new HelloPayload(a.Identity), 0);
        await Say(b, ledgerB, netB, new HelloPayload(b.Identity), 0);
        await Say(a, ledgerA, netA, new ClaimPayload("migration step 16") { Repo = "egarim/creatura", Branch = "main" }, 1);
        await Say(b, ledgerB, netB, new ClaimPayload("cells slice 1") { Folder = "~/Projects/creatura", Tool = "claude-code" }, 1);
        var claimA = ledgerA.OpenClaims().Single().Id;
        var claimB = ledgerB.OpenClaims().Single().Id;
        await Say(a, ledgerA, netA, new ProgressPayload(claimA, "golden diff empty"), 2);
        await Say(b, ledgerB, netB, new ProgressPayload(claimB, "C1 written") { Stress = 0.2 }, 2);
        await Say(a, ledgerA, netA, new ReleasePayload(claimA, "529 tests green; commit 9d8616a"), 3);
        await Say(b, ledgerB, netB, new HeartbeatPayload(0.4), 3);

        // Each cell takes in what reached it (with duplicates), then both have synced.
        ledgerA.Merge(netA.Drain());
        ledgerB.Merge(netB.Drain());
        ledgerA.Merge(await netA.SyncAsync(new CellAddress(b.Id), ledgerA.Cursor, CancellationToken.None));
        ledgerB.Merge(await netB.SyncAsync(new CellAddress(a.Id), ledgerB.Cursor, CancellationToken.None));

        Assert.Equal(written.Count, ledgerA.Count);
        Assert.Equal(ledgerA.Entries, ledgerB.Entries);
        Assert.Equal(ledgerA.Digest(), ledgerB.Digest());

        // Whatever order: the same entries, merged into fresh ledgers in many orders and with duplicates, converge.
        for (var seed = 0; seed < 25; seed++)
        {
            var random = new Random(seed);
            var arrivals = written.Concat(written.Where(_ => random.Next(3) == 0)).OrderBy(_ => random.Next()).ToList();
            var fresh = new WorkLedger(owner.PublicKey);
            foreach (var entry in arrivals) fresh.Merge([entry]);
            Assert.Equal(ledgerA.Digest(), fresh.Digest());
            Assert.Equal(ledgerA.Entries, fresh.Entries);
        }
    }

    /// <summary>C2: an entry altered after signing is rejected on merge. Falsifier: a tampered entry accepted.</summary>
    [Fact]
    public void C2_AnEntryAlteredAfterSigningIsRejectedOnMerge()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        var source = new WorkLedger(owner.PublicKey);
        var claim = source.Append(a, new ClaimPayload("the real goal") { Repo = "egarim/creatura" }, T0);
        var release = source.Append(a, new ReleasePayload(claim.Id, "tests green"), T0.AddMinutes(1));

        var otherGoal = SignedEntry.Parse(claim.ToJson().Replace("the real goal", "a goal nobody claimed", StringComparison.Ordinal));
        var tampered = new[]
        {
            otherGoal,
            claim with { Sequence = 5 },
            claim with { Timestamp = claim.Timestamp.AddHours(1) },
            claim with { Kind = EntryKind.HandOff },
            release with { Cell = "cell-0000000000000000" },
            release with { Signature = claim.Signature },
        };

        var target = new WorkLedger(owner.PublicKey);
        foreach (var entry in tampered)
        {
            var result = target.Merge([entry]);
            Assert.Equal(0, result.Added);
            Assert.Single(result.Rejected);
        }
        Assert.Equal(0, target.Count);

        // The untouched entries still go in, so the rejections above were about the alteration.
        Assert.Equal(2, target.Merge([claim, release]).Added);

        // A cell that the trusted owner never endorsed is rejected too, however well it signs.
        using var strangerDir = new TempHome();
        var (strangerHome, _) = NewHome(strangerDir);
        using var stranger = NewCell(strangerHome, "raw-vessel-s", "stranger");
        var foreign = new WorkLedger(strangerHome.LoadOwner()!.PublicKey).Append(stranger, new ClaimPayload("not the owner's"), T0);
        Assert.Single(target.Merge([foreign]).Rejected);
        Assert.Equal(2, target.Count);
    }

    /// <summary>
    /// C3: a creatura moved to another vessel keeps its id and record and gets a new vessel id and key. Falsifier: a
    /// new creatura id, or the old key still valid for the new vessel.
    /// </summary>
    [Fact]
    public void C3_AMovedCreaturaKeepsItsIdAndRecordAndGetsANewVesselIdAndKey()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);

        using var before = NewCell(home, "raw-vessel-old", "wanderer");
        var record = home.Records.Load("wanderer")!;
        record.Cases = 12;
        record.RecentErrors.Add("deploy failed on the old vessel");
        home.Records.Save("wanderer", record);

        // The whole home is carried to another machine (a backup restored there): the probe now reads another vessel.
        using var after = NewCell(home, "raw-vessel-new", "wanderer");

        Assert.Equal(before.Identity.Creatura, after.Identity.Creatura);
        var moved = home.Records.Load("wanderer")!;
        Assert.Equal(before.Identity.Creatura, CreaturaId.OfRecord(moved));
        Assert.Equal(12, moved.Cases);
        Assert.Contains("deploy failed on the old vessel", moved.RecentErrors);

        Assert.NotEqual(before.Identity.Hardware.VesselId, after.Identity.Hardware.VesselId);
        Assert.NotEqual(before.Identity.PublicKey, after.Identity.PublicKey);
        Assert.NotEqual(before.Id, after.Id);

        // The move is an event in the record, not a new agent.
        var history = CellHome.VesselHistoryOf(moved);
        Assert.Equal([before.Identity.Hardware.VesselId, after.Identity.Hardware.VesselId], history.Select(h => h.VesselId));

        // The old key is not valid for the new vessel: its endorsement names the old vessel, and pairing the new
        // vessel's endorsement with an entry the old key signed fails on merge.
        Assert.Equal(before.Identity.Hardware.VesselId, before.Identity.Endorsement!.VesselId);
        Assert.Equal(after.Identity.Hardware.VesselId, after.Identity.Endorsement!.VesselId);
        Assert.False(after.Identity.Endorsement.Covers(before.Identity.PublicKey));

        var ledger = new WorkLedger(owner.PublicKey);
        var oldSigned = new WorkLedger(owner.PublicKey).Append(before, new ClaimPayload("speaking from the new vessel"), T0);
        var dressedUp = oldSigned with { Endorsement = after.Identity.Endorsement };
        Assert.Single(ledger.Merge([dressedUp]).Rejected);
        Assert.Equal(0, ledger.Count);

        // Opened again on the new vessel, the creatura keeps the new key: a move happens once.
        using var again = home.OpenCell(new FakeVessel("raw-vessel-new"), Position.InProcess("wanderer"), "wanderer");
        Assert.Equal(after.Identity.PublicKey, again.Identity.PublicKey);
        Assert.Equal(2, CellHome.VesselHistoryOf(home.Records.Load("wanderer")!).Count);
    }

    /// <summary>
    /// C4: the raw hardware identifier never appears in an entry, a log line or a file the cell writes. Falsifier: the
    /// identifier found anywhere outside the probe. Run twice: with a sentinel identifier from a fake vessel, and with
    /// this machine's real one (skipped where the platform has none to read).
    /// </summary>
    [Fact]
    public void C4_TheRawHardwareIdentifierNeverAppearsInAnEntryALogLineOrAFile()
    {
        const string sentinel = "C4-SENTINEL-1F2E3D4C-5B6A-4798-8A9B-0C1D2E3F4A5B";
        AssertNeverWritten(sentinel, new FakeVessel(sentinel));

        var real = RawIdentifier.Read();
        if (real is null) return;
        AssertNeverWritten(real, new PlatformVesselSource());
    }

    private static void AssertNeverWritten(string raw, IVesselSource source)
    {
        using var dir = new TempHome();
        var log = new StringBuilder();
        var home = new CellHome(dir.Path);
        var owner = home.EnsureOwner(out _);
        using var cell = home.Endorse(home.OpenCell(source, Position.InProcess("c4"), "c4"));

        var ledger = new WorkLedger(owner.PublicKey);
        var claim = ledger.Append(cell, new ClaimPayload("look for the identifier") { Folder = dir.Path }, T0);
        ledger.Append(cell, new HelloPayload(cell.Identity), T0);
        ledger.Append(cell, new ProgressPayload(claim.Id, "probing"), T0.AddMinutes(1));
        ledger.Append(cell, new HeartbeatPayload(0.1), T0.AddMinutes(2));
        ledger.Append(cell, new HandOffPayload(claim.Id, "stopping"), T0.AddMinutes(3));
        home.Ledger.Save(ledger);

        // Log lines: everything a reader would print about the cell.
        log.AppendLine(cell.Identity.ToString());
        log.AppendLine(cell.Identity.Hardware.Render());
        log.AppendLine(cell.Identity.Position.Render());
        foreach (var entry in ledger.Entries) log.AppendLine(entry.ToJson());

        // And the command, run in process under the same home.
        var output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "init", "--home", dir.Path, "--no-mesh"], output, output, source));
        Assert.Equal(0, CellCommand.Run(["cell", "whoami", "--home", dir.Path, "--no-mesh"], output, output, source));
        Assert.Equal(0, CellCommand.Run(["cell", "claim", "--goal", "c4", "--home", dir.Path, "--no-mesh"], output, output, source));
        Assert.Equal(0, CellCommand.Run(["cell", "ledger", "--home", dir.Path], output, output, source));
        log.Append(output);

        var forms = RawIdentifier.Forms(raw);
        var text = log.ToString();
        Assert.True(forms.All(f => !text.Contains(f, StringComparison.Ordinal)), "the raw identifier appeared in an entry or a log line");

        var files = Directory.EnumerateFiles(dir.Path, "*", SearchOption.AllDirectories).ToList();
        Assert.NotEmpty(files);
        foreach (var file in files)
        {
            var content = Encoding.UTF8.GetString(File.ReadAllBytes(file));
            Assert.True(forms.All(f => !content.Contains(f, StringComparison.Ordinal)), $"the raw identifier appeared in {Path.GetFileName(file)}");
        }
    }

    /// <summary>
    /// C5: a cell offline during three claims catches up with one sync and ends identical. Falsifier: a missing or
    /// duplicated entry after sync.
    /// </summary>
    [Fact]
    public async Task C5_ACellOfflineDuringThreeClaimsCatchesUpWithOneSync()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-vessel-a", "alpha");
        using var b = NewCell(home, "raw-vessel-b", "beta");

        var ledgerA = new WorkLedger(owner.PublicKey);
        var ledgerB = new WorkLedger(owner.PublicKey);
        var hub = new InProcessCellHub();
        var netA = hub.Join(new CellAddress(a.Id), ledgerA);
        var netB = hub.Join(new CellAddress(b.Id), ledgerB);

        await netA.PublishAsync(ledgerA.Append(a, new HelloPayload(a.Identity), T0), CancellationToken.None);
        ledgerB.Merge(netB.Drain());
        Assert.Equal(1, ledgerB.Count);

        netB.Online = false;
        for (var i = 1; i <= 3; i++)
        {
            await netA.PublishAsync(ledgerA.Append(a, new ClaimPayload($"claim {i}"), T0.AddMinutes(i)), CancellationToken.None);
        }
        netB.Online = true;
        Assert.Empty(netB.Drain());
        Assert.Equal(1, ledgerB.Count);

        var caughtUp = await netB.SyncAsync(new CellAddress(a.Id), ledgerB.Cursor, CancellationToken.None);
        Assert.Equal(3, caughtUp.Count);
        var result = ledgerB.Merge(caughtUp);
        Assert.Equal(3, result.Added);
        Assert.Equal(0, result.Duplicates);

        Assert.Equal(4, ledgerB.Count);
        Assert.Equal(ledgerA.Entries, ledgerB.Entries);
        Assert.Equal(ledgerA.Digest(), ledgerB.Digest());
        Assert.Equal(3, ledgerB.OpenClaims().Count);
        Assert.Equal(ledgerB.Entries.Count, ledgerB.Entries.Select(e => (e.Cell, e.Sequence)).Distinct().Count());

        // A second sync from nothing brings everything again, and nothing is duplicated.
        var again = ledgerB.Merge(await netB.SyncAsync(new CellAddress(a.Id), LedgerCursor.Empty, CancellationToken.None));
        Assert.Equal(0, again.Added);
        Assert.Equal(4, ledgerB.Count);
    }
}
