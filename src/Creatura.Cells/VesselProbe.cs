using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Creatura.Cells;

/// <summary>
/// The hardware half of a vessel: a <see cref="VesselId"/> derived from the platform's hardware identifier hashed with
/// the owner's salt (the raw identifier is never kept), and what the machine is. The model half stays the Core's
/// <see cref="Vessel"/>, unchanged.
/// </summary>
public sealed record Hardware(string VesselId)
{
    public string? Chip { get; init; }

    public int? Cores { get; init; }

    public long? MemoryBytes { get; init; }

    public string? Os { get; init; }

    public string? OsVersion { get; init; }

    public string? Architecture { get; init; }

    public string? HostName { get; init; }

    /// <summary>The machine on one line: chip, cores, memory, OS, architecture, host. Only what is known.</summary>
    public string Render()
    {
        var parts = new List<string>();
        if (Chip is not null) parts.Add(Chip);
        if (Cores is { } cores) parts.Add($"{cores} cores");
        if (MemoryBytes is { } bytes) parts.Add((bytes / (1024.0 * 1024 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " GB");
        var os = string.Join(" ", new[] { Os, OsVersion }.Where(s => !string.IsNullOrWhiteSpace(s)));
        if (os.Length > 0) parts.Add(Architecture is null ? os : $"{os} ({Architecture})");
        else if (Architecture is not null) parts.Add(Architecture);
        if (HostName is not null) parts.Add($"host {HostName}");
        return parts.Count == 0 ? VesselId : string.Join(", ", parts);
    }

    public override string ToString() => $"{VesselId}: {Render()}";
}

/// <summary>
/// Where a vessel's facts come from. <see cref="PlatformVesselSource"/> reads this machine; a test gives a fixed one.
/// <see cref="RawIdentifier"/> is read by <see cref="VesselProbe"/> alone, which hashes it and drops it.
/// </summary>
public interface IVesselSource
{
    string? RawIdentifier();

    string? Chip();

    int? Cores();

    long? MemoryBytes();

    string? Os();

    string? OsVersion();

    string? Architecture();

    string? HostName();

    /// <summary>The mesh's own status as JSON (<c>tailscale status --json</c>), or null when there is no mesh client or it is not running.</summary>
    string? MeshStatusJson();
}

/// <summary>
/// Turns a vessel source into <see cref="Hardware"/> and a mesh <see cref="Position"/>. The raw hardware identifier
/// is hashed here with the owner's salt (HMAC-SHA256) and goes no further: nothing it returns, logs or throws
/// carries it.
/// </summary>
public sealed class VesselProbe
{
    private readonly byte[] _salt;

    public VesselProbe(byte[] ownerSalt, IVesselSource? source = null)
    {
        if (ownerSalt.Length < 16) throw new ArgumentException("the owner's salt is shorter than 16 bytes", nameof(ownerSalt));
        _salt = ownerSalt;
        Source = source ?? new PlatformVesselSource();
    }

    public IVesselSource Source { get; }

    public Hardware Probe()
    {
        var raw = Source.RawIdentifier();
        if (string.IsNullOrWhiteSpace(raw)) throw new InvalidOperationException("this platform gave no hardware identifier to derive a vessel id from");
        return new Hardware(VesselIdOf(_salt, raw))
        {
            Chip = Source.Chip(),
            Cores = Source.Cores(),
            MemoryBytes = Source.MemoryBytes(),
            Os = Source.Os(),
            OsVersion = Source.OsVersion(),
            Architecture = Source.Architecture(),
            HostName = Source.HostName(),
        };
    }

    /// <summary>The cell's mesh position from the mesh client's own status, or null when there is none to read.</summary>
    public Position? ProbeMesh() => Position.FromTailscaleStatus(Source.MeshStatusJson());

    /// <summary>"v-" and the first 24 hex digits of HMAC-SHA256(salt, the identifier trimmed and lower-cased).</summary>
    public static string VesselIdOf(byte[] salt, string rawIdentifier)
    {
        var mac = HMACSHA256.HashData(salt, Encoding.UTF8.GetBytes("creatura vessel v1:" + rawIdentifier.Trim().ToLowerInvariant()));
        return "v-" + Convert.ToHexStringLower(mac)[..24];
    }
}

/// <summary>
/// This machine, read per platform behind a runtime check: macOS (<c>ioreg</c>, <c>sysctl</c>, <c>sw_vers</c>), Linux
/// (<c>/etc/machine-id</c>, <c>/proc</c>, <c>/etc/os-release</c>), Windows (the registry's <c>MachineGuid</c>). Every
/// read is best effort: a fact it cannot get is null. Command output is parsed in memory and never put in an
/// exception or a log.
/// </summary>
public sealed class PlatformVesselSource : IVesselSource
{
    public string? RawIdentifier()
    {
        if (OperatingSystem.IsMacOS())
        {
            var match = Regex.Match(Run("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice") ?? "", "\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"");
            return match.Success ? match.Groups[1].Value : null;
        }

        if (OperatingSystem.IsLinux())
        {
            return new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" }.Select(ReadTrimmed).FirstOrDefault(s => !string.IsNullOrEmpty(s));
        }

        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
            return key?.GetValue("MachineGuid") as string;
        }

        return null;
    }

    public string? Chip()
    {
        if (OperatingSystem.IsMacOS()) return Run("/usr/sbin/sysctl", "-n machdep.cpu.brand_string")?.Trim() is { Length: > 0 } brand ? brand : null;
        if (OperatingSystem.IsLinux()) return CpuInfo("model name") ?? CpuInfo("Hardware") ?? CpuInfo("Model");
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            return (key?.GetValue("ProcessorNameString") as string)?.Trim();
        }
        return null;
    }

    public int? Cores()
    {
        if (OperatingSystem.IsMacOS() && int.TryParse(Run("/usr/sbin/sysctl", "-n hw.ncpu")?.Trim(), out var cores)) return cores;
        return Environment.ProcessorCount;
    }

    public long? MemoryBytes()
    {
        if (OperatingSystem.IsMacOS() && long.TryParse(Run("/usr/sbin/sysctl", "-n hw.memsize")?.Trim(), out var bytes)) return bytes;
        if (OperatingSystem.IsLinux())
        {
            var line = SafeLines("/proc/meminfo").FirstOrDefault(l => l.StartsWith("MemTotal:", StringComparison.Ordinal));
            var match = Regex.Match(line ?? "", @"(\d+)\s*kB");
            if (match.Success) return long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 1024;
        }
        var total = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
        return total > 0 ? total : null;
    }

    public string? Os()
    {
        if (OperatingSystem.IsMacOS()) return Run("/usr/bin/sw_vers", "-productName")?.Trim() ?? "macOS";
        if (OperatingSystem.IsLinux()) return OsRelease("NAME") ?? "Linux";
        if (OperatingSystem.IsWindows()) return "Windows";
        return RuntimeInformation.OSDescription;
    }

    public string? OsVersion()
    {
        if (OperatingSystem.IsMacOS())
        {
            var version = Run("/usr/bin/sw_vers", "-productVersion")?.Trim();
            var build = Run("/usr/bin/sw_vers", "-buildVersion")?.Trim();
            return string.IsNullOrEmpty(build) ? version : $"{version} ({build})";
        }
        if (OperatingSystem.IsLinux()) return OsRelease("VERSION_ID") ?? Environment.OSVersion.Version.ToString();
        return Environment.OSVersion.Version.ToString();
    }

    public string? Architecture() => RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant();

    public string? HostName()
    {
        var name = Environment.MachineName;
        return string.IsNullOrWhiteSpace(name) ? null : name;
    }

    public string? MeshStatusJson()
    {
        var tailscale = FindTailscale();
        return tailscale is null ? null : Run(tailscale, "status --json", timeoutMs: 4000);
    }

    private static string? FindTailscale()
    {
        var names = OperatingSystem.IsWindows() ? new[] { "tailscale.exe" } : ["tailscale"];
        var path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        var known = OperatingSystem.IsMacOS()
            ? new[] { "/opt/homebrew/bin/tailscale", "/usr/local/bin/tailscale", "/Applications/Tailscale.app/Contents/MacOS/Tailscale" }
            : OperatingSystem.IsWindows() ? [@"C:\Program Files\Tailscale\tailscale.exe"] : ["/usr/bin/tailscale", "/usr/local/bin/tailscale"];
        return path.SelectMany(dir => names.Select(n => System.IO.Path.Combine(dir, n))).Concat(known).FirstOrDefault(File.Exists);
    }

    private static string? CpuInfo(string field)
    {
        var line = SafeLines("/proc/cpuinfo").FirstOrDefault(l => l.StartsWith(field, StringComparison.Ordinal) && l.Contains(':'));
        return line?[(line.IndexOf(':') + 1)..].Trim() is { Length: > 0 } value ? value : null;
    }

    private static string? OsRelease(string field)
    {
        var line = SafeLines("/etc/os-release").FirstOrDefault(l => l.StartsWith(field + "=", StringComparison.Ordinal));
        return line?[(field.Length + 1)..].Trim().Trim('"') is { Length: > 0 } value ? value : null;
    }

    private static string? ReadTrimmed(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path).Trim() : null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> SafeLines(string path)
    {
        try { return File.Exists(path) ? File.ReadAllLines(path) : []; }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    /// <summary>A command's standard output, or null when it cannot start, fails or takes longer than the timeout.</summary>
    private static string? Run(string file, string arguments, int timeoutMs = 5000)
    {
        try
        {
            var info = new ProcessStartInfo(file, arguments)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(timeoutMs))
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                return null;
            }
            return process.ExitCode == 0 ? output.GetAwaiter().GetResult() : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return null;
        }
    }
}
