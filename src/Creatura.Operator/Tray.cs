using System.Globalization;

namespace Creatura.Operator;

/// <summary>Where the tray's page comes from: the console's web page (<c>creatura-console web</c>), on 127.0.0.1 only.</summary>
public interface IPageHost
{
    /// <summary>Whether the console's page answers at <paramref name="page"/> (one the tray started or one already running).</summary>
    bool Answers(Uri page);

    /// <summary>Starts the page on this port; disposing the handle stops it.</summary>
    IAsyncDisposable Start(int port);

    /// <summary>Opens the page in the browser.</summary>
    void Show(Uri page);
}

/// <summary>Whether the ledger hub answers.</summary>
public interface IHubProbe
{
    bool Reachable(Uri hub);
}

/// <summary>What the tray shows: the badge count, the warning dot, and the lines of its menu.</summary>
public sealed record TrayState(int Blocked, int OffersWaiting, bool Warning, string HubLine, IReadOnlyList<string> Cells, string? Problem)
{
    /// <summary>The badge: what is waiting for someone, blocked cells and offers not yet taken.</summary>
    public int Badge => Blocked + OffersWaiting;

    public string Tooltip =>
        "Creatura" + (Problem is not null ? ": " + Problem
            : Badge == 0 ? $", {Cells.Count} cell{(Cells.Count == 1 ? "" : "s")}, nothing waiting"
            : $", {Badge} waiting ({Blocked} blocked, {OffersWaiting} offer{(OffersWaiting == 1 ? "" : "s")})")
        + (Warning ? "; " + HubLine : "");
}

/// <summary>
/// The tray icon's model (issue #53), UI-free so the tests drive it: it reads the Fleet through the <c>creatura</c>
/// command, asks the hub whether it answers, opens the console's page (starting it when nothing answers on its port)
/// and syncs on request. Like the console it holds no key and opens no store: a sync is <c>creatura cell sync</c>'s
/// own work, and quitting stops only the page it started.
/// </summary>
public sealed class TrayModel(FleetModel? fleet, IPageHost pages, IHubProbe probe, string? hub, int port = 7358, Func<DateTimeOffset?>? lastSync = null)
    : IAsyncDisposable
{
    /// <summary>A hub not synced with for this long is stale: the sync agents run every minute (launchd-config's default).</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(10);

    private IAsyncDisposable? _started;

    public Uri Page { get; } = new($"http://127.0.0.1:{port}/");

    public string? Hub { get; } = hub is null ? null : hub.TrimEnd('/') + "/";

    /// <summary>True while the page the tray started is running.</summary>
    public bool StartedThePage => _started is not null;

    public TrayState Refresh(DateTimeOffset? now = null)
    {
        var (hubLine, hubWarning) = HubStatus(now ?? DateTimeOffset.UtcNow);
        if (fleet is null) return new TrayState(0, 0, hubWarning, hubLine, [], "no cell in this home: run creatura cell init");
        var snapshot = fleet.Read();
        if (snapshot.Problem is { } problem) return new TrayState(0, 0, true, hubLine, [], problem);
        var cells = snapshot.Rows.Select(r => $"{r.Name}: {(r.Blocked ? "blocked, " : "")}{r.Claim ?? (r.Quiet ? "quiet" : "idle")}"
            + (r.PendingOffers > 0 ? $" ({r.PendingOffers} offer{(r.PendingOffers == 1 ? "" : "s")} waiting)" : "")).ToList();
        return new TrayState(snapshot.Rows.Count(r => r.Blocked), snapshot.Rows.Sum(r => r.PendingOffers), hubWarning, hubLine, cells, null);
    }

    private (string Line, bool Warning) HubStatus(DateTimeOffset now)
    {
        if (Hub is null) return ("Hub: none (this home has not synced with one)", false);
        var host = new Uri(Hub).Authority;
        if (!probe.Reachable(new Uri(Hub))) return ($"Hub: {host} unreachable", true);
        if (lastSync?.Invoke() is { } at && now - at > StaleAfter)
            return ($"Hub: {host} reachable, last sync {(int)(now - at).TotalMinutes} min ago", true);
        return ($"Hub: {host} reachable", false);
    }

    /// <summary>Opens the page, starting it first when nothing answers on its port; the problem, or null.</summary>
    public async Task<string?> OpenAsync(TimeSpan? wait = null)
    {
        if (!pages.Answers(Page))
        {
            if (_started is null) _started = pages.Start(Page.Port);
            var until = DateTimeOffset.UtcNow + (wait ?? TimeSpan.FromSeconds(10));
            while (!pages.Answers(Page))
            {
                if (DateTimeOffset.UtcNow > until) return $"the console's page did not answer at {Page}; is the port taken by something else?";
                await Task.Delay(100).ConfigureAwait(false);
            }
        }

        pages.Show(Page);
        return null;
    }

    /// <summary>One <c>creatura cell sync</c> with the hub; what it said.</summary>
    public string SyncNow()
    {
        if (fleet is null) return "no cell in this home: run creatura cell init";
        if (Hub is null) return "no hub: this home has not synced with one (creatura cell sync --hub <url>)";
        var (code, output, error) = fleet.Cli.Run(["cell", "sync", "--hub", Hub, "--quiet", .. fleet.HomeArgs]);
        return code == 0 ? (output.Trim().Length > 0 ? output.Trim() : "synced") : $"sync failed ({code}): {(error.Trim().Length > 0 ? error.Trim() : output.Trim())}";
    }

    /// <summary>Quit: stops the page the tray started, and nothing else.</summary>
    public async ValueTask DisposeAsync()
    {
        if (_started is { } started)
        {
            _started = null;
            await started.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>When this home last synced with a hub: its newest <c>sync/&lt;host&gt;_&lt;port&gt;.json</c>.</summary>
    public static DateTimeOffset? LastSyncOf(string root)
    {
        var sync = Path.Combine(root, "sync");
        if (!Directory.Exists(sync)) return null;
        var last = Directory.EnumerateFiles(sync, "*_*.json").Select(File.GetLastWriteTimeUtc).DefaultIfEmpty().Max();
        return last == default ? null : new DateTimeOffset(last, TimeSpan.Zero);
    }
}

/// <summary>The console's page run in this process (<see cref="ConsoleProgram.Web"/>), and opened in the default browser.</summary>
public sealed class ConsolePageHost(IReadOnlyList<string> webArgs) : IPageHost
{
    public bool Answers(Uri page)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.GetAsync(page).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode && response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("<title>Creatura console</title>", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    public IAsyncDisposable Start(int port)
    {
        var stop = new CancellationTokenSource();
        string[] args = [.. webArgs, "--port", port.ToString(CultureInfo.InvariantCulture), "--no-open"];
        var running = Task.Run(() => ConsoleProgram.Web(args, TextWriter.Null, TextWriter.Null, null, stop.Token));
        return new Running(stop, running);
    }

    public void Show(Uri page) => ConsoleProgram.OpenInBrowser(page);

    private sealed class Running(CancellationTokenSource stop, Task running) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            await stop.CancelAsync().ConfigureAwait(false);
            try
            {
                await running.ConfigureAwait(false);
            }
            finally
            {
                stop.Dispose();
            }
        }
    }
}

/// <summary>The hub answers when its revocations list does (a read every hub serves, no key needed).</summary>
public sealed class HttpHubProbe : IHubProbe
{
    public bool Reachable(Uri hub)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(3) };
            using var response = http.GetAsync(new Uri(hub, "revocations")).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }
}

/// <summary>
/// The tray's arguments: <c>creatura-tray [--home &lt;dir&gt;] [--store json|sqlite] [--as &lt;creatura&gt;] [--creatura
/// &lt;command&gt;] [--hub &lt;url&gt;] [--port &lt;n&gt;] [--runs &lt;dir&gt;]...</c>. The home options go to every creatura
/// command and to the page; the hub defaults to the one this home last synced with; the port is the page's (7358).
/// </summary>
public static class TrayProgram
{
    public const string Usage = """
        creatura-tray: the Creatura icon in the menu bar or system tray (issue #53)

          creatura-tray [--home <dir>] [--store json|sqlite] [--as <creatura>] [--creatura <command>] [--hub <url>] [--port <n>] [--runs <dir>]...

        Click opens the console's page (creatura-console web) on 127.0.0.1, starting it when nothing answers on its
        port (default 7358). The badge counts blocked cells and offers not yet taken; a dot warns when the hub does not
        answer or has not been synced with for ten minutes. The menu lists the cells, syncs now (creatura cell sync),
        and quits, which stops only the page the tray started.
        """;

    public static TrayModel? Model(string[] args, TextWriter error, ICellCommand? cli = null, IPageHost? pages = null, IHubProbe? probe = null)
    {
        var home = new List<string>();
        var web = new List<string>();
        string? command = null;
        string? creatura = null;
        string? hub = null;
        var port = 7358;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--home" or "--store" when i + 1 < args.Length:
                    home.Add(args[i]);
                    home.Add(args[++i]);
                    break;
                case "--creatura" when i + 1 < args.Length:
                    command = args[++i];
                    break;
                case "--as" when i + 1 < args.Length:
                    creatura = args[++i];
                    break;
                case "--hub" when i + 1 < args.Length:
                    hub = args[++i];
                    break;
                case "--runs" when i + 1 < args.Length:
                    web.Add("--runs");
                    web.Add(args[++i]);
                    break;
                case "--port" when i + 1 < args.Length && int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var p) && p is > 0 and <= 65535:
                    port = p;
                    i++;
                    break;
                case "-h" or "--help":
                    error.WriteLine(Usage);
                    return null;
                default:
                    error.WriteLine($"unknown argument '{args[i]}'");
                    error.WriteLine(Usage);
                    return null;
            }
        }

        var root = home.SkipWhile(a => a != "--home").Skip(1).FirstOrDefault() ?? Environment.GetEnvironmentVariable("CREATURA_HOME") ?? Creatura.Cells.CellHome.DefaultRoot;
        web.AddRange(home);
        if (command is not null) web.AddRange(["--creatura", command]);
        if (creatura is not null) web.AddRange(["--as", creatura]);
        if (hub is not null) web.AddRange(["--hub", hub]);

        FleetModel? fleet = null;
        if (cli is not null || Directory.Exists(Path.Combine(root, "cells")))
        {
            creatura ??= ConsoleProgram.EndorsedCreatura(root);
            var fleetArgs = new List<string>(home);
            if (creatura is not null) fleetArgs.AddRange(["--creatura", creatura]);
            fleet = new FleetModel(cli ?? new ProcessCellCommand(command), fleetArgs);
        }

        return new TrayModel(fleet, pages ?? new ConsolePageHost(web), probe ?? new HttpHubProbe(), hub ?? ConsoleProgram.LastHub(root), port,
            () => TrayModel.LastSyncOf(root));
    }
}
