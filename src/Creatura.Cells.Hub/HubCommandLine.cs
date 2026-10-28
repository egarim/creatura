using System.Security.Cryptography;

namespace Creatura.Cells.Hub;

/// <summary>What the hub was asked to do: its options, the address it listens on, and where its trust came from.</summary>
public sealed record HubLaunch(HubOptions Options, string Listen, string TrustedFrom);

/// <summary>
/// The hub's command line:
/// <code>creatura-hub --listen &lt;address:port&gt; --trust &lt;owner keys file&gt; --data &lt;dir&gt;</code>
/// <code>creatura-hub --listen &lt;address:port&gt; [--home &lt;dir&gt;]</code>
/// The first is for a hub on a machine that holds no creatura key at all: it trusts the owner public keys in the file
/// and never reads a private key (C19). The second is for the owner's own vessel and trusts the home's owner key.
/// </summary>
public static class HubCommandLine
{
    public const string Usage = """
        usage: creatura-hub --listen <address:port> --trust <owner keys file> --data <dir>
               creatura-hub --listen <address:port> [--home <dir>]
          --listen   the address to bind, the vessel's mesh address (e.g. 100.100.1.5:5181), never every interface
          --trust    owner public keys to trust, one per line (`creatura cell owner-key --out <file>` writes one);
                     the hub then needs no creatura home and no private key
          --data     where the hub keeps hub.db (required with --trust unless --home is given)
          --home     a creatura home: without --trust the hub trusts its owner key; its hub/ folder is the default --data
        """;

    /// <summary>Throws <see cref="ArgumentException"/> with a message for the person when the arguments do not add up.</summary>
    public static HubLaunch Parse(string[] args)
    {
        string? Option(string name) => Array.IndexOf(args, name) is var i and >= 0 && i + 1 < args.Length ? args[i + 1] : null;

        var listen = Option("--listen") ?? throw new ArgumentException("--listen <address:port> is required");
        var trust = Option("--trust");
        var homeRoot = Option("--home");
        var data = Option("--data");

        IReadOnlyList<string> owners;
        string from;
        if (trust is not null)
        {
            owners = ReadTrustFile(trust);
            from = $"{owners.Count} owner key{(owners.Count == 1 ? "" : "s")} from {Path.GetFullPath(trust)}";
            data ??= homeRoot is null ? throw new ArgumentException("--data <dir> is required with --trust (or give --home)") : Path.Combine(new CellHome(homeRoot).Root, "hub");
        }
        else
        {
            var home = new CellHome(homeRoot ?? Environment.GetEnvironmentVariable("CREATURA_HOME"));
            using var owner = home.LoadOwner() ?? throw new ArgumentException($"no owner key under {home.Root}: give --trust <owner keys file> and --data <dir> instead");
            owners = [owner.PublicKey];
            from = $"the owner key of {home.Root}";
            data ??= Path.Combine(home.Root, "hub");
        }

        var database = Path.Combine(Path.GetFullPath(data), "hub.db");
        return new HubLaunch(new HubOptions(database, owners, "http://" + listen), listen, from);
    }

    /// <summary>
    /// The owner public keys in a trust file: one per line, blank lines and lines starting with # left out. Each must
    /// be an ECDSA P-256 public key (base64 SubjectPublicKeyInfo, as the cells write them). Anything else, a private
    /// key above all, refuses the whole file, and the message never repeats what the file holds.
    /// </summary>
    public static IReadOnlyList<string> ReadTrustFile(string path)
    {
        var keys = new List<string>();
        var lineNumber = 0;
        foreach (var raw in File.ReadLines(path))
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.Contains("PRIVATE", StringComparison.OrdinalIgnoreCase) || line.StartsWith("-----", StringComparison.Ordinal))
                throw new ArgumentException($"{path} line {lineNumber} looks like a PEM block, perhaps a private key: a trust file holds public keys only");
            try
            {
                using var key = ECDsa.Create();
                key.ImportSubjectPublicKeyInfo(Convert.FromBase64String(line), out _);
                if (key.KeySize != 256) throw new CryptographicException("not P-256");
            }
            catch (Exception e) when (e is FormatException or CryptographicException)
            {
                throw new ArgumentException($"{path} line {lineNumber} is not an ECDSA P-256 public key");
            }
            if (!keys.Contains(line, StringComparer.Ordinal)) keys.Add(line);
        }
        return keys.Count > 0 ? keys : throw new ArgumentException($"{path} holds no owner public key");
    }
}
