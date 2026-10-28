using Creatura.Cells;
using Creatura.Cells.Hub;

// creatura-hub --listen <address:port> [--home <dir>]
// Serves the ledger hub at http://<address:port>/sync/, trusting the owner key of the home (default ~/.creatura),
// with its deltas in <home>/hub/hub.db. Bind it to the vessel's mesh address, not to every interface.
string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

var listen = Option("--listen");
if (listen is null || args.Contains("--help"))
{
    Console.Error.WriteLine("usage: creatura-hub --listen <address:port> [--home <dir>]   (bind to the mesh address, e.g. 100.100.1.5:5181)");
    return listen is null ? 2 : 0;
}

var home = new CellHome(Option("--home") ?? Environment.GetEnvironmentVariable("CREATURA_HOME"));
using var owner = home.LoadOwner();
if (owner is null)
{
    Console.Error.WriteLine($"no owner key under {home.Root}: the hub runs on the owner's vessel and trusts its key");
    return 1;
}

OwnerOnly.CreateDirectory(Path.Combine(home.Root, "hub"));
var options = new HubOptions(Path.Combine(home.Root, "hub", "hub.db"), [owner.PublicKey], "http://" + listen);
var app = HubApp.Create(options, line => Console.WriteLine($"{DateTimeOffset.UtcNow:O} {line}"));
Console.WriteLine($"hub at http://{listen}/sync/, trusting owner {owner.Fingerprint}, deltas in {options.DatabasePath}");
await app.RunAsync();
return 0;
