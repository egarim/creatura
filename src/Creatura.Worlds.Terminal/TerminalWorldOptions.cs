using Sentiora.Client;

namespace Creatura.Worlds.Terminal;

/// <summary>
/// How a terminal world is opened and bounded: how long one action may take, how long a settle and a wait may take,
/// how much text the world keeps, which keys a persona may press, which screen prompts mean a person is required, and
/// the runtime it drives. The shape follows <see cref="Creatura.Worlds.Web.WebWorldOptions"/>: a driver, a client and
/// a session can be injected, so a matrix can open several worlds on one host, and a simulated driver needs no host at
/// all. <see cref="Problems"/> names every unusable value at once; <see cref="TerminalWorld.OpenAsync"/> refuses to
/// start on any of them.
/// </summary>
public sealed record TerminalWorldOptions
{
    /// <summary>The prompts this world will not answer: seeing one on screen is a person's turn, not the persona's.</summary>
    public static readonly IReadOnlyList<string> DefaultApprovalPrompts =
    [
        "(y/n)",
        "[y/N]",
        "[Y/n]",
        "Do you want to proceed?",
        "Are you sure?",
        "Press Y to continue",
    ];

    /// <summary>How long one read or input may take before it is a failed action.</summary>
    public int ActionTimeoutMs { get; init; } = 5000;

    /// <summary>How long the world waits for the revision to move after input before it reads the screen as it is.</summary>
    public int SettleMs { get; init; } = 300;

    /// <summary>How long a <c>wait-for-text</c> may take before it fails, saying what it waited for.</summary>
    public int WaitTimeoutMs { get; init; } = 20000;

    /// <summary>The screen is cut here: the persona's text budget in characters.</summary>
    public int MaxText { get; init; } = 2000;

    /// <summary>The keys a persona may press; every one must be a runtime key name (<see cref="TerminalKeys"/>).</summary>
    public IReadOnlyList<string> Keys { get; init; } = TerminalKeys.Names;

    /// <summary>Screen text that means a person must answer (a prompt, an approval); empty disables the handoff.</summary>
    public IReadOnlyList<string> ApprovalPrompts { get; init; } = DefaultApprovalPrompts;

    /// <summary>A driver that speaks for a real or scripted terminal; one is built from the options below when null.</summary>
    public ITerminalDriver? Driver { get; init; }

    /// <summary>A runtime already opened; the world uses it, creates its own session on it, and does not dispose it.</summary>
    public SentioraClient? Client { get; init; }

    /// <summary>A session already created; the world uses it and does not close it.</summary>
    public SessionHandle? Session { get; init; }

    /// <summary>The runtime's server entry point, started as <c>node ServerPath</c>.</summary>
    public string? ServerPath { get; init; }

    /// <summary>The node executable; <c>node</c> from <c>PATH</c> when null.</summary>
    public string? NodeExecutable { get; init; }

    /// <summary>The runtime's working directory; the current directory when null.</summary>
    public string? WorkingDirectory { get; init; }

    /// <summary>What the world's own session spawns when no session is injected.</summary>
    public CreateSessionRequest? Create { get; init; }

    /// <summary>Every reason these options cannot open a world, in one list; empty when they can.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        if (ActionTimeoutMs <= 0) problems.Add("ActionTimeoutMs must be positive");
        if (SettleMs < 0) problems.Add("SettleMs must not be negative");
        if (WaitTimeoutMs <= 0) problems.Add("WaitTimeoutMs must be positive");
        if (MaxText <= 0) problems.Add("MaxText must be positive");
        if (Keys.Count == 0) problems.Add("Keys must name at least one key");
        if (Keys.Distinct(StringComparer.Ordinal).Count() != Keys.Count) problems.Add("Keys names a key twice");
        foreach (var key in Keys.Where(k => !TerminalKeys.Known(k))) problems.Add($"Keys names \"{key}\", which the runtime does not have");
        if (ApprovalPrompts.Any(string.IsNullOrWhiteSpace)) problems.Add("ApprovalPrompts must not contain an empty prompt");
        if (Create is { } create && string.IsNullOrWhiteSpace(create.Command)) problems.Add("Create.Command must name the program to spawn");

        if (Driver is null)
        {
            if (Client is null && string.IsNullOrWhiteSpace(ServerPath)) problems.Add("no runtime: set ServerPath, inject a Client, or inject a Driver");
            if (Session is null && Create is null) problems.Add("no session to create: set Create (the command the terminal spawns), or inject a Session");
            if (Session is not null && Client is null) problems.Add("a Session belongs to a Client: inject both, or neither");
        }

        return problems;
    }
}
