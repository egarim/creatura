using System.Globalization;
using System.Text.Json.Nodes;

namespace Creatura.Cells;

/// <summary>One cell as the Fleet shows it: who it is and where, the work it holds and for how long, its last progress, and when it was last seen.</summary>
public sealed record FleetRow(
    string Cell,
    string Name,
    string Kind,
    string Creatura,
    string Vessel,
    string VesselName,
    string Ring,
    DateTimeOffset LastSeen,
    bool Quiet)
{
    /// <summary>The goal of the claim it is on, its entry and since when; null when it holds none.</summary>
    public string? Claim { get; init; }

    public string? ClaimId { get; init; }

    public DateTimeOffset? ClaimedAt { get; init; }

    /// <summary>The last progress row on that claim, with the stress it reported and whether it said it was blocked.</summary>
    public string? Progress { get; init; }

    public double? Stress { get; init; }

    public bool Blocked { get; init; }

    /// <summary>The vessel's load from its last heartbeat.</summary>
    public double? Load { get; init; }

    /// <summary>Offers to this cell or its vessel not yet taken, declined, withdrawn or expired.</summary>
    public int PendingOffers { get; init; }
}

/// <summary>
/// The Fleet (docs/design-console.md, U3): who is working on what, now. One row per cell in the verified ledger, a
/// revoked cell left out, the most recently seen first; a cell not seen for <see cref="QuietTime"/> is quiet, the same
/// fifteen minutes as the stream page's. Like the stream it is a view, never the record: a pure function of the
/// ledger and the time it is read at, so two homes with the same entries show the same Fleet byte for byte.
/// </summary>
public static class FleetView
{
    /// <summary>When a cell not heard from is shown as quiet: the stream page's QUIET_MINUTES.</summary>
    public static readonly TimeSpan QuietTime = TimeSpan.FromMinutes(15);

    public static IReadOnlyList<FleetRow> Rows(WorkLedger ledger, DateTimeOffset now)
    {
        var names = ActivityStream.ActorNames(ledger);
        var rows = new List<FleetRow>();
        foreach (var group in ledger.Entries.GroupBy(e => e.Cell).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            if (ledger.IsRevoked(group.Key)) continue;
            var entries = group.ToList();
            var last = entries[^1];
            var hello = entries.LastOrDefault(e => e.Kind == EntryKind.Hello)?.Body<HelloPayload>().Cell;
            var vessel = last.Endorsement.VesselId;
            var claim = ledger.LatestActiveClaimOf(group.Key);
            var progress = claim is null ? null : ledger.PathOf(claim.Id).LastOrDefault(e => e.Kind == EntryKind.Progress)?.Body<ProgressPayload>();
            rows.Add(new FleetRow(group.Key, names[group.Key], ActivityStream.KindOf(entries), last.Endorsement.Creatura, vessel,
                ActivityStream.VesselName(hello, vessel), ActivityStream.Ring(vessel), last.Timestamp, now - last.Timestamp > QuietTime)
            {
                Claim = claim?.Body<ClaimPayload>().Goal,
                ClaimId = claim?.Id,
                ClaimedAt = claim?.Timestamp,
                Progress = progress?.Row,
                Stress = progress?.Stress,
                Blocked = progress?.Blocked == true,
                Load = entries.LastOrDefault(e => e.Kind == EntryKind.Heartbeat)?.Body<HeartbeatPayload>().Load,
                PendingOffers = ledger.PendingOffers(group.Key, vessel, now).Count,
            });
        }

        // The most recently seen first; the id breaks a tie, so the order is the same on every home.
        return [.. rows.OrderByDescending(r => r.LastSeen).ThenBy(r => r.Cell, StringComparer.Ordinal)];
    }

    /// <summary>The Fleet as JSON: what <c>creatura cell fleet --json</c> writes and the console reads.</summary>
    public static string Document(WorkLedger ledger, DateTimeOffset now)
    {
        var cells = new JsonArray();
        foreach (var r in Rows(ledger, now))
        {
            var o = new JsonObject
            {
                ["cell"] = r.Cell,
                ["name"] = r.Name,
                ["kind"] = r.Kind,
                ["creatura"] = r.Creatura,
                ["vessel"] = r.Vessel,
                ["vesselName"] = r.VesselName,
                ["ring"] = r.Ring,
                ["lastSeen"] = Stamp(r.LastSeen),
                ["quiet"] = r.Quiet,
            };
            if (r.Claim is not null)
            {
                o["claim"] = r.Claim;
                o["claimId"] = r.ClaimId;
                o["claimedAt"] = Stamp(r.ClaimedAt!.Value);
            }

            if (r.Progress is not null) o["progress"] = r.Progress;
            if (r.Stress is { } stress) o["stress"] = stress;
            if (r.Blocked) o["blocked"] = true;
            if (r.Load is { } load) o["load"] = load;
            if (r.PendingOffers > 0) o["pendingOffers"] = r.PendingOffers;
            cells.Add(o);
        }

        return new JsonObject
        {
            ["format"] = "creatura.fleet/1",
            ["at"] = Stamp(now),
            ["quietMinutes"] = (int)QuietTime.TotalMinutes,
            ["cells"] = cells,
        }.ToJsonString();
    }

    /// <summary>The Fleet as text, for a terminal with no screen and for an agent: one line per cell.</summary>
    public static string Render(IReadOnlyList<FleetRow> rows, DateTimeOffset now)
    {
        if (rows.Count == 0) return "no cells in this ledger yet\n";
        var lines = rows.Select(r =>
        {
            var work = r.Claim is null ? "holds no claim" : $"on '{r.Claim}' for {Ago(now - r.ClaimedAt!.Value)}";
            var progress = r.Progress is null ? "" : $"; last: {r.Progress}";
            var stress = r.Stress is { } s ? string.Create(CultureInfo.InvariantCulture, $"; stress {s:0.00}") : "";
            var blocked = r.Blocked ? "; BLOCKED" : "";
            var offers = r.PendingOffers > 0 ? string.Create(CultureInfo.InvariantCulture, $"; {r.PendingOffers} offer{(r.PendingOffers == 1 ? "" : "s")} waiting") : "";
            var seen = (r.Quiet ? "QUIET, " : "") + $"seen {Ago(now - r.LastSeen)} ago";
            return $"{r.Name} ({r.Creatura}, {r.Cell}): {work}{progress}{stress}{blocked}{offers}; {seen}";
        });
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>A span in the largest unit that fits: seconds, minutes, hours or days.</summary>
    public static string Ago(TimeSpan span)
    {
        var ic = CultureInfo.InvariantCulture;
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        return span.TotalMinutes < 1 ? string.Create(ic, $"{(int)span.TotalSeconds} s")
            : span.TotalHours < 1 ? string.Create(ic, $"{(int)span.TotalMinutes} min")
            : span.TotalDays < 1 ? string.Create(ic, $"{span.TotalHours:0.#} h")
            : string.Create(ic, $"{span.TotalDays:0.#} d");
    }

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);
}
