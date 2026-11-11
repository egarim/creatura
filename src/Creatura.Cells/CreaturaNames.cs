using System.Security.Cryptography;
using System.Text;

namespace Creatura.Cells;

/// <summary>
/// Suggests names for creaturas (issue #52): <c>&lt;role&gt;-&lt;creature&gt;</c>, such as <c>renderer-wyvern</c> or
/// <c>critic-sphinx</c>. The role keeps the name readable; the creature tells two cells with the same role apart. The
/// creature comes from a closed list in this file, picked from a hash of the vessel and the role, so the same vessel
/// suggests the same name every time; a name already taken (a cell in this home, or one the owner names) is skipped. It only suggests: the
/// owner can always pass <c>--creatura &lt;anything&gt;</c>.
/// </summary>
public static class CreaturaNames
{
    /// <summary>Mythical and heraldic creatures: lowercase ASCII letters only, so every name passes the home's rules.</summary>
    public static IReadOnlyList<string> Creatures { get; } =
    [
        "griffin", "wyvern", "sphinx", "manticore", "chimera", "hippogriff", "basilisk", "kirin", "simurgh", "qilin",
        "phoenix", "kraken", "hydra", "pegasus", "unicorn", "cockatrice", "roc", "thunderbird", "kitsune", "tengu",
        "garuda", "lamassu", "minotaur", "centaur", "selkie", "kelpie", "salamander", "harpy", "gorgon", "leviathan",
        "behemoth", "ziz", "baku", "peryton", "amphisbaena", "caladrius", "alicorn", "firebird", "sirin", "alkonost",
        "gamayun", "zmey", "naga", "makara", "bunyip", "tarasque", "drake", "lindworm", "ouroboros", "senmurv",
    ];

    /// <summary>The role as it goes into a name: lowercase letters and digits, anything else one '-', none at the ends.</summary>
    public static string Role(string role)
    {
        var sb = new StringBuilder();
        foreach (var ch in role.Trim().ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }

        var clean = sb.ToString().Trim('-');
        if (clean.Length > 32) clean = clean[..32].TrimEnd('-');
        return clean.Length == 0 ? throw new ArgumentException("a role needs at least one letter or digit, such as renderer or critic") : clean;
    }

    /// <summary>
    /// Up to <paramref name="count"/> names for a creatura with this role on this vessel, none of them in
    /// <paramref name="taken"/> (compared without case). The same arguments give the same names.
    /// </summary>
    public static IReadOnlyList<string> Suggest(string vesselId, string role, IEnumerable<string> taken, int count = 1)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(count, 1);
        var prefix = Role(role);
        var used = new HashSet<string>(taken, StringComparer.OrdinalIgnoreCase);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{vesselId}\n{prefix}"));
        var start = (int)(BitConverter.ToUInt32(hash, 0) % (uint)Creatures.Count);
        var names = new List<string>(count);
        // Every creature once, from the hashed start; when all are taken, the same order again with -2, -3, ...
        for (var round = 1; names.Count < count; round++)
        {
            for (var i = 0; i < Creatures.Count && names.Count < count; i++)
            {
                var name = $"{prefix}-{Creatures[(start + i) % Creatures.Count]}" + (round == 1 ? "" : $"-{round}");
                if (used.Add(name)) names.Add(name);
            }
        }

        return names;
    }

    /// <summary>
    /// The creatura names a home holds a cell for, on any vessel: each <c>cells/&lt;vessel&gt;/&lt;name&gt;.key</c>,
    /// including cells that have not said a hello yet (see <see cref="InLedger"/> for the names the fleet said).
    /// </summary>
    public static IEnumerable<string> InHome(string cellsDirectory) =>
        !Directory.Exists(cellsDirectory) ? [] : Directory.EnumerateFiles(cellsDirectory, "*.key", SearchOption.AllDirectories)
            .Select(Path.GetFileName).OfType<string>()
            .Where(f => !f.EndsWith(".encryption.key", StringComparison.Ordinal))
            .Select(f => f[..^".key".Length]);

    /// <summary>
    /// Each name the ledger's cells said in their hellos, with the creatura ids that said it (a revoked cell left out).
    /// One name with two ids is two creaturas sharing a name; the same id on several vessels is one creatura that moved.
    /// </summary>
    public static IReadOnlyDictionary<string, IReadOnlySet<string>> InLedger(WorkLedger ledger)
    {
        var names = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in ledger.Entries.Where(e => e.Kind == EntryKind.Hello))
        {
            if (ledger.IsRevoked(entry.Cell)) continue;
            var cell = entry.Body<HelloPayload>().Cell;
            if (string.IsNullOrWhiteSpace(cell.Name)) continue;
            if (!names.TryGetValue(cell.Name, out var ids)) names[cell.Name] = ids = new HashSet<string>(StringComparer.Ordinal);
            ids.Add(cell.Creatura.Value);
        }

        return names.ToDictionary(p => p.Key, p => (IReadOnlySet<string>)p.Value, StringComparer.OrdinalIgnoreCase);
    }
}
