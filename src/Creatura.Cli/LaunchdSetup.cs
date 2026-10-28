using System.Diagnostics;
using System.Security;
using System.Text;

namespace Creatura.Cells;

/// <summary>
/// The two launchd agents that make sync automatic on a Mac (docs/design-cells.md, "Automatic sync"):
/// <list type="bullet">
/// <item><c>com.creatura.hub</c>: the hub, started at login and kept running, bound to the mesh address, trusting a
/// file of owner public keys.</item>
/// <item><c>com.creatura.sync</c>: <c>creatura cell sync --quiet</c> every <c>interval</c> seconds.</item>
/// </list>
/// The plists are generated, never edited by hand, so <c>launchd-config</c> shows exactly what <c>install-launchd</c>
/// writes. Installing backs up a plist it replaces; uninstalling unloads and removes only these two labels.
/// </summary>
public static class LaunchdSetup
{
    public const string HubLabel = "com.creatura.hub";
    public const string SyncLabel = "com.creatura.sync";

    public static string AgentsDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public static string HubPlist(string dotnet, string hubDll, string listen, string trustFile, string dataDirectory, string log) =>
        Plist(HubLabel, [dotnet, hubDll, "--listen", listen, "--trust", trustFile, "--data", dataDirectory], log,
            """
                <key>RunAtLoad</key>
                <true/>
                <key>KeepAlive</key>
                <true/>
                <key>ThrottleInterval</key>
                <integer>30</integer>
            """);

    public static string SyncPlist(string command, string home, string creatura, string hub, int intervalSeconds, string log) =>
        Plist(SyncLabel, [command, "cell", "sync", "--home", home, "--creatura", creatura, "--hub", hub, "--quiet"], log,
            $"""
                <key>RunAtLoad</key>
                <true/>
                <key>StartInterval</key>
                <integer>{intervalSeconds}</integer>
            """);

    private static string Plist(string label, IReadOnlyList<string> arguments, string log, string schedule)
    {
        var text = new StringBuilder();
        text.Append("""
            <?xml version="1.0" encoding="UTF-8"?>
            <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
            <plist version="1.0">
            <dict>
                <key>Label</key>

            """);
        text.Append("    <string>").Append(SecurityElement.Escape(label)).Append("</string>\n");
        text.Append("    <key>ProgramArguments</key>\n    <array>\n");
        foreach (var argument in arguments) text.Append("        <string>").Append(SecurityElement.Escape(argument)).Append("</string>\n");
        text.Append("    </array>\n");
        text.Append(schedule.TrimEnd()).Append('\n');
        text.Append("    <key>StandardOutPath</key>\n    <string>").Append(SecurityElement.Escape(log)).Append("</string>\n");
        text.Append("    <key>StandardErrorPath</key>\n    <string>").Append(SecurityElement.Escape(log)).Append("</string>\n");
        text.Append("    <key>ProcessType</key>\n    <string>Background</string>\n");
        text.Append("</dict>\n</plist>\n");
        return text.ToString();
    }

    /// <summary>Writes each plist (backing up one it replaces) and, unless <paramref name="load"/> is false, reloads it with launchctl.</summary>
    public static IReadOnlyList<string> Install(IReadOnlyDictionary<string, string> plists, string agentsDirectory, bool load, DateTimeOffset now)
    {
        Directory.CreateDirectory(agentsDirectory);
        var done = new List<string>();
        foreach (var (label, text) in plists)
        {
            var path = Path.Combine(agentsDirectory, label + ".plist");
            var same = File.Exists(path) && File.ReadAllText(path) == text;
            if (same)
            {
                done.Add($"unchanged {path}");
            }
            else
            {
                if (File.Exists(path))
                {
                    var backup = $"{path}.bak-{now:yyyyMMddHHmmss}";
                    File.Copy(path, backup);
                    done.Add($"backed up {backup}");
                }
                File.WriteAllText(path, text);
                done.Add($"wrote     {path}");
            }
            if (load)
            {
                Launchctl("bootout", path, ignoreFailure: true);
                Launchctl("bootstrap", path, ignoreFailure: false);
                done.Add($"loaded    {label}");
            }
        }
        return done;
    }

    /// <summary>Unloads and removes the plists of these labels; anything else in the directory is left alone.</summary>
    public static IReadOnlyList<string> Uninstall(IEnumerable<string> labels, string agentsDirectory, bool unload)
    {
        var done = new List<string>();
        foreach (var label in labels)
        {
            var path = Path.Combine(agentsDirectory, label + ".plist");
            if (!File.Exists(path)) { done.Add($"absent    {path}"); continue; }
            if (unload) Launchctl("bootout", path, ignoreFailure: true);
            File.Delete(path);
            done.Add($"removed   {path}");
        }
        return done;
    }

    private static void Launchctl(string verb, string plist, bool ignoreFailure)
    {
        var domain = $"gui/{Environment.GetEnvironmentVariable("UID") ?? UserId()}";
        var start = new ProcessStartInfo("/bin/launchctl", [verb, domain, plist]) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("launchctl did not start");
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 && !ignoreFailure) throw new InvalidOperationException($"launchctl {verb} {plist} failed ({process.ExitCode}): {error.Trim()}");
    }

    private static string UserId()
    {
        var start = new ProcessStartInfo("/usr/bin/id", ["-u"]) { RedirectStandardOutput = true, UseShellExecute = false };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("id did not start");
        var output = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();
        return output;
    }
}
