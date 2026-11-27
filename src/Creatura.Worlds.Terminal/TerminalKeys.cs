namespace Creatura.Worlds.Terminal;

/// <summary>
/// The runtime's key names and the bytes each stands for (its <c>keys</c> table, <c>src/server.js</c>), so a key is
/// sent as the same input <c>terminal.type</c> would send. The vendored C# client exposes no <c>terminal.key</c>
/// call; the runtime's method is the same <c>input</c> with one of these sequences, so nudging the client is not
/// needed to press a key. A key the table does not name is refused before anything is sent.
/// </summary>
public static class TerminalKeys
{
    private static readonly Dictionary<string, string> Bytes = new(StringComparer.Ordinal)
    {
        ["Enter"] = "\r",
        ["Escape"] = "\x1b",
        ["Tab"] = "\t",
        ["Backspace"] = "\x7f",
        ["Up"] = "\x1b[A",
        ["Down"] = "\x1b[B",
        ["Right"] = "\x1b[C",
        ["Left"] = "\x1b[D",
        ["CtrlC"] = "\x03",
    };

    /// <summary>The keys the runtime knows, in the order its table lists them.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. Bytes.Keys];

    public static bool Known(string name) => Bytes.ContainsKey(name);

    public static string Sequence(string name) => Bytes.TryGetValue(name, out var sequence)
        ? sequence
        : throw new ArgumentException($"the runtime has no key named \"{name}\"; its keys are {string.Join(", ", Names)}", nameof(name));
}
