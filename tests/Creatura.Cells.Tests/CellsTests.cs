using System.Reflection;

namespace Creatura.Cells.Tests;

/// <summary>
/// The parts of the first cells slice beyond the five pre-registered claims: conflicts settle alike everywhere, files
/// are owner-only, the ledger file round-trips, the queries say who is on what, the mesh position is read and not
/// guessed, and the command does what its usage says.
/// </summary>
public class CellsTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 28, 9, 0, 0, TimeSpan.Zero);

    private static (CellHome Home, OwnerKey Owner, LocalCell Cell) Setup(TempHome dir, string raw = "raw-vessel-a", string creatura = "alpha")
    {
        var home = new CellHome(dir.Path);
        var owner = home.EnsureOwner(out _);
        var cell = home.Endorse(home.OpenCell(new FakeVessel(raw), Position.InProcess(creatura), creatura));
        return (home, owner, cell);
    }

    [Fact]
    public void TwoDifferentEntriesForOneCellAndSequenceSettleTheSameWayInEveryOrder()
    {
        using var dir = new TempHome();
        var (_, owner, cell) = Setup(dir);
        var left = new WorkLedger(owner.PublicKey).Append(cell, new ClaimPayload("left"), T0);
        var right = new WorkLedger(owner.PublicKey).Append(cell, new ClaimPayload("right"), T0);
        Assert.Equal(left.Id, right.Id);

        var one = new WorkLedger(owner.PublicKey);
        one.Merge([left]);
        var result = one.Merge([right]);
        var other = new WorkLedger(owner.PublicKey);
        other.Merge([right]);
        other.Merge([left]);

        Assert.Equal(1, result.Conflicts);
        Assert.Equal(1, one.Count);
        Assert.Equal(one.Digest(), other.Digest());
    }

    [Fact]
    public void KeysSaltAndLedgerAreOwnerOnly()
    {
        if (OperatingSystem.IsWindows()) return;
        using var dir = new TempHome();
        var (home, owner, cell) = Setup(dir);
        var ledger = new WorkLedger(owner.PublicKey);
        ledger.Append(cell, new ClaimPayload("perms"), T0);
        home.Ledger.Save(ledger);

        foreach (var path in new[] { home.OwnerKeyPath, home.SaltPath, home.KeyPathFor(cell.Identity.Hardware.VesselId, "alpha"), home.LedgerPath })
        {
            Assert.True(File.Exists(path), path);
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(home.CellsDirectory));
        Assert.DoesNotContain("PRIVATE KEY", File.ReadAllText(home.LedgerPath), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLedgerFileRoundTripsAndATamperedLineIsLeftOut()
    {
        using var dir = new TempHome();
        var (home, owner, cell) = Setup(dir);
        var ledger = new WorkLedger(owner.PublicKey);
        var claim = ledger.Append(cell, new ClaimPayload("round trip") { Repo = "egarim/creatura", Branch = "main" }, T0);
        ledger.Append(cell, new ProgressPayload(claim.Id, "half way") { Stress = 0.25, Blocked = true }, T0.AddMinutes(1));
        home.Ledger.Save(ledger);

        var loaded = new WorkLedger(owner.PublicKey);
        Assert.Equal(2, home.Ledger.LoadInto(loaded).Added);
        Assert.Equal(ledger.Digest(), loaded.Digest());

        File.WriteAllText(home.LedgerPath, File.ReadAllText(home.LedgerPath).Replace("half way", "all the way", StringComparison.Ordinal));
        var tampered = new WorkLedger(owner.PublicKey);
        var result = home.Ledger.LoadInto(tampered);
        Assert.Equal(1, result.Added);
        Assert.Single(result.Rejected);
    }

    [Fact]
    public void TheQueriesSayWhoIsOnWhatAndThePathOfTheWork()
    {
        using var dir = new TempHome();
        var (home, owner, a) = Setup(dir);
        using var b = home.Endorse(home.OpenCell(new FakeVessel("raw-vessel-b"), Position.InProcess("beta"), "beta"));
        var ledger = new WorkLedger(owner.PublicKey);

        var first = ledger.Append(a, new ClaimPayload("first"), T0);
        var second = ledger.Append(b, new ClaimPayload("second"), T0.AddMinutes(1));
        var third = ledger.Append(a, new ClaimPayload("third"), T0.AddMinutes(2));
        ledger.Append(a, new ProgressPayload(first.Id, "step"), T0.AddMinutes(3));
        ledger.Append(a, new ReleasePayload(first.Id, "done"), T0.AddMinutes(4));
        ledger.Append(b, new HandOffPayload(second.Id, "out of budget"), T0.AddMinutes(5));

        Assert.Equal([third.Id], ledger.WhoIsWorkingOn().Select(w => w.Claim.Id));
        Assert.Equal([second.Id, third.Id], ledger.OpenClaims().Select(e => e.Id));
        Assert.Equal([EntryKind.Claim, EntryKind.Progress, EntryKind.Release], ledger.PathOf(first.Id).Select(e => e.Kind));
        Assert.Equal(ClaimState.Released, ledger.StateOf(first.Id));
        Assert.Equal(ClaimState.HandedOff, ledger.StateOf(second.Id));
        Assert.Equal(third.Id, ledger.LatestActiveClaimOf(a.Id)!.Id);
        Assert.Null(ledger.LatestActiveClaimOf(b.Id));

        var item = Assert.Single(ledger.WhoIsWorkingOn());
        Assert.Equal(a.Identity.Creatura.Value, item.Creatura);
        Assert.Equal(a.Identity.Hardware.VesselId, item.VesselId);
        Assert.Empty(ledger.PathOf("cell-unknown#1"));
    }

    [Fact]
    public void AnUnendorsedCellCannotAppend()
    {
        using var dir = new TempHome();
        var home = new CellHome(dir.Path);
        using var owner = home.EnsureOwner(out _);
        using var cell = home.OpenCell(new FakeVessel("raw"), Position.InProcess("x"), "x");
        Assert.False(cell.Identity.IsEndorsed);
        Assert.Throws<InvalidOperationException>(() => new WorkLedger(owner.PublicKey).Append(cell, new ClaimPayload("no"), T0));
    }

    [Fact]
    public void InitIsIdempotent()
    {
        using var dir = new TempHome();
        var home = new CellHome(dir.Path);
        string key, endorsement;
        using (var first = home.Init(new FakeVessel("raw"), Position.InProcess("x"), "x"))
        {
            key = first.Identity.PublicKey;
            endorsement = first.Identity.Endorsement!.Signature;
        }
        using var second = home.Init(new FakeVessel("raw"), Position.InProcess("x"), "x");
        Assert.Equal(key, second.Identity.PublicKey);
        Assert.Equal(endorsement, second.Identity.Endorsement!.Signature);
        Assert.Single(CellHome.VesselHistoryOf(home.Records.Load("x")!));
    }

    [Fact]
    public void TheMeshPositionIsReadFromTheMeshClientsStatusOrLeftEmpty()
    {
        const string running = """{"BackendState":"Running","Self":{"HostName":"air","TailscaleIPs":["fd7a:115c:a1e0::1","100.100.1.2"]}}""";
        var position = Position.FromTailscaleStatus(running)!;
        Assert.Equal(PositionKind.Mesh, position.Kind);
        Assert.Equal("air", position.Node);
        Assert.Equal("100.100.1.2", position.Address);

        Assert.Null(Position.FromTailscaleStatus(null));
        Assert.Null(Position.FromTailscaleStatus("not json"));
        Assert.Null(Position.FromTailscaleStatus("""{"BackendState":"Stopped","Self":{"HostName":"air","TailscaleIPs":["100.100.1.2"]}}"""));
    }

    [Fact]
    public void TheVesselIdDependsOnTheOwnersSalt()
    {
        var salt1 = new byte[32];
        var salt2 = Enumerable.Repeat((byte)1, 32).ToArray();
        Assert.Equal(VesselProbe.VesselIdOf(salt1, "ABC-def"), VesselProbe.VesselIdOf(salt1, " abc-DEF "));
        Assert.NotEqual(VesselProbe.VesselIdOf(salt1, "abc"), VesselProbe.VesselIdOf(salt2, "abc"));
        Assert.StartsWith("v-", VesselProbe.VesselIdOf(salt1, "abc"));
    }

    [Fact]
    public void TheCommandClaimsProgressesReleasesAndListsOpenWork()
    {
        using var dir = new TempHome();
        var source = new FakeVessel("raw-cli");
        string Run(params string[] args)
        {
            var output = new StringWriter();
            var code = CellCommand.Run([.. args, "--home", dir.Path, "--no-mesh"], output, output, source);
            Assert.True(code == 0, $"{string.Join(' ', args)} exited {code}: {output}");
            return output.ToString();
        }

        var uninitialized = new StringWriter();
        Assert.Equal(1, CellCommand.Run(["cell", "whoami", "--home", dir.Path, "--no-mesh"], uninitialized, uninitialized, source));

        Assert.Contains("owner key    created", Run("cell", "init"));
        Assert.Contains("hello        already in the ledger", Run("cell", "init"));
        Assert.Contains("owner key    present", Run("cell", "init"));
        Assert.Contains("claimed", Run("cell", "claim", "--goal", "cells slice 1", "--repo", "egarim/creatura", "--branch", "main", "--tool", "claude-code"));
        Run("cell", "progress", "--row", "C1 to C5 green", "--stress", "0.1");
        Run("cell", "claim", "--goal", "second");
        Run("cell", "handoff", "--reason", "stopping for the night");

        var open = Run("cell", "ledger", "--open");
        Assert.Contains("2 open claims", open);
        Assert.Contains("handed off", open);
        Assert.Contains("egarim/creatura@main", open);

        Run("cell", "release", "--evidence", "tests green", "--claim", open.Split('\n')[1].Split(' ')[0]);
        var all = Run("cell", "ledger");
        Assert.Contains("6 entries from 1 cell", all);
        Assert.Contains("1 open claim", Run("cell", "ledger", "--open"));

        var bad = new StringWriter();
        Assert.Equal(2, CellCommand.Run(["cell", "progress", "--home", dir.Path, "--no-mesh"], bad, bad, source));
        Assert.Equal(2, CellCommand.Run(["cell", "fly", "--home", dir.Path], bad, bad, source));
    }

    [Fact]
    public void WhoAmIShowsHashesAndFingerprintsOnly()
    {
        using var dir = new TempHome();
        var source = new FakeVessel("raw-whoami-1234");
        var output = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "init", "--home", dir.Path, "--no-mesh"], output, output, source));
        var whoami = new StringWriter();
        Assert.Equal(0, CellCommand.Run(["cell", "whoami", "--home", dir.Path, "--no-mesh"], whoami, whoami, source));
        var text = whoami.ToString();
        Assert.Contains("vessel       v-", text);
        Assert.Contains("public key   SHA256:", text);
        Assert.Contains("endorsed by owner SHA256:", text);
        Assert.DoesNotContain("raw-whoami-1234", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PRIVATE", text, StringComparison.Ordinal);
    }
}

/// <summary>
/// Creatura.Cells and the creatura command stay package-free like the Core: no <c>PackageReference</c> in their
/// project files and no framework, model abstraction or provider SDK among their assemblies' references.
/// </summary>
public class CellsPurityTest
{
    public static TheoryData<string> PackageFreeProjects => ["Creatura.Cells", "Creatura.Cli"];

    private static string SourceRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Creatura.slnx"))) directory = directory.Parent;
            return Path.Combine(directory?.FullName ?? throw new DirectoryNotFoundException("Creatura.slnx not found above the test output"), "src");
        }
    }

    private static Assembly AssemblyOf(string project) => project switch
    {
        "Creatura.Cells" => typeof(WorkLedger).Assembly,
        "Creatura.Cli" => typeof(CellCommand).Assembly,
        _ => throw new ArgumentException(project),
    };

    [Theory]
    [MemberData(nameof(PackageFreeProjects))]
    public void TheProjectFileHasNoPackageReference(string project)
    {
        var path = Path.Combine(SourceRoot, project, project + ".csproj");
        Assert.True(File.Exists(path), $"{path} not found");
        Assert.DoesNotContain("PackageReference", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PackageFreeProjects))]
    public void TheAssemblyReferencesOnlyTheBclAndCreatura(string project)
    {
        var assembly = AssemblyOf(project);
        Assert.Equal(project, assembly.GetName().Name);
        var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        Assert.NotEmpty(references);
        Assert.All(references, name => Assert.True(
            name.StartsWith("System", StringComparison.Ordinal) || name.StartsWith("Microsoft.Win32", StringComparison.Ordinal)
            || name is "netstandard" or "Creatura" or "Creatura.Cells",
            $"{project} references {name}"));
    }
}
