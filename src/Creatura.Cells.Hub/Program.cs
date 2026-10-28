using Creatura.Cells;
using Creatura.Cells.Hub;

// The ledger hub: SyncFramework's sync controller at http://<listen>/sync/ (HubCommandLine has the options).
if (args.Contains("--help") || args.Contains("-h"))
{
    Console.WriteLine(HubCommandLine.Usage);
    return 0;
}

HubLaunch launch;
try
{
    launch = HubCommandLine.Parse(args);
}
catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine(e.Message);
    Console.Error.WriteLine(HubCommandLine.Usage);
    return 2;
}

OwnerOnly.CreateDirectory(Path.GetDirectoryName(launch.Options.DatabasePath)!);
var app = HubApp.Create(launch.Options, line => Console.WriteLine($"{DateTimeOffset.UtcNow:O} {line}"));
Console.WriteLine($"hub at http://{launch.Listen}/sync/, trusting {launch.TrustedFrom}, deltas in {launch.Options.DatabasePath}");
await app.RunAsync();
return 0;
