using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Creatura.Cells;
using Creatura.Seats;

namespace Creatura.Cli;

/// <summary>Everything <c>creatura doctor</c> reads from outside, so tests can replace it.</summary>
public sealed record DoctorEnvironment
{
    /// <summary>An environment variable's value. Only whether it is set is ever shown.</summary>
    public Func<string, string?> Variable { get; init; } = Environment.GetEnvironmentVariable;

    /// <summary>The output of <c>dotnet --list-sdks</c>, or null when there is no <c>dotnet</c> to run.</summary>
    public Func<string?> Sdks { get; init; } = DoctorCommand.ListSdks;

    /// <summary>The status code a GET of the address answers with, or null when nothing answers in a few seconds.</summary>
    public Func<Uri, int?> Get { get; init; } = DoctorCommand.StatusOf;

    public string Home { get; init; } = Environment.GetEnvironmentVariable("CREATURA_HOME") ?? CellHome.DefaultRoot;

    public string ClaudeSettings { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");

    /// <summary>Where <c>creatura cell install-launchd</c> puts its agents (macOS).</summary>
    public string LaunchAgents { get; init; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents");

    public ModelCatalog? Models { get; init; }

    /// <summary>Skip the checks that use the network (nuget.org, the hub).</summary>
    public bool Offline { get; init; }
}

/// <summary>
/// <c>creatura doctor</c>: what is set up and what is missing, one line each with the fix (docs/design-onboarding.md,
/// O4). It only reads: no key is made, no file is written, and a secret's value is never shown, only whether its
/// variable is set. Exit codes: 0 when nothing failed (a warning is not a failure), 1 when something did.
/// </summary>
public static class DoctorCommand
{
    public const string Usage = """
        usage: creatura doctor [--home <dir>] [--settings <file>] [--offline]

          checks the .NET SDK (for creatura new), whether the Creatura packages can be found, the cell home and its
          ledger, the Claude Code hooks, the hub a scheduled sync talks to, and which model keys are set (never their
          values); --offline skips the checks that use the network
        """;

    private enum Level
    {
        Ok,
        Info,
        Warn,
        Fail,
    }

    private sealed record Check(Level Level, string Text, string? Fix = null);

    public static int Run(string[] args, TextWriter output, DoctorEnvironment? env = null)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            output.WriteLine(Usage);
            return 0;
        }
        env ??= new DoctorEnvironment();
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i] == "--offline") env = env with { Offline = true };
            else if (args[i] == "--home" && i + 1 < args.Length) env = env with { Home = args[++i] };
            else if (args[i] == "--settings" && i + 1 < args.Length) env = env with { ClaudeSettings = args[++i] };
            else
            {
                output.WriteLine($"unexpected argument: {args[i]}");
                output.WriteLine(Usage);
                return 2;
            }
        }

        output.WriteLine($"creatura doctor (creatura {NewCommand.ToolVersion()})");
        var checks = new List<Check>();
        foreach (var check in new Func<DoctorEnvironment, IEnumerable<Check>>[] { Sdk, Packages, Home, Hooks, Hub, ModelKeys })
        {
            try
            {
                checks.AddRange(check(env));
            }
            catch (Exception e)
            {
                checks.Add(new Check(Level.Fail, $"{check.Method.Name.ToLowerInvariant()}: the check itself failed: {e.GetType().Name}: {Scrubber.Scrub(e.Message)}"));
            }
        }

        foreach (var check in checks)
        {
            output.WriteLine($"  {check.Level.ToString().ToLowerInvariant(),-6}{check.Text}");
            if (check.Fix is not null) output.WriteLine($"  {"",-6}fix: {check.Fix}");
        }
        var warnings = checks.Count(c => c.Level == Level.Warn);
        var failures = checks.Count(c => c.Level == Level.Fail);
        output.WriteLine($"{warnings} warning{(warnings == 1 ? "" : "s")}, {failures} failure{(failures == 1 ? "" : "s")}");
        return failures == 0 ? 0 : 1;
    }

    private static IEnumerable<Check> Sdk(DoctorEnvironment env)
    {
        var listed = env.Sdks();
        if (listed is null)
        {
            yield return new Check(Level.Warn, ".NET SDK: no dotnet found; creatura run and creatura cell work without it, creatura new's projects need it",
                "install the .NET 10 SDK: https://dotnet.microsoft.com/download");
            yield break;
        }
        var versions = listed.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split(' ')[0]).ToList();
        var newest = versions.Where(v => Version.TryParse(v.Split('-')[0], out _)).OrderBy(v => Version.Parse(v.Split('-')[0])).LastOrDefault();
        if (newest is not null && Version.Parse(newest.Split('-')[0]).Major >= 10) yield return new Check(Level.Ok, $".NET SDK {newest}");
        else yield return new Check(Level.Warn, $".NET SDK: {(newest is null ? "none installed" : $"only {newest}")}; creatura new's projects target .NET 10",
            "install the .NET 10 SDK: https://dotnet.microsoft.com/download");
    }

    private static IEnumerable<Check> Packages(DoctorEnvironment env)
    {
        if (env.Offline) yield break;
        var status = env.Get(new Uri("https://api.nuget.org/v3-flatcontainer/creatura/index.json"));
        yield return status switch
        {
            200 => new Check(Level.Ok, "packages: Creatura is on nuget.org"),
            404 => new Check(Level.Warn, "packages: Creatura is not on nuget.org yet, so a project from creatura new will not restore from there",
                "download the .nupkg files from the GitHub release into a folder and pass it: creatura new agent --source <folder>"),
            null => new Check(Level.Warn, "packages: nuget.org did not answer", "check the network, or use creatura new --source <folder of .nupkg files>"),
            _ => new Check(Level.Warn, $"packages: nuget.org answered {status}"),
        };
    }

    private static IEnumerable<Check> Home(DoctorEnvironment env)
    {
        var home = new CellHome(env.Home);
        if (!Directory.Exists(home.Root))
        {
            yield return new Check(Level.Info, $"cells: no home at {home.Root} (only needed for cells: a ledger of who works on what)", "creatura cell init");
            yield break;
        }
        if (!home.IsInitialized)
        {
            yield return new Check(Level.Warn, $"cells: {home.Root} exists but was never initialized", "creatura cell init");
            yield break;
        }

        var cells = new List<string>();
        var owners = new HashSet<string>(StringComparer.Ordinal);
        var unendorsed = new List<string>();
        if (Directory.Exists(home.CellsDirectory))
        {
            foreach (var key in Directory.EnumerateFiles(home.CellsDirectory, "*.key", SearchOption.AllDirectories)
                         .Where(k => !k.EndsWith(".encryption.key", StringComparison.Ordinal)).OrderBy(k => k, StringComparer.Ordinal))
            {
                var name = Path.GetFileNameWithoutExtension(key);
                var endorsement = Path.ChangeExtension(key, null) + ".endorsement.json";
                if (File.Exists(endorsement))
                {
                    cells.Add(name);
                    if (JsonSerializer.Deserialize<VesselEndorsement>(File.ReadAllText(endorsement), CellJson.Options) is { } e) owners.Add(e.OwnerPublicKey);
                }
                else unendorsed.Add(name);
            }
        }
        using var owner = home.LoadOwner();
        if (owner is not null) owners.Add(owner.PublicKey);
        yield return new Check(Level.Ok, $"cells: home {home.Root}; {(owner is null ? "no owner key on this vessel" : "the owner key is here")}; " +
            $"endorsed: {(cells.Count == 0 ? "none" : string.Join(", ", cells.Distinct()))}");
        if (unendorsed.Count > 0)
            yield return new Check(Level.Warn, $"cells: not endorsed: {string.Join(", ", unendorsed.Distinct())}",
                owner is null ? "creatura cell request, then endorse it on the owner's vessel" : $"creatura cell init --creatura {unendorsed[0]}");

        if (!File.Exists(home.LedgerPath))
        {
            yield return new Check(Level.Info, "ledger: empty so far");
            yield break;
        }
        var ledger = new WorkLedger(owners);
        home.ApplyRevocations(ledger);
        var merged = home.JsonLedger.LoadInto(ledger);
        yield return merged.Rejected.Count == 0
            ? new Check(Level.Ok, $"ledger: {ledger.Count} entries, digest {ledger.Digest()}")
            : new Check(Level.Warn, $"ledger: {ledger.Count} entries; {merged.Rejected.Count} rejected (signed by a cell or owner this vessel does not trust, or altered)",
                "creatura cell ledger lists what was kept");
    }

    private static IEnumerable<Check> Hooks(DoctorEnvironment env)
    {
        if (!File.Exists(env.ClaudeSettings))
        {
            yield return new Check(Level.Info, $"Claude Code: no settings at {env.ClaudeSettings}");
            yield break;
        }
        var settings = JsonNode.Parse(File.ReadAllText(env.ClaudeSettings)) as JsonObject;
        var commands = new List<(string Event, string Command)>();
        if (settings?["hooks"] is JsonObject hooks)
        {
            foreach (var (name, groups) in hooks)
            {
                foreach (var group in groups as JsonArray ?? [])
                {
                    foreach (var hook in group?["hooks"] as JsonArray ?? [])
                    {
                        if (hook?["command"]?.GetValue<string>() is { } command && command.Contains("cell hook", StringComparison.Ordinal)) commands.Add((name, command));
                    }
                }
            }
        }
        if (commands.Count == 0)
        {
            yield return new Check(Level.Info, "Claude Code: the hooks are not installed, so its sessions do not reach the ledger",
                $"creatura cell install-hooks --settings {env.ClaudeSettings} --creatura claude-code");
            yield break;
        }
        var programs = commands.Select(c => Program(c.Command)).Distinct().ToList();
        var missing = programs.Where(p => !File.Exists(p)).ToList();
        var events = string.Join(", ", commands.Select(c => c.Event).Distinct());
        yield return missing.Count == 0
            ? new Check(Level.Ok, $"Claude Code: hooks on {events}, calling {string.Join(", ", programs)}")
            : new Check(Level.Fail, $"Claude Code: the hooks call {string.Join(", ", missing)}, which is not there, so they do nothing",
                "creatura cell publish-bin, or install-hooks again with --command <path to creatura>");
    }

    private static IEnumerable<Check> Hub(DoctorEnvironment env)
    {
        var hubs = new SortedSet<string>(StringComparer.Ordinal);
        if (Directory.Exists(env.LaunchAgents))
        {
            foreach (var plist in Directory.EnumerateFiles(env.LaunchAgents, "*creatura*.plist"))
            {
                foreach (Match m in Regex.Matches(File.ReadAllText(plist), @"<string>--hub</string>\s*<string>([^<]+)</string>")) hubs.Add(m.Groups[1].Value.Trim());
            }
        }
        if (hubs.Count == 0)
        {
            yield return new Check(Level.Info, "hub: no scheduled sync on this vessel (cells sync only when a command runs)");
            yield break;
        }
        foreach (var hub in hubs)
        {
            if (env.Offline)
            {
                yield return new Check(Level.Info, $"hub: a scheduled sync talks to {hub} (not checked: --offline)");
                continue;
            }
            var status = Uri.TryCreate(hub.TrimEnd('/') + "/revocations", UriKind.Absolute, out var uri) ? env.Get(uri) : null;
            yield return status is null
                ? new Check(Level.Warn, $"hub: {hub} did not answer", "is the mesh up on this vessel? creatura cell sync --hub <url> retries by hand")
                : new Check(Level.Ok, $"hub: {hub} answers");
        }
    }

    private static IEnumerable<Check> ModelKeys(DoctorEnvironment env)
    {
        var catalog = env.Models ?? ModelCatalog.Current;
        var variables = catalog.Endpoints.Select(e => e.KeyEnv).OfType<string>().Distinct(StringComparer.Ordinal).OrderBy(v => v, StringComparer.Ordinal).ToList();
        if (variables.Count == 0) yield break;
        // A row may name alternatives (DEEPSEEK_API_KEY|deepseek): the key is set when any of them is.
        var set = variables.Where(v => v.Split('|').Any(name => !string.IsNullOrEmpty(env.Variable(name)))).ToList();
        var unset = variables.Except(set).ToList();
        yield return new Check(set.Count > 0 ? Level.Ok : Level.Info,
            $"model keys (for --seat model:<name> and the sk template): {(set.Count == 0 ? "none set" : "set: " + Names(set))}" +
            (unset.Count == 0 ? "" : $"; not set: {Names(unset)}"));
    }

    private static string Names(IEnumerable<string> variables) => string.Join(", ", variables.Select(v => v.Replace("|", " or ", StringComparison.Ordinal)));

    /// <summary>The program a hook command runs: its first word, unquoted.</summary>
    private static string Program(string command)
    {
        var trimmed = command.TrimStart();
        if (trimmed.StartsWith('"')) return trimmed[1..].Split('"')[0];
        return trimmed.Split(' ')[0];
    }

    public static string? ListSdks()
    {
        try
        {
            var start = new ProcessStartInfo("dotnet", "--list-sdks") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            using var process = Process.Start(start);
            if (process is null) return null;
            var text = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(5000)) { try { process.Kill(); } catch (InvalidOperationException) { } return null; }
            return process.ExitCode == 0 ? text : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    public static int? StatusOf(Uri uri)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            using var response = http.Send(new HttpRequestMessage(HttpMethod.Get, uri));
            return (int)response.StatusCode;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or OperationCanceledException)
        {
            return null;
        }
    }
}
