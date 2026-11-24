using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using Creatura.Operator;

namespace Creatura.Operator.Tests;

/// <summary>The tray icon (issue #53): its pre-registered tests T1 to T4, on the UI-free model the icon draws.</summary>
[Collection("page")]
public class TrayTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 11, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A creatura command whose Fleet the test sets, keeping every call.</summary>
    private sealed class Cli : ICellCommand
    {
        public List<string[]> Calls { get; } = [];

        public int Offers { get; set; }

        public bool Blocked { get; set; }

        public (int Code, string Output, string Error) Run(IReadOnlyList<string> args)
        {
            Calls.Add([.. args]);
            if (args is ["cell", "sync", ..]) return (0, "pushed 0, pulled 2", "");
            var cell = new JsonObject
            {
                ["cell"] = "cell-aaaa", ["name"] = "renderer-gamayun on laptop", ["kind"] = "claude-code", ["creatura"] = "cr-1", ["vessel"] = "v-1",
                ["vesselName"] = "laptop", ["ring"] = "#2f6f8f", ["lastSeen"] = Now.UtcDateTime.ToString("o"), ["quiet"] = false,
            };
            if (Offers > 0) cell["pendingOffers"] = Offers;
            if (Blocked)
            {
                cell["blocked"] = true;
                cell["claim"] = "render the reel";
                cell["claimId"] = "cell-aaaa#2";
                cell["claimedAt"] = Now.UtcDateTime.ToString("o");
            }

            return (0, new JsonObject { ["format"] = "creatura.fleet/1", ["at"] = Now.UtcDateTime.ToString("o"), ["quietMinutes"] = 15, ["cells"] = new JsonArray(cell) }.ToJsonString(), "");
        }

        /// <summary>The calls that could write: anything but reading the Fleet.</summary>
        public IEnumerable<string[]> Writes => Calls.Where(c => c is not ["cell", "fleet", ..]);
    }

    private sealed class Hub(bool reachable) : IHubProbe
    {
        public bool Up { get; set; } = reachable;

        public bool Reachable(Uri hub) => Up;
    }

    /// <summary>A page host that is the real one, except that showing the page is recorded instead of opening a browser.</summary>
    private sealed class Recorded(IPageHost real) : IPageHost
    {
        public List<Uri> Shown { get; } = [];

        public int Starts { get; private set; }

        public bool Answers(Uri page) => real.Answers(page);

        public IAsyncDisposable Start(int port)
        {
            Starts++;
            return real.Start(port);
        }

        public void Show(Uri page) => Shown.Add(page);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static TrayModel Model(Cli cli, IPageHost pages, IHubProbe hub, string? hubUrl = "http://100.64.0.4:5181", int port = 7358, Func<DateTimeOffset?>? lastSync = null) =>
        new(new FleetModel(cli), pages, hub, hubUrl, port, lastSync);

    private static string Status(object o) => System.Text.Json.JsonSerializer.Serialize(o);

    private static T InHome<T>(Func<string, T> use, params (string File, string Text)[] files)
    {
        var home = Directory.CreateTempSubdirectory("creatura-status-");
        try
        {
            if (files.Length > 0) Directory.CreateDirectory(Path.Combine(home.FullName, "status"));
            foreach (var (file, text) in files) File.WriteAllText(Path.Combine(home.FullName, "status", file), text);
            return use(home.FullName);
        }
        finally
        {
            home.Delete(true);
        }
    }

    private static TrayState Drawn(string home, Cli? cli = null) =>
        new TrayModel(cli is null ? null : new FleetModel(cli), new Recorded(new ConsolePageHost([])), new Hub(true), null, programs: at => ProgramStatuses.Read(home, at)).Refresh(Now);

    [Fact]
    public void Status_T1_AWorkingProgramIsShown()
    {
        var state = InHome(h => Drawn(h, new Cli()), ("vivarium.json", Status(new { format = 1, name = "Vivarium", state = "working", line = "T-FW-06, building", updatedAt = Now, lines = new[] { "Opus 5.5" }, link = "https://viv.os.sv" })));
        var p = Assert.Single(state.Programs);
        Assert.Equal(("Vivarium", "T-FW-06, building", true, "https://viv.os.sv/"), (p.Name, p.Line, p.Working, p.Link!.ToString()));
        Assert.Equal(["Opus 5.5"], p.Lines);
        Assert.True(state.Working);
        Assert.Contains("T-FW-06", state.Tooltip);
    }

    [Theory]
    [InlineData("attention", 0, 1)]
    [InlineData("idle", 2, 2)]
    [InlineData("idle", 0, 0)]
    [InlineData("idle", -5, 0)]
    public void Status_T2_WhatWaitsIsInTheBadge(string state, int waiting, int badge) =>
        Assert.Equal(badge, InHome(h => Drawn(h, new Cli()), ("a.json", Status(new { format = 1, state, waiting, updatedAt = Now }))).Badge);

    [Fact]
    public void Status_T3_AStaleFileSaysSoAndNoFolderChangesNothing()
    {
        var stale = InHome(h => Drawn(h, new Cli()), ("a.json", Status(new { format = 1, name = "A", state = "working", line = "x", waiting = 4, updatedAt = Now.AddMinutes(-4) })));
        Assert.StartsWith("not answering since", stale.Programs[0].Line);
        Assert.Equal((true, false, 0), (stale.Dot, stale.Working, stale.Badge));
        Assert.False(InHome(h => Drawn(h, new Cli()), ("a.json", Status(new { format = 1, state = "idle", staleAfterSeconds = 600, updatedAt = Now.AddMinutes(-4) }))).Dot);
        var none = InHome(h => Drawn(h, new Cli()));
        Assert.Equal((0, false, 0), (none.Programs.Count, none.Dot, none.Badge));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"format\":2}")]
    [InlineData("{\"format\":1}")]
    [InlineData("{\"format\":\"1\",\"updatedAt\":5}")]
    [InlineData("{\"format\":1,\"name\":\"\\ud800\",\"updatedAt\":\"x\"}")]
    public void Status_T4_ABrokenFileIsOneLineWithAWarning(string text) => Assert.True(Assert.Single(InHome(h => Drawn(h, new Cli()), ("a.json", text)).Programs).Warning);

    [Fact]
    public void Status_T4_AFileAsAWindowsShellWritesItIsRead()
    {
        var home = Directory.CreateTempSubdirectory("creatura-status-");
        try
        {
            var folder = Directory.CreateDirectory(Path.Combine(home.FullName, "status")).FullName;
            foreach (var encoding in new System.Text.Encoding[] { new System.Text.UTF8Encoding(true), System.Text.Encoding.Unicode })
            {
                File.WriteAllText(Path.Combine(folder, "a.json"), Status(new { format = 1, state = "idle", line = "ok", updatedAt = Now }), encoding);
                Assert.Equal(("ok", false), (Drawn(home.FullName, new Cli()).Programs[0].Line, Drawn(home.FullName, new Cli()).Dot));
            }

            File.WriteAllText(Path.Combine(folder, "b.tmp.json"), "{");
            Assert.Single(Drawn(home.FullName, new Cli()).Programs);
        }
        finally
        {
            home.Delete(true);
        }
    }

    [Fact]
    public void Status_T4_HugeManyAndHostileFilesAreBounded()
    {
        Assert.True(InHome(h => Drawn(h, new Cli()), ("a.json", Status(new { format = 1, line = new string('x', 70_000), updatedAt = Now }))).Programs[0].Warning);
        var many = InHome(h => Drawn(h, new Cli()), [.. Enumerable.Range(0, 30).Select(i => ($"p{i:00}.json", Status(new { format = 1, state = "idle", updatedAt = Now, lines = Enumerable.Repeat("l", 50) })))]);
        Assert.Equal(ProgramStatuses.MaxFiles + 1, many.Programs.Count);
        Assert.Equal(ProgramStatuses.MaxLines, many.Programs[0].Lines.Count);
        var p = InHome(h => Drawn(h, new Cli()), ("a.json", Status(new { format = 1, line = "a\nb\u202ec\u0000" + new string('y', 500), state = "idle", updatedAt = Now }))).Programs[0];
        Assert.StartsWith("a b c", p.Line);
        Assert.Equal(ProgramStatuses.MaxLength, p.Line.Length);
        foreach (var link in new[] { "file:///c:/windows", "javascript:alert(1)", "http://example.org", "https://a@b.c", "vscode://x", "\\\\host\\share", "http://127.0.0.1.evil.example" })
            Assert.Null(InHome(h => Drawn(h, new Cli()), ("a.json", Status(new { format = 1, link, updatedAt = Now }))).Programs[0].Link);
        Assert.NotNull(InHome(h => Drawn(h, new Cli()), ("a.json", Status(new { format = 1, link = "http://127.0.0.1:7358/", updatedAt = Now }))).Programs[0].Link);
    }

    [Fact]
    public void TheHomesOwnCommandIsUsedOnlyWhenNothingElseNamesOne()
    {
        var home = Directory.CreateTempSubdirectory("creatura-home-");
        try
        {
            var bin = Directory.CreateDirectory(Path.Combine(home.FullName, "bin")).FullName;
            foreach (var n in new[] { "creatura", "creatura.exe" }) File.WriteAllText(Path.Combine(bin, n), "");
            Assert.Equal(Path.Combine(bin, "creatura"), TrayProgram.HomeCommand(home.FullName, null, "", false));
            Assert.Equal(Path.Combine(bin, "creatura.exe"), TrayProgram.HomeCommand(home.FullName, null, null, true));
            Assert.Null(TrayProgram.HomeCommand(home.FullName, "/opt/creatura", "", false));
            Assert.Null(TrayProgram.HomeCommand(home.FullName, null, bin, false));
            File.Delete(Path.Combine(bin, "creatura.exe"));
            Assert.Null(TrayProgram.HomeCommand(home.FullName, null, "", true));
            var spaced = Directory.CreateDirectory(Path.Combine(home.FullName, "a b", "bin")).FullName;
            File.WriteAllText(Path.Combine(spaced, "creatura"), "");
            Assert.Null(TrayProgram.HomeCommand(Path.Combine(home.FullName, "a b"), null, "", false));
        }
        finally
        {
            home.Delete(true);
        }
    }

    [Fact]
    public void Menu_EveryLineIsShortAndErrorsArePlain()
    {
        var long1 = new string('x', 300);
        var state = new TrayState(1, 2, true, "Hub: " + long1, [.. Enumerable.Repeat(long1, 20)], "could not run 'creatura': An error occurred trying to start process " + long1)
        {
            Programs = [new ProgramStatus("Vivarium", "building T-FW-08, 9 min " + long1, true, 0, false, [], null)],
        };
        var lines = TrayMenu.Creatura(state).Append(TrayMenu.CreaturaTitle(state)).Append(TrayMenu.Title(state.Programs[0])).ToList();
        Assert.All(lines, l => Assert.InRange(l.Length, 1, l.StartsWith("Hub", StringComparison.Ordinal) ? 90 : TrayMenu.Width));
        Assert.Contains("The creatura command could not be started here", lines);
        Assert.StartsWith("Vivarium: building T-FW-08, 9 min", TrayMenu.Title(state.Programs[0]));
        Assert.Equal("Creatura", TrayMenu.CreaturaTitle(new TrayState(0, 0, false, "Hub: h reachable", ["a: idle"], null)));
        Assert.Equal("Creatura: 2 waiting", TrayMenu.CreaturaTitle(new TrayState(1, 1, false, "Hub", [], null)));
    }

    [Fact]
    public void Status_T5_WithNoCellTheProgramsAreStillShown()
    {
        var state = InHome(h => Drawn(h), ("a.json", Status(new { format = 1, name = "A", state = "idle", updatedAt = Now })));
        Assert.NotNull(state.Problem);
        Assert.Equal("A", state.Programs[0].Name);
    }

    [Fact]
    public async Task T1_WithThePageStoppedOpenStartsItOn127001AndOpensIt()
    {
        var home = Directory.CreateTempSubdirectory("creatura-tray-");
        try
        {
            var port = FreePort();
            var pages = new Recorded(new ConsolePageHost(["--home", home.FullName]));
            await using var tray = Model(new Cli(), pages, new Hub(true), port: port);
            Assert.False(pages.Answers(tray.Page));

            Assert.Null(await tray.OpenAsync());
            Assert.True(tray.StartedThePage);
            Assert.Equal([new Uri($"http://127.0.0.1:{port}/")], pages.Shown);
            Assert.True(pages.Answers(tray.Page));
            Assert.Equal(1, pages.Starts);

            // A second click opens the same page; nothing new is started.
            Assert.Null(await tray.OpenAsync());
            Assert.Equal(1, pages.Starts);
            Assert.Equal(2, pages.Shown.Count);

            // It listens on the loopback only: not on this machine's other addresses.
            var others = (await Dns.GetHostAddressesAsync(Dns.GetHostName())).Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a));
            foreach (var address in others)
            {
                using var client = new TcpClient();
                await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(address, port));
            }
        }
        finally
        {
            home.Delete(true);
        }
    }

    [Fact]
    public async Task T2_WhatWaitsShowsInTheBadgeAndClearsWhenAnswered()
    {
        var cli = new Cli();
        await using var tray = Model(cli, new Recorded(new ConsolePageHost([])), new Hub(true));
        Assert.Equal(0, tray.Refresh(Now).Badge);

        cli.Offers = 2;
        cli.Blocked = true;
        var waiting = tray.Refresh(Now);
        Assert.Equal((1, 2, 3), (waiting.Blocked, waiting.OffersWaiting, waiting.Badge));
        Assert.Contains("renderer-gamayun on laptop: blocked, render the reel (2 offers waiting)", waiting.Cells);
        Assert.Contains("3 waiting", waiting.Tooltip);

        cli.Offers = 0;
        cli.Blocked = false;
        Assert.Equal(0, tray.Refresh(Now).Badge);
        Assert.Empty(cli.Writes);                                                    // reading the badge wrote nothing
    }

    [Fact]
    public async Task T3_AnUnreachableOrStaleHubShowsTheDotAndChangesNothingElse()
    {
        var cli = new Cli { Offers = 1 };
        var hub = new Hub(false);
        DateTimeOffset? synced = Now.AddMinutes(-1);
        await using var tray = Model(cli, new Recorded(new ConsolePageHost([])), hub, lastSync: () => synced);

        var down = tray.Refresh(Now);
        Assert.True(down.Warning);
        Assert.Equal("Hub: 100.64.0.4:5181 unreachable", down.HubLine);
        Assert.Equal(1, down.Badge);                                                 // the rest of the state is read as before
        Assert.Empty(cli.Writes);

        hub.Up = true;
        Assert.False(tray.Refresh(Now).Warning);
        synced = Now.AddMinutes(-25);
        var stale = tray.Refresh(Now);
        Assert.True(stale.Warning);
        Assert.Equal("Hub: 100.64.0.4:5181 reachable, last sync 25 min ago", stale.HubLine);

        // No hub at all is not a warning: the home has simply not synced with one.
        await using var alone = Model(cli, new Recorded(new ConsolePageHost([])), hub, hubUrl: null);
        Assert.False(alone.Refresh(Now).Warning);
        Assert.StartsWith("no hub", alone.SyncNow());
        Assert.Empty(cli.Writes);
    }

    [Fact]
    public async Task T4_QuitStopsThePageTheTrayStartedAndLeavesTheCellsAlone()
    {
        var home = Directory.CreateTempSubdirectory("creatura-tray-");
        try
        {
            var cli = new Cli();
            var port = FreePort();
            var pages = new Recorded(new ConsolePageHost(["--home", home.FullName]));
            var before = Directory.EnumerateFileSystemEntries(home.FullName, "*", SearchOption.AllDirectories).ToList();
            var tray = Model(cli, pages, new Hub(true), port: port);
            Assert.Null(await tray.OpenAsync());
            Assert.True(pages.Answers(tray.Page));

            await tray.DisposeAsync();
            Assert.False(tray.StartedThePage);
            Assert.False(pages.Answers(tray.Page));
            Assert.Empty(cli.Writes);                                                 // no command that writes was run
            Assert.Equal(before, Directory.EnumerateFileSystemEntries(home.FullName, "*", SearchOption.AllDirectories).ToList());

            // A page the tray did not start is left running when it quits.
            var other = new ConsolePageHost(["--home", home.FullName]).Start(port);
            try
            {
                var second = Model(cli, pages, new Hub(true), port: port);
                await WaitFor(() => pages.Answers(second.Page));
                Assert.Null(await second.OpenAsync());
                Assert.False(second.StartedThePage);
                await second.DisposeAsync();
                Assert.True(pages.Answers(second.Page));
            }
            finally
            {
                await other.DisposeAsync();
            }
        }
        finally
        {
            home.Delete(true);
        }
    }

    [Fact]
    public async Task SyncNowIsTheCreaturaCommandsOwnSync()
    {
        var cli = new Cli();
        await using var tray = Model(cli, new Recorded(new ConsolePageHost([])), new Hub(true));
        Assert.Equal("pushed 0, pulled 2", tray.SyncNow());
        Assert.Equal(["cell", "sync", "--hub", "http://100.64.0.4:5181/", "--quiet"], Assert.Single(cli.Writes));
    }

    [Fact]
    public void TheArgumentsAreTheConsolesAndUnknownOnesAreRefused()
    {
        var error = new StringWriter();
        Assert.Null(TrayProgram.Model(["--bogus"], error));
        Assert.Contains("unknown argument '--bogus'", error.ToString());
        var tray = TrayProgram.Model(["--home", Path.GetTempPath(), "--hub", "http://hub:5181", "--port", "7400"], error, new Cli());
        Assert.Equal(new Uri("http://127.0.0.1:7400/"), tray!.Page);
        Assert.Equal("http://hub:5181/", tray.Hub);
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        for (var i = 0; i < 100 && !condition(); i++) await Task.Delay(100);
    }
}
