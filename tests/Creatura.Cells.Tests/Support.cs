using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Creatura.Cells.Tests;

/// <summary>A temporary directory deleted on dispose: every test home lives in one, never in the real <c>~/.creatura</c>.</summary>
public sealed class TempHome : IDisposable
{
    public TempHome() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "creatura-cells-" + Guid.NewGuid().ToString("N")[..12]);

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A vessel with a known raw identifier, so a test can move a creatura between two of them and look for the identifier afterwards.</summary>
public sealed class FakeVessel(string rawIdentifier, string host = "fake-host") : IVesselSource
{
    public string? RawIdentifier() => rawIdentifier;

    public string? Chip() => "Test Chip";

    public int? Cores() => 4;

    public long? MemoryBytes() => 8L * 1024 * 1024 * 1024;

    public string? Os() => "TestOS";

    public string? OsVersion() => "1.0";

    public string? Architecture() => "arm64";

    public string? HostName() => host;

    public string? MeshStatusJson() => null;
}

/// <summary>
/// The platform's raw hardware identifier, read here independently of <see cref="VesselProbe"/> so C4 can look for
/// it. Held in memory only; a test compares against it and never prints it.
/// </summary>
public static class RawIdentifier
{
    public static string? Read()
    {
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                var output = Run("/usr/sbin/ioreg", "-rd1 -c IOPlatformExpertDevice");
                var match = Regex.Match(output ?? "", "\"IOPlatformUUID\"\\s*=\\s*\"([^\"]+)\"");
                return match.Success ? match.Groups[1].Value : null;
            }

            if (OperatingSystem.IsLinux())
            {
                foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                {
                    if (File.Exists(path)) return File.ReadAllText(path).Trim();
                }
                return null;
            }

            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return key?.GetValue("MachineGuid") as string;
            }
        }
        catch (Exception) when (!Debugger.IsAttached)
        {
        }

        return null;
    }

    /// <summary>The forms the identifier could be written in: as read, lower and upper case, with and without dashes.</summary>
    public static IReadOnlyList<string> Forms(string raw)
    {
        var bare = raw.Replace("-", "", StringComparison.Ordinal);
        return new[] { raw, raw.ToLowerInvariant(), raw.ToUpperInvariant(), bare, bare.ToLowerInvariant(), bare.ToUpperInvariant() }
            .Distinct(StringComparer.Ordinal).ToList();
    }

    private static string? Run(string file, string arguments)
    {
        var info = new ProcessStartInfo(file, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        using var process = Process.Start(info);
        if (process is null) return null;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        return output;
    }
}
