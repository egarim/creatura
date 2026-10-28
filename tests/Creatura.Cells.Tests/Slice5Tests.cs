using System.Net;
using System.Security.Cryptography;
using System.Text;
using Creatura.Cells.Hub;
using Creatura.Cells.Sync;

namespace Creatura.Cells.Tests;

/// <summary>
/// The pre-registered tests of the shared-files design (docs/design-shared-files.md, F1 to F8): files sealed for their
/// recipients, carried by the hub, which can read neither the content nor the name, and opened only where they were
/// meant to be.
/// </summary>
public sealed class Slice5Tests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 15, 0, 0, TimeSpan.Zero);

    /// <summary>The owner's vessel and a vessel that joined it, both synced once through a running hub.</summary>
    private sealed class Fleet : IAsyncDisposable
    {
        public TempHome Air { get; } = new();
        public TempHome Surface { get; } = new();
        public TempHome Hand { get; } = new();
        public FakeVessel AirVessel { get; } = new("raw-air", "air");
        public FakeVessel SurfaceVessel { get; } = new("raw-surface", "surface");
        public RunningHub Hub { get; private set; } = null!;
        public string Url => Hub.Url.ToString();

        public static async Task<Fleet> StartAsync()
        {
            var fleet = new Fleet();
            fleet.Run(fleet.Air, "cell", "init");
            var request = Path.Combine(fleet.Hand.Path, "request.json");
            var endorsement = Path.Combine(fleet.Hand.Path, "endorsement.json");
            fleet.Run(fleet.Surface, "cell", "request", "--out", request);
            fleet.Run(fleet.Air, "cell", "endorse", "--request", request, "--out", endorsement);
            fleet.Run(fleet.Surface, "cell", "accept", "--endorsement", endorsement);
            using var owner = new CellHome(fleet.Air.Path).LoadOwner()!;
            // The hub keeps its own folder, as on a real install (~/.creatura/hub), apart from the home's own files.
            Directory.CreateDirectory(Path.Combine(fleet.Air.Path, "hub"));
            fleet.Hub = await RunningHub.StartAsync(Path.Combine(fleet.Air.Path, "hub", "hub.db"), owner.PublicKey);
            fleet.Sync(fleet.Surface);
            fleet.Sync(fleet.Air);
            fleet.Sync(fleet.Surface);
            return fleet;
        }

        public FakeVessel VesselOf(TempHome home) => home == Air ? AirVessel : SurfaceVessel;

        public (int Code, string Text) Try(TempHome home, params string[] args)
        {
            var output = new StringWriter();
            var code = CellCommand.Run([.. args, "--home", home.Path, "--no-mesh", "--creatura", "claude-code"], output, output, VesselOf(home));
            return (code, output.ToString());
        }

        public string Run(TempHome home, params string[] args)
        {
            var (code, text) = Try(home, args);
            Assert.True(code == 0, $"{string.Join(' ', args)} exited {code}: {text}");
            return text;
        }

        public string Sync(TempHome home) => Run(home, "cell", "sync", "--hub", Url);

        /// <summary>Shares a file and returns the artifact id the command prints.</summary>
        public string Share(TempHome home, string path, params string[] more)
        {
            var text = Run(home, ["cell", "share", "--file", path, "--hub", Url, .. more]);
            return text.Split('\n').Single(l => l.StartsWith("shared ", StringComparison.Ordinal)).Split(' ', StringSplitOptions.RemoveEmptyEntries)[1];
        }

        public async ValueTask DisposeAsync()
        {
            if (Hub is not null) await Hub.DisposeAsync();
            Air.Dispose();
            Surface.Dispose();
            Hand.Dispose();
        }
    }

    private static (CellHome Home, OwnerKey Owner) NewHome(TempHome dir)
    {
        var home = new CellHome(dir.Path);
        return (home, home.EnsureOwner(out _));
    }

    private static LocalCell NewCell(CellHome home, string raw, string creatura) =>
        home.Endorse(home.OpenCell(new FakeVessel(raw), Position.InProcess(creatura), creatura));

    /// <summary>A ledger holding the hellos of the given cells, so each is a recipient the sharer knows.</summary>
    private static WorkLedger LedgerOf(string owner, params LocalCell[] cells)
    {
        var ledger = new WorkLedger(owner);
        foreach (var cell in cells) ledger.Append(cell, new HelloPayload(cell.Identity), T0);
        return ledger;
    }

    /// <summary>F1: a file shared by one cell is fetched byte-identical by another through the hub, text and binary alike.</summary>
    [Fact]
    public async Task F1_AFileIsFetchedByteIdenticalOnTheOtherVessel()
    {
        await using var fleet = await Fleet.StartAsync();
        var text = Path.Combine(fleet.Surface.Path, "notes ñ.md");
        File.WriteAllText(text, "# Informe\n\nCiudad de Guatemala: 22 °C, lluvia. Привет.\n");
        var binary = Path.Combine(fleet.Surface.Path, "capture.bin");
        File.WriteAllBytes(binary, RandomNumberGenerator.GetBytes(1_500_000));

        var textId = fleet.Share(fleet.Surface, text);
        var binaryId = fleet.Share(fleet.Surface, binary);
        fleet.Sync(fleet.Surface);
        fleet.Sync(fleet.Air);

        var listed = fleet.Run(fleet.Air, "cell", "files");
        Assert.Contains("notes ñ.md", listed);
        Assert.Contains("capture.bin", listed);
        Assert.Contains("text/markdown", listed);

        foreach (var (id, original) in new[] { (textId, text), (binaryId, binary) })
        {
            var output = Path.Combine(fleet.Air.Path, "fetched-" + Path.GetFileName(original));
            fleet.Run(fleet.Air, "cell", "fetch", "--artifact", id, "--out", output, "--hub", fleet.Url);
            Assert.Equal(File.ReadAllBytes(original), File.ReadAllBytes(output));
        }

        // And the other way: the owner's vessel shares, the joined one fetches.
        var back = Path.Combine(fleet.Air.Path, "answer.txt");
        File.WriteAllText(back, "recibido");
        var backId = fleet.Share(fleet.Air, back);
        fleet.Sync(fleet.Air);
        fleet.Sync(fleet.Surface);
        var there = Path.Combine(fleet.Surface.Path, "answer.txt");
        fleet.Run(fleet.Surface, "cell", "fetch", "--artifact", backId, "--out", there, "--hub", fleet.Url);
        Assert.Equal("recibido", File.ReadAllText(there));
    }

    /// <summary>F2: the hub's database and files hold neither the content nor the name of a shared file.</summary>
    [Fact]
    public async Task F2_TheHubHoldsNeitherTheContentNorTheName()
    {
        await using var fleet = await Fleet.StartAsync();
        var marker = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
        var name = $"secret-name-{marker[..12]}.txt";
        var path = Path.Combine(fleet.Surface.Path, name);
        File.WriteAllText(path, $"the content marker is {marker}, repeated: {marker}");
        fleet.Share(fleet.Surface, path);
        fleet.Sync(fleet.Surface);

        var hubFiles = Directory.GetFiles(Path.Combine(fleet.Air.Path, "hub"), "*", SearchOption.AllDirectories).ToList();
        Assert.Contains(hubFiles, f => Path.GetDirectoryName(f) == fleet.Hub.Files.Directory);
        foreach (var file in hubFiles)
        {
            var bytes = File.ReadAllBytes(file);
            foreach (var needle in new[] { marker, marker[..16], name, Path.GetFileNameWithoutExtension(name) })
            {
                Assert.True(IndexOf(bytes, Encoding.UTF8.GetBytes(needle)) < 0, $"{needle} found in {file}");
                Assert.True(IndexOf(bytes, Encoding.Unicode.GetBytes(needle)) < 0, $"{needle} (UTF-16) found in {file}");
            }
        }
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    /// <summary>F3: a file changed by one byte at the hub is refused at fetch, and nothing is written.</summary>
    [Fact]
    public async Task F3_AFileChangedAtTheHubIsRefusedAndNothingIsWritten()
    {
        await using var fleet = await Fleet.StartAsync();
        var path = Path.Combine(fleet.Surface.Path, "report.txt");
        File.WriteAllText(path, "the original report");
        var id = fleet.Share(fleet.Surface, path);
        fleet.Sync(fleet.Surface);
        fleet.Sync(fleet.Air);

        var stored = Assert.Single(Directory.GetFiles(fleet.Hub.Files.Directory));
        var bytes = File.ReadAllBytes(stored);
        bytes[^1] ^= 0x01;
        File.WriteAllBytes(stored, bytes);

        var output = Path.Combine(fleet.Air.Path, "report.txt");
        var (code, text) = fleet.Try(fleet.Air, "cell", "fetch", "--artifact", id, "--out", output, "--hub", fleet.Url);
        Assert.NotEqual(0, code);
        Assert.Contains("altered", text);
        Assert.False(File.Exists(output));
        Assert.Empty(Directory.GetFiles(Path.Combine(fleet.Air.Path, "files"), "*", SearchOption.AllDirectories).Where(f => !f.EndsWith(".tmp", StringComparison.Ordinal)));
    }

    /// <summary>F4: a cell that is not a recipient, a revoked cell, and another owner's cell cannot fetch or open a file.</summary>
    [Fact]
    public async Task F4_OnlyRecipientsOpenAFile()
    {
        using var dir = new TempHome();
        using var strangerDir = new TempHome();
        var (home, owner) = NewHome(dir);
        var (strangerHome, _) = NewHome(strangerDir);
        using var a = NewCell(home, "raw-a", "alpha");
        using var b = NewCell(home, "raw-b", "beta");
        using var c = NewCell(home, "raw-c", "gamma");
        using var stranger = NewCell(strangerHome, "raw-s", "mallory");
        var ledger = LedgerOf(owner.PublicKey, a, b, c);
        var content = Encoding.UTF8.GetBytes("for alpha and beta only");

        // Not a recipient: c is left out with --to; it cannot read the label or the file.
        var sealedFile = SharedFiles.Seal(content, "plan.txt", "text/plain", SharedFiles.Recipients(ledger, a, [b.Id]));
        Assert.Equal(new[] { a.Id, b.Id }.Order(StringComparer.Ordinal), sealedFile.Payload.Keys.Select(k => k.Cell).Order(StringComparer.Ordinal));
        Assert.Equal(SharedFileProblem.NotARecipient, Assert.Throws<SharedFileException>(() => SharedFiles.Open(sealedFile.Ciphertext, sealedFile.Payload, c)).Problem);
        Assert.Equal(SharedFileProblem.NotARecipient, Assert.Throws<SharedFileException>(() => SharedFiles.Label(sealedFile.Payload, c)).Problem);
        Assert.Equal(content, SharedFiles.Open(sealedFile.Ciphertext, sealedFile.Payload, b).Content);

        // A wrapped key copied under c's name opens nothing for c: the key was wrapped for b's encryption key.
        var stolen = sealedFile.Payload with { Keys = [.. sealedFile.Payload.Keys, sealedFile.Payload.Keys.Single(k => k.Cell == b.Id) with { Cell = c.Id }] };
        Assert.Throws<SharedFileException>(() => SharedFiles.Open(sealedFile.Ciphertext, stolen, c));

        // Revoked: after b is revoked, it is not a recipient of new files, and the hub serves it nothing.
        await using var hub = await RunningHub.StartAsync(Path.Combine(dir.Path, "hub.db"), owner.PublicKey);
        await LedgerHub.PutFileAsync(hub.Url, a, sealedFile.Ciphertext, CancellationToken.None);
        Assert.Equal(sealedFile.Ciphertext, await LedgerHub.GetFileAsync(hub.Url, b, sealedFile.Payload.Ciphertext, CancellationToken.None));
        var revocation = owner.Revoke(b.Identity.PublicKey, 1, "lost", T0.AddHours(1));
        Assert.True(ledger.Revoke(revocation));
        await LedgerHub.ExchangeRevocationsAsync(hub.Url, a, [revocation], CancellationToken.None);
        Assert.DoesNotContain(SharedFiles.Recipients(ledger, a), r => r.Cell == b.Id);
        await Assert.ThrowsAsync<HttpRequestException>(() => LedgerHub.GetFileAsync(hub.Url, b, sealedFile.Payload.Ciphertext, CancellationToken.None));

        // Another owner's cell: the hub refuses it, and it was never a recipient.
        await Assert.ThrowsAsync<HttpRequestException>(() => LedgerHub.GetFileAsync(hub.Url, stranger, sealedFile.Payload.Ciphertext, CancellationToken.None));
        Assert.Throws<SharedFileException>(() => SharedFiles.Open(sealedFile.Ciphertext, sealedFile.Payload, stranger));
    }

    /// <summary>F5: an upload whose bytes do not hash to its name is refused by the hub.</summary>
    [Fact]
    public async Task F5_TheHubRefusesBytesFiledUnderAnotherName()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-a", "alpha");
        var ledger = LedgerOf(owner.PublicKey, a);
        var one = SharedFiles.Seal(Encoding.UTF8.GetBytes("one"), "one.txt", "text/plain", SharedFiles.Recipients(ledger, a));
        var other = SharedFiles.Seal(Encoding.UTF8.GetBytes("other"), "other.txt", "text/plain", SharedFiles.Recipients(ledger, a));

        await using var hub = await RunningHub.StartAsync(Path.Combine(dir.Path, "hub.db"), owner.PublicKey);
        var refused = await Assert.ThrowsAsync<SharedFileException>(() => LedgerHub.PutFileAsync(hub.Url, a, other.Ciphertext, CancellationToken.None, name: one.Payload.Ciphertext));
        Assert.Equal(SharedFileProblem.Altered, refused.Problem);
        var missing = await Assert.ThrowsAsync<SharedFileException>(() => LedgerHub.GetFileAsync(hub.Url, a, one.Payload.Ciphertext, CancellationToken.None));
        Assert.Equal(SharedFileProblem.NotFound, missing.Problem);
        Assert.Empty(Directory.GetFiles(hub.Files.Directory));
    }

    /// <summary>F6: a file over the size limit is refused before upload, and the hub refuses an upload over it.</summary>
    [Fact]
    public async Task F6_FilesOverTheLimitAreRefused()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-a", "alpha");
        var ledger = LedgerOf(owner.PublicKey, a);

        var tooLarge = new byte[SharedFiles.MaxBytes + 1];
        Assert.Equal(SharedFileProblem.TooLarge, Assert.Throws<SharedFileException>(() => SharedFiles.Seal(tooLarge, "big.bin", "application/octet-stream", SharedFiles.Recipients(ledger, a))).Problem);

        await using var hub = await RunningHub.StartAsync(new HubOptions(Path.Combine(dir.Path, "hub.db"), [owner.PublicKey], "") { MaxFileBytes = 4096 });
        var sealedFile = SharedFiles.Seal(RandomNumberGenerator.GetBytes(8192), "medium.bin", "application/octet-stream", SharedFiles.Recipients(ledger, a));
        var refused = await Assert.ThrowsAsync<SharedFileException>(() => LedgerHub.PutFileAsync(hub.Url, a, sealedFile.Ciphertext, CancellationToken.None));
        Assert.Equal(SharedFileProblem.TooLarge, refused.Problem);
        Assert.Empty(Directory.GetFiles(hub.Files.Directory));
    }

    /// <summary>F7: after retention the hub has deleted the file, the Artifact entry remains, and fetch says expired.</summary>
    [Fact]
    public async Task F7_AnExpiredFileIsGoneButItsEntryStays()
    {
        using var dir = new TempHome();
        var (home, owner) = NewHome(dir);
        using var a = NewCell(home, "raw-a", "alpha");
        var ledger = LedgerOf(owner.PublicKey, a);
        var clock = new ManualClock(DateTimeOffset.UtcNow);
        var sealedFile = SharedFiles.Seal(Encoding.UTF8.GetBytes("short-lived"), "note.txt", "text/plain", SharedFiles.Recipients(ledger, a));
        var entry = ledger.Append(a, sealedFile.Payload, clock.Now);

        await using var hub = await RunningHub.StartAsync(new HubOptions(Path.Combine(dir.Path, "hub.db"), [owner.PublicKey], "") { Clock = clock });
        var node = new LedgerSyncNode(ledger, a.Id, LedgerHub.Http(hub.Url, a, clock: clock));
        await LedgerHub.PutFileAsync(hub.Url, a, sealedFile.Ciphertext, CancellationToken.None, clock: clock);
        Assert.True((await node.SyncAsync(CancellationToken.None)).Pushed);

        clock.Advance(TimeSpan.FromDays(89));
        Assert.Equal(sealedFile.Ciphertext, await LedgerHub.GetFileAsync(hub.Url, a, sealedFile.Payload.Ciphertext, CancellationToken.None, clock: clock));

        clock.Advance(TimeSpan.FromDays(2));
        var expired = await Assert.ThrowsAsync<SharedFileException>(() => LedgerHub.GetFileAsync(hub.Url, a, sealedFile.Payload.Ciphertext, CancellationToken.None, clock: clock));
        Assert.Equal(SharedFileProblem.Expired, expired.Problem);
        Assert.Empty(Directory.GetFiles(hub.Files.Directory));
        Assert.Contains((await hub.Store.GetDeltasAsync("-1")).Select(LedgerDelta.Entry), e => e.Equals(entry));
        Assert.Contains(ledger.Entries, e => e.Kind == EntryKind.Artifact);
    }

    /// <summary>F8: a fetched file is opened again without the network.</summary>
    [Fact]
    public async Task F8_ASecondFetchNeedsNoNetwork()
    {
        await using var fleet = await Fleet.StartAsync();
        var path = Path.Combine(fleet.Surface.Path, "plan.txt");
        File.WriteAllText(path, "fetch me twice");
        var id = fleet.Share(fleet.Surface, path);
        fleet.Sync(fleet.Surface);
        fleet.Sync(fleet.Air);

        fleet.Run(fleet.Air, "cell", "fetch", "--artifact", id, "--out", Path.Combine(fleet.Air.Path, "first.txt"), "--hub", fleet.Url);
        await fleet.Hub.DisposeAsync();
        var second = Path.Combine(fleet.Air.Path, "second.txt");
        var text = fleet.Run(fleet.Air, "cell", "fetch", "--artifact", id, "--out", second, "--hub", "http://127.0.0.1:9");
        Assert.Contains("from this vessel's copy", text);
        Assert.Equal("fetch me twice", File.ReadAllText(second));
    }
}
