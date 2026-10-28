using System.Net;
using System.Net.Sockets;
using BIT.Data.Sync.AspNetCore.Controllers;
using BIT.Data.Sync.Server;
using Creatura.Cells.Sync;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Mvc;

namespace Creatura.Cells.Hub;

/// <summary>SyncFramework's sync controller at <c>/sync</c>: Push, Fetch and HandShake, over the hub's server node.</summary>
[Route("sync")]
public sealed class LedgerSyncController(ILogger<SyncControllerBase> logger, ISyncFrameworkServer server) : SyncControllerBase(logger, server);

/// <summary>Who may talk to the hub: loopback, and the mesh's address range (100.64.0.0/10), nothing else (C18).</summary>
public static class HubAccess
{
    private static readonly IPNetwork Mesh = IPNetwork.Parse("100.64.0.0/10");

    public static bool IsAllowed(IPAddress? address)
    {
        if (address is null) return false;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return true;
        return address.AddressFamily == AddressFamily.InterNetwork && Mesh.Contains(address);
    }

    /// <summary>The middleware: 403 for anyone else, before any controller runs.</summary>
    public static Task Middleware(HttpContext context, RequestDelegate next)
    {
        if (IsAllowed(context.Connection.RemoteIpAddress)) return next(context);
        context.Response.StatusCode = StatusCodes.Status403Forbidden;
        return Task.CompletedTask;
    }
}

/// <summary>Where the hub keeps its deltas, whom it trusts, and where it listens.</summary>
public sealed record HubOptions(string DatabasePath, IReadOnlyCollection<string> TrustedOwners, string Url);

/// <summary>The hub as a web application, for the command line and for tests.</summary>
public static class HubApp
{
    public static WebApplication Create(HubOptions options, Action<string>? log = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { ContentRootPath = AppContext.BaseDirectory });
        builder.WebHost.UseUrls(options.Url);
        builder.Logging.ClearProviders();

        var store = new HubDeltaStore(options.DatabasePath, options.TrustedOwners);
        if (log is not null) store.Refused += (_, reason) => log($"refused {reason}");
        builder.Services.AddSingleton(store);
        builder.Services.AddSyncServerWithDeltaStoreNode(store, LedgerHub.NodeId);
        builder.Services.AddControllers().AddApplicationPart(typeof(HubApp).Assembly);

        var app = builder.Build();
        app.Use(HubAccess.Middleware);
        app.MapControllers();
        return app;
    }
}
