namespace Creatura.Worlds.Web;

/// <summary>
/// The controls on screen as the world's tools, rebuilt every tick. A control is a tool with its id; a click is kind
/// <c>click</c>, a field <c>type</c> (needs the text as payload), a list <c>choose</c> (needs the option as payload),
/// and a control the task names as a commit (a save, a send) is kind <c>submit</c> and promises the task's done fact:
/// the one promise in a web world, which only the app's data can keep. <c>back</c> and <c>wait</c> are always there.
/// Nothing requires anything: a person can press any button they see.
/// </summary>
public static class WebTools
{
    public const string Back = "back";

    public const string Wait = "wait";

    public const string SubmitKind = "submit";

    public static readonly Tool BackTool = new(Back, [], [], "navigate");

    public static readonly Tool WaitTool = new(Wait, [], [], "wait");

    public static IReadOnlyList<Tool> For(Observation observation, UxTask task)
    {
        var tools = observation.Controls.Select(c => For(c, task)).ToList();
        tools.Add(BackTool);
        tools.Add(WaitTool);
        return tools;
    }

    public static Tool For(Control control, UxTask task)
    {
        var commit = IsCommit(control, task);
        return control.Kind switch
        {
            ControlKind.Type => new Tool(control.Id, [], commit ? [task.DoneFact] : [], "type") { NeedsPayload = true, PayloadHint = $"the text to type into \"{control.Name}\"" },
            ControlKind.Choose => new Tool(control.Id, [], commit ? [task.DoneFact] : [], "choose") { NeedsPayload = true, PayloadHint = $"one of: {string.Join(" | ", control.Options)}" },
            _ => new Tool(control.Id, [], commit ? [task.DoneFact] : [], commit ? SubmitKind : "click"),
        };
    }

    /// <summary>The task names this control as one that writes: its visible name, whole, without case.</summary>
    public static bool IsCommit(Control control, UxTask task) =>
        task.Commits.Any(name => string.Equals(name.Trim(), control.Name.Trim(), StringComparison.OrdinalIgnoreCase));
}
