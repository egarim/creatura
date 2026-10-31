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
/// </summary>
public sealed class DemoApp : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<string> _notes = [];
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
                case ("POST", "/api/reset"):
                    lock (_notes) _notes.Clear();
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

    public static string AdapterJson => WebWorld.Resource("demo/notes.adapter.json");

    public static IReadOnlyList<string> Script => WebWorld.Resource("demo/notes.script").Split('\n');

    public static UxAdapter Adapter() => UxAdapter.Parse(AdapterJson);
}
