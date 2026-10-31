using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Creatura.Worlds.Web;

/// <summary>
/// The bundled offline demo: a notes app served on 127.0.0.1 from this process, so a web world can be run and tested
/// with no backend and no model. <c>GET /</c> is the page; <c>GET /api/notes</c> the notes; <c>POST /api/notes</c>
/// adds one (<c>{"text": ...}</c>); <c>POST /api/reset</c> empties them. With <see cref="DropWrites"/> the app itself
/// lies: it answers a save with success and keeps nothing (the barrier <c>save-lies</c> does the same from the
/// browser's side, without the app knowing).
/// <para>
/// It also signs people in, the way the Singular starter does: <c>GET /login</c> is a sign-in page that keeps the token
/// it gets from <c>POST /api/login</c> in sessionStorage only; <c>GET /private</c> is a private list read and written with
/// that token (<c>/api/private/notes</c>), and shows "Please sign in" without it. <c>GET /api/admin/notes?user=</c> is
/// the test tenant's back door the harness checks with. One account: <see cref="User"/> / <see cref="Password"/>.
/// </para>
/// </summary>
public sealed class DemoApp : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    public const string User = "ana@example.test";

    public const string Password = "demo-password";

    private readonly List<string> _notes = [];
    private readonly Dictionary<string, List<string>> _private = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _tokens = new(StringComparer.Ordinal);
    private int _signIns;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private DemoApp(int port)
    {
        BaseUrl = $"http://127.0.0.1:{port}/";
        _listener.Prefixes.Add(BaseUrl);
        _listener.Start();
        _loop = Task.Run(ServeAsync);
    }

    public string BaseUrl { get; }

    /// <summary>The app answers saves with success and keeps nothing.</summary>
    public bool DropWrites { get; set; }

    public IReadOnlyList<string> Notes
    {
        get
        {
            lock (_notes) return _notes.ToList();
        }
    }

    /// <summary>How many times someone signed in.</summary>
    public int SignIns => Volatile.Read(ref _signIns);

    /// <summary>The private list of a user.</summary>
    public IReadOnlyList<string> PrivateNotes(string user)
    {
        lock (_notes) return _private.TryGetValue(user, out var notes) ? notes.ToList() : [];
    }

    /// <summary>Every token stops working: each signed-in browser is signed out at its next request.</summary>
    public void ExpireSessions()
    {
        lock (_notes) _tokens.Clear();
    }

    /// <summary>Starts the app on a free port on 127.0.0.1.</summary>
    public static DemoApp Start()
    {
        for (var attempt = 0; ; attempt++)
        {
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            try
            {
                return new DemoApp(port);
            }
            catch (HttpListenerException) when (attempt < 5)
            {
            }
        }
    }

    /// <summary>The demo's adapter, with this app's base url.</summary>
    public UxAdapter Adapter() => DemoFiles.Adapter() with { BaseUrl = BaseUrl };

    private async Task ServeAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_listener.IsListening)
            {
                return;
            }
            catch (HttpListenerException)
            {
                continue;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            var path = request.Url?.AbsolutePath ?? "/";
            switch (request.HttpMethod, path)
            {
                case ("GET", "/" or "/index.html"):
                    await WriteAsync(response, 200, "text/html; charset=utf-8", DemoFiles.Page).ConfigureAwait(false);
                    break;
                case ("GET", "/api/notes"):
                    JsonArray items;
                    lock (_notes) items = new JsonArray(_notes.Select(n => (JsonNode)new JsonObject { ["text"] = n }).ToArray());
                    await WriteAsync(response, 200, "application/json", new JsonObject { ["items"] = items }.ToJsonString()).ConfigureAwait(false);
                    break;
                case ("POST", "/api/notes"):
                    using (var reader = new StreamReader(request.InputStream, Encoding.UTF8))
                    {
                        var body = await reader.ReadToEndAsync().ConfigureAwait(false);
                        string? text = null;
                        try
                        {
                            text = JsonNode.Parse(body)?["text"]?.GetValue<string>();
                        }
                        catch (Exception e) when (e is JsonException or InvalidOperationException)
                        {
                        }

                        if (string.IsNullOrWhiteSpace(text))
                        {
                            await WriteAsync(response, 400, "application/json", """{"error":"text is needed"}""").ConfigureAwait(false);
                            break;
                        }

                        if (!DropWrites)
                        {
                            lock (_notes) _notes.Add(text.Trim());
                        }

                        await WriteAsync(response, 200, "application/json", """{"ok":true}""").ConfigureAwait(false);
                    }

                    break;
                case ("GET", "/login"):
                    await WriteAsync(response, 200, "text/html; charset=utf-8", DemoFiles.Login).ConfigureAwait(false);
                    break;
                case ("GET", "/private"):
                    await WriteAsync(response, 200, "text/html; charset=utf-8", DemoFiles.Private).ConfigureAwait(false);
                    break;
                case ("POST", "/api/login"):
                    var login = await BodyAsync(request).ConfigureAwait(false);
                    if (login?["email"]?.GetValue<string>() != User || login?["password"]?.GetValue<string>() != Password)
                    {
                        await WriteAsync(response, 401, "application/json", """{"error":"wrong email or password"}""").ConfigureAwait(false);
                        break;
                    }

                    var token = Guid.NewGuid().ToString("N");
                    lock (_notes) _tokens[token] = User;
                    Interlocked.Increment(ref _signIns);
                    await WriteAsync(response, 200, "application/json", new JsonObject { ["token"] = token, ["email"] = User }.ToJsonString()).ConfigureAwait(false);
                    break;
                case (_, "/api/private/notes"):
                    var auth = request.Headers["Authorization"] ?? "";
                    string? user;
                    lock (_notes) user = auth.StartsWith("Bearer ", StringComparison.Ordinal) && _tokens.TryGetValue(auth[7..], out var u) ? u : null;
                    if (user is null)
                    {
                        await WriteAsync(response, 401, "application/json", """{"error":"sign in"}""").ConfigureAwait(false);
                        break;
                    }

                    if (request.HttpMethod == "POST")
                    {
                        var text = (await BodyAsync(request).ConfigureAwait(false))?["text"]?.GetValue<string>();
                        if (string.IsNullOrWhiteSpace(text))
                        {
                            await WriteAsync(response, 400, "application/json", """{"error":"text is needed"}""").ConfigureAwait(false);
                            break;
                        }

                        if (!DropWrites)
                        {
                            lock (_notes) (_private.TryGetValue(user, out var list) ? list : _private[user] = []).Add(text.Trim());
                        }

                        await WriteAsync(response, 200, "application/json", """{"ok":true}""").ConfigureAwait(false);
                        break;
                    }

                    await WriteAsync(response, 200, "application/json", new JsonObject { ["user"] = user, ["items"] = Items(PrivateNotes(user)) }.ToJsonString()).ConfigureAwait(false);
                    break;
                case ("GET", "/api/admin/notes"):
                    var who = request.QueryString["user"] ?? "";
                    await WriteAsync(response, 200, "application/json", new JsonObject { ["user"] = who, ["items"] = Items(PrivateNotes(who)) }.ToJsonString()).ConfigureAwait(false);
                    break;
                case ("POST", "/api/reset"):
                    lock (_notes)
                    {
                        _notes.Clear();
                        _private.Clear();
                    }

                    await WriteAsync(response, 200, "application/json", """{"ok":true}""").ConfigureAwait(false);
                    break;
                default:
                    await WriteAsync(response, 404, "text/plain", "not found").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception) when (_stop.IsCancellationRequested)
        {
        }
        catch (HttpListenerException)
        {
        }
    }

    private static JsonArray Items(IEnumerable<string> notes) => new(notes.Select(n => (JsonNode)new JsonObject { ["text"] = n }).ToArray());

    private static async Task<JsonNode?> BodyAsync(HttpListenerRequest request)
    {
        using var reader = new StreamReader(request.InputStream, Encoding.UTF8);
        try
        {
            return JsonNode.Parse(await reader.ReadToEndAsync().ConfigureAwait(false));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task WriteAsync(HttpListenerResponse response, int status, string type, string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        response.StatusCode = status;
        response.ContentType = type;
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        _listener.Close();
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (Exception)
        {
        }

        _stop.Dispose();
    }
}

/// <summary>The demo's files, embedded: the page, the adapter, the offline persona's script.</summary>
public static class DemoFiles
{
    public static string Page => WebWorld.Resource("demo/notes.html");

    public static string Login => WebWorld.Resource("demo/login.html");

    public static string Private => WebWorld.Resource("demo/private.html");

    public static string AdapterJson => WebWorld.Resource("demo/notes.adapter.json");

    /// <summary>The offline persona's script for the first task, <c>remember-milk</c>.</summary>
    public static IReadOnlyList<string> Script => ScriptFor("remember-milk");

    /// <summary>The offline persona's script for a demo task.</summary>
    public static IReadOnlyList<string> ScriptFor(string task) => WebWorld.Resource($"demo/{task}.script").Split('\n');

    public static UxAdapter Adapter() => UxAdapter.Parse(AdapterJson);
}
