using System.Globalization;
using System.Text.Json;
using Creatura.Cells.Sync;

namespace Creatura.Cells;

/// <summary>
/// <c>creatura cell ...</c>: the cell on this vessel, from a shell or a host tool's hook. Everything it writes goes
/// under <c>--home</c> (default <c>~/.creatura</c>, or <c>$CREATURA_HOME</c>). It prints ids, hashes and fingerprints,
/// never a private key or the raw hardware identifier. Exit codes: 0 done, 1 not initialized, 2 a usage or ledger error,
/// 3 <c>turn</c> says wait.
/// </summary>
public static class CellCommand
{
    public const string Usage = """
        usage: creatura cell <command> [options]

          init                         create the owner key if absent, endorse this vessel's cell, say hello (idempotent)
          whoami                       creatura id, vessel id (hashed), hardware, position, key fingerprint, endorsement
          claim --goal <text>          claim work; where with --repo --branch --folder --tool --session
          progress --row <text>        a step of the latest active claim (--claim <id>, --stress <0..1>, --blocked)
          release --evidence <text>    done, with evidence (--claim <id>)
          handoff --reason <text>      stopping, and why (--claim <id>)
          heartbeat                    still alive (--load <number>)
          ledger                       every entry; --open: the claims not released; --path <claim>: one claim's path
          activity [--out <file>]      the ledger as an Activity Streams 2.0 collection (docs/design-activity-stream.md)
          stream [--port <n>] [--no-open]
                                       serve the activity stream as a page on 127.0.0.1 only, read-only, and open it

        turns, so two cells neither wait on each other nor both lead (docs/design-turns.md):
          wait --on <cell or entry id> --row <why>
                                       this cell waits; the wait stands until its next entry that is not one (--claim <id>)
          propose --topic <name> (--row <text> | --file <path> --hub <url>) [--with <cell,...>]
                                       propose on a topic (plan, api, ...); --file shares the file first and names it
          answer --proposal <entry id> (--accept | --object --row <why>)
          turn [--json] [--quiet <minutes>]
                                       act or wait, with the reason, from the verified ledger: exit 0 to act, 3 to wait

        goals offered to cells (docs/design-activity-stream.md):
          offer --to <cell, vessel or host name> --goal <text> [--repo --branch --folder] [--expires <24h|3d>] [--quiet <minutes>]
                                       on the owner's vessel: offer a goal, signed with the owner key
          withdraw --offer <id>        on the owner's vessel: take an offer back
          offers [--all]               the offers waiting for this cell; --all: every offer and where it stands
          take --offer <id>            claim an offer waiting for this cell
          decline --offer <id> --reason <text>
                                       say no to an offer waiting for this cell

        joining Claude Code (docs/design-cells.md, "How host tools join"):
          hook                         one hook event on stdin; prints nothing, always exits 0 (--goal folder|prompt)
          hooks-config                 the "hooks" block for ~/.claude/settings.json (--command <path to creatura>)
          install-hooks --settings <f> merge that block into a settings file, backing it up first (--dry-run: report only)
          uninstall-hooks --settings <f>  remove exactly that block again (--dry-run: report only)
          publish-bin                  publish this CLI into <home>/bin with a `creatura` wrapper (--project <csproj>)
          request [--out <file>]       on a vessel without the owner key: write a request for the owner to endorse
          endorse --request <file> [--out <file>]
                                       on the owner's vessel: endorse a request, write the endorsement
          accept --endorsement <file>  back on the new vessel: keep the endorsement if it covers this cell, say hello
          sync --hub <url> [--quiet]   push to and pull from a ledger hub (Creatura.Cells.Hub), e.g. http://100.100.1.5:5181;
                                       --quiet prints only when something moved or failed
          publish-hub                  publish the hub into <home>/hub/app (--project <csproj>)
          launchd-config --hub <url> [--listen <address:port>] [--trust <file>] [--interval <seconds>]
                                       print the launchd agents: sync every interval (default 60), and the hub with --listen
          install-launchd ...          the same options: write them to ~/Library/LaunchAgents and load them (--dry-run)
          uninstall-launchd            unload and remove both agents
          owner-key [--out <file>]     on the owner's vessel: the owner's PUBLIC key, for a hub's --trust file
          share --file <path> --hub <url> [--claim <id>] [--to <cell,...>] [--type <content type>]
                                       seal a file (64 MB at most) for every endorsed cell, or only --to, upload it, record it
          files [--claim <id>]         the shared files this cell can open, with name, type and size
          fetch --artifact <id> --out <path> [--hub <url>]
                                       download, check and open a shared file; a second fetch uses this vessel's copy
          revoke --cell <id> | --endorsement <file>  --reason <text> [--up-to <n>] [--hub <url>]
                                       on the owner's vessel: revoke a cell (a lost vessel); entries past n are refused;
                                       --endorsement names a cell that never wrote here by the endorsement you gave it

        progress, release and handoff take --session <id> to name the claim of a host-tool session.

        options for every command:
          --home <dir>                 where keys, record and ledger live (default ~/.creatura, or $CREATURA_HOME)
          --creatura <name>            which creatura in that home (default creatura)
          --no-mesh                    do not read the mesh client's status
          --store json|sqlite          where the ledger lives (default json: <home>/ledger.json; sqlite: <home>/ledger.db,
                                       which imports ledger.json on its first run and is the ledger from then on)
        """;

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--open", "--blocked", "--no-mesh", "--help", "--dry-run", "--no-load", "--no-open", "--all", "--json", "--accept", "--object" };

    private static readonly TimeSpan InteractiveLockWait = TimeSpan.FromSeconds(10);

    /// <summary>Runs one command. <paramref name="source"/> replaces this machine's vessel source (tests).</summary>
    public static int Run(string[] args, TextWriter output, TextWriter error, IVesselSource? source = null)
    {
        if (args.Length < 2 || args[0] != "cell" || args.Contains("--help") || args.Contains("-h"))
        {
            (args.Contains("--help") || args.Contains("-h") ? output : error).WriteLine(Usage);
            return args.Contains("--help") || args.Contains("-h") ? 0 : 2;
        }

        Dictionary<string, string?> options;
        Func<CellHome, ILedgerStore>? store;
        try
        {
            options = Parse(args[1], args[2..]);
            store = LedgerStores.Factory(options.GetValueOrDefault("--store"));
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            return 2;
        }

        var home = new CellHome(options.GetValueOrDefault("--home") ?? Environment.GetEnvironmentVariable("CREATURA_HOME"), store);
        var context = new Context(home, options.GetValueOrDefault("--creatura") ?? "creatura", options.ContainsKey("--no-mesh"), source, output, error, options);
        try
        {
            return args[1] switch
            {
                "init" => Init(context),
                "whoami" => WhoAmI(context),
                "claim" => Claim(context),
                "progress" => OnClaim(context, "--row", (claim, row) => new ProgressPayload(claim, row)
                {
                    Stress = options.GetValueOrDefault("--stress") is { } s ? double.Parse(s, CultureInfo.InvariantCulture) : null,
                    Blocked = options.ContainsKey("--blocked") ? true : null,
                }),
                "release" => OnClaim(context, "--evidence", (claim, evidence) => new ReleasePayload(claim, evidence)),
                "handoff" => OnClaim(context, "--reason", (claim, reason) => new HandOffPayload(claim, reason)),
                "heartbeat" => Heartbeat(context),
                "ledger" => Ledger(context),
                "activity" => Activity(context),
                "stream" => Stream(context),
                "wait" => Wait(context),
                "propose" => Propose(context),
                "answer" => Answer(context),
                "turn" => Turn(context),
                "offer" => Offer(context),
                "withdraw" => Withdraw(context),
                "offers" => Offers(context),
                "take" => Take(context),
                "decline" => Decline(context),
                "hooks-config" => HooksConfig(context),
                "install-hooks" => InstallHooks(context, install: true),
                "uninstall-hooks" => InstallHooks(context, install: false),
                "publish-bin" => PublishBin(context),
                "request" => Request(context),
                "endorse" => Endorse(context),
                "accept" => Accept(context),
                "sync" => Sync(context),
                "owner-key" => OwnerKeyCommand(context),
                "revoke" => RevokeCommand(context),
                "share" => Share(context),
                "files" => Files(context),
                "fetch" => Fetch(context),
                "publish-hub" => PublishHubCommand(context),
                "launchd-config" => LaunchdConfig(context),
                "install-launchd" => InstallLaunchd(context),
                "uninstall-launchd" => UninstallLaunchd(context),
                var other => Fail(error, $"unknown command: cell {other}\n\n{Usage}"),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FormatException or ArgumentException
                                      or HttpRequestException or TaskCanceledException or UriFormatException or SharedFileException
                                      or UnauthorizedAccessException or JsonException or System.Security.Cryptography.CryptographicException)
        {
            error.WriteLine($"creatura cell {args[1]}: {e.Message}");
            return 2;
        }
    }

    private sealed record Context(CellHome Home, string Creatura, bool NoMesh, IVesselSource? Source, TextWriter Out, TextWriter Err, Dictionary<string, string?> Options);

    private static int Init(Context c)
    {
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using (var owner = c.Home.EnsureOwner(out var created))
        {
            c.Out.WriteLine($"owner key    {(created ? "created" : "present")} {owner.Fingerprint} ({c.Home.OwnerKeyPath}, owner-only)");
        }

        using var cell = c.Home.Init(c.Source, Position(c), c.Creatura);
        var endorsement = cell.Identity.Endorsement!;
        c.Out.WriteLine($"cell         {cell.Id}, creatura {cell.Identity.Creatura} on vessel {cell.Identity.Hardware.VesselId}");
        c.Out.WriteLine($"endorsed     by {endorsement.OwnerFingerprint} at {CellJson.Stamp(endorsement.IssuedAt)}");

        var (ledger, store) = OpenLedger(c, cell);
        var hello = SayHelloIfNeeded(ledger, cell);
        store.Save(ledger);
        c.Out.WriteLine(hello is null ? "hello        already in the ledger" : $"hello        {hello.Id} written to {store.Path}");
        return 0;
    }

    private static int WhoAmI(Context c)
    {
        if (!c.Home.IsInitialized)
        {
            c.Err.WriteLine($"no cell under {c.Home.Root}: run `creatura cell init`");
            return 1;
        }

        using var cell = c.Home.OpenCell(c.Source, Position(c), c.Creatura);
        var id = cell.Identity;
        c.Out.WriteLine($"creatura     {id.Creatura} ({cell.Name})");
        c.Out.WriteLine($"cell         {id.Cell}");
        c.Out.WriteLine($"vessel       {id.Hardware.VesselId} (the hardware identifier, hashed with this home's salt)");
        c.Out.WriteLine($"hardware     {id.Hardware.Render()}");
        c.Out.WriteLine($"position     {id.Position.Render()}{(id.Position.Kind == PositionKind.InProcess ? (c.NoMesh ? " (mesh not read)" : " (no mesh status)") : "")}");
        c.Out.WriteLine($"public key   {id.Fingerprint}");
        c.Out.WriteLine(id.IsEndorsed
            ? $"endorsement  endorsed by owner {id.Endorsement!.OwnerFingerprint} at {CellJson.Stamp(id.Endorsement.IssuedAt)}"
            : "endorsement  not endorsed: run `creatura cell init`");
        var history = CellHome.VesselHistoryOf(c.Home.Records.Load(c.Creatura)!);
        c.Out.WriteLine($"vessels      {history.Count} ({string.Join(" -> ", history.Select(h => h.VesselId))})");
        c.Out.WriteLine($"home         {c.Home.Root}");
        return 0;
    }

    private static int Claim(Context c)
    {
        var goal = Required(c, "--goal");
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        SayHelloIfNeeded(ledger, cell);
        var claim = ledger.Append(cell, new ClaimPayload(goal)
        {
            Repo = c.Options.GetValueOrDefault("--repo"),
            Branch = c.Options.GetValueOrDefault("--branch"),
            Folder = c.Options.GetValueOrDefault("--folder"),
            Tool = c.Options.GetValueOrDefault("--tool"),
            Session = c.Options.GetValueOrDefault("--session"),
        });
        store.Save(ledger);
        c.Out.WriteLine($"claimed {claim.Id}: {goal}");
        return 0;
    }

    /// <summary>A goal offered by the owner to a cell or a vessel, written as an entry of this home's cell with the owner's signature inside.</summary>
    private static int Offer(Context c)
    {
        var goal = Required(c, "--goal");
        var to = Required(c, "--to");
        var lifetime = Lifetime(c.Options.GetValueOrDefault("--expires") ?? "24h");
        var quiet = c.Options.GetValueOrDefault("--quiet") is { } minutes ? int.Parse(minutes, CultureInfo.InvariantCulture) : (int?)null;
        if (quiet is <= 0) throw new ArgumentException("--quiet takes a number of minutes above 0");
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var owner = c.Home.LoadOwner() ?? throw new InvalidOperationException("only the owner's vessel can offer a goal: this home has no owner key");
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        SayHelloIfNeeded(ledger, cell);
        var (target, name) = Addressee(ledger, to);
        var offer = owner.Offer(target, goal, DateTimeOffset.UtcNow, lifetime,
            c.Options.GetValueOrDefault("--repo"), c.Options.GetValueOrDefault("--branch"), c.Options.GetValueOrDefault("--folder"), quiet);
        ledger.Append(cell, new OfferPayload(offer));
        store.Save(ledger);
        c.Out.WriteLine($"offered      {offer.Id} to {name}: {goal}{(quiet is { } q ? $" (quiet time {q} min)" : "")}");
        c.Out.WriteLine($"open until   {CellJson.Stamp(offer.ExpiresAt)[..16]}Z; it reaches the other vessels with the next sync");
        return 0;
    }

    private static int Withdraw(Context c)
    {
        var id = Required(c, "--offer");
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var owner = c.Home.LoadOwner() ?? throw new InvalidOperationException("only the owner's vessel can withdraw an offer: this home has no owner key");
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        var state = ledger.OfferState(id) ?? throw new InvalidOperationException($"no offer {id} in the ledger");
        if (state.Offer.OwnerPublicKey != owner.PublicKey) throw new InvalidOperationException($"{id} was offered by another owner");
        ledger.Append(cell, new WithdrawPayload(owner.Withdraw(id, DateTimeOffset.UtcNow)));
        store.Save(ledger);
        c.Out.WriteLine($"withdrew     {id}: {state.Offer.Goal}{(state.Status is OfferStatus.Taken or OfferStatus.Conflict ? " (already taken; the claim stands)" : "")}");
        return 0;
    }

    private static int Offers(Context c)
    {
        using var cell = OpenEndorsed(c);
        WorkLedger ledger;
        using (LedgerLock.Acquire(c.Home.Root, InteractiveLockWait)) ledger = OpenLedger(c, cell).Ledger;
        var now = DateTimeOffset.UtcNow;
        if (c.Options.ContainsKey("--all"))
        {
            var all = ledger.OfferStates();
            c.Out.WriteLine($"{all.Count} offer{(all.Count == 1 ? "" : "s")}");
            foreach (var s in all)
            {
                var status = s.Status == OfferStatus.Open && s.Offer.ExpiresAt <= now ? "lapsed" : s.Status.ToString().ToLowerInvariant();
                c.Out.WriteLine($"{s.Offer.Id}  {status,-9}  to {s.Offer.To}: {s.Offer.Goal}{(s.Claims.Count > 0 ? $"  (claim{(s.Claims.Count == 1 ? "" : "s")} {string.Join(", ", s.Claims.Select(x => x.Id))})" : "")}");
            }
            return 0;
        }

        var pending = ledger.PendingOffers(cell.Id, cell.Identity.Hardware.VesselId, now);
        c.Out.WriteLine($"{pending.Count} offer{(pending.Count == 1 ? "" : "s")} waiting for {cell.Id}");
        foreach (var o in pending) c.Out.WriteLine($"{o.Id}  until {CellJson.Stamp(o.ExpiresAt)[..16]}Z  {o.Goal}");
        return 0;
    }

    /// <summary>A claim that takes an offer waiting for this cell: the offer's goal and place, citing it.</summary>
    private static int Take(Context c)
    {
        var id = Required(c, "--offer");
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        var offer = ledger.PendingOffers(cell.Id, cell.Identity.Hardware.VesselId, DateTimeOffset.UtcNow).FirstOrDefault(o => o.Id == id)
            ?? throw new InvalidOperationException($"{id} is not waiting for {cell.Id} (`creatura cell offers` lists what is)");
        SayHelloIfNeeded(ledger, cell);
        var claim = ledger.Append(cell, new ClaimPayload(offer.Goal)
        {
            Repo = offer.Repo,
            Branch = offer.Branch,
            Folder = offer.Folder,
            Tool = c.Options.GetValueOrDefault("--tool"),
            Session = c.Options.GetValueOrDefault("--session"),
            Offer = offer.Id,
        });
        store.Save(ledger);
        c.Out.WriteLine($"claimed {claim.Id}: {offer.Goal} (offer {offer.Id})");
        return 0;
    }

    private static int Decline(Context c)
    {
        var id = Required(c, "--offer");
        var reason = Required(c, "--reason");
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        if (ledger.PendingOffers(cell.Id, cell.Identity.Hardware.VesselId, DateTimeOffset.UtcNow).All(o => o.Id != id))
            throw new InvalidOperationException($"{id} is not waiting for {cell.Id} (`creatura cell offers` lists what is)");
        var state = ledger.OfferState(id)!;
        SayHelloIfNeeded(ledger, cell);
        ledger.Append(cell, new HandOffPayload(state.Entry.Id, reason) { Offer = id });
        store.Save(ledger);
        c.Out.WriteLine($"declined     {id}: {reason}");
        return 0;
    }

    /// <summary>Whom an offer goes to: a cell id, a vessel id, or a host name a hello gave (the vessel it names).</summary>
    private static (string Target, string Name) Addressee(WorkLedger ledger, string to)
    {
        var hellos = ledger.Entries.Where(e => e.Kind == EntryKind.Hello).Select(e => e.Body<HelloPayload>().Cell).ToList();
        if (hellos.Any(h => CellKey.CellIdOf(h.PublicKey) == to)) return (to, $"cell {to}");
        if (hellos.Any(h => h.Hardware.VesselId == to)) return (to, $"vessel {to}");
        var named = hellos.Where(h => string.Equals(h.Hardware.HostName, to, StringComparison.OrdinalIgnoreCase) || string.Equals(h.Position.Node, to, StringComparison.OrdinalIgnoreCase))
            .Select(h => h.Hardware.VesselId).Distinct().ToList();
        return named.Count switch
        {
            1 => (named[0], $"any cell on {to} (vessel {named[0]})"),
            0 => throw new ArgumentException($"no cell, vessel or host named {to} in the ledger"),
            _ => throw new ArgumentException($"{to} names {named.Count} vessels; use a vessel id"),
        };
    }

    /// <summary>"24h", "3d" or "90m".</summary>
    private static TimeSpan Lifetime(string text)
    {
        var unit = text[^1];
        if (text.Length < 2 || !double.TryParse(text[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var n) || n <= 0)
            throw new ArgumentException($"--expires takes a number and m, h or d, e.g. 24h; not {text}");
        return unit switch
        {
            'm' => TimeSpan.FromMinutes(n),
            'h' => TimeSpan.FromHours(n),
            'd' => TimeSpan.FromDays(n),
            _ => throw new ArgumentException($"--expires takes a number and m, h or d, e.g. 24h; not {text}"),
        };
    }

    private static int OnClaim<T>(Context c, string field, Func<string, string, T> payload) where T : IEntryPayload
    {
        var text = Required(c, field);
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        var claim = ClaimFor(c, ledger, cell);
        SayHelloIfNeeded(ledger, cell);
        var entry = ledger.Append(cell, payload(claim, text));
        store.Save(ledger);
        c.Out.WriteLine($"{T.Kind.ToString().ToLowerInvariant()} {entry.Id} on {claim}: {text}");
        return 0;
    }

    /// <summary>The claim an entry is about: <c>--claim</c>, the open claim of <c>--session</c>, or this cell's latest active claim.</summary>
    private static string ClaimFor(Context c, WorkLedger ledger, LocalCell cell)
    {
        var claim = c.Options.GetValueOrDefault("--claim")
            ?? (c.Options.GetValueOrDefault("--session") is { } session
                ? ledger.WhoIsWorkingOn(activeOnly: false).LastOrDefault(w => w.Goal.Session == session && w.State != ClaimState.Released)?.Claim.Id
                  ?? throw new InvalidOperationException($"no open claim for session {session} in the ledger")
                : null)
            ?? ledger.LatestActiveClaimOf(cell.Id)?.Id
            ?? throw new InvalidOperationException($"{cell.Id} has no active claim: run `creatura cell claim --goal ...` or pass --claim <id>");
        if (ledger.PathOf(claim).Count == 0) throw new InvalidOperationException($"no claim {claim} in the ledger");
        return claim;
    }

    /// <summary>A wait on a cell or an entry (docs/design-turns.md), as a step of this cell's claim. It stands until this cell's next entry that is not a wait.</summary>
    private static int Wait(Context c)
    {
        var on = Required(c, "--on");
        var why = Required(c, "--row");
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        var peer = TurnLedger.CellOf(on);
        if (peer == cell.Id) throw new InvalidOperationException("a cell cannot wait on itself: `creatura cell turn` already waits for the answers to its proposals");
        var claim = ClaimFor(c, ledger, cell);
        SayHelloIfNeeded(ledger, cell);
        var entry = ledger.Append(cell, TurnLedger.Wait(claim, on, why));
        store.Save(ledger);
        c.Out.WriteLine($"waiting      {entry.Id} on {on}: {why}");
        if (!ledger.Entries.Any(e => e.Cell == peer)) c.Out.WriteLine($"note         nothing from {peer} is in this ledger yet; its quiet time counts from now");
        c.Out.WriteLine("stands       until this cell's next entry that is not a wait; `creatura cell turn` says when to move");
        return 0;
    }

    /// <summary>
    /// A proposal on a topic (docs/design-turns.md), as a step of this cell's claim, carrying the proposal entry ids on
    /// the topic this home holds (Seen), so two proposals that saw nothing of each other compete and one that saw the
    /// other is a counter. With --file the file is shared first, sealed for the parties, and the proposal names it.
    /// </summary>
    private static int Propose(Context c)
    {
        var topic = Required(c, "--topic");
        var row = c.Options.GetValueOrDefault("--row");
        var file = c.Options.GetValueOrDefault("--file");
        if (string.IsNullOrWhiteSpace(row) && string.IsNullOrWhiteSpace(file)) throw new ArgumentException("--row <text> or --file <path> is required");
        var named = c.Options.GetValueOrDefault("--with") is { Length: > 0 } with ? with.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : [];
        using var cell = OpenEndorsed(c);

        SealedFile? sealedFile = null;
        IReadOnlyList<Recipient> recipients = [];
        string? type = null;
        if (file is { Length: > 0 })
        {
            var path = Path.GetFullPath(file);
            var hub = new Uri(Required(c, "--hub"));
            WorkLedger snapshot;
            using (LedgerLock.Acquire(c.Home.Root, InteractiveLockWait)) snapshot = OpenLedger(c, cell).Ledger;
            var parties = snapshot.PartiesOf(cell.Id).Union(named, StringComparer.Ordinal).ToList();
            (sealedFile, recipients, type) = Upload(c, cell, snapshot, path, hub, parties.Count > 0 ? parties : null);
        }

        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        var (ledger, store) = OpenLedger(c, cell);
        var claim = ClaimFor(c, ledger, cell);
        SayHelloIfNeeded(ledger, cell);
        SignedEntry? artifact = sealedFile is null ? null : ledger.Append(cell, sealedFile.Payload with { Claim = claim });
        var payload = ledger.Propose(claim, topic, string.IsNullOrWhiteSpace(row) ? $"proposal in {Path.GetFileName(file)}" : row, named, artifact?.Id);
        var entry = ledger.Append(cell, payload);
        store.Save(ledger);
        var parties2 = ledger.PartiesOf(cell.Id);
        c.Out.WriteLine($"proposed     {entry.Id} on '{payload.Topic}': {payload.Row}");
        c.Out.WriteLine($"seen         {payload.Seen!.Count} earlier proposal{(payload.Seen.Count == 1 ? "" : "s")} on it; parties {(parties2.Count == 0 ? "none known yet" : string.Join(", ", parties2.Order(StringComparer.Ordinal)))}");
        if (artifact is not null) c.Out.WriteLine($"file         {Path.GetFileName(file)} ({type}) shared as {artifact.Id}, sealed for {recipients.Count} cell{(recipients.Count == 1 ? "" : "s")}");
        return 0;
    }

    /// <summary>An answer to another cell's proposal: accept, or object and say why.</summary>
    private static int Answer(Context c)
    {
        var id = Required(c, "--proposal");
        var accept = c.Options.ContainsKey("--accept");
        var objects = c.Options.ContainsKey("--object");
        if (accept == objects) throw new ArgumentException("--accept or --object, one of them");
        var row = c.Options.GetValueOrDefault("--row");
        if (objects && string.IsNullOrWhiteSpace(row)) throw new ArgumentException("--object takes --row <why>");
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        var proposal = ledger.Entries.FirstOrDefault(e => e.Id == id) ?? throw new InvalidOperationException($"no entry {id} in the ledger: sync first");
        if (!TurnLedger.IsProposal(proposal)) throw new InvalidOperationException($"{id} is not a proposal");
        if (proposal.Cell == cell.Id) throw new InvalidOperationException("a proposal is answered by the other parties, not by its proposer");
        var claim = ClaimFor(c, ledger, cell);
        SayHelloIfNeeded(ledger, cell);
        var entry = ledger.Append(cell, TurnLedger.Answer(claim, id, accept ? AnswerVerdict.Accept : AnswerVerdict.Object, string.IsNullOrWhiteSpace(row) ? "accept" : row));
        store.Save(ledger);
        c.Out.WriteLine($"{(accept ? "accepted" : "objected"),-12} {id} with {entry.Id}{(string.IsNullOrWhiteSpace(row) ? "" : $": {row}")}");
        return 0;
    }

    /// <summary>Whose move it is (docs/design-turns.md): read-only, from the verified ledger; exit 0 to act, 3 to wait.</summary>
    private static int Turn(Context c)
    {
        using var cell = OpenEndorsed(c);
        WorkLedger ledger;
        using (LedgerLock.Acquire(c.Home.Root, InteractiveLockWait)) ledger = OpenLedger(c, cell).Ledger;
        var quiet = c.Options.GetValueOrDefault("--quiet") is { } minutes ? TimeSpan.FromMinutes(double.Parse(minutes, CultureInfo.InvariantCulture)) : (TimeSpan?)null;
        if (quiet is { } q && q <= TimeSpan.Zero) throw new ArgumentException("--quiet takes a number of minutes above 0");
        var result = ledger.Turn(cell.Id, DateTimeOffset.UtcNow, quiet);
        if (c.Options.ContainsKey("--json"))
        {
            c.Out.WriteLine(result.ToJson());
            return result.ExitCode;
        }

        c.Out.WriteLine($"{result.Move.ToString().ToLowerInvariant(),-12} {result.Reason} ({result.Rule})");
        if (result.Waiting is { } waiting) c.Out.WriteLine($"waiting      on {waiting.On} since {CellJson.Stamp(waiting.Entry.Timestamp)[..19]}Z ({waiting.Entry.Id})");
        foreach (var t in result.Topics)
        {
            var state = t.Settled ? "settled" : t.BySilence ? "settled by silence" : t.Objected.Count > 0 ? $"objected by {string.Join(", ", t.Objected)}" : $"waiting for {string.Join(", ", t.Unanswered)}";
            var among = t.Competitors.Count > 1 ? $", won among {t.Competitors.Count} by hash" : "";
            var plan = t.Plan is { } p && p.Id != t.Live.Id ? $"; the plan stands at {p.Id} until this is accepted" : "";
            c.Out.WriteLine($"topic        {t.Topic}: {t.Live.Id} by {t.Live.Cell}, {state}{among}{plan}");
        }
        return result.ExitCode;
    }

    private static int Heartbeat(Context c)
    {
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        SayHelloIfNeeded(ledger, cell);
        var load = c.Options.GetValueOrDefault("--load") is { } l ? double.Parse(l, CultureInfo.InvariantCulture) : (double?)null;
        var entry = ledger.Append(cell, new HeartbeatPayload(load));
        store.Save(ledger);
        c.Out.WriteLine($"heartbeat {entry.Id}");
        return 0;
    }

    private static int Ledger(Context c)
    {
        if (!c.Home.IsInitialized)
        {
            c.Err.WriteLine($"no cell under {c.Home.Root}: run `creatura cell init`");
            return 1;
        }

        // Trusts the home's owner key and the owner who endorsed this cell: a joined vessel has no owner key of its own.
        using var cell = c.Home.OpenCell(c.Source, Position(c), c.Creatura);
        var (ledger, _) = OpenLedger(c, cell);

        if (c.Options.GetValueOrDefault("--path") is { } claimId)
        {
            var path = ledger.PathOf(claimId);
            if (path.Count == 0) throw new InvalidOperationException($"no claim {claimId} in the ledger");
            c.Out.WriteLine($"{claimId}: {ledger.StateOf(claimId)}");
            foreach (var entry in path) c.Out.WriteLine(Line(entry));
            return 0;
        }

        if (c.Options.ContainsKey("--open"))
        {
            var open = ledger.WhoIsWorkingOn(activeOnly: false).Where(w => w.State != ClaimState.Released).ToList();
            c.Out.WriteLine($"{open.Count} open claim{(open.Count == 1 ? "" : "s")}");
            foreach (var item in open)
            {
                var where = Where(item.Goal);
                c.Out.WriteLine($"{item.Claim.Id}  {(item.State == ClaimState.Active ? "active" : "handed off"),-10}  creatura {item.Creatura} on {item.VesselId}");
                c.Out.WriteLine($"    goal    {item.Goal.Goal}{(where.Length > 0 ? $"  ({where})" : "")}");
                c.Out.WriteLine($"    latest  {Line(item.Latest).Trim()}");
            }
            return 0;
        }

        var cells = ledger.Entries.Select(e => e.Cell).Distinct().Count();
        c.Out.WriteLine($"{ledger.Count} entr{(ledger.Count == 1 ? "y" : "ies")} from {cells} cell{(cells == 1 ? "" : "s")}, digest {ledger.Digest()[..16]}");
        foreach (var entry in ledger.Entries) c.Out.WriteLine(Line(entry));
        return 0;
    }

    /// <summary>The ledger as one Activity Streams 2.0 document, to a file or the console.</summary>
    private static int Activity(Context c)
    {
        if (!c.Home.IsInitialized)
        {
            c.Err.WriteLine($"no cell under {c.Home.Root}: run `creatura cell init`");
            return 1;
        }

        using var cell = c.Home.OpenCell(c.Source, Position(c), c.Creatura);
        WorkLedger ledger;
        using (LedgerLock.Acquire(c.Home.Root, InteractiveLockWait)) ledger = OpenLedger(c, cell).Ledger;
        var document = ActivityStream.Document(ledger);
        if (c.Options.GetValueOrDefault("--out") is { } path)
        {
            File.WriteAllText(path, document);
            c.Out.WriteLine($"activity     {ledger.Entries.Count(e => e.Kind != EntryKind.Heartbeat)} activities written to {path}");
        }
        else
        {
            c.Out.WriteLine(document);
        }

        return 0;
    }

    /// <summary>
    /// The activity stream as a page (docs/design-activity-stream.md): served on 127.0.0.1 only, read-only, from this
    /// home's ledger, read again for every request; opened in the browser unless --no-open. Runs until stopped.
    /// </summary>
    private static int Stream(Context c)
    {
        if (!c.Home.IsInitialized)
        {
            c.Err.WriteLine($"no cell under {c.Home.Root}: run `creatura cell init`");
            return 1;
        }

        using var cell = c.Home.OpenCell(c.Source, Position(c), c.Creatura);
        var port = c.Options.GetValueOrDefault("--port") is { } p ? int.Parse(p, CultureInfo.InvariantCulture) : 7357;
        WorkLedger Read()
        {
            using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
            return OpenLedger(c, cell).Ledger;
        }

        StreamServer server;
        try
        {
            server = StreamServer.Start(Read, port);
        }
        catch (System.Net.Sockets.SocketException e) when (e.SocketErrorCode == System.Net.Sockets.SocketError.AddressAlreadyInUse)
        {
            // Another stream already serves this machine's ledger there: open it rather than start a second one.
            var there = new Uri($"http://127.0.0.1:{port}/");
            if (StreamServer.IsStream(there))
            {
                c.Out.WriteLine($"stream       already running at {there}");
                if (!c.Options.ContainsKey("--no-open")) OpenInBrowser(there);
                return 0;
            }

            if (c.Options.ContainsKey("--port"))
            {
                c.Err.WriteLine($"creatura cell stream: port {port} is in use by something else; choose another with --port");
                return 2;
            }

            server = StreamServer.Start(Read, 0);
            c.Out.WriteLine($"port {port} is in use by something else; using {server.Address.Port}");
        }

        c.Out.WriteLine($"stream       {server.Address} (127.0.0.1 only, read-only; Ctrl+C to stop)");
        if (!c.Options.ContainsKey("--no-open")) OpenInBrowser(server.Address);

        using var stopped = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; stopped.Set(); };
        stopped.Wait();
        server.DisposeAsync().AsTask().GetAwaiter().GetResult();
        return 0;
    }

    private static void OpenInBrowser(Uri address)
    {
        try
        {
            var (file, arguments) = OperatingSystem.IsMacOS() ? ("open", address.ToString())
                : OperatingSystem.IsWindows() ? ("explorer.exe", address.ToString())
                : ("xdg-open", address.ToString());
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(file, arguments) { UseShellExecute = false })?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    private static string Line(SignedEntry e)
    {
        var summary = e.Kind switch
        {
            EntryKind.Hello => e.Body<HelloPayload>().Cell is var id ? $"creatura {id.Creatura} on {id.Hardware.VesselId}, {id.Position.Render()}, key {id.Fingerprint}" : "",
            EntryKind.Claim => e.Body<ClaimPayload>() is var claim ? claim.Goal + (Where(claim) is { Length: > 0 } w ? $"  ({w})" : "") : "",
            EntryKind.Progress => e.Body<ProgressPayload>() is var p
                ? $"on {p.Claim}: {p.Row}{(p.Stress is { } s ? $" (stress {s.ToString("0.##", CultureInfo.InvariantCulture)})" : "")}{(p.Blocked == true ? " [blocked]" : "")}{Turns(p)}" : "",
            EntryKind.HandOff => e.Body<HandOffPayload>() is var h ? $"on {h.Claim}: {h.Reason}" : "",
            EntryKind.Release => e.Body<ReleasePayload>() is var r ? $"on {r.Claim}: {r.Evidence}" : "",
            EntryKind.Heartbeat => e.Body<HeartbeatPayload>().Load is { } load ? $"load {load.ToString("0.##", CultureInfo.InvariantCulture)}" : "alive",
            EntryKind.Warning => e.Body<WarningPayload>() is var warning ? $"{warning.Subject}: {warning.Evidence}" : "",
            _ => "",
        };
        return $"{e.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}  {e.Id,-24}  {e.Kind,-9}  {summary}";
    }

    /// <summary>What a Progress entry says beyond its row, when it is a wait, a proposal or an answer (docs/design-turns.md).</summary>
    private static string Turns(ProgressPayload p) =>
        p.WaitingOn is { } on ? $" [waits on {on}]"
        : p.Answers is { } answers ? $" [{p.Verdict?.ToString().ToLowerInvariant()} {answers}]"
        : p.Topic is { } topic ? $" [proposal on {topic}, saw {p.Seen?.Count ?? 0}{(p.Artifact is { } a ? $", file {a}" : "")}]"
        : "";

    private static string Where(ClaimPayload claim) => string.Join(", ", new[]
    {
        claim.Repo is null ? null : claim.Branch is null ? claim.Repo : $"{claim.Repo}@{claim.Branch}",
        claim.Repo is null && claim.Branch is not null ? $"branch {claim.Branch}" : null,
        claim.Folder,
        claim.Tool is null ? null : claim.Session is null ? claim.Tool : $"{claim.Tool} {claim.Session}",
    }.OfType<string>());

    private static LocalCell OpenEndorsed(Context c)
    {
        if (!c.Home.IsInitialized) throw new InvalidOperationException($"no cell under {c.Home.Root}: run `creatura cell init`");
        var cell = c.Home.OpenCell(c.Source, Position(c), c.Creatura);
        if (cell.Identity.IsEndorsed) return cell;
        cell.Dispose();
        throw new InvalidOperationException($"this vessel's cell is not endorsed: run `creatura cell init`");
    }

    /// <summary>
    /// The position the command reports: the mesh when the mesh client's status says this node is on it, otherwise
    /// in-process with no process id (each run is a new process, and the cell should not say hello again for that).
    /// </summary>
    private static Position Position(Context c) =>
        (c.NoMesh ? null : Cells.Position.FromTailscaleStatus((c.Source ?? new PlatformVesselSource()).MeshStatusJson()))
        ?? new Position(PositionKind.InProcess) { Channel = "creatura cell" };

    private static (WorkLedger Ledger, ILedgerStore Store) OpenLedger(Context c, LocalCell cell)
    {
        using var owner = c.Home.LoadOwner();
        var trusted = new[] { owner?.PublicKey, cell.Identity.Endorsement?.OwnerPublicKey }.OfType<string>().Distinct();
        var ledger = new WorkLedger(trusted);
        c.Home.ApplyRevocations(ledger);
        var store = c.Home.Ledger;
        Report(c, LedgerStores.LoadInto(c.Home, store, ledger));
        return (ledger, store);
    }

    private static void Report(Context c, MergeResult loaded)
    {
        if (loaded.Rejected.Count > 0) c.Err.WriteLine($"{loaded.Rejected.Count} entr{(loaded.Rejected.Count == 1 ? "y" : "ies")} in the ledger file did not verify and were left out");
    }

    /// <summary>A hello when this cell has none in the ledger, or when what it said last no longer matches it (a move, a new position).</summary>
    internal static SignedEntry? SayHelloIfNeeded(WorkLedger ledger, LocalCell cell)
    {
        var said = ledger.HelloOf(cell.Id);
        if (said is not null && CellJson.Canonical(CellJson.ToElement(said.Body<HelloPayload>().Cell)) == CellJson.Canonical(CellJson.ToElement(cell.Identity))) return null;
        return ledger.Append(cell, new HelloPayload(cell.Identity));
    }

    private static string HookCommandPath(Context c) =>
        c.Options.GetValueOrDefault("--command") ?? Path.Combine(c.Home.Root, "bin", "creatura");

    private static System.Text.Json.Nodes.JsonObject Block(Context c) =>
        HooksSetup.HooksBlock(HookCommandPath(c), c.Home.Root, c.Options.GetValueOrDefault("--creatura") ?? HookCommand.DefaultCreatura,
            c.Options.GetValueOrDefault("--goal") is "prompt" ? "prompt" : "folder", c.Options.GetValueOrDefault("--store"));

    private static System.Text.Json.Nodes.JsonObject Legacy(Context c) =>
        HooksSetup.LegacyBlock(HookCommandPath(c), c.Home.Root, c.Options.GetValueOrDefault("--creatura") ?? HookCommand.DefaultCreatura,
            c.Options.GetValueOrDefault("--goal") is "prompt" ? "prompt" : "folder", c.Options.GetValueOrDefault("--store"));

    private static int HooksConfig(Context c)
    {
        var wrapper = new System.Text.Json.Nodes.JsonObject { ["hooks"] = Block(c) };
        c.Out.WriteLine(wrapper.ToJsonString(HooksSetup.Pretty));
        return 0;
    }

    private static int InstallHooks(Context c, bool install)
    {
        var settings = Required(c, "--settings");
        var dryRun = c.Options.ContainsKey("--dry-run");
        c.Out.WriteLine(install
            ? HooksSetup.Install(settings, Block(c), dryRun, DateTimeOffset.UtcNow, Legacy(c))
            : HooksSetup.Uninstall(settings, Block(c), dryRun, DateTimeOffset.UtcNow, Legacy(c)));
        return 0;
    }

    private static int PublishBin(Context c)
    {
        HooksSetup.PublishBin(HooksSetup.FindProject(c.Options.GetValueOrDefault("--project")), c.Home.Root, c.Out);
        return 0;
    }

    private static readonly JsonSerializerOptions Indented = new(CellJson.Options) { WriteIndented = true };

    /// <summary>Step one of joining: the request, public values only, written where the owner can be handed it.</summary>
    private static int Request(Context c)
    {
        var request = c.Home.Request(c.Source, Position(c), c.Creatura);
        var path = Path.GetFullPath(c.Options.GetValueOrDefault("--out") ?? Path.Combine(c.Home.Root, $"{c.Creatura}.request.json"));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(request, Indented));
        c.Out.WriteLine($"cell         {request.Cell}, creatura {request.Creatura} on vessel {request.VesselId}");
        c.Out.WriteLine($"request      {path} (public values only)");
        c.Out.WriteLine("next         on the owner's vessel: creatura cell endorse --request <that file>");
        return 0;
    }

    /// <summary>Step two, on the owner's vessel.</summary>
    private static int Endorse(Context c)
    {
        var requestPath = Path.GetFullPath(Required(c, "--request"));
        var request = JsonSerializer.Deserialize<EndorsementRequest>(File.ReadAllText(requestPath), CellJson.Options)
            ?? throw new FormatException($"{requestPath} is not an endorsement request");
        var endorsement = c.Home.EndorseRequest(request);
        var path = Path.GetFullPath(c.Options.GetValueOrDefault("--out")
            ?? Path.Combine(Path.GetDirectoryName(requestPath)!, $"{request.Name}.endorsement.json"));
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(endorsement, Indented));
        c.Out.WriteLine($"endorsed     {request.Cell}, creatura {request.Name} ({request.Creatura})");
        c.Out.WriteLine($"vessel       {request.VesselId}: {request.Hardware}");
        c.Out.WriteLine($"owner        {endorsement.OwnerFingerprint}");
        c.Out.WriteLine($"endorsement  {path}");
        c.Out.WriteLine("next         on the new vessel: creatura cell accept --endorsement <that file>");
        return 0;
    }

    /// <summary>Step three, back on the new vessel: keep the endorsement and say hello.</summary>
    private static int Accept(Context c)
    {
        var path = Path.GetFullPath(Required(c, "--endorsement"));
        var endorsement = JsonSerializer.Deserialize<VesselEndorsement>(File.ReadAllText(path), CellJson.Options)
            ?? throw new FormatException($"{path} is not an endorsement");
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var cell = c.Home.Accept(endorsement, c.Source, Position(c), c.Creatura);
        c.Out.WriteLine($"cell         {cell.Id}, creatura {cell.Identity.Creatura} on vessel {cell.Identity.Hardware.VesselId}");
        c.Out.WriteLine($"endorsed     by {endorsement.OwnerFingerprint} at {CellJson.Stamp(endorsement.IssuedAt)}");
        var (ledger, store) = OpenLedger(c, cell);
        var hello = SayHelloIfNeeded(ledger, cell);
        store.Save(ledger);
        c.Out.WriteLine(hello is null ? "hello        already in the ledger" : $"hello        {hello.Id} written to {store.Path}");
        return 0;
    }

    /// <summary>
    /// One sync with a hub. The ledger is read under the home's lock, the network work runs without it (a hook never
    /// waits on the network), and what came back is merged into a fresh read under the lock before the cursors are
    /// saved, so a crash in between asks the hub again instead of losing entries.
    /// </summary>
    private static int Sync(Context c)
    {
        if (!c.Home.IsInitialized)
        {
            c.Err.WriteLine($"no cell under {c.Home.Root}: run `creatura cell init`, or `request` on a vessel without the owner key");
            return 1;
        }

        var hub = new Uri(Required(c, "--hub"));
        using var cell = c.Home.OpenCell(c.Source, Position(c), c.Creatura);
        if (!cell.Identity.IsEndorsed) throw new InvalidOperationException($"{cell.Id} is not endorsed: run `creatura cell accept` first");

        WorkLedger snapshot;
        using (LedgerLock.Acquire(c.Home.Root, InteractiveLockWait)) snapshot = OpenLedger(c, cell).Ledger;
        var held = snapshot.Count;

        var syncDirectory = Path.Combine(c.Home.Root, "sync");
        OwnerOnly.CreateDirectory(syncDirectory);
        var statePath = Path.Combine(syncDirectory, $"{hub.Host}_{hub.Port}.json");
        var report = LedgerHub.SyncAsync(snapshot, cell, hub, statePath, ledger =>
        {
            using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
            var (fresh, store) = OpenLedger(c, cell);
            foreach (var revocation in ledger.Revocations) fresh.Revoke(revocation);
            fresh.MergeVerified(ledger.Entries);
            store.Save(fresh);
            c.Home.SaveRevocations(fresh.Revocations);
        }, CancellationToken.None).GetAwaiter().GetResult();

        var moved = report.Sent > 0 || report.Pulled.Added > 0 || report.Pulled.Rejected.Count > 0 || report.RevocationsTaken > 0 || !report.Pushed;
        if (c.Options.ContainsKey("--quiet") && !moved) return 0;
        if (c.Options.ContainsKey("--quiet")) c.Out.WriteLine($"{CellJson.Stamp(DateTimeOffset.UtcNow)} {cell.Id}");
        c.Out.WriteLine($"hub          {hub}");
        c.Out.WriteLine($"sent         {report.Sent} entr{(report.Sent == 1 ? "y" : "ies")}{(report.Pushed ? "" : $": the push failed: {report.Message}")}");
        c.Out.WriteLine($"received     {report.Pulled.Added} new, {report.Pulled.Duplicates} already held, {report.Pulled.Rejected.Count} refused");
        if (report.RevocationsTaken > 0) c.Out.WriteLine($"revocations  {report.RevocationsTaken} new from the hub");
        foreach (var rejection in report.Pulled.Rejected.Take(5)) c.Out.WriteLine($"  refused    {rejection.EntryId}: {rejection.Reason}");
        c.Out.WriteLine($"ledger       {held + report.Pulled.Added} entries, digest {snapshot.Digest()[..16]}");
        return report.Pushed ? 0 : 1;
    }

    /// <summary>The owner's public key, one line, for a hub's <c>--trust</c> file. The private key is never written or printed.</summary>
    private static int OwnerKeyCommand(Context c)
    {
        using var owner = c.Home.LoadOwner();
        if (owner is null)
        {
            c.Err.WriteLine($"no owner key under {c.Home.Root}: run this on the owner's vessel");
            return 1;
        }

        var line = $"# creatura owner {owner.Fingerprint}\n{owner.PublicKey}\n";
        if (c.Options.GetValueOrDefault("--out") is { Length: > 0 } path)
        {
            File.WriteAllText(path, line);
            c.Out.WriteLine($"owner        {owner.Fingerprint}");
            c.Out.WriteLine($"public key   {Path.GetFullPath(path)} (public only; give it to a hub with --trust)");
        }
        else
        {
            c.Out.Write(line);
        }
        return 0;
    }

    /// <summary>
    /// Revokes a cell with the owner key: trusted up to <c>--up-to</c> (default: the highest sequence this ledger holds
    /// for it with no gap), refused after. Applied to this home's ledger at once, kept in its revocations file, and
    /// handed to the hub now with <c>--hub</c> or at the next sync.
    /// </summary>
    private static int RevokeCommand(Context c)
    {
        var reason = Required(c, "--reason");
        var fromFile = c.Options.GetValueOrDefault("--endorsement") is { Length: > 0 } endorsementPath
            ? JsonSerializer.Deserialize<VesselEndorsement>(File.ReadAllText(endorsementPath), CellJson.Options) ?? throw new FormatException($"{endorsementPath} is not an endorsement")
            : null;
        var target = c.Options.GetValueOrDefault("--cell") ?? (fromFile is null ? Required(c, "--cell") : CellKey.CellIdOf(fromFile.CellPublicKey));
        using var owner = c.Home.LoadOwner();
        if (owner is null)
        {
            c.Err.WriteLine($"no owner key under {c.Home.Root}: revoke on the owner's vessel");
            return 1;
        }

        using var cell = c.Home.OpenCell(c.Source, Position(c), c.Creatura);
        Revocation revocation;
        using (LedgerLock.Acquire(c.Home.Root, InteractiveLockWait))
        {
            var (ledger, store) = OpenLedger(c, cell);
            var key = (fromFile is not null && CellKey.CellIdOf(fromFile.CellPublicKey) == target ? fromFile.CellPublicKey : null)
                ?? ledger.Entries.FirstOrDefault(e => e.Cell == target)?.Endorsement.CellPublicKey
                ?? throw new InvalidOperationException($"no entry from {target} in this ledger, so its key is not known here: sync first, or pass --endorsement <the file you endorsed it with>");
            var upTo = c.Options.GetValueOrDefault("--up-to") is { } given ? long.Parse(given, CultureInfo.InvariantCulture) : ledger.Cursor.Of(target);
            revocation = owner.Revoke(key, upTo, reason, DateTimeOffset.UtcNow);
            var before = ledger.Count;
            ledger.Revoke(revocation);
            store.Save(ledger);
            c.Home.SaveRevocations([revocation]);
            c.Out.WriteLine($"revoked      {target} after sequence {upTo}: {reason}");
            c.Out.WriteLine($"ledger       {before - ledger.Count} entr{(before - ledger.Count == 1 ? "y" : "ies")} past it dropped here; kept in {c.Home.RevocationsPath}");
        }

        if (c.Options.GetValueOrDefault("--hub") is { Length: > 0 } hub)
        {
            var theirs = LedgerHub.ExchangeRevocationsAsync(new Uri(hub), cell, [revocation], CancellationToken.None).GetAwaiter().GetResult();
            c.Out.WriteLine($"hub          {hub} now holds {theirs.Count} revocation{(theirs.Count == 1 ? "" : "s")}");
        }
        else
        {
            c.Out.WriteLine("hub          handed over at the next sync");
        }
        return 0;
    }

    private static int PublishHubCommand(Context c)
    {
        HooksSetup.PublishHub(HooksSetup.FindHubProject(c.Options.GetValueOrDefault("--project")), c.Home.Root, c.Out);
        return 0;
    }

    /// <summary>The launchd agents these options describe, by label: the sync agent always, the hub agent with --listen.</summary>
    private static Dictionary<string, string> LaunchdPlists(Context c)
    {
        var hub = Required(c, "--hub");
        _ = new Uri(hub);
        var interval = c.Options.GetValueOrDefault("--interval") is { } i ? int.Parse(i, CultureInfo.InvariantCulture) : 60;
        if (interval < 10) throw new ArgumentException("--interval is at least 10 seconds");
        var plists = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [LaunchdSetup.SyncLabel] = LaunchdSetup.SyncPlist(HookCommandPath(c), c.Home.Root, c.Creatura, hub, interval, Path.Combine(c.Home.Root, "sync", "sync.log")),
        };
        if (c.Options.GetValueOrDefault("--listen") is { Length: > 0 } listen)
        {
            var hubDirectory = Path.Combine(c.Home.Root, "hub");
            plists[LaunchdSetup.HubLabel] = LaunchdSetup.HubPlist(HooksSetup.Dotnet(), Path.Combine(hubDirectory, "app", "Creatura.Cells.Hub.dll"), listen,
                Path.GetFullPath(c.Options.GetValueOrDefault("--trust") ?? Path.Combine(hubDirectory, "owners.txt")), hubDirectory, Path.Combine(hubDirectory, "hub.log"));
        }
        return plists;
    }

    private static string AgentsDirectory(Context c) => c.Options.GetValueOrDefault("--agents") ?? LaunchdSetup.AgentsDirectory;

    private static int LaunchdConfig(Context c)
    {
        foreach (var (label, text) in LaunchdPlists(c))
        {
            c.Out.WriteLine($"<!-- {Path.Combine(AgentsDirectory(c), label + ".plist")} -->");
            c.Out.Write(text);
        }
        return 0;
    }

    private static int InstallLaunchd(Context c)
    {
        var plists = LaunchdPlists(c);
        var missing = new List<string>();
        if (!File.Exists(HookCommandPath(c))) missing.Add($"{HookCommandPath(c)} (run `creatura cell publish-bin`)");
        if (plists.ContainsKey(LaunchdSetup.HubLabel))
        {
            var hubDirectory = Path.Combine(c.Home.Root, "hub");
            if (!File.Exists(Path.Combine(hubDirectory, "app", "Creatura.Cells.Hub.dll"))) missing.Add($"{Path.Combine(hubDirectory, "app")} (run `creatura cell publish-hub`)");
            var trust = Path.GetFullPath(c.Options.GetValueOrDefault("--trust") ?? Path.Combine(hubDirectory, "owners.txt"));
            if (!File.Exists(trust)) missing.Add($"{trust} (run `creatura cell owner-key --out {trust}`)");
        }
        if (missing.Count > 0) throw new InvalidOperationException("not installed, missing: " + string.Join("; ", missing));

        if (c.Options.ContainsKey("--dry-run"))
        {
            LaunchdConfig(c);
            c.Out.WriteLine($"dry run: nothing written to {AgentsDirectory(c)}");
            return 0;
        }
        OwnerOnly.CreateDirectory(Path.Combine(c.Home.Root, "sync"));
        foreach (var line in LaunchdSetup.Install(plists, AgentsDirectory(c), load: !c.Options.ContainsKey("--no-load"), DateTimeOffset.Now)) c.Out.WriteLine(line);
        return 0;
    }

    private static int UninstallLaunchd(Context c)
    {
        foreach (var line in LaunchdSetup.Uninstall([LaunchdSetup.SyncLabel, LaunchdSetup.HubLabel], AgentsDirectory(c), unload: !c.Options.ContainsKey("--no-load"))) c.Out.WriteLine(line);
        return 0;
    }

    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "text/plain", [".log"] = "text/plain", [".md"] = "text/markdown", [".csv"] = "text/csv", [".html"] = "text/html",
        [".json"] = "application/json", [".jsonl"] = "application/x-ndjson", [".xml"] = "application/xml", [".yaml"] = "application/yaml", [".yml"] = "application/yaml",
        [".cs"] = "text/plain", [".py"] = "text/plain", [".js"] = "text/javascript", [".ts"] = "text/plain", [".sh"] = "text/plain",
        [".png"] = "image/png", [".jpg"] = "image/jpeg", [".jpeg"] = "image/jpeg", [".gif"] = "image/gif", [".webp"] = "image/webp", [".svg"] = "image/svg+xml",
        [".pdf"] = "application/pdf", [".zip"] = "application/zip", [".mp3"] = "audio/mpeg", [".wav"] = "audio/wav", [".mp4"] = "video/mp4",
    };

    private static LocalCell EndorsedCell(Context c)
    {
        if (!c.Home.IsInitialized) throw new InvalidOperationException($"no cell under {c.Home.Root}: run `creatura cell init`, or `request` on a vessel without the owner key");
        var cell = c.Home.OpenCell(c.Source, Position(c), c.Creatura);
        if (cell.Identity.IsEndorsed) return cell;
        cell.Dispose();
        throw new InvalidOperationException($"the {c.Creatura} cell is not endorsed: run `creatura cell accept` first");
    }

    /// <summary>
    /// Seals a file for its recipients, uploads the ciphertext to the hub, keeps a copy here, and only then records the
    /// Artifact entry, so an entry never names bytes the hub does not have. The other cells see it after their next sync.
    /// </summary>
    private static int Share(Context c)
    {
        var path = Path.GetFullPath(Required(c, "--file"));
        var hub = new Uri(Required(c, "--hub"));
        using var cell = EndorsedCell(c);

        WorkLedger snapshot;
        using (LedgerLock.Acquire(c.Home.Root, InteractiveLockWait)) snapshot = OpenLedger(c, cell).Ledger;
        var only = c.Options.GetValueOrDefault("--to") is { Length: > 0 } to ? to.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries) : null;
        var (sealedFile, recipients, type) = Upload(c, cell, snapshot, path, hub, only);
        var size = new FileInfo(path).Length;

        SignedEntry entry;
        using (LedgerLock.Acquire(c.Home.Root, InteractiveLockWait))
        {
            var (ledger, store) = OpenLedger(c, cell);
            SayHelloIfNeeded(ledger, cell);
            var claim = c.Options.GetValueOrDefault("--claim") ?? ledger.LatestActiveClaimOf(cell.Id)?.Id;
            entry = ledger.Append(cell, sealedFile.Payload with { Claim = claim });
            store.Save(ledger);
        }

        c.Out.WriteLine($"shared       {entry.Id}");
        c.Out.WriteLine($"file         {Path.GetFileName(path)} ({type}, {size:N0} bytes), sealed for {recipients.Count} cell{(recipients.Count == 1 ? "" : "s")}");
        c.Out.WriteLine($"hub          {hub} holds the ciphertext for 90 days; the other cells see the entry after their next sync");
        return 0;
    }

    /// <summary>
    /// A file sealed for its recipients (every endorsed cell, or only those named), uploaded to the hub, with a copy
    /// kept here: what <c>share</c> and <c>propose --file</c> do before they write an entry, so an entry never names
    /// bytes the hub does not have.
    /// </summary>
    private static (SealedFile File, IReadOnlyList<Recipient> Recipients, string Type) Upload(Context c, LocalCell cell, WorkLedger snapshot, string path, Uri hub, IEnumerable<string>? only)
    {
        var size = new FileInfo(path).Length;
        if (size > SharedFiles.MaxBytes) throw new SharedFileException(SharedFileProblem.TooLarge, $"{Path.GetFileName(path)} is {size:N0} bytes; the limit is {SharedFiles.MaxBytes:N0}");
        var recipients = SharedFiles.Recipients(snapshot, cell, only);
        var type = c.Options.GetValueOrDefault("--type") ?? ContentTypes.GetValueOrDefault(Path.GetExtension(path)) ?? "application/octet-stream";
        var sealedFile = SharedFiles.Seal(File.ReadAllBytes(path), Path.GetFileName(path), type, recipients);

        LedgerHub.PutFileAsync(hub, cell, sealedFile.Ciphertext, CancellationToken.None).GetAwaiter().GetResult();
        OwnerOnly.CreateDirectory(c.Home.FilesDirectory);
        OwnerOnly.Replace(Path.Combine(c.Home.FilesDirectory, sealedFile.Payload.Ciphertext), sealedFile.Ciphertext);
        return (sealedFile, recipients, type);
    }

    /// <summary>The Artifact entries this cell can open, labels read without downloading anything.</summary>
    private static int Files(Context c)
    {
        using var cell = EndorsedCell(c);
        var (ledger, _) = OpenLedger(c, cell);
        var claim = c.Options.GetValueOrDefault("--claim");
        var mine = ledger.Entries
            .Where(e => e.Kind == EntryKind.Artifact && (claim is null || e.ClaimRef == claim))
            .Select(e => (Entry: e, Payload: e.Body<ArtifactPayload>()))
            .Where(a => a.Payload.Keys.Any(k => k.Cell == cell.Id))
            .ToList();
        c.Out.WriteLine($"{mine.Count} file{(mine.Count == 1 ? "" : "s")} {cell.Id} can open");
        foreach (var (entry, payload) in mine)
        {
            string label;
            try
            {
                var (name, type) = SharedFiles.Label(payload, cell);
                label = $"{name}  {type}";
            }
            catch (SharedFileException e)
            {
                label = $"(cannot open: {e.Message})";
            }
            var kept = File.Exists(Path.Combine(c.Home.FilesDirectory, payload.Ciphertext)) ? ", kept here" : "";
            c.Out.WriteLine($"{entry.Id,-28} {label}  {payload.Size:N0} bytes  from {entry.Cell} at {CellJson.Stamp(entry.Timestamp)[..16]}Z{kept}");
            if (payload.Claim is { } about) c.Out.WriteLine($"{"",-28} for claim {about}");
        }
        return 0;
    }

    /// <summary>
    /// Fetches a shared file: this vessel's copy when it has one, the hub's otherwise; checked and opened before
    /// anything is written, so an altered file leaves nothing behind.
    /// </summary>
    private static int Fetch(Context c)
    {
        var id = Required(c, "--artifact");
        var output = Path.GetFullPath(Required(c, "--out"));
        if (File.Exists(output)) throw new InvalidOperationException($"{output} already exists");
        using var cell = EndorsedCell(c);
        var (ledger, _) = OpenLedger(c, cell);
        var entry = ledger.Entries.FirstOrDefault(e => e.Id == id && e.Kind == EntryKind.Artifact)
            ?? throw new InvalidOperationException($"no shared file {id} in this ledger: sync first");
        var payload = entry.Body<ArtifactPayload>();

        OwnerOnly.CreateDirectory(c.Home.FilesDirectory);
        var copy = Path.Combine(c.Home.FilesDirectory, payload.Ciphertext);
        var local = File.Exists(copy);
        byte[] ciphertext;
        if (local)
        {
            ciphertext = File.ReadAllBytes(copy);
        }
        else
        {
            var hub = c.Options.GetValueOrDefault("--hub") is { Length: > 0 } h ? new Uri(h) : throw new ArgumentException("--hub <url> is required: this vessel has no copy of the file");
            ciphertext = LedgerHub.GetFileAsync(hub, cell, payload.Ciphertext, CancellationToken.None).GetAwaiter().GetResult();
        }

        var opened = SharedFiles.Open(ciphertext, payload, cell);
        if (!local) OwnerOnly.Replace(copy, ciphertext);
        var temporary = output + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        File.WriteAllBytes(temporary, opened.Content);
        File.Move(temporary, output);
        c.Out.WriteLine($"fetched      {opened.Name} ({opened.ContentType}, {opened.Content.Length:N0} bytes) {(local ? "from this vessel's copy" : "from the hub")}");
        c.Out.WriteLine($"written      {output}");
        return 0;
    }

    private static string Required(Context c, string option) =>
        c.Options.GetValueOrDefault(option) is { Length: > 0 } value ? value : throw new ArgumentException($"{option} <text> is required");

    /// <summary>The options of a command. <c>--quiet</c> is a flag for <c>sync</c> and takes minutes for <c>turn</c> and <c>offer</c>.</summary>
    private static Dictionary<string, string?> Parse(string command, string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"unexpected argument: {name}");
            if (Flags.Contains(name) || (name == "--quiet" && command == "sync"))
            {
                options[name] = null;
                continue;
            }
            if (i + 1 >= args.Length) throw new ArgumentException($"{name} needs a value");
            options[name] = args[++i];
        }
        return options;
    }

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        return 2;
    }
}
