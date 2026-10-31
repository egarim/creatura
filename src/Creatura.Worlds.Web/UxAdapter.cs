using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Creatura.Worlds.Web;

/// <summary>
/// What an app supplies to be a web world (docs/design-web-worlds.md): who uses it (personas), on what (environments),
/// what they try to do (tasks, each with the honest check that reads the app's own data), the limits, and the barriers
/// that can be put between the browser and the app. Read from JSON, as the model catalog reads <c>models.json</c>:
/// names in camelCase, comments and trailing commas allowed. A code hook (<see cref="IUxHooks"/>) is the escape hatch
/// for what a declarative check cannot say.
/// </summary>
public sealed record UxAdapter
{
    /// <summary>The app's name, for the report.</summary>
    public required string App { get; init; }

    /// <summary>Where the app is served; a task's start and a check's url are resolved against it. A <c>--base-url</c> overrides it.</summary>
    public string BaseUrl { get; init; } = "";

    public IReadOnlyList<UxPersona> Personas { get; init; } = [];

    public IReadOnlyList<UxEnvironment> Environments { get; init; } = [];

    public IReadOnlyList<UxTask> Tasks { get; init; } = [];

    public UxLimits Limits { get; init; } = new();

    /// <summary>The barriers this app can be run with; none is on unless a run names it.</summary>
    public IReadOnlyList<UxBarrier> Barriers { get; init; } = [];

    /// <summary>The accounts tasks sign in as; their credentials are <c>env:NAME</c>, read at sign-in, or a test tenant's literals.</summary>
    public IReadOnlyList<UxAccount> Accounts { get; init; } = [];

    /// <summary>How an account signs in, declared; <see cref="IUxHooks.SignInAsync"/> is the escape hatch. Needed when a task names an account.</summary>
    public UxSignIn? SignIn { get; init; }

    /// <summary>The CI gate's thresholds for a matrix of this app (<c>creatura ux gate</c>); none when absent.</summary>
    public UxGate? Gate { get; init; }

    /// <summary>Visible text that means the page itself reports a failure (an error banner): an action after which any of it appears is reported failed, honestly.</summary>
    public IReadOnlyList<string> ErrorText { get; init; } = [];

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.KebabCaseLower) },
        WriteIndented = true,
    };

    public static UxAdapter Parse(string json)
    {
        var adapter = JsonSerializer.Deserialize<UxAdapter>(json, Json) ?? throw new FormatException("the adapter is empty");
        adapter.Validate();
        return adapter;
    }

    public static UxAdapter Load(string path) => Parse(File.ReadAllText(path));

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Throws with every problem found, in one message: what a person fixing the file needs.</summary>
    public void Validate()
    {
        var problems = new List<string>();
        if (string.IsNullOrWhiteSpace(App)) problems.Add("app is empty");
        if (Personas.Count == 0) problems.Add("no personas");
        if (Environments.Count == 0) problems.Add("no environments");
        if (Tasks.Count == 0) problems.Add("no tasks");
        Dupes(Personas.Select(p => p.Id), "persona", problems);
        Dupes(Environments.Select(e => e.Id), "environment", problems);
        Dupes(Tasks.Select(t => t.Id), "task", problems);
        Dupes(Barriers.Select(b => b.Id), "barrier", problems);
        foreach (var e in Environments.Where(e => e.Width < 200 || e.Height < 200)) problems.Add($"environment {e.Id}: {e.Width}x{e.Height} is too small");
        foreach (var t in Tasks)
        {
            if (string.IsNullOrWhiteSpace(t.Prompt)) problems.Add($"task {t.Id}: no prompt");
            if (t.Check is null) problems.Add($"task {t.Id}: no check (the app's own data is the only judge of done)");
            else if (t.Check.Problem() is { } p) problems.Add($"task {t.Id}: check {p}");
        }

        foreach (var b in Barriers.Where(b => string.IsNullOrWhiteSpace(b.Url))) problems.Add($"barrier {b.Id}: no url pattern");
        Dupes(Accounts.Select(a => a.Id), "account", problems);
        foreach (var t in Tasks.Where(t => t.Account is not null && Accounts.All(a => a.Id != t.Account))) problems.Add($"task {t.Id}: no account '{t.Account}'");
        if (SignIn is { } signIn)
        {
            if (signIn.Steps.Count == 0) problems.Add("signIn has no steps");
            if (string.IsNullOrWhiteSpace(signIn.SignedInText)) problems.Add("signIn has no signedInText (how a signed-in page is told apart)");
        }
        if (Gate?.MinDone is < 0 or > 1) problems.Add("gate minDone is a share, from 0 to 1");
        if (problems.Count > 0) throw new FormatException("the adapter is not valid: " + string.Join("; ", problems));
    }

    private static void Dupes(IEnumerable<string> ids, string what, List<string> problems)
    {
        foreach (var id in ids.GroupBy(i => i, StringComparer.Ordinal).Where(g => g.Count() > 1)) problems.Add($"{what} {id.Key} twice");
    }

    public UxPersona Persona(string? id) => Find(Personas, p => p.Id, id, "persona");

    public UxEnvironment Environment(string? id) => Find(Environments, e => e.Id, id, "environment");

    public UxTask Task(string? id) => Find(Tasks, t => t.Id, id, "task");

    public UxBarrier Barrier(string id) => Find(Barriers, b => b.Id, id, "barrier");

    public UxAccount Account(string id) => Find(Accounts, a => a.Id, id, "account");

    private static T Find<T>(IReadOnlyList<T> items, Func<T, string> key, string? id, string what) =>
        id is null ? items[0]
        : items.FirstOrDefault(i => key(i) == id) ?? throw new ArgumentException($"no {what} '{id}'; known: {string.Join(", ", items.Select(key))}");

    /// <summary>An absolute url: a relative one resolved against <see cref="BaseUrl"/>.</summary>
    public string Resolve(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" ? abs.ToString()
        : new Uri(new Uri(BaseUrl.EndsWith('/') ? BaseUrl : BaseUrl + "/"), url.TrimStart('/')).ToString();
}

/// <summary>
/// Who a task signs in as. <see cref="User"/> and <see cref="Password"/> are <c>env:NAME</c> (read when the account signs
/// in, never written anywhere) or, for a test tenant, the value itself.
/// </summary>
public sealed record UxAccount(string Id, string User, string Password)
{
    public string UserValue => Http.Environment(User);

    public string PasswordValue => Http.Environment(Password);
}

/// <summary>
/// The sign-in, declared: open <see cref="Start"/>, take <see cref="Steps"/> (the offline script's lines, with
/// <c>{user}</c> and <c>{password}</c> filled from the account), and the account is signed in when a page shows
/// <see cref="SignedInText"/>. Done once per account; the browser's storage state (cookies, localStorage) is kept and every
/// later context for the account opens with it. <see cref="SessionStorage"/> keeps the signed-in origin's sessionStorage too
/// and puts it back before the app's scripts run, which Playwright's storage state does not (the Singular starter keeps its
/// Keycloak refresh token there).
/// </summary>
public sealed record UxSignIn
{
    public string Start { get; init; } = "/login";

    public IReadOnlyList<string> Steps { get; init; } = [];

    public string SignedInText { get; init; } = "";

    public bool SessionStorage { get; init; } = true;

    /// <summary>How long a sign-in may take to show <see cref="SignedInText"/>.</summary>
    public int TimeoutMs { get; init; } = 15000;
}

/// <summary>A tester: how the person behaves, never what the app looks like.</summary>
public sealed record UxPersona(string Id, string Description);

/// <summary>A device: the viewport, and whether it is a phone or tablet (touch, mobile layout).</summary>
public sealed record UxEnvironment(string Id, int Width, int Height, bool Mobile = false);

/// <summary>
/// One thing a person tries to do. <see cref="Prompt"/> is what the person knows and wants, in their words, never
/// the names of screens or buttons. <see cref="Check"/> is the only judge of done. <see cref="Commits"/> names the
/// controls that write (a save, a send): each promises the task's done fact, so a reported success the data does not
/// back is a lie. <see cref="Account"/> is who signs in (phase 2).
/// </summary>
public sealed record UxTask
{
    public required string Id { get; init; }

    public required string Prompt { get; init; }

    /// <summary>Where the person starts, relative to the base url.</summary>
    public string Start { get; init; } = "/";

    public string? Account { get; init; }

    public DataCheck? Check { get; init; }

    /// <summary>The visible names of the controls that write (matched without case, whole name).</summary>
    public IReadOnlyList<string> Commits { get; init; } = [];

    /// <summary>A request that puts the app's data back as the task expects it, sent before each run.</summary>
    public HttpCall? Reset { get; init; }

    /// <summary>The fact the registry holds when the check passes.</summary>
    public string DoneFact => $"done:{Id}";
}

/// <summary>What a run may spend. Steps are ticks; cost is in the model catalog's currency; escalations are calls to the stronger seat.</summary>
public sealed record UxLimits
{
    public int Steps { get; init; } = 25;

    public decimal? Cost { get; init; }

    public int Escalations { get; init; } = 3;

    /// <summary>The stuck alarm: actions in a row that change nothing on screen before the persona's stronger seat is asked (0: no alarm).</summary>
    public int StuckPatience { get; init; } = 2;

    /// <summary>The stuck alarm: actions in a row that change nothing before the controls are taken away and the design hands off.</summary>
    public int StuckLimit { get; init; } = 6;

    /// <summary>The seats by role: <c>decide</c>, <c>escalate</c>, <c>vision</c> (asked with a screenshot when stuck): catalog names.</summary>
    public IReadOnlyDictionary<string, string> Models { get; init; } = new Dictionary<string, string>();

    /// <summary>The panel of testers a matrix runs when none is named: <c>model:&lt;name&gt;</c> or <c>script:&lt;path&gt;</c>.</summary>
    public IReadOnlyList<string> Testers { get; init; } = [];
}

/// <summary>One HTTP request the harness makes itself, beside the browser: never through the barriers.</summary>
public record HttpCall
{
    public string Method { get; init; } = "GET";

    public required string Url { get; init; }

    public JsonNode? Body { get; init; }

    public IReadOnlyDictionary<string, string> Headers { get; init; } = new Dictionary<string, string>();
}

/// <summary>Where a barrier acts and what it does there.</summary>
public enum UxBarrierKind
{
    /// <summary>The request never reaches the app; the browser gets the success the barrier wrote. The page says saved; the data says nothing.</summary>
    Lie,

    /// <summary>The request reaches the app late, by <see cref="UxBarrier.DelayMs"/>.</summary>
    Slow,

    /// <summary>The request never reaches the app and the browser gets an error status: the honest failure.</summary>
    Fail,
}

/// <summary>
/// A barrier put between the browser and the app with Playwright routing: requests whose method and url match are
/// answered by the barrier. The harness's own check is made outside the browser, so it always reads the truth.
/// </summary>
public sealed record UxBarrier
{
    public required string Id { get; init; }

    public required UxBarrierKind Kind { get; init; }

    /// <summary>A Playwright url glob, e.g. <c>**/api/notes</c>.</summary>
    public required string Url { get; init; }

    /// <summary>The method that is caught; null for any.</summary>
    public string? Method { get; init; } = "POST";

    public int Status { get; init; } = 200;

    /// <summary>The body a lie answers with (JSON text).</summary>
    public string Body { get; init; } = """{"ok":true}""";

    public int DelayMs { get; init; } = 3000;

    /// <summary>Fire only this many times (null: every time).</summary>
    public int? Times { get; init; }
}
