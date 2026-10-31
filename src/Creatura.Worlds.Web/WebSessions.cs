using System.Text.Json;
using Microsoft.Playwright;

namespace Creatura.Worlds.Web;

/// <summary>
/// A signed-in account, as a browser context can be opened with it: Playwright's storage state (cookies, localStorage)
/// and the signed-in origin's sessionStorage, which the storage state leaves out.
/// </summary>
public sealed record UxSession
{
    public required string Account { get; init; }

    /// <summary>Playwright's storage state, as it writes it (JSON).</summary>
    public required string StorageState { get; init; }

    /// <summary>The origin the account was signed in on, whose sessionStorage is kept.</summary>
    public string? Origin { get; init; }

    public IReadOnlyDictionary<string, string> SessionStorage { get; init; } = new Dictionary<string, string>();

    public DateTimeOffset At { get; init; }
}

/// <summary>
/// The sessions of a run or a matrix: each account signs in once, the first time a task needs it, and every later
/// context for it opens with the kept session (docs/design-web-worlds.md, "Sign-in and session reuse"). With a folder the
/// sessions are also kept on disk, one owner-only file per account, so the next matrix reuses them; a session the app no
/// longer accepts is dropped and the account signs in again. The files hold tokens: keep the folder out of the repository.
/// </summary>
public sealed class UxSessions
{
    /// <summary>A key the restore script sets once it has put the kept sessionStorage back, so it never does it twice in a tab.</summary>
    public const string RestoredMarker = "__creatura_restored";

    private readonly Dictionary<string, UxSession> _kept = new(StringComparer.Ordinal);
    private readonly string? _folder;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <param name="folder">Where sessions are kept between runs; in memory only when null.</param>
    public UxSessions(string? folder = null) => _folder = folder;

    /// <summary>How many times an account was signed in by this instance: once per account, unless a session expired.</summary>
    public int SignIns { get; private set; }

    /// <summary>How many times a kept session was handed out instead of signing in.</summary>
    public int Reused { get; private set; }

    /// <summary>The account's session: the kept one, else one from the folder, else a new sign-in.</summary>
    public async Task<UxSession> GetAsync(WebBrowser browser, UxAdapter adapter, UxAccount account, IUxHooks? hooks = null, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_kept.TryGetValue(account.Id, out var kept) || (kept = Read(account.Id)) is not null)
            {
                _kept[account.Id] = kept;
                Reused++;
                return kept;
            }

            var session = await SignInAsync(browser, adapter, account, hooks, cancellationToken).ConfigureAwait(false);
            SignIns++;
            _kept[account.Id] = session;
            Write(session);
            return session;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Drops the account's session (the app no longer accepts it): the next <see cref="GetAsync"/> signs in again.</summary>
    public void Invalidate(string account)
    {
        _kept.Remove(account);
        if (PathFor(account) is { } path && File.Exists(path)) File.Delete(path);
    }

    private string? PathFor(string account) => _folder is null ? null : Path.Combine(_folder, Observation.Slug(account, max: 80) + ".session.json");

    private UxSession? Read(string account)
    {
        if (PathFor(account) is not { } path || !File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<UxSession>(File.ReadAllText(path), UxAdapter.Json);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private void Write(UxSession session)
    {
        if (PathFor(session.Account) is not { } path) return;
        Directory.CreateDirectory(_folder!);
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using var stream = new FileStream(path, options);
        JsonSerializer.Serialize(stream, session, UxAdapter.Json);
    }

    /// <summary>
    /// Signs the account in on a fresh context: the hook when there is one, else the adapter's declared steps, then waits
    /// for the signed-in text and keeps the storage state and the origin's sessionStorage. An error never shows the
    /// password: it names the step as the adapter wrote it, with <c>{password}</c> unfilled.
    /// </summary>
    public static async Task<UxSession> SignInAsync(WebBrowser browser, UxAdapter adapter, UxAccount account, IUxHooks? hooks = null, CancellationToken cancellationToken = default)
    {
        var signIn = adapter.SignIn;
        if (signIn is null && hooks is null) throw new InvalidOperationException($"account {account.Id}: the adapter has no signIn, and no hook signs in");
        await using var context = await browser.Browser.NewContextAsync().ConfigureAwait(false);
        var page = await context.NewPageAsync().ConfigureAwait(false);
        if (hooks is not null)
        {
            await hooks.SignInAsync(page, account.Id, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await page.GotoAsync(adapter.Resolve(signIn!.Start)).ConfigureAwait(false);
            foreach (var line in signIn.Steps)
            {
                var step = ScriptStep.Parse(line) ?? throw new InvalidOperationException($"account {account.Id}: the sign-in step '{line}' needs 'name: text'");
                var text = step.Text.Replace("{user}", account.UserValue, StringComparison.Ordinal).Replace("{password}", account.PasswordValue, StringComparison.Ordinal);
                var control = await FindAsync(page, step, signIn.TimeoutMs, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException($"account {account.Id}: no {step.Verb} control named '{step.Name}' for the sign-in step '{line}'");
                await WebWorld.DoAsync(page, control, text, 5000).ConfigureAwait(false);
            }
        }

        if (signIn is not null && !await ShowsAsync(page, signIn.SignedInText, signIn.TimeoutMs, cancellationToken).ConfigureAwait(false))
            throw new InvalidOperationException($"account {account.Id}: signed in, but no page showed \"{signIn.SignedInText}\" within {signIn.TimeoutMs} ms");

        var origin = Uri.TryCreate(page.Url, UriKind.Absolute, out var url) ? url.GetLeftPart(UriPartial.Authority) : null;
        var storage = signIn?.SessionStorage == false ? new Dictionary<string, string>()
            : (await page.EvaluateAsync<JsonElement>("() => Object.fromEntries(Object.entries(sessionStorage))").ConfigureAwait(false))
                .EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetString() ?? "", StringComparer.Ordinal);
        return new UxSession
        {
            Account = account.Id, StorageState = await context.StorageStateAsync().ConfigureAwait(false), Origin = origin, SessionStorage = storage,
            At = DateTimeOffset.UtcNow,
        };
    }

    /// <summary>The step's control once the page shows it (a sign-in page loads, then redirects to the identity provider).</summary>
    private static async Task<Control?> FindAsync(IPage page, ScriptStep step, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var screen = await WebWorld.ObserveAsync(page).ConfigureAwait(false);
            if (ScriptStep.Find(screen, step.Verb, step.Name) is { } control) return control;
            if (Environment.TickCount64 >= deadline) return null;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<bool> ShowsAsync(IPage page, string text, int timeoutMs, CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            var screen = await WebWorld.ObserveAsync(page).ConfigureAwait(false);
            if (screen.Text.Contains(text, StringComparison.OrdinalIgnoreCase)) return true;
            if (Environment.TickCount64 >= deadline) return false;
            await Task.Delay(150, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Puts the session's sessionStorage back in every page of the context before the app's scripts run, on the origin it
    /// was kept from only, once per tab (a later sign-out in the tab is not undone).
    /// </summary>
    public static Task RestoreAsync(IBrowserContext context, UxSession session)
    {
        if (session.Origin is null || session.SessionStorage.Count == 0) return Task.CompletedTask;
        var script = $$"""
            (() => {
              const origin = {{JsonSerializer.Serialize(session.Origin)}};
              const items = {{JsonSerializer.Serialize(session.SessionStorage)}};
              if (location.origin !== origin) return;
              try {
                if (sessionStorage.getItem('{{RestoredMarker}}')) return;
                for (const [k, v] of Object.entries(items)) if (sessionStorage.getItem(k) === null) sessionStorage.setItem(k, v);
                sessionStorage.setItem('{{RestoredMarker}}', '1');
              } catch (e) { }
            })();
            """;
        return context.AddInitScriptAsync(script);
    }
}
