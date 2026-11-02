using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Creatura.Worlds.Web;

/// <summary>The recording's first line: which cell this is, on what screen, with which barriers.</summary>
public sealed record UxRecordingHeader(string App, string Task, string Persona, string Environment, string Tester, string Design)
{
    public string Type { get; init; } = "header";

    public string Format { get; init; } = "creatura.web-replay/1";

    public DateTimeOffset At { get; init; } = DateTimeOffset.UtcNow;

    public int Width { get; init; }

    public int Height { get; init; }

    public bool Mobile { get; init; }

    public IReadOnlyList<string> Barriers { get; init; } = [];

    /// <summary>The task in the persona's words, as the seat was told it.</summary>
    public string? Prompt { get; init; }
}

/// <summary>
/// A web run written down as it happens, to be watched back (docs/design-web-worlds.md, record and replay): one folder
/// per cell, holding <c>replay.jsonl</c> and a JPEG of the viewport for every screen. The lines, in order: the header; a
/// <c>frame</c> for every screen the persona saw (frame 0 is the start page), with the visible text and the numbered
/// controls as the persona read them, the data's check then, and the image's file name; a <c>tick</c> for every action,
/// naming the frame it was decided on (<c>saw</c>), the persona's answer in its own words, the control pressed and what
/// was typed, the outcome, the control layer's verdict, whether the world told a lie on it, and the frame it led to
/// (<c>now</c>); then every trace row and note; then <c>end</c>, the cell's own numbers. A reader must ignore what it
/// does not know, so later versions can add to it.
/// <para>
/// It is an extension, so it rides every design unchanged. A frame holds what the persona saw and nothing else: a
/// secret field (a password) is in it as "(filled)", never its value, and nothing typed into one is written; the
/// sign-in happens before the world opens, so no sign-in page is a frame; no model call is in it. Recording is the
/// owner's choice (<c>--record</c>): a screen of a real app shows people's data, and a recording stays out of the
/// repository.
/// </para>
/// </summary>
public sealed class UxRecorder : IAgentExtension
{
    public const string FileName = "replay.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Func<Observation> _screen;
    private readonly Func<byte[]?> _shot;
    private readonly Func<CheckResult> _check;
    private readonly Func<IReadOnlyList<WebLie>> _lies;
    private readonly Func<string?> _payload;
    private Observation? _lastScreen;
    private int _frames;
    private bool _finished;

    /// <summary>The recorder over a web world.</summary>
    public UxRecorder(string folder, WebWorld world, UxRecordingHeader header)
        : this(folder, () => world.Current, world.Screenshot, () => world.LastCheck, () => world.Lies, () => world.LastAct?.Payload, header)
    {
    }

    /// <param name="folder">Where the recording goes; made if missing, emptied of an earlier recording.</param>
    /// <param name="screen">The screen as the persona reads it now.</param>
    /// <param name="shot">The screen as an image now (JPEG), or null when none can be taken.</param>
    /// <param name="check">The data's last check.</param>
    /// <param name="lies">The lies the world has told so far, by tick.</param>
    /// <param name="payload">What the last action handed the world: the text typed or the option chosen.</param>
    public UxRecorder(string folder, Func<Observation> screen, Func<byte[]?> shot, Func<CheckResult> check, Func<IReadOnlyList<WebLie>> lies, Func<string?> payload, UxRecordingHeader header)
    {
        Folder = folder;
        _screen = screen;
        _shot = shot;
        _check = check;
        _lies = lies;
        _payload = payload;
        Directory.CreateDirectory(folder);
        if (File.Exists(Path)) File.Delete(Path);
        foreach (var old in Directory.EnumerateFiles(folder, "tick-*.jpg")) File.Delete(old);
        Line(header);
    }

    public string Name => "ux:recorder";

    public string Folder { get; }

    /// <summary>The recording's own file, <c>replay.jsonl</c>, in the folder.</summary>
    public string Path => System.IO.Path.Combine(Folder, FileName);

    /// <summary>How many screens were written: one more than the actions that reached the world.</summary>
    public int Frames => _frames;

    /// <summary>How many images were taken: fewer than the frames when a screen could not be captured.</summary>
    public int Images { get; private set; }

    public void BeforeRun(TickContext context) => Frame(0);

    public void AfterAction(TickContext context, Tool tool, ActionOutcome outcome, ActionVerdict verdict)
    {
        var saw = _frames - 1;
        var control = _lastScreen?.ById(tool.Id);
        var now = Frame(context.Tick);
        Line(new
        {
            type = "tick",
            tick = context.Tick,
            saw,
            action = new
            {
                tool = tool.Id,
                kind = tool.Kind,
                name = control?.Name,
                number = control?.Number,
                content = control is { Secret: true } ? null : _payload(),
            },
            answer = Answer(context, tool),
            outcome = new { succeeded = outcome.Succeeded, observation = outcome.Observation },
            verdict = verdict.ToString().ToLowerInvariant(),
            lie = _lies().Any(l => l.Tick == context.Tick && l.Tool == tool.Id),
            now,
        });
    }

    /// <summary>The persona's answer in its own words: the decision row the design wrote for this tick, before the action.</summary>
    private static string Answer(TickContext context, Tool tool)
    {
        for (var i = context.Trace.Count - 1; i >= 0; i--)
        {
            var row = context.Trace[i];
            if (row.Tick != context.Tick) break;
            if (row.Action == tool.Id && row.Kind == TraceKind.Acted && row.Level != "cell") return row.Detail;
        }

        return "";
    }

    /// <summary>Writes the screen now as the next frame; returns its number.</summary>
    private int Frame(int tick)
    {
        var screen = _screen();
        _lastScreen = screen;
        var n = _frames++;
        string? image = null;
        if (_shot() is { Length: > 0 } bytes)
        {
            image = string.Create(CultureInfo.InvariantCulture, $"tick-{n}.jpg");
            File.WriteAllBytes(System.IO.Path.Combine(Folder, image), bytes);
            Images++;
        }

        var check = _check();
        Line(new
        {
            type = "frame",
            frame = n,
            tick,
            image,
            screen = ScreenOf(screen),
            check = new { done = check.Done, detail = check.Detail },
        });
        return n;
    }

    /// <summary>The screen as a frame keeps it: what the persona read, with a secret field's value left out.</summary>
    public static object ScreenOf(Observation screen) => new
    {
        url = screen.Url,
        title = screen.Title,
        path = screen.Path,
        text = screen.Text,
        controls = screen.Controls.Select(c => new
        {
            number = c.Number,
            id = c.Id,
            role = c.Role,
            kind = c.Kind.ToString().ToLowerInvariant(),
            name = c.Name,
            value = c.Secret ? (c.Value.Length == 0 ? "" : "(filled)") : c.Value,
            @checked = c.Checked,
            options = c.Options.Count == 0 ? null : c.Options,
            inViewport = c.InViewport,
            covered = c.Covered,
            secret = c.Secret ? true : (bool?)null,
            line = c.Line(),
        }).ToList(),
    };

    /// <summary>Writes the trace rows, the notes and the cell's end line; after it the recording is complete.</summary>
    public void Finish(UxCell cell)
    {
        if (_finished) return;
        _finished = true;
        if (cell.Run is { } run)
        {
            foreach (var row in run.Trace)
                Line(new { type = "row", tick = row.Tick, level = row.Level, setPoint = row.SetPoint, stress = row.Stress, freedom = row.Freedom, action = row.Action, kind = row.Kind.ToString().ToLowerInvariant(), detail = row.Detail });
            foreach (var note in run.Notes) Line(new { type = "note", text = note });
        }

        Line(new
        {
            type = "end",
            frames = _frames,
            images = Images,
            ticks = cell.Ticks,
            dataDone = cell.DataDone,
            checkDetail = cell.CheckDetail,
            pageSaidDone = cell.PageSaidDone,
            believedDoneAt = cell.BelievedDoneAt,
            liesTold = cell.LiesTold,
            liesCaught = cell.LiesCaught,
            inputLost = cell.InputLost,
            escalations = cell.Escalations,
            cost = cell.Cost,
            error = cell.Error,
        });
    }

    private void Line(object value) => File.AppendAllText(Path, JsonSerializer.Serialize(value, Json) + "\n");

    /// <summary>The folder a cell's recording goes in under <paramref name="root"/>: the cell's key slugged, as its trace file is named.</summary>
    public static string FolderFor(string root, string task, string persona, string environment, string tester, string design) =>
        System.IO.Path.Combine(root, Observation.Slug($"{task}-{persona}-{environment}-{tester}-{design}", max: 200));
}
