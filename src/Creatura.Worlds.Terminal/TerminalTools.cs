namespace Creatura.Worlds.Terminal;

/// <summary>
/// The terminal's tools, the same for every driver and every task. Reading, typing, pressing a key and waiting promise
/// nothing: the screen is the report, so there is no fact to grant. <c>close</c> promises <c>session-closed</c> and
/// <c>commit</c> promises <c>done:&lt;task&gt;</c>, which only the injected independent check may keep. Nothing
/// requires anything except a commit, which requires the facts the task's check implies — a person can do any of the
/// rest at any time.
/// </summary>
public static class TerminalTools
{
    public const string ReadScreen = "read-screen";

    public const string Type = "type";

    public const string Line = "line";

    public const string Key = "key";

    public const string WaitForText = "wait-for-text";

    public const string Close = "close";

    public const string Commit = "commit";

    public static IReadOnlyList<Tool> For(TerminalTask task, TerminalWorldOptions options) =>
    [
        new Tool(ReadScreen, [], [], "observe"),
        new Tool(Type, [], [], "type") { NeedsPayload = true, PayloadHint = "the text to type into the application" },
        new Tool(Line, [], [], "line") { NeedsPayload = true, PayloadHint = "the line to type, then Enter" },
        new Tool(Key, [], [], "key") { NeedsPayload = true, PayloadHint = $"one of: {string.Join(" | ", options.Keys)}" },
        new Tool(WaitForText, [], [], "wait") { NeedsPayload = true, PayloadHint = "the text to wait for on screen (bounded by the world's wait timeout)" },
        new Tool(Close, [], [TerminalFacts.Closed], "close"),
        new Tool(Commit, task.CommitRequires, [task.DoneFact], "commit"),
    ];
}
