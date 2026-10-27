using System.Text.Json;

namespace Creatura.Cells;

public enum PositionKind
{
    /// <summary>A channel in one process: tests, simulations, a command run on its own.</summary>
    InProcess,

    /// <summary>A node on the owner's mesh, by its name and mesh address.</summary>
    Mesh,

    /// <summary>Off the mesh, reached through a relay address.</summary>
    Relay,
}

/// <summary>
/// How a cell is reachable: a fact the cell reports and a peer can check, not configuration. The mesh position is
/// read from the mesh client's own status (<see cref="FromTailscaleStatus"/>); nothing here changes the mesh.
/// </summary>
public sealed record Position(PositionKind Kind)
{
    /// <summary>In process: the operating system's process id.</summary>
    public int? Process { get; init; }

    /// <summary>In process: the channel's name.</summary>
    public string? Channel { get; init; }

    /// <summary>On the mesh: the node's name.</summary>
    public string? Node { get; init; }

    /// <summary>On the mesh: the node's mesh address. On a relay: the relay's address.</summary>
    public string? Address { get; init; }

    public static Position InProcess(string channel) => new(PositionKind.InProcess) { Channel = channel, Process = Environment.ProcessId };

    public static Position Mesh(string node, string address) => new(PositionKind.Mesh) { Node = node, Address = address };

    public static Position Relay(string address) => new(PositionKind.Relay) { Address = address };

    /// <summary>
    /// The mesh position in <c>tailscale status --json</c>: the Self node's host name and first IPv4 mesh address,
    /// when the backend is running. Null for no status, another state or anything it cannot read.
    /// </summary>
    public static Position? FromTailscaleStatus(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (!root.TryGetProperty("BackendState", out var state) || state.GetString() != "Running") return null;
            if (!root.TryGetProperty("Self", out var self)) return null;
            var node = self.TryGetProperty("HostName", out var host) ? host.GetString() : null;
            string? address = null;
            if (self.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array)
            {
                var all = ips.EnumerateArray().Select(ip => ip.GetString()).OfType<string>().ToList();
                address = all.FirstOrDefault(ip => ip.Contains('.')) ?? all.FirstOrDefault();
            }
            return node is null || address is null ? null : Mesh(node, address);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public string Render() => Kind switch
    {
        PositionKind.InProcess => Process is { } pid ? $"in-process (channel {Channel}, pid {pid})" : $"in-process (channel {Channel})",
        PositionKind.Mesh => $"mesh {Node} at {Address}",
        _ => $"relay {Address}",
    };

    public override string ToString() => Render();
}
