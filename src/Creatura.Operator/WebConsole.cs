using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Creatura.Operator;

/// <summary>
/// What the page may write: an offer only on the owner's vessel (<paramref name="CanOffer"/>; otherwise <paramref name="OfferNote"/>
/// says where offers are made), and a file attached to one only with a hub to hold it (<paramref name="Hub"/>).
/// </summary>
public sealed record WebOptions(bool CanOffer = true, string? OfferNote = null, string? Hub = null);

/// <summary>
/// The console as a page (docs/design-console.md, U5): the Fleet and the Run view in a browser, served on 127.0.0.1
/// only, from the same models as the terminal and the same JSON. The page loads nothing from anywhere else. It reads
/// the Fleet and a cell's activity through the <c>creatura</c> command, at most every couple of seconds however many
/// tabs ask, and the runs the person listed under <c>--runs</c>, by id. Its one write is an offer, which <c>cell
/// offer</c> makes; a request to make one must carry the token written into the page this server served, from this
/// server's own address, so another site open in the same browser cannot make one.
/// </summary>
public sealed class WebConsole : IAsyncDisposable
{
    /// <summary>How long a read of the Fleet or of the activity is reused before <c>creatura</c> is run again.</summary>
    public static readonly TimeSpan ReadAgainAfter = TimeSpan.FromSeconds(2);

    private readonly WebApplication _app;
    private readonly FleetModel? _fleet;
    private readonly RunCatalog _runs;
    private readonly WebOptions _options;
    private readonly Lock _gate = new();
    private (DateTimeOffset At, FleetSnapshot Snapshot)? _fleetRead;
    private (DateTimeOffset At, string Document)? _activityRead;

    private WebConsole(WebApplication app, FleetModel? fleet, RunCatalog runs, WebOptions options, string token)
    {
        _app = app;
        _fleet = fleet;
        _runs = runs;
        _options = options;
        Token = token;
    }

    /// <summary>The largest file the page takes: the shared files' own limit.</summary>
    public const long MaxAttachment = 64L * 1024 * 1024;

    /// <summary>The page's address: http://127.0.0.1:port/.</summary>
    public Uri Address { get; private set; } = null!;

    /// <summary>The token an offer must carry; written into the page and nowhere else.</summary>
    public string Token { get; }

    /// <summary>Starts serving on 127.0.0.1, on <paramref name="port"/> or a free one when it is 0. <paramref name="fleet"/> null: no home, runs only.</summary>
    public static async Task<WebConsole> StartAsync(FleetModel? fleet, RunCatalog runs, int port = 0, WebOptions? options = null)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(o =>
        {
            o.Listen(IPAddress.Loopback, port);
            o.Limits.MaxRequestBodySize = MaxAttachment + 1024 * 1024;
        });
        var app = builder.Build();
        var console = new WebConsole(app, fleet, runs, options ?? new WebOptions(), Convert.ToHexString(RandomNumberGenerator.GetBytes(24)).ToLowerInvariant());
        console.Map();
        await app.StartAsync().ConfigureAwait(false);
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        console.Address = new Uri(bound.Replace("[::1]", "127.0.0.1", StringComparison.Ordinal).TrimEnd('/') + "/");
        return console;
    }

    private void Map()
    {
        // Only this machine, by the names it was served under: a page on another name (DNS rebinding) is refused.
        _app.Use(async (context, next) =>
        {
            var host = context.Request.Host.Host;
            if (host is not ("127.0.0.1" or "localhost"))
            {
                context.Response.StatusCode = StatusCodes.Status421MisdirectedRequest;
                return;
            }

            var headers = context.Response.Headers;
            headers.ContentSecurityPolicy = "default-src 'none'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
            headers["Referrer-Policy"] = "no-referrer";
            headers["X-Content-Type-Options"] = "nosniff";
            headers.CacheControl = "no-store";
            await next(context).ConfigureAwait(false);
        });

        _app.MapGet("/", () => Results.Content(Page.Replace("__CREATURA_TOKEN__", Token, StringComparison.Ordinal), "text/html; charset=utf-8"));
        _app.MapGet("/icon.svg", () => Results.Bytes(Icon, "image/svg+xml"));
        _app.MapGet("/api/state", () => Results.Content(State().ToJsonString(), "application/json"));
        _app.MapGet("/api/run/{id}", (string id) =>
        {
            var entry = _runs.Find(id);
            return entry is null ? Results.NotFound() : Results.Content(RunJson.Of(RunCatalog.Open(entry)).ToJsonString(), "application/json");
        });
        _app.MapGet("/api/cell/{cell}", (string cell) =>
        {
            var row = Fleet().Rows.FirstOrDefault(r => r.Cell == cell);
            if (row is null || _fleet is null) return Results.NotFound();
            var model = new RunModel();
            if (Activity() is { } document)
                foreach (var line in ActivityRun.Lines(document, row)) model.Apply(line);
            return Results.Content(RunJson.Of([(row.Name, model)]).ToJsonString(), "application/json");
        });
        _app.MapPost("/api/offer", async (HttpContext context) =>
        {
            if (!Allowed(context)) return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (_fleet is null) return Results.Json(new { ok = false, said = "no home: start creatura-console web on a vessel with a cell" });
            if (!_options.CanOffer) return Results.Json(new { ok = false, said = _options.OfferNote ?? "only the owner's vessel can offer a goal" });

            // A goal as JSON, or a goal with one file as a form.
            string? cell, goal;
            IFormFile? file = null;
            if (context.Request.HasFormContentType)
            {
                var form = await context.Request.ReadFormAsync().ConfigureAwait(false);
                cell = form["cell"].ToString();
                goal = form["goal"].ToString();
                file = form.Files.GetFile("file");
            }
            else
            {
                JsonObject? body;
                try
                {
                    body = await JsonNode.ParseAsync(context.Request.Body).ConfigureAwait(false) as JsonObject;
                }
                catch (System.Text.Json.JsonException)
                {
                    body = null;
                }

                cell = (string?)body?["cell"];
                goal = (string?)body?["goal"];
            }

            if (string.IsNullOrEmpty(cell) || goal is null || Fleet().Rows.All(r => r.Cell != cell)) return Results.Json(new { ok = false, said = "name a cell of the Fleet and a goal" });
            if (string.IsNullOrWhiteSpace(goal)) return Results.Json(new { ok = false, said = "an offer needs a goal" });

            // The file first: shared with that cell alone, through the hub; the offer then names it. No share, no offer.
            var shared = "";
            if (file is not null)
            {
                if (_options.Hub is null) return Results.Json(new { ok = false, said = "no hub to hold the file: start creatura-console web with --hub <url>" });
                if (file.Length > MaxAttachment) return Results.Json(new { ok = false, said = $"{file.FileName} is larger than 64 MB, the shared files' limit" });
                var name = SafeName(file.FileName);
                var folder = Path.Combine(Path.GetTempPath(), "creatura-attach-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(folder);
                try
                {
                    var path = Path.Combine(folder, name);
                    await using (var stream = File.Create(path)) await file.CopyToAsync(stream).ConfigureAwait(false);
                    var (sharedOk, artifact, sharedSaid) = _fleet.Share(cell, path, _options.Hub);
                    if (!sharedOk) return Results.Json(new { ok = false, said = "the file was not shared, so nothing was offered: " + sharedSaid });
                    goal = goal.TrimEnd() + $"\n\nAttached: {name}, shared with you as {artifact}. Fetch it with: creatura cell fetch --artifact {artifact} --out {name}";
                    shared = sharedSaid.Split('\n')[0] + "\n";
                }
                finally
                {
                    try
                    {
                        Directory.Delete(folder, recursive: true);
                    }
                    catch (IOException)
                    {
                    }
                }
            }

            var (ok, said) = _fleet.Offer(cell, goal);
            said = shared + said;
            lock (_gate)
            {
                _fleetRead = null;
                _activityRead = null;
            }

            return Results.Json(new { ok, said });
        });
    }

    /// <summary>An offer comes from this page: its token, and, when the browser says where from, this server's own address.</summary>
    private bool Allowed(HttpContext context)
    {
        var token = context.Request.Headers["X-Creatura-Token"].ToString();
        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(Token))) return false;
        var origin = context.Request.Headers.Origin.ToString();
        return origin.Length == 0 || origin == $"http://127.0.0.1:{Address.Port}" || origin == $"http://localhost:{Address.Port}";
    }

    private JsonObject State()
    {
        var fleet = Fleet();
        var cells = new JsonArray();
        foreach (var r in fleet.Rows)
        {
            var o = new JsonObject
            {
                ["cell"] = r.Cell,
                ["name"] = r.Name,
                ["kind"] = r.Kind,
                ["creatura"] = r.Creatura,
                ["vesselName"] = r.VesselName,
                ["ring"] = r.Ring,
                ["lastSeen"] = r.LastSeen.UtcDateTime.ToString("o"),
                ["quiet"] = r.Quiet,
                ["look"] = FleetScreen.LookOf(r).ToString().ToLowerInvariant(),
            };
            if (r.Claim is not null)
            {
                o["claim"] = r.Claim;
                o["claimedAt"] = r.ClaimedAt!.Value.UtcDateTime.ToString("o");
            }

            if (r.Progress is not null) o["progress"] = r.Progress;
            if (r.Stress is { } s) o["stress"] = s;
            if (r.Blocked) o["blocked"] = true;
            if (r.PendingOffers > 0) o["pendingOffers"] = r.PendingOffers;
            cells.Add(o);
        }

        return new JsonObject
        {
            ["at"] = fleet.At.UtcDateTime.ToString("o"),
            ["home"] = _fleet is not null,
            ["canOffer"] = _fleet is not null && _options.CanOffer,
            ["offerNote"] = _options.OfferNote,
            ["hub"] = _options.Hub,
            ["problem"] = fleet.Problem,
            ["cells"] = cells,
            ["runs"] = new JsonArray([.. _runs.List().Select(r => (JsonNode)new JsonObject
            {
                ["id"] = r.Id,
                ["kind"] = r.Kind,
                ["title"] = r.Title,
                ["subtitle"] = r.Subtitle,
                ["when"] = r.When.UtcDateTime.ToString("o"),
            })]),
        };
    }

    /// <summary>A file's name as it is kept: its last part, with nothing that could leave the folder or trouble a shell.</summary>
    public static string SafeName(string name)
    {
        var last = Path.GetFileName(name.Replace('\\', '/').Split('/').Last());
        var safe = new string([.. last.Select(c => char.IsLetterOrDigit(c) || c is '.' or '-' or '_' ? c : '_')]).TrimStart('.');
        return safe.Length == 0 ? "attachment" : safe.Length > 120 ? safe[..120] : safe;
    }

    private FleetSnapshot Fleet()
    {
        if (_fleet is null) return new FleetSnapshot(DateTimeOffset.UtcNow, [], null);
        lock (_gate)
        {
            if (_fleetRead is { } held && DateTimeOffset.UtcNow - held.At < ReadAgainAfter) return held.Snapshot;
            var snapshot = _fleet.Read();
            _fleetRead = (DateTimeOffset.UtcNow, snapshot);
            return snapshot;
        }
    }

    private string? Activity()
    {
        if (_fleet is null) return null;
        lock (_gate)
        {
            if (_activityRead is { } held && DateTimeOffset.UtcNow - held.At < ReadAgainAfter) return held.Document;
            var (code, output, _) = _fleet.Cli.Run(["cell", "activity", .. _fleet.HomeArgs]);
            if (code != 0) return null;
            _activityRead = (DateTimeOffset.UtcNow, output);
            return output;
        }
    }

    private static string Page { get; } = Resource("console.html");

    private static byte[] Icon { get; } = Encoding.UTF8.GetBytes(Resource("icon.svg"));

    private static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"the page's {name} is not in the program");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
