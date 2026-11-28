namespace Creatura.Worlds.Terminal.Tests;

/// <summary>
/// A fact that needs the real Sentiora runtime and <c>node</c>. Without either it is skipped with the reason and the
/// fix: <c>CREATURA_SENTIORA</c> names a runtime checkout (default
/// <c>C:\Users\joche\source\repos\st-failspawn</c>), and <c>node</c> (22 or later) must be on <c>PATH</c>. The live
/// tests are in the <c>Terminal</c> category (<c>dotnet test --filter Category!=Terminal</c> leaves them out).
/// </summary>
public sealed class TerminalFactAttribute : FactAttribute
{
    public TerminalFactAttribute()
    {
        if (TerminalProbe.Missing is { } why) Skip = why;
    }
}

internal static class TerminalProbe
{
    private const string DefaultRuntime = @"C:\Users\joche\source\repos\st-failspawn";

    private static readonly (string? Missing, string? Node, string? Runtime) Probed = Probe();

    public static string? Missing => Probed.Missing;

    public static string Node => Probed.Node!;

    public static string Runtime => Probed.Runtime!;

    public static string Server => Path.Combine(Runtime, "src", "server.js");

    private static (string? Missing, string? Node, string? Runtime) Probe()
    {
        string? node = null;
        string[] names = OperatingSystem.IsWindows() ? ["node.exe", "node.cmd", "node.bat"] : ["node"];
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var name in names)
            {
                var candidate = Path.Combine(directory.Trim('"'), name);
                if (File.Exists(candidate))
                {
                    node = candidate;
                    break;
                }
            }

            if (node is not null) break;
        }

        if (node is null) return ("no node on PATH: install Node.js 22 or later (https://nodejs.org), or put node on PATH", null, null);

        var runtime = Environment.GetEnvironmentVariable("CREATURA_SENTIORA") is { Length: > 0 } named ? named : DefaultRuntime;
        var server = Path.Combine(runtime, "src", "server.js");
        return File.Exists(server)
            ? (null, node, runtime)
            : ($"no Sentiora runtime: set CREATURA_SENTIORA to a checkout with src/server.js (looked for {server})", node, null);
    }
}
