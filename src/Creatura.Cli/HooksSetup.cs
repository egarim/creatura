using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;

namespace Creatura.Cells;

/// <summary>
/// The Claude Code side of joining: the <c>hooks</c> block to add to a settings file, a dry-run diff of adding it,
/// the install and uninstall that merge into (never replace) the file after backing it up, and the published binary
/// the hooks call. Every hook is side-effect-only: async where the event allows it, output thrown away, and a command
/// that cannot fail the session (<c>|| true</c>).
/// </summary>
public static class HooksSetup
{
    /// <summary>The events the cell listens to, and whether each runs in the background.</summary>
    public static readonly (string Event, bool Async)[] Events =
    [
        ("SessionStart", true),
        ("UserPromptSubmit", true),
        ("PostToolUse", true),
        ("Stop", true),
        ("SessionEnd", false),       // runs at exit inside a 1.5 s budget, raised by this hook's timeout; kept synchronous so it finishes
    ];

    public const int SessionEndTimeoutSeconds = 5;

    /// <summary>Indented, and with '&gt;' and '&amp;' left as they are, so a settings file reads as a person would write it.</summary>
    public static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string HookCommandLine(string command, string home, string creatura, string goal) =>
        $"{ShellQuote(command)} cell hook --home {ShellQuote(home)} --creatura {ShellQuote(creatura)} --goal {goal} >/dev/null 2>&1 || true";

    /// <summary>The <c>hooks</c> object: one group per event, each with one command hook.</summary>
    public static JsonObject HooksBlock(string command, string home, string creatura = HookCommand.DefaultCreatura, string goal = "folder")
    {
        var line = HookCommandLine(command, home, creatura, goal);
        var hooks = new JsonObject();
        foreach (var (name, isAsync) in Events)
        {
            var hook = new JsonObject { ["type"] = "command", ["command"] = line };
            if (isAsync) hook["async"] = true;
            else hook["timeout"] = SessionEndTimeoutSeconds;
            hooks[name] = new JsonArray(new JsonObject { ["hooks"] = new JsonArray(hook) });
        }
        return hooks;
    }

    /// <summary>The settings merged with the block: existing keys and hooks kept, each event's group added once.</summary>
    public static (JsonObject Merged, IReadOnlyList<string> Added) Merge(JsonObject settings, JsonObject block)
    {
        var merged = (JsonObject)settings.DeepClone();
        var hooks = merged["hooks"] as JsonObject ?? new JsonObject();
        merged["hooks"] = hooks;
        var added = new List<string>();
        foreach (var (name, groups) in block)
        {
            var line = ((JsonArray)groups!)[0]!["hooks"]![0]!["command"]!.GetValue<string>();
            var existing = hooks[name] as JsonArray ?? new JsonArray();
            hooks[name] = existing;
            if (Commands(existing).Contains(line)) continue;
            existing.Add(((JsonArray)groups)[0]!.DeepClone());
            added.Add(name);
        }
        return (merged, added);
    }

    /// <summary>The settings with this block's groups removed, and the events they were removed from.</summary>
    public static (JsonObject Cleaned, IReadOnlyList<string> Removed) Remove(JsonObject settings, JsonObject block)
    {
        var cleaned = (JsonObject)settings.DeepClone();
        var removed = new List<string>();
        if (cleaned["hooks"] is not JsonObject hooks) return (cleaned, removed);
        foreach (var (name, groups) in block)
        {
            var line = ((JsonArray)groups!)[0]!["hooks"]![0]!["command"]!.GetValue<string>();
            if (hooks[name] is not JsonArray existing) continue;
            var ours = existing.Where(g => g?["hooks"] is JsonArray hs && hs.Count > 0 && hs.All(h => h?["command"]?.GetValue<string>() == line)).ToList();
            foreach (var group in ours) existing.Remove(group);
            if (ours.Count > 0) removed.Add(name);
            if (existing.Count == 0) hooks.Remove(name);
        }
        if (hooks.Count == 0) cleaned.Remove("hooks");
        return (cleaned, removed);
    }

    /// <summary>
    /// What adding the block changes, without the values of keys the file already has (a settings file holds env vars):
    /// the keys kept, the events added, and the added JSON.
    /// </summary>
    public static string Diff(JsonObject before, JsonObject after, IReadOnlyList<string> added, JsonObject block)
    {
        var lines = new List<string>();
        var kept = before.Select(p => p.Key).Where(k => k != "hooks").ToList();
        lines.Add($"keys kept unchanged ({kept.Count}): {string.Join(", ", kept)}");
        var hadHooks = before["hooks"] is JsonObject h ? h.Select(p => p.Key).ToList() : [];
        lines.Add(hadHooks.Count == 0 ? "hooks before: none" : $"hooks before: {string.Join(", ", hadHooks)} (kept)");
        lines.Add(added.Count == 0 ? "added: nothing (already installed)" : $"added: hooks.{string.Join(", hooks.", added)}");
        var unchanged = kept.All(k => JsonNode.DeepEquals(before[k], after[k]));
        lines.Add($"every existing key byte-for-byte the same after the merge: {(unchanged ? "yes" : "NO")}");
        if (added.Count > 0)
        {
            var addedOnly = new JsonObject();
            foreach (var name in added) addedOnly[name] = block[name]!.DeepClone();
            lines.Add("+ \"hooks\": " + addedOnly.ToJsonString(Pretty).ReplaceLineEndings("\n+ "));
        }
        return string.Join("\n", lines);
    }

    /// <summary>Installs into the settings file (backup first), or with <paramref name="dryRun"/> only reports. Returns the report.</summary>
    public static string Install(string settingsPath, JsonObject block, bool dryRun, DateTimeOffset now)
    {
        var before = Read(settingsPath);
        var (after, added) = Merge(before, block);
        var report = Diff(before, after, added, block);
        if (dryRun || added.Count == 0) return report + (dryRun ? "\n(dry run: nothing written)" : "");
        var backup = Backup(settingsPath, now);
        Write(settingsPath, after);
        return report + $"\nbacked up to {backup}\nwritten {settingsPath}";
    }

    public static string Uninstall(string settingsPath, JsonObject block, bool dryRun, DateTimeOffset now)
    {
        var before = Read(settingsPath);
        var (after, removed) = Remove(before, block);
        var report = removed.Count == 0 ? "removed: nothing (not installed)" : $"removed: hooks.{string.Join(", hooks.", removed)}";
        if (dryRun || removed.Count == 0) return report + (dryRun ? "\n(dry run: nothing written)" : "");
        var backup = Backup(settingsPath, now);
        Write(settingsPath, after);
        return report + $"\nbacked up to {backup}\nwritten {settingsPath}";
    }

    /// <summary>
    /// Publishes the CLI framework-dependent into <c>&lt;home&gt;/bin/app</c> and writes <c>&lt;home&gt;/bin/creatura</c>, a
    /// wrapper that runs it with an absolute path to dotnet (hooks run with Claude Code's environment, not a shell's).
    /// Returns the wrapper's path.
    /// </summary>
    public static string PublishBin(string project, string homeRoot, TextWriter output)
    {
        var dotnet = Dotnet();
        var app = Path.Combine(homeRoot, "bin", "app");
        OwnerOnly.CreateDirectory(homeRoot);
        OwnerOnly.CreateDirectory(Path.Combine(homeRoot, "bin"));
        var start = new ProcessStartInfo(dotnet, ["publish", project, "-c", "Release", "-o", app, "--nologo", "-v", "quiet"])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var process = Process.Start(start) ?? throw new InvalidOperationException("dotnet publish did not start");
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new InvalidOperationException($"dotnet publish failed ({process.ExitCode}): {stderr.Result}{stdout.Result}".Trim());

        var wrapper = Path.Combine(homeRoot, "bin", "creatura");
        var script = $"#!/bin/sh\nexec {ShellQuote(dotnet)} {ShellQuote(Path.Combine(app, "Creatura.Cli.dll"))} \"$@\"\n";
        OwnerOnly.Replace(wrapper, System.Text.Encoding.UTF8.GetBytes(script));
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(wrapper, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        output.WriteLine($"published {app}");
        output.WriteLine($"wrapper   {wrapper} (runs {dotnet})");
        return wrapper;
    }

    /// <summary>The project file of the CLI: the one given, else found by walking up from the current directory.</summary>
    public static string FindProject(string? given)
    {
        if (given is not null) return Path.GetFullPath(given);
        for (var dir = new DirectoryInfo(Directory.GetCurrentDirectory()); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Creatura.Cli", "Creatura.Cli.csproj");
            if (File.Exists(candidate)) return candidate;
        }
        throw new InvalidOperationException("cannot find src/Creatura.Cli/Creatura.Cli.csproj from here: pass --project <path>");
    }

    public static string ShellQuote(string value) =>
        value.Length > 0 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '.' or '-' or '_' or ':' or '~' or '+' or '=' or ',' or '@')
            ? value
            : "'" + value.Replace("'", "'\\''", StringComparison.Ordinal) + "'";

    private static string Dotnet()
    {
        var self = Environment.ProcessPath;
        if (self is not null && Path.GetFileNameWithoutExtension(self).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) return self;
        foreach (var known in new[] { "/usr/local/share/dotnet/dotnet", "/opt/homebrew/bin/dotnet", "/usr/share/dotnet/dotnet", "/usr/bin/dotnet" })
        {
            if (File.Exists(known)) return known;
        }
        return "dotnet";
    }

    private static IEnumerable<string> Commands(JsonArray groups) =>
        groups.OfType<JsonObject>().SelectMany(g => g["hooks"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(h => h["command"]?.GetValue<string>()).OfType<string>();

    private static JsonObject Read(string path) =>
        File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject
              ?? throw new FormatException($"{path} is not a JSON object")
            : new JsonObject();

    private static string Backup(string path, DateTimeOffset now)
    {
        if (!File.Exists(path)) return "(no file to back up)";
        var backup = $"{path}.bak-{now.UtcDateTime.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture)}";
        File.Copy(path, backup, overwrite: false);
        return backup;
    }

    private static void Write(string path, JsonObject settings)
    {
        var text = settings.ToJsonString(Pretty) + "\n";
        var temporary = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllText(temporary, text);
        if (File.Exists(path) && !OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
        File.Move(temporary, path, overwrite: true);
    }
}
