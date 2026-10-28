using Creatura.Cells.Hub;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;

namespace Creatura.Cells.Tests;

/// <summary>A running hub on a free loopback port, for the hub tests of slices 4 and 5.</summary>
internal sealed class RunningHub : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RunningHub(WebApplication app, Uri url)
    {
        _app = app;
        Url = url;
    }

    public Uri Url { get; }

    public HubDeltaStore Store => _app.Services.GetRequiredService<HubDeltaStore>();

    public HubFileStore Files => _app.Services.GetRequiredService<HubFileStore>();

    public static Task<RunningHub> StartAsync(string database, params string[] trustedOwners) =>
        StartAsync(new HubOptions(database, trustedOwners, "http://127.0.0.1:0"));

    public static async Task<RunningHub> StartAsync(HubOptions options)
    {
        var app = HubApp.Create(options with { Url = "http://127.0.0.1:0" });
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        return new RunningHub(app, new Uri(address));
    }

    private bool _disposed;

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _app.StopAsync();
        await _app.DisposeAsync();
    }
}

/// <summary>A clock a test moves by hand.</summary>
internal sealed class ManualClock(DateTimeOffset start) : TimeProvider
{
    public DateTimeOffset Now { get; private set; } = start;

    public override DateTimeOffset GetUtcNow() => Now;

    public void Advance(TimeSpan by) => Now += by;
}
