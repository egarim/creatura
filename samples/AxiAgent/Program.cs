// AXI (#91): runs Creatura's Singular agent for every configured tenant until Ctrl+C. See README.md for the variables.
// One service key per tenant, read from the environment and put on that tenant's HttpClient only; never printed.
using Creatura.Samples.AxiAgent;

string Need(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new InvalidOperationException($"{name} is not set");

var baseUrl = new Uri(Need("SINGULAR_URL").TrimEnd('/') + "/");
var app = Need("AXI_APP");
var tenants = Need("AXI_TENANTS").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
if (tenants.Length == 0) throw new InvalidOperationException("AXI_TENANTS names no tenant");
// Two slugs on one variable (a-b and a_b) would share a key: refuse rather than guess.
if (tenants.GroupBy(SingularAgent.KeyVariable).FirstOrDefault(g => g.Count() > 1) is { } clash)
    throw new InvalidOperationException($"tenants {string.Join(", ", clash)} share {clash.Key}");

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

var agents = tenants.Select(tenant =>
{
    var http = SingularAgent.Client(baseUrl, app, tenant, Need(SingularAgent.KeyVariable(tenant)));
    return new SingularAgent(http, TimeProvider.System, line => Console.Error.WriteLine($"{tenant}: {line}"));
}).ToList();

Console.Error.WriteLine($"polling {agents.Count} tenant(s) of {app} at {baseUrl}");
try
{
    while (true)
    {
        var waits = await Task.WhenAll(agents.Select(a => a.StepAsync(cts.Token)));
        await Task.Delay(waits.Min(), cts.Token);
    }
}
catch (OperationCanceledException) when (cts.IsCancellationRequested)
{
}
return 0;
