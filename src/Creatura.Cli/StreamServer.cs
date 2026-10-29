using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;

namespace Creatura.Cells;

/// <summary>
/// <c>creatura cell stream</c>'s server (docs/design-activity-stream.md, C26): the page, the stream as JSON and the
/// icons, read-only, on the loopback address and nowhere else. It is a small HTTP/1.1 server on a socket bound to
/// 127.0.0.1, so no other interface can reach it on any platform and nothing needs an administrator. GET is the
/// only method; anything else gets 405. The ledger is read again for every request, so the page follows what the
/// sync job and the hooks write.
/// </summary>
public sealed class StreamServer : IAsyncDisposable
{
    private readonly TcpListener _listener;
    private readonly Func<WorkLedger> _ledger;
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    private StreamServer(TcpListener listener, Func<WorkLedger> ledger)
    {
        _listener = listener;
        _ledger = ledger;
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Address = new Uri($"http://{IPAddress.Loopback}:{port}/");
        _loop = Task.Run(AcceptLoop);
    }

    /// <summary>Where it answers: http://127.0.0.1:port/.</summary>
    public Uri Address { get; }

    /// <summary>Starts serving <paramref name="ledger"/> on 127.0.0.1, on <paramref name="port"/> or a free one when it is 0.</summary>
    public static StreamServer Start(Func<WorkLedger> ledger, int port = 0)
    {
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();
        return new StreamServer(listener, ledger);
    }

    /// <summary>Whether a Creatura activity stream answers at <paramref name="address"/> (its collection names Creatura's terms).</summary>
    public static bool IsStream(Uri address)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var response = http.GetAsync(new Uri(address, "activity")).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode
                && response.Content.Headers.ContentType?.MediaType == ActivityStream.MediaType
                && response.Content.ReadAsStringAsync().GetAwaiter().GetResult().Contains("urn:creatura", StringComparison.Ordinal);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException)
        {
            return false;
        }
    }

    private async Task AcceptLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => Serve(client));
        }
    }

    private async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                stream.ReadTimeout = 5000;
                var request = await ReadHead(stream).ConfigureAwait(false);
                if (request is null) return;
                var (method, target) = request.Value;
                var (status, type, body) = method == "GET" ? SafeAnswer(target) : (405, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("read-only: GET only\n"));
                var head = new StringBuilder()
                    .Append($"HTTP/1.1 {status} {Reason(status)}\r\n")
                    .Append($"Content-Type: {type}\r\n")
                    .Append($"Content-Length: {body.Length}\r\n")
                    .Append(status == 405 ? "Allow: GET\r\n" : "")
                    .Append("Cache-Control: no-store\r\n")
                    .Append("X-Content-Type-Options: nosniff\r\n")
                    .Append("Content-Security-Policy: default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:\r\n")
                    .Append("Connection: close\r\n\r\n");
                await stream.WriteAsync(Encoding.ASCII.GetBytes(head.ToString())).ConfigureAwait(false);
                await stream.WriteAsync(body).ConfigureAwait(false);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
            }
        }
    }

    /// <summary>The request line and headers; the body, if any, is not read (nothing here takes one).</summary>
    private static async Task<(string Method, string Target)?> ReadHead(NetworkStream stream)
    {
        var buffer = new byte[16 * 1024];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length)).ConfigureAwait(false);
            if (read == 0) break;
            length += read;
            if (Encoding.ASCII.GetString(buffer, 0, length).Contains("\r\n\r\n", StringComparison.Ordinal)) break;
        }

        var text = Encoding.ASCII.GetString(buffer, 0, length);
        var line = text.Split("\r\n", 2)[0].Split(' ');
        return line.Length < 2 ? null : (line[0], line[1]);
    }

    /// <summary>The answer, or a 500 that says why: a ledger this build cannot read (an entry kind from a newer version) is reported, not a dropped connection.</summary>
    private (int Status, string Type, byte[] Body) SafeAnswer(string target)
    {
        try
        {
            return Answer(target);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or IOException or FormatException or NotSupportedException or TimeoutException)
        {
            return (500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(
                $"this creatura cannot read the ledger: {e.Message}\nif the ledger has entries from a newer version, update this tool and start the stream again\n"));
        }
    }

    private (int Status, string Type, byte[] Body) Answer(string target)
    {
        var path = target.Split('?', 2)[0];
        var query = target.Contains('?') ? target.Split('?', 2)[1] : "";
        switch (path)
        {
            case "/":
            case "/index.html":
                return (200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(StreamPage.Html));
            case "/activity":
                var ledger = _ledger();
                var page = query.Split('&').Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "page" && p.Length == 2)?[1];
                if (page is null) return (200, ActivityStream.MediaType, Encoding.UTF8.GetBytes(ActivityStream.Collection(ledger)));
                return int.TryParse(page, out var n) && n >= 1
                    ? (200, ActivityStream.MediaType, Encoding.UTF8.GetBytes(ActivityStream.Page(ledger, n)))
                    : (400, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("page must be a number from 1\n"));
            case "/icon.svg":
                return (200, "image/svg+xml", StreamPage.Icon(light: false));
            case "/icon-light.svg":
                return (200, "image/svg+xml", StreamPage.Icon(light: true));
            default:
                return (404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("not found\n"));
        }
    }

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        404 => "Not Found",
        500 => "Internal Server Error",
        405 => "Method Not Allowed",
        _ => "Error",
    };

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        try { await _loop.ConfigureAwait(false); } catch (OperationCanceledException) { }
        _stop.Dispose();
    }
}

/// <summary>The stream's page and the icons it shows, embedded in the tool: nothing is fetched from anywhere else.</summary>
public static class StreamPage
{
    public static string Html { get; } = Text("stream.html");

    public static byte[] Icon(bool light) => Encoding.UTF8.GetBytes(Text(light ? "icon-light.svg" : "icon.svg"));

    private static string Text(string name)
    {
        using var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"the resource {name} is missing from the tool");
        using var reader = new StreamReader(resource, Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
