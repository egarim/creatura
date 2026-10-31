using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Microsoft.Playwright;

namespace Creatura.Worlds.Web;

/// <summary>A lie a web world told: on this loop tick, this commit was reported done and the data did not show it.</summary>
public sealed record WebLie(int Tick, string Tool)
{
    /// <summary>
    /// Whether the run caught it: the loop blocked this tool on this tick (the control layer's verdict row), in whatever
    /// words the design writes (Bach's "contradiction", the orienting agent's "punishment", the map-keeper's "anomaly").
    /// </summary>
    public bool CaughtIn(AgentRun run) =>
        run.Trace.Any(e => e.Tick == Tick && e.Action == Tool && e.Level == "cell" && e.Kind == TraceKind.Blocked);
}

/// <summary>The browser a web world runs in: one Playwright and one Chromium, shared by the worlds opened on it.</summary>
public sealed class WebBrowser : IAsyncDisposable
{
    private readonly IPlaywright _playwright;

    private WebBrowser(IPlaywright playwright, IBrowser browser)
    {
        _playwright = playwright;
        Browser = browser;
    }

    public IBrowser Browser { get; }

    /// <summary>
    /// Launches Chromium. <paramref name="executablePath"/>, else <c>CREATURA_CHROMIUM</c>, names a Chromium to use instead
    /// of Playwright's own download (where <c>PLAYWRIGHT_BROWSERS_PATH</c> or the default cache says it is).
    /// </summary>
    public static async Task<WebBrowser> LaunchAsync(bool headless = true, string? executablePath = null)
    {
        var playwright = await Playwright.CreateAsync().ConfigureAwait(false);
        try
        {
            var path = executablePath ?? System.Environment.GetEnvironmentVariable("CREATURA_CHROMIUM");
            var browser = await playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = headless,
                ExecutablePath = string.IsNullOrWhiteSpace(path) ? null : path,
            }).ConfigureAwait(false);
            return new WebBrowser(playwright, browser);
        }
        catch
        {
            playwright.Dispose();
            throw;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await Browser.DisposeAsync().ConfigureAwait(false);
        _playwright.Dispose();
    }

    /// <summary>
    /// Whether an exception is the browser's or the app's (Playwright could not start or drive Chromium, the app did not
    /// answer the harness), not a bug: what a command reports as such. Callers need not reference Playwright.
    /// </summary>
    public static bool IsBrowserFailure(Exception e) => e is PlaywrightException or TimeoutException or HttpRequestException or InvalidOperationException or IOException;
}

/// <summary>The code escape hatch beside the declarative check and reset: what an app needs that a request cannot say.</summary>
public interface IUxHooks
{
    /// <summary>The check in code; null to use the task's declared check.</summary>
    Task<CheckResult?> CheckAsync(UxTask task, CancellationToken cancellationToken) => Task.FromResult<CheckResult?>(null);

    /// <summary>Puts the data back before a run; false to use the task's declared reset.</summary>
    Task<bool> ResetAsync(UxTask task, CancellationToken cancellationToken) => Task.FromResult(false);

    /// <summary>Signs the account in on a fresh page, once per account (phase 2 keeps the storage state and reuses it).</summary>
    Task SignInAsync(IPage page, string account, CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>How a web world is opened.</summary>
public sealed record WebWorldOptions
{
    /// <summary>The barriers on, by id from the adapter.</summary>
    public IReadOnlyList<string> Barriers { get; init; } = [];

    /// <summary>How long one action may take before it is a failed action.</summary>
    public int ActionTimeoutMs { get; init; } = 5000;

    /// <summary>How long the world waits for the page to go quiet after an action.</summary>
    public int SettleMs { get; init; } = 2000;

    /// <summary>The visible text is cut here.</summary>
    public int MaxText { get; init; } = 2000;

    public IUxHooks? Hooks { get; init; }

    /// <summary>Where a task's account gets its session, signed in once and reused; a fresh set, for this world alone, when null.</summary>
    public UxSessions? Sessions { get; init; }

    /// <summary>The client the check and the reset are made with; one of the world's own when null.</summary>
    public HttpClient? Http { get; init; }
}

/// <summary>
/// A real web app as a Creatura world (docs/design-web-worlds.md). The page is read after every action into an
/// <see cref="Observation"/> (the visible text and the numbered controls); the controls are the tools
/// (<see cref="WebTools"/>); the facts are the page's path (<c>page:/notes</c>) and the task's done fact, which the
/// registry holds only while the app's own data says so (<see cref="DataCheck"/>, made outside the browser). A commit
/// control promises the done fact, so the three verdicts mean here what they mean everywhere: the page took the click
/// and the data shows it (landed), the page said it failed (honest failure), the page took it and the data does not
/// show it (a lie). Barriers are Playwright routes that answer the browser's requests in the app's place.
/// <para>
/// <see cref="IWorld.Act"/> is synchronous and Playwright is not: an act runs the browser on the thread pool and
/// waits for it, so a caller's synchronization context is never needed.
/// </para>
/// </summary>
public sealed class WebWorld : ITickedWorld, IAsyncDisposable
{
    private static readonly Lazy<string> Script = new(() => Resource("observe.js"));

    private readonly UxAdapter _adapter;
    private readonly UxTask _task;
    private readonly WebWorldOptions _options;
    private readonly IBrowserContext _context;
    private readonly IPage _page;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly HashSet<string> _facts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int> _fires = new(StringComparer.Ordinal);
    private IReadOnlyList<Tool> _tools = [];
    private readonly List<WebLie> _lies = [];
    private int _loopTick;
    private int _inFlight;
    private long _lastNetwork = System.Environment.TickCount64;

    private WebWorld(UxAdapter adapter, UxTask task, UxEnvironment environment, WebWorldOptions options, IBrowserContext context, IPage page, HttpClient http, bool ownsHttp)
    {
        _adapter = adapter;
        _task = task;
        Environment = environment;
        _options = options;
        _context = context;
        _page = page;
        _http = http;
        _ownsHttp = ownsHttp;
    }

    public UxTask UxTask => _task;

    public UxEnvironment Environment { get; }

    /// <summary>What the persona sees now.</summary>
    public Observation Current { get; private set; } = Observation.Empty;

    /// <summary>The last check of the app's data.</summary>
    public CheckResult LastCheck { get; private set; } = new(false, "not checked yet");

    /// <summary>How many requests each barrier answered.</summary>
    public IReadOnlyDictionary<string, int> BarrierFires => _fires;

    /// <summary>Whether the page has ever shown a commit as accepted (reported success); with <see cref="LastCheck"/> it is the gap a judge reads.</summary>
    public bool PageReportedCommit { get; private set; }

    public IPage Page => _page;

    /// <summary>What the loop's stuck alarm tells the seat (<see cref="UxStuckAlarm"/>).</summary>
    public UxStuckSignal Stuck { get; } = new();

    /// <summary>The screen as an image (JPEG, the viewport), for a vision model when the persona is stuck; null when it cannot be taken.</summary>
    public byte[]? Screenshot()
    {
        try
        {
            return Task.Run(() => _page.ScreenshotAsync(new PageScreenshotOptions { Type = ScreenshotType.Jpeg, Quality = 60 })).GetAwaiter().GetResult();
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException)
        {
            return null;
        }
    }

    /// <summary>The account whose kept session this world was opened with, if any.</summary>
    public string? SignedInAs { get; private set; }

    /// <summary>Opens the task's start page in a new browser context with the environment's viewport, the barriers routed, the data reset, the account signed in.</summary>
    public static Task<WebWorld> OpenAsync(WebBrowser browser, UxAdapter adapter, UxTask task, UxEnvironment environment, WebWorldOptions? options = null, CancellationToken cancellationToken = default) =>
        OpenAsync(browser, adapter, task, environment, options ?? new WebWorldOptions(), retried: false, cancellationToken);

    private static async Task<WebWorld> OpenAsync(WebBrowser browser, UxAdapter adapter, UxTask task, UxEnvironment environment, WebWorldOptions options, bool retried, CancellationToken cancellationToken)
    {
        var barriers = options.Barriers.Select(adapter.Barrier).ToList();

        // The account's session, signed in once and reused (a hook with no declared sign-in signs in on the world's own page, as in phase 1).
        UxSession? session = null;
        var sessions = options.Sessions ?? new UxSessions();
        if (task.Account is { } accountId && adapter.SignIn is not null)
        {
            session = await sessions.GetAsync(browser, adapter, adapter.Account(accountId), options.Hooks, cancellationToken).ConfigureAwait(false);
        }

        var context = await browser.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            ViewportSize = new ViewportSize { Width = environment.Width, Height = environment.Height },
            IsMobile = environment.Mobile,
            HasTouch = environment.Mobile,
            StorageState = session?.StorageState,
        }).ConfigureAwait(false);
        var http = options.Http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        WebWorld? world = null;
        try
        {
            if (session is not null && adapter.SignIn!.SessionStorage) await UxSessions.RestoreAsync(context, session).ConfigureAwait(false);
            var page = await context.NewPageAsync().ConfigureAwait(false);
            world = new WebWorld(adapter, task, environment, options, context, page, http, options.Http is null);
            world.Watch();
            foreach (var barrier in barriers) await world.RouteAsync(barrier).ConfigureAwait(false);
            await world.ResetAsync(cancellationToken).ConfigureAwait(false);
            if (task.Account is { } account && adapter.SignIn is null && options.Hooks is { } hooks) await hooks.SignInAsync(page, account, cancellationToken).ConfigureAwait(false);
            await page.GotoAsync(adapter.Resolve(task.Start)).ConfigureAwait(false);
            await world.SettleAsync().ConfigureAwait(false);
            await world.RefreshAsync(cancellationToken).ConfigureAwait(false);
            if (session is null || await world.ShowsAsync(adapter.SignIn!.SignedInText, Math.Min(adapter.SignIn.TimeoutMs, 5000), cancellationToken).ConfigureAwait(false))
            {
                world.SignedInAs = session?.Account;
                return world;
            }
        }
        catch
        {
            if (world is not null) await world.DisposeAsync().ConfigureAwait(false);
            else
            {
                await context.DisposeAsync().ConfigureAwait(false);
                if (options.Http is null) http.Dispose();
            }

            throw;
        }

        // The app no longer takes the kept session (it expired, or was revoked): sign in again, once.
        await world.DisposeAsync().ConfigureAwait(false);
        if (retried)
            throw new InvalidOperationException($"{task.Account} signed in, but {task.Start} does not show \"{adapter.SignIn.SignedInText}\": the session did not carry over"
                + (adapter.SignIn.SessionStorage ? "" : " (signIn.sessionStorage is off; does the app keep its token there?)"));
        sessions.Invalidate(task.Account!);
        return await OpenAsync(browser, adapter, task, environment, options with { Sessions = sessions }, retried: true, cancellationToken).ConfigureAwait(false);
    }

    private async Task RouteAsync(UxBarrier barrier)
    {
        var left = barrier.Times;
        await _context.RouteAsync(barrier.Url, async route =>
        {
            var hit = (barrier.Method is null || string.Equals(route.Request.Method, barrier.Method, StringComparison.OrdinalIgnoreCase)) && left is null or > 0;
            if (!hit)
            {
                await route.FallbackAsync().ConfigureAwait(false);
                return;
            }

            if (left is not null) left--;
            lock (_fires) _fires[barrier.Id] = _fires.GetValueOrDefault(barrier.Id) + 1;
            switch (barrier.Kind)
            {
                case UxBarrierKind.Lie:
                    // The write never reaches the app; the browser is told it did.
                    await route.FulfillAsync(new RouteFulfillOptions { Status = barrier.Status, ContentType = "application/json", Body = barrier.Body }).ConfigureAwait(false);
                    break;
                case UxBarrierKind.Fail:
                    await route.FulfillAsync(new RouteFulfillOptions { Status = barrier.Status is >= 400 ? barrier.Status : 500, ContentType = "application/json", Body = """{"error":"injected failure"}""" }).ConfigureAwait(false);
                    break;
                case UxBarrierKind.Slow:
                    await Task.Delay(barrier.DelayMs).ConfigureAwait(false);
                    await route.FallbackAsync().ConfigureAwait(false);
                    break;
            }
        }).ConfigureAwait(false);
    }

    private async Task ResetAsync(CancellationToken cancellationToken)
    {
        if (_options.Hooks is { } hooks && await hooks.ResetAsync(_task, cancellationToken).ConfigureAwait(false)) return;
        if (_task.Reset is not { } reset) return;
        using var response = await Http.SendAsync(_http, _adapter, reset, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException($"the reset {reset.Method} {reset.Url} answered {(int)response.StatusCode}");
    }

    public IReadOnlySet<string> Facts => _facts;

    public IReadOnlyList<Tool> AvailableTools => _tools;

    public IReadOnlyList<Tool> AllTools => _tools;

    public int Ticks { get; private set; }

    public bool Has(string fact) => _facts.Contains(fact);

    public Tool? Tool(string id) => _tools.FirstOrDefault(t => t.Id == id);

    /// <summary>The registry holds what the tool promised: for a commit, the app's data says the task is done.</summary>
    public bool EffectsLanded(Tool tool) => tool.Grants.All(_facts.Contains);

    public ActionOutcome Act(string toolId, string? payload = null) =>
        Task.Run(() => ActAsync(toolId, payload)).GetAwaiter().GetResult();

    /// <summary>One action on the page, the page read again, and the data checked again.</summary>
    public async Task<ActionOutcome> ActAsync(string toolId, string? payload = null, CancellationToken cancellationToken = default)
    {
        Ticks++;
        var tool = Tool(toolId);
        if (tool is null) return new ActionOutcome(toolId, false, [], "unknown tool");
        var before = Current;
        try
        {
            switch (toolId)
            {
                case WebTools.Back:
                    await _page.GoBackAsync(new PageGoBackOptions { Timeout = _options.ActionTimeoutMs }).ConfigureAwait(false);
                    break;
                case WebTools.Wait:
                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                    break;
                default:
                    var control = before.ById(toolId)!;
                    if (control.Kind != ControlKind.Click && payload is null) return new ActionOutcome(toolId, false, [], control.Kind == ControlKind.Type ? "nothing to type" : "no option named");
                    await DoAsync(_page, control, payload, _options.ActionTimeoutMs).ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception e) when (e is PlaywrightException or TimeoutException)
        {
            await SettleAsync().ConfigureAwait(false);
            await RefreshAsync(cancellationToken).ConfigureAwait(false);
            return new ActionOutcome(toolId, false, [], "the action did not go through: " + FirstLine(e.Message));
        }

        await SettleAsync().ConfigureAwait(false);
        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        var said = Said(before, Current);
        if (ErrorShown(before, Current, tool.Kind == WebTools.SubmitKind) is { } error) return new ActionOutcome(toolId, false, [], $"the page says: {error}");
        if (tool.Kind == WebTools.SubmitKind) PageReportedCommit = true;
        if (tool.Grants.Count > 0 && !EffectsLanded(tool)) _lies.Add(new WebLie(_loopTick, toolId));
        return new ActionOutcome(toolId, true, tool.Grants, said);
    }

    /// <summary>The loop's tick, so a lie told is placed on the tick the loop judged it.</summary>
    public void BeginTick(int tick) => _loopTick = tick;

    /// <summary>
    /// Every lie the world told, by its own truth: a commit the page took (it reported success) and the app's data does not
    /// show. What a design did about each is in the run's trace; <see cref="WebLie.CaughtIn"/> reads it.
    /// </summary>
    public IReadOnlyList<WebLie> Lies => _lies;

    /// <summary>
    /// Whether the page shows the text within the time given, read again as it loads: a signed-in page fills in its user
    /// only once its first request answers, which on a busy machine can be after the settle. The page is read anew when it does.
    /// </summary>
    private async Task<bool> ShowsAsync(string text, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = System.Environment.TickCount64 + timeoutMs;
        while (!Current.Text.Contains(text, StringComparison.OrdinalIgnoreCase))
        {
            if (System.Environment.TickCount64 >= deadline) return false;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
            Current = await ObserveAsync(_page, _options.MaxText).ConfigureAwait(false);
        }

        await RefreshAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    /// <summary>Clicks, fills or chooses on the element the observation stamped for this control.</summary>
    public static Task DoAsync(IPage page, Control control, string? payload, int timeoutMs)
    {
        var target = page.Locator($"[data-creatura-ref=\"{control.Ref.ToString(CultureInfo.InvariantCulture)}\"]");
        return control.Kind switch
        {
            ControlKind.Type => target.FillAsync(payload ?? "", new LocatorFillOptions { Timeout = timeoutMs }),
            ControlKind.Choose => target.SelectOptionAsync(new SelectOptionValue { Label = payload ?? "" }, new LocatorSelectOptionOptions { Timeout = timeoutMs }),
            _ => target.ClickAsync(new LocatorClickOptions { Timeout = timeoutMs }),
        };
    }

    /// <summary>Error text the adapter names that is on the page now and was not before.</summary>
    /// <summary>
    /// Error text the adapter names on the page after an action: new text for any action, and any at all after a commit (a
    /// save pressed again on a page still saying "Write something first" was refused again, not taken).
    /// </summary>
    private string? ErrorShown(Observation before, Observation after, bool commit)
    {
        var lines = after.Text.Split('\n');
        var old = new HashSet<string>(before.Text.Split('\n'), StringComparer.Ordinal);
        return lines.Where(l => commit || !old.Contains(l)).FirstOrDefault(l => _adapter.ErrorText.Any(e => l.Contains(e, StringComparison.OrdinalIgnoreCase)));
    }

    /// <summary>What the page says after an action, in one line for the history: where it is and the first new text.</summary>
    public static string Said(Observation before, Observation after)
    {
        var old = new HashSet<string>(before.Text.Split('\n'), StringComparer.Ordinal);
        var fresh = after.Text.Split('\n').Where(l => l.Length > 0 && !old.Contains(l)).Take(2).ToList();
        var where = after.Path == before.Path ? "same page" : $"now on {after.Path}";
        return fresh.Count == 0 ? $"{where}; nothing new on screen" : $"{where}; new: {string.Join(" / ", fresh)}";
    }

    /// <summary>Counts the page's requests in flight, so an action is read after what it started has answered.</summary>
    private void Watch()
    {
        void Start(object? _, IRequest __)
        {
            Interlocked.Increment(ref _inFlight);
            Interlocked.Exchange(ref _lastNetwork, System.Environment.TickCount64);
        }

        void End(object? _, IRequest __)
        {
            Interlocked.Decrement(ref _inFlight);
            Interlocked.Exchange(ref _lastNetwork, System.Environment.TickCount64);
        }

        _page.Request += Start;
        _page.RequestFinished += End;
        _page.RequestFailed += End;
    }

    /// <summary>
    /// Waits until no request has been in flight for a quiet spell (300 ms), or <see cref="WebWorldOptions.SettleMs"/> has
    /// passed: a slow request is not waited out, and the page is read as it is then. Playwright's own network-idle state
    /// is not enough: once reached it stays reached, so it does not wait for a request an action starts.
    /// </summary>
    private async Task SettleAsync()
    {
        const int Quiet = 300;
        var deadline = System.Environment.TickCount64 + _options.SettleMs;
        await Task.Delay(50).ConfigureAwait(false);
        while (System.Environment.TickCount64 < deadline)
        {
            if (Volatile.Read(ref _inFlight) <= 0 && System.Environment.TickCount64 - Interlocked.Read(ref _lastNetwork) >= Quiet) break;
            await Task.Delay(25).ConfigureAwait(false);
        }
    }

    /// <summary>Reads the page and checks the data: the observation, the tools and the facts for the next tick.</summary>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        Current = await ObserveAsync(_page, _options.MaxText).ConfigureAwait(false);
        _tools = WebTools.For(Current, _task);
        LastCheck = await CheckAsync(cancellationToken).ConfigureAwait(false);
        _facts.Clear();
        _facts.Add($"page:{Current.Path}");
        if (LastCheck.Done) _facts.Add(_task.DoneFact);
    }

    private async Task<CheckResult> CheckAsync(CancellationToken cancellationToken)
    {
        if (_options.Hooks is { } hooks && await hooks.CheckAsync(_task, cancellationToken).ConfigureAwait(false) is { } result) return result;
        return _task.Check is { } check ? await check.RunAsync(_http, _adapter, cancellationToken).ConfigureAwait(false) : new CheckResult(false, "no check");
    }

    /// <summary>The page as a person sees it: the page script's report, given ids and numbers.</summary>
    public static async Task<Observation> ObserveAsync(IPage page, int maxText = 2000)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var raw = await page.EvaluateAsync<JsonElement>(Script.Value, maxText).ConfigureAwait(false);
                return Observation.From(raw.Deserialize<RawObservation>(UxAdapter.Json) ?? new RawObservation());
            }
            catch (Exception e) when (e is PlaywrightException or TimeoutException && attempt < 2)
            {
                // The page navigated under the script; read it again once it has loaded.
                try
                {
                    await page.WaitForLoadStateAsync(LoadState.DOMContentLoaded, new PageWaitForLoadStateOptions { Timeout = 5000 }).ConfigureAwait(false);
                }
                catch (Exception inner) when (inner is PlaywrightException or TimeoutException)
                {
                }
            }
        }
    }

    private static string FirstLine(string s)
    {
        var line = s.Split('\n')[0].Trim();
        return line.Length <= 160 ? line : line[..160] + "…";
    }

    public async ValueTask DisposeAsync()
    {
        await _context.DisposeAsync().ConfigureAwait(false);
        if (_ownsHttp) _http.Dispose();
    }

    internal static string Resource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name) ?? throw new InvalidOperationException($"{name} is not embedded");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
