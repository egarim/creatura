using System.Globalization;
using System.Text.Json;

namespace Creatura.Cells;

/// <summary>
/// <c>creatura cell ...</c>: the cell on this vessel, from a shell or a host tool's hook. Everything it writes goes
/// under <c>--home</c> (default <c>~/.creatura</c>, or <c>$CREATURA_HOME</c>). It prints ids, hashes and fingerprints,
/// never a private key or the raw hardware identifier. Exit codes: 0 done, 1 not initialized, 2 a usage or ledger error.
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

        joining Claude Code (docs/design-cells.md, "How host tools join"):
          hook                         one hook event on stdin; prints nothing, always exits 0 (--goal folder|prompt)
          hooks-config                 the "hooks" block for ~/.claude/settings.json (--command <path to creatura>)
          install-hooks --settings <f> merge that block into a settings file, backing it up first (--dry-run: report only)
          uninstall-hooks --settings <f>  remove exactly that block again (--dry-run: report only)
          publish-bin                  publish this CLI into <home>/bin with a `creatura` wrapper (--project <csproj>)

        progress, release and handoff take --session <id> to name the claim of a host-tool session.

        options for every command:
          --home <dir>                 where keys, record and ledger live (default ~/.creatura, or $CREATURA_HOME)
          --creatura <name>            which creatura in that home (default creatura)
          --no-mesh                    do not read the mesh client's status
        """;

    private static readonly HashSet<string> Flags = new(StringComparer.Ordinal) { "--open", "--blocked", "--no-mesh", "--help", "--dry-run" };

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
        try
        {
            options = Parse(args[2..]);
        }
        catch (ArgumentException e)
        {
            error.WriteLine(e.Message);
            return 2;
        }

        var home = new CellHome(options.GetValueOrDefault("--home") ?? Environment.GetEnvironmentVariable("CREATURA_HOME"));
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
                "hooks-config" => HooksConfig(context),
                "install-hooks" => InstallHooks(context, install: true),
                "uninstall-hooks" => InstallHooks(context, install: false),
                "publish-bin" => PublishBin(context),
                var other => Fail(error, $"unknown command: cell {other}\n\n{Usage}"),
            };
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or FormatException or ArgumentException
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
        c.Out.WriteLine($"vessel       {id.Hardware.VesselId} (the hardware identifier, hashed with the owner's salt)");
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

    private static int OnClaim<T>(Context c, string field, Func<string, string, T> payload) where T : IEntryPayload
    {
        var text = Required(c, field);
        using var locked = LedgerLock.Acquire(c.Home.Root, InteractiveLockWait);
        using var cell = OpenEndorsed(c);
        var (ledger, store) = OpenLedger(c, cell);
        var claim = c.Options.GetValueOrDefault("--claim")
            ?? (c.Options.GetValueOrDefault("--session") is { } session
                ? ledger.WhoIsWorkingOn(activeOnly: false).LastOrDefault(w => w.Goal.Session == session && w.State != ClaimState.Released)?.Claim.Id
                  ?? throw new InvalidOperationException($"no open claim for session {session} in the ledger")
                : null)
            ?? ledger.LatestActiveClaimOf(cell.Id)?.Id
            ?? throw new InvalidOperationException($"{cell.Id} has no active claim: run `creatura cell claim --goal ...` or pass --claim <id>");
        if (ledger.PathOf(claim).Count == 0) throw new InvalidOperationException($"no claim {claim} in the ledger");
        SayHelloIfNeeded(ledger, cell);
        var entry = ledger.Append(cell, payload(claim, text));
        store.Save(ledger);
        c.Out.WriteLine($"{T.Kind.ToString().ToLowerInvariant()} {entry.Id} on {claim}: {text}");
        return 0;
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

        using var owner = c.Home.LoadOwner();
        var ledger = new WorkLedger(owner is null ? [] : [owner.PublicKey]);
        Report(c, c.Home.Ledger.LoadInto(ledger));

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

    private static string Line(SignedEntry e)
    {
        var summary = e.Kind switch
        {
            EntryKind.Hello => e.Body<HelloPayload>().Cell is var id ? $"creatura {id.Creatura} on {id.Hardware.VesselId}, {id.Position.Render()}, key {id.Fingerprint}" : "",
            EntryKind.Claim => e.Body<ClaimPayload>() is var claim ? claim.Goal + (Where(claim) is { Length: > 0 } w ? $"  ({w})" : "") : "",
            EntryKind.Progress => e.Body<ProgressPayload>() is var p
                ? $"on {p.Claim}: {p.Row}{(p.Stress is { } s ? $" (stress {s.ToString("0.##", CultureInfo.InvariantCulture)})" : "")}{(p.Blocked == true ? " [blocked]" : "")}" : "",
            EntryKind.HandOff => e.Body<HandOffPayload>() is var h ? $"on {h.Claim}: {h.Reason}" : "",
            EntryKind.Release => e.Body<ReleasePayload>() is var r ? $"on {r.Claim}: {r.Evidence}" : "",
            EntryKind.Heartbeat => e.Body<HeartbeatPayload>().Load is { } load ? $"load {load.ToString("0.##", CultureInfo.InvariantCulture)}" : "alive",
            _ => "",
        };
        return $"{e.Timestamp.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}  {e.Id,-24}  {e.Kind,-9}  {summary}";
    }

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

    private static (WorkLedger Ledger, JsonLedgerStore Store) OpenLedger(Context c, LocalCell cell)
    {
        using var owner = c.Home.LoadOwner();
        var trusted = new[] { owner?.PublicKey, cell.Identity.Endorsement?.OwnerPublicKey }.OfType<string>().Distinct();
        var ledger = new WorkLedger(trusted);
        var store = c.Home.Ledger;
        Report(c, store.LoadInto(ledger));
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
            c.Options.GetValueOrDefault("--goal") is "prompt" ? "prompt" : "folder");

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
            ? HooksSetup.Install(settings, Block(c), dryRun, DateTimeOffset.UtcNow)
            : HooksSetup.Uninstall(settings, Block(c), dryRun, DateTimeOffset.UtcNow));
        return 0;
    }

    private static int PublishBin(Context c)
    {
        HooksSetup.PublishBin(HooksSetup.FindProject(c.Options.GetValueOrDefault("--project")), c.Home.Root, c.Out);
        return 0;
    }

    private static string Required(Context c, string option) =>
        c.Options.GetValueOrDefault(option) is { Length: > 0 } value ? value : throw new ArgumentException($"{option} <text> is required");

    private static Dictionary<string, string?> Parse(string[] args)
    {
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            var name = args[i];
            if (!name.StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException($"unexpected argument: {name}");
            if (Flags.Contains(name))
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
