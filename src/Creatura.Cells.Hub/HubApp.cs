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

/// <summary>
/// Who the hub answers: requests signed by an endorsed cell of an owner it trusts, within five minutes, over exactly
/// that request (<see cref="HubRequest"/>, C20). Everything else gets 401 and the reason, never ledger content.
/// </summary>
public static class HubAuth
{
    public const string CellItem = "creatura.cell";

    public static async Task Middleware(HttpContext context, RequestDelegate next, IReadOnlyCollection<string> trustedOwners, TimeProvider clock, Action<string>? log = null, HubDeltaStore? store = null)
    {
        context.Request.EnableBuffering();
        byte[] body;
        using (var buffer = new MemoryStream())
        {
            await context.Request.Body.CopyToAsync(buffer, context.RequestAborted).ConfigureAwait(false);
            body = buffer.ToArray();
        }
        context.Request.Body.Position = 0;

        // The request target exactly as it came over the wire: what the client signed.
        var target = context.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget is { Length: > 0 } raw
            ? raw
            : context.Request.Path + context.Request.QueryString;
        var headers = context.Request.Headers;
        var now = clock.GetUtcNow();
        var reason = HubRequest.Verify(trustedOwners, headers[HubRequest.EndorsementHeader], headers[HubRequest.TimeHeader], headers[HubRequest.SignatureHeader],
            context.Request.Method, target, body, now, out var cell, store is null ? null : store.IsRevoked);
        if (reason is null && store is not null && !store.RememberRequest(headers[HubRequest.SignatureHeader].ToString(), now))
            reason = "the request was already used";
        if (reason is not null)
        {
            log?.Invoke($"401 {context.Connection.RemoteIpAddress} {context.Request.Method} {context.Request.Path}: {reason}");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(reason, context.RequestAborted).ConfigureAwait(false);
            return;
        }

        context.Items[CellItem] = cell;
        await next(context).ConfigureAwait(false);
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
        app.Use((context, next) => HubAuth.Middleware(context, next, options.TrustedOwners, TimeProvider.System, log, store));
        app.MapControllers();

        // Revocations: any signed request may read them or hand them in; the store keeps only those an owner it trusts signed.
        app.MapGet("/revocations", () => Results.Text(System.Text.Json.JsonSerializer.Serialize(store.Revocations, CellJson.Options), "application/json"));
        app.MapPost("/revocations", async (HttpRequest request) =>
        {
            var given = await System.Text.Json.JsonSerializer.DeserializeAsync<List<Revocation>>(request.Body, CellJson.Options) ?? [];
            var taken = 0;
            foreach (var revocation in given)
            {
                if (await store.AddRevocationAsync(revocation))
                {
                    taken++;
                    log?.Invoke($"revoked {revocation.Cell} after sequence {revocation.UpToSequence}: {revocation.Reason}");
                }
            }
            return Results.Text(System.Text.Json.JsonSerializer.Serialize(new { taken }), "application/json");
        });
        return app;
    }
}
