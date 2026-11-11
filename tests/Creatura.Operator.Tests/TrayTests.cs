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
