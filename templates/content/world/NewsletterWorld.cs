using Creatura;

/// <summary>
/// Send this week's newsletter. A world is facts and tools: each tool declares the facts it requires and the facts it
/// grants, and its kind (read, write, verify, release, close). The control layer offers only the tools whose facts are
/// in place, and checks after every call that the facts the tool promised really appeared.
/// </summary>
public static class NewsletterWorld
{
    /// <summary>Facts true at the start: permission to send.</summary>
    public static readonly string[] Initial = ["permission.send"];

    public static IReadOnlyList<Tool> Tools() =>
    [
        new("write-draft", [], ["draft.written"], "write"),
        new("check-links", ["draft.written"], ["links.checked"], "verify"),
        new("upload-images", ["draft.written"], ["images.uploaded"], "write"),
        new("host-images-elsewhere", ["draft.written"], ["images.uploaded"], "write"),   // a second route to the same fact
        new("get-approval", ["links.checked", "images.uploaded"], ["approved"], "verify"),
        new("send", ["approved", "permission.send"], ["sent"], "release"),
        new("archive", ["sent"], ["archived"], "close"),
    ];

    /// <summary>The steps toward the goal, in order: progress is the share of them done.</summary>
    public static readonly string[] Steps = ["draft.written", "links.checked", "images.uploaded", "approved", "sent", "archived"];

    /// <summary>A fresh world; with <paramref name="lie"/>, "upload-images" reports success and uploads nothing.</summary>
    public static World New(bool lie = false)
    {
        var world = new World(Tools(), Initial);
        if (lie) world.MakeLiar("upload-images");
        return world;
    }

    public static AgentTask Task { get; } = new(
        "send the newsletter",
        "Newsletter sent and archived: draft written, links checked, images uploaded, approved, sent.",
        facts => facts.Contains("archived"),
        world => (double)Steps.Count(world.Has) / Steps.Length,
        world => world.Has("archived") ? 1.0 : 0.0,
        ["upload-images"]);   // the habit: the tool reached for first
}
