using System.Globalization;
using System.Text.Json;

namespace Creatura.Operator;

/// <summary>One program's work on this computer, as its status file says it (issue #99): what the tray draws.</summary>
/// <param name="Name">The program's name.</param>
/// <param name="Line">What it is doing, one line.</param>
/// <param name="Working">It is working now.</param>
/// <param name="Waiting">How many things of it wait for a person (the badge).</param>
/// <param name="Warning">Its file is stale or cannot be read.</param>
/// <param name="Lines">More lines, each shown under it.</param>
/// <param name="Link">Where a click goes, or null: https anywhere, http on this computer only.</param>
public sealed record ProgramStatus(string Name, string Line, bool Working, int Waiting, bool Warning, IReadOnlyList<string> Lines, Uri? Link);

/// <summary>
/// The status files of other programs: <c>&lt;home&gt;/status/&lt;name&gt;.json</c>, <c>"format": 1</c>, written by the
/// program itself and only read here. Text is drawn as text, nothing in a file is run, and a file that cannot be read is
/// one line, never an exception.
/// </summary>
public static class ProgramStatuses
{
    public const int MaxFiles = 10;
    public const int MaxLines = 10;
    public const int MaxLength = 100;
    public const int MaxBytes = 64 * 1024;

    private static readonly TimeSpan MaxStale = TimeSpan.FromDays(1);

    public static IReadOnlyList<ProgramStatus> Read(string root, DateTimeOffset now)
    {
        List<string> files;
        try
        {
            var folder = Path.Combine(root, "status");
            if (!Directory.Exists(folder)) return [];
            files = [.. Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal).Take(MaxFiles + 1)];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }

        var programs = files.Take(MaxFiles).Select(f => One(f, now)).ToList();
        if (files.Count > MaxFiles) programs.Add(new ProgramStatus("Other programs", $"only the first {MaxFiles} status files are shown", false, 0, false, [], null));
        return programs;
    }

    private static ProgramStatus One(string file, DateTimeOffset now)
    {
        var fallback = Text(Path.GetFileNameWithoutExtension(file)) is { Length: > 0 } n ? n : "program";
        try
        {
            if (Content(file) is not { } content) return Unreadable(fallback, "its status file is too large");
            using var json = JsonDocument.Parse(content, new JsonDocumentOptions { MaxDepth = 8 });
            var o = json.RootElement;
            if (o.ValueKind != JsonValueKind.Object || !(o.TryGetProperty("format", out var f) && f.ValueKind == JsonValueKind.Number && f.TryGetInt32(out var format) && format == 1))
                return Unreadable(fallback, "its status file has a format this tray does not know");
            var name = Field(o, "name") is { Length: > 0 } given ? given : fallback;
            if (!DateTimeOffset.TryParse(Raw(o, "updatedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                return Unreadable(name, "its status file does not say when it was written");
            var link = Link(Raw(o, "link"));
            var stale = Number(o, "staleAfterSeconds") is > 0 and var s ? TimeSpan.FromSeconds(Math.Min(s, MaxStale.TotalSeconds)) : TimeSpan.FromMinutes(3);
            if (now - at > stale)
                return new ProgramStatus(name, $"not answering since {at.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)}", false, 0, true, [], link);
            var state = Field(o, "state");
            var line = Field(o, "line") is { Length: > 0 } said ? said : state;
            var lines = o.TryGetProperty("lines", out var more) && more.ValueKind == JsonValueKind.Array
                ? more.EnumerateArray().Take(MaxLines).Where(x => x.ValueKind == JsonValueKind.String).Select(x => Text(x.GetString())).Where(x => x.Length > 0).ToList()
                : [];
            var waiting = Math.Clamp(Number(o, "waiting"), 0, 999);
            return new ProgramStatus(name, line, state == "working", state == "attention" ? Math.Max(waiting, 1) : waiting, false, lines, link);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or ArgumentException)
        {
            return Unreadable(fallback, "its status file cannot be read");
        }
    }

    /// <summary>The file's bytes, or null when there are more than the limit. Read while its program may be replacing it.</summary>
    private static byte[]? Content(string file)
    {
        using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var buffer = new byte[MaxBytes + 1];
        var read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        return read > MaxBytes ? null : buffer[..read];
    }

    private static ProgramStatus Unreadable(string name, string why) => new(name, why, false, 0, true, [], null);

    private static string? Raw(JsonElement o, string field) => o.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Field(JsonElement o, string field) => Text(Raw(o, field));

    private static int Number(JsonElement o, string field) => o.TryGetProperty(field, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : 0;

    /// <summary>One line of plain text: no control or direction characters, cut to a length.</summary>
    private static string Text(string? value)
    {
        if (value is null) return "";
        var line = string.Concat(value.Take(MaxLength * 4).Select(c => char.IsControl(c) || char.IsSurrogate(c) || char.GetUnicodeCategory(c) is UnicodeCategory.Format
            or UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c));
        line = string.Join(' ', line.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return line.Length > MaxLength ? line[..(MaxLength - 1)] + "…" : line;
    }

    /// <summary>A link a click may open: https, or http on this computer, and never with a user name in it.</summary>
    private static Uri? Link(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.UserInfo.Length == 0
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && (uri.Host is "127.0.0.1" or "localhost"))) ? uri : null;
}
