using System.Text;
using System.Text.Json.Serialization;

namespace Creatura.Worlds.Web;

/// <summary>How a control is used: clicked, typed into, or an option chosen from it.</summary>
public enum ControlKind
{
    Click,
    Type,
    Choose,
}

/// <summary>One control as the page script reports it, before ids are given.</summary>
public sealed record RawControl
{
    public int Ref { get; init; }

    public string Role { get; init; } = "button";

    public string Kind { get; init; } = "click";

    public string Tag { get; init; } = "";

    public string Name { get; init; } = "";

    public string Value { get; init; } = "";

    public bool? Checked { get; init; }

    public IReadOnlyList<string> Options { get; init; } = [];

    public bool InViewport { get; init; } = true;

    public bool Covered { get; init; }
}

/// <summary>The page as the script reports it.</summary>
public sealed record RawObservation
{
    public string Url { get; init; } = "";

    public string Title { get; init; } = "";

    public string Text { get; init; } = "";

    public IReadOnlyList<RawControl> Controls { get; init; } = [];
}

/// <summary>
/// One control on screen: its number in the list the persona reads (1-based, viewport first), its id (a slug of role
/// and name, the same for the same control on the next tick, so the control layer's distrust of a control outlives a
/// re-render), and the element reference the world acts on.
/// </summary>
public sealed record Control(int Number, string Id, int Ref, string Role, ControlKind Kind, string Name, string Value, bool? Checked, IReadOnlyList<string> Options, bool InViewport)
{
    /// <summary>Something else is on top of it (a banner, a dialog's veil): it can be seen and not pressed.</summary>
    public bool Covered { get; init; }

    /// <summary>The line the persona reads: <c>3. [button] Save</c>, with the value typed so far or the option chosen.</summary>
    public string Line()
    {
        var sb = new StringBuilder();
        sb.Append(Number).Append(". [").Append(Role).Append("] ").Append(Name.Length == 0 ? "(no label)" : Name);
        if (Kind == ControlKind.Type) sb.Append(Value.Length == 0 ? " (empty)" : $" (contains \"{Value}\")");
        if (Kind == ControlKind.Choose) sb.Append($" (chosen: \"{Value}\"; options: {string.Join(" | ", Options)})");
        if (Checked is { } on) sb.Append(on ? " (checked)" : " (not checked)");
        if (!InViewport) sb.Append(" (below, scroll to reach)");
        if (Covered) sb.Append(" (covered by something on top)");
        return sb.ToString();
    }
}

/// <summary>What the persona sees this tick: the page's address and title, the visible text, and the numbered controls. Nothing else.</summary>
public sealed record Observation(string Url, string Title, string Text, IReadOnlyList<Control> Controls)
{
    public static readonly Observation Empty = new("", "", "", []);

    /// <summary>The path of the address (no host, no query): the <c>page:</c> fact.</summary>
    [JsonIgnore]
    public string Path => Uri.TryCreate(Url, UriKind.Absolute, out var u) ? u.AbsolutePath : Url;

    public Control? ById(string id) => Controls.FirstOrDefault(c => c.Id == id);

    public Control? ByNumber(int number) => Controls.FirstOrDefault(c => c.Number == number);

    /// <summary>The screen as the persona prompt shows it.</summary>
    public string Render()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"SCREEN: {(Title.Length == 0 ? Path : Title)}");
        sb.AppendLine("VISIBLE TEXT:");
        foreach (var line in Text.Split('\n')) sb.AppendLine($"  {line}");
        sb.AppendLine("CONTROLS:");
        if (Controls.Count == 0) sb.AppendLine("  (none)");
        foreach (var c in Controls) sb.AppendLine($"  {c.Line()}");
        return sb.ToString();
    }

    /// <summary>
    /// Ids and numbers for the page script's controls. Numbers follow the script's order (viewport first). The id is
    /// <c>role-name</c> slugged (<c>button-save</c>, <c>textbox-note</c>); a second control with the same slug is
    /// <c>-2</c>, a third <c>-3</c>, in order; a control with no name is its role and its number.
    /// </summary>
    public static Observation From(RawObservation raw)
    {
        var seen = new Dictionary<string, int>(StringComparer.Ordinal);
        var controls = new List<Control>();
        var number = 0;
        foreach (var c in raw.Controls)
        {
            number++;
            var role = Slug(c.Role);
            var name = Slug(c.Name);
            var id = name.Length == 0 ? $"{role}-{number}" : $"{role}-{name}";
            if (seen.TryGetValue(id, out var n)) seen[id] = ++n;
            else seen[id] = n = 1;
            if (n > 1) id = $"{id}-{n}";
            var kind = c.Kind switch { "type" => ControlKind.Type, "choose" => ControlKind.Choose, _ => ControlKind.Click };
            controls.Add(new Control(number, id, c.Ref, c.Role, kind, c.Name, c.Value, c.Checked, c.Options, c.InViewport) { Covered = c.Covered });
        }

        return new Observation(raw.Url, raw.Title, raw.Text, controls);
    }

    /// <summary>Lower-case letters and digits, runs of anything else one dash, at most <paramref name="max"/> characters (40 for an id).</summary>
    public static string Slug(string s, int max = 40)
    {
        var sb = new StringBuilder();
        foreach (var ch in s.ToLowerInvariant())
        {
            if (char.IsAsciiLetterOrDigit(ch)) sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
            if (sb.Length >= max) break;
        }

        return sb.ToString().Trim('-');
    }
}
