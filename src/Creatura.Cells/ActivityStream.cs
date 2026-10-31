using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace Creatura.Cells;

/// <summary>
/// The work ledger as an Activity Streams 2.0 collection (docs/design-activity-stream.md): each entry one activity,
/// newest first, except heartbeats, which only update their cell's actor. A view, never the record: it is a pure
/// function of the verified ledger (revocations included), every activity carries its entry's cell, sequence and
/// signature, and nothing the ledger seals is copied into it (an artifact shows its size and its recipients' count,
/// never its name, ciphertext, hashes or keys). The same entries give the same bytes on every home (C23).
/// </summary>
public static class ActivityStream
{
    public const string MediaType = "application/activity+json";

    public const int DefaultPageSize = 200;

    private const string Standard = "https://www.w3.org/ns/activitystreams";

    /// <summary>The ring colours a vessel is given, by its id, the same on every page and machine.</summary>
    public static readonly IReadOnlyList<string> Rings = ["#2f6f8f", "#b5683a", "#4f8a4a", "#8a4f86", "#a68a2c", "#3f5fb0", "#9a3f4a", "#3a8a84"];

    /// <summary>The whole stream in one document, every activity inline: what <c>creatura cell activity</c> writes.</summary>
    public static string Document(WorkLedger ledger)
    {
        var activities = Activities(ledger);
        var collection = Head("OrderedCollection", "urn:creatura:stream");
        collection["summary"] = Summary(ledger, activities.Count);
        collection["totalItems"] = activities.Count;
        collection["creatura:actors"] = new JsonArray([.. Actors(ledger)]);
        collection["orderedItems"] = new JsonArray([.. activities]);
        return collection.ToJsonString();
    }

    /// <summary>The collection as served at <c>/activity</c>: the actors, the count, and links to its pages.</summary>
    public static string Collection(WorkLedger ledger, int pageSize = DefaultPageSize)
    {
        var total = Activities(ledger).Count;
        var collection = Head("OrderedCollection", "activity");
        collection["summary"] = Summary(ledger, total);
        collection["totalItems"] = total;
        collection["first"] = "activity?page=1";
        collection["last"] = $"activity?page={Pages(total, pageSize)}";
        collection["creatura:actors"] = new JsonArray([.. Actors(ledger)]);
        return collection.ToJsonString();
    }

    /// <summary>One page of the collection, numbered from 1, newest first.</summary>
    public static string Page(WorkLedger ledger, int page, int pageSize = DefaultPageSize)
    {
        if (page < 1) throw new ArgumentOutOfRangeException(nameof(page), "pages are numbered from 1");
        if (pageSize < 1) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var activities = Activities(ledger);
        var pages = Pages(activities.Count, pageSize);
        var result = Head("OrderedCollectionPage", $"activity?page={page}");
        result["partOf"] = "activity";
        result["startIndex"] = (page - 1) * pageSize;
        if (page > 1) result["prev"] = $"activity?page={page - 1}";
        if (page < pages) result["next"] = $"activity?page={page + 1}";
        result["orderedItems"] = new JsonArray([.. activities.Skip((page - 1) * pageSize).Take(pageSize)]);
        return result.ToJsonString();
    }

    /// <summary>Every activity, newest first: one per entry that is not a heartbeat.</summary>
    public static IReadOnlyList<JsonObject> Activities(WorkLedger ledger)
    {
        var names = ActorNames(ledger);
        var offers = ledger.OfferStates().ToDictionary(o => o.Offer.Id, StringComparer.Ordinal);
        var vessels = VesselNames(ledger);
        return ledger.Entries.Where(e => e.Kind != EntryKind.Heartbeat).Reverse().Select(e => Activity(e, names, offers, vessels)).ToList();
    }

    /// <summary>One actor per cell in the ledger, in id order: a <c>Service</c> with its name, kind, vessel, ring colour, last-seen time and load.</summary>
    public static IReadOnlyList<JsonObject> Actors(WorkLedger ledger)
    {
        var names = ActorNames(ledger);
        var actors = new List<JsonObject>();
        foreach (var group in ledger.Entries.GroupBy(e => e.Cell).OrderBy(g => g.Key, StringComparer.Ordinal))
        {
            var entries = group.ToList();
            var hello = entries.LastOrDefault(e => e.Kind == EntryKind.Hello)?.Body<HelloPayload>().Cell;
            var vessel = entries[^1].Endorsement.VesselId;
            var actor = new JsonObject
            {
                ["id"] = ActorId(group.Key),
                ["type"] = "Service",
                ["name"] = names[group.Key],
                ["icon"] = new JsonObject { ["type"] = "Image", ["mediaType"] = "image/svg+xml", ["url"] = "icon.svg" },
                ["creatura:kind"] = KindOf(entries),
                ["creatura:vessel"] = vessel,
                ["creatura:vesselName"] = VesselName(hello, vessel),
                ["creatura:ring"] = Ring(vessel),
                ["creatura:lastSeen"] = Stamp(entries[^1].Timestamp),
            };
            if (hello is not null) actor["creatura:hardware"] = hello.Hardware.Render();
            if (entries.LastOrDefault(e => e.Kind == EntryKind.Heartbeat)?.Body<HeartbeatPayload>().Load is { } load) actor["creatura:load"] = load;
            actors.Add(actor);
        }

        // The owners who offered goals, as people.
        foreach (var owner in ledger.OfferStates().Select(o => o.Offer).GroupBy(o => o.OwnerPublicKey).OrderBy(g => g.First().OwnerFingerprint, StringComparer.Ordinal))
        {
            var fingerprint = owner.First().OwnerFingerprint;
            actors.Add(new JsonObject
            {
                ["id"] = OwnerId(fingerprint),
                ["type"] = "Person",
                ["name"] = "the owner",
                ["icon"] = new JsonObject { ["type"] = "Image", ["mediaType"] = "image/svg+xml", ["url"] = "icon.svg" },
                ["creatura:kind"] = "owner",
                ["creatura:fingerprint"] = fingerprint,
            });
        }

        return actors;
    }

    /// <summary>The ring colour of a vessel: one of <see cref="Rings"/>, chosen by the first byte of the SHA-256 of its id.</summary>
    public static string Ring(string vesselId) => Rings[SHA256.HashData(Encoding.UTF8.GetBytes(vesselId))[0] % Rings.Count];

    private static JsonObject Head(string type, string id) => new()
    {
        ["@context"] = new JsonArray(Standard, new JsonObject { ["creatura"] = "urn:creatura:ns#" }),
        ["type"] = type,
        ["id"] = id,
    };

    private static string Summary(WorkLedger ledger, int activities)
    {
        var cells = ledger.Entries.Select(e => e.Cell).Distinct().Count();
        return $"{activities} activit{(activities == 1 ? "y" : "ies")} from {cells} cell{(cells == 1 ? "" : "s")}";
    }

    private static int Pages(int total, int pageSize) => Math.Max(1, (total + pageSize - 1) / pageSize);

    private static string ActorId(string cell) => $"urn:creatura:cell:{cell}";

    private static string ClaimId(string claimEntryId) => $"urn:creatura:claim:{claimEntryId}";

    private static string OfferId(string offerId) => $"urn:creatura:offer:{offerId}";

    private static string OwnerId(string fingerprint) => $"urn:creatura:owner:{fingerprint}";

    /// <summary>Each vessel's name, from the hellos of the cells on it.</summary>
    private static Dictionary<string, string> VesselNames(WorkLedger ledger) => ledger.Entries.Where(e => e.Kind == EntryKind.Hello)
        .GroupBy(e => e.Endorsement.VesselId)
        .ToDictionary(g => g.Key, g => VesselName(g.Last().Body<HelloPayload>().Cell, g.Key), StringComparer.Ordinal);

    private static string Stamp(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    /// <summary>What kind of actor a cell is, from the tool its claims name: the face it wears (claude-code, codex, ...), or <c>cell</c>.</summary>
    private static string KindOf(IEnumerable<SignedEntry> entries) =>
        entries.Where(e => e.Kind == EntryKind.Claim).Select(e => e.Body<ClaimPayload>().Tool).OfType<string>().Select(Kind)
            .GroupBy(t => t, StringComparer.Ordinal).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal)
            .Select(g => g.Key).FirstOrDefault() ?? "cell";

    /// <summary>A tool's name as a kind: lower case, words joined by hyphens, so "Claude Code" and "claude-code" are one kind.</summary>
    private static string Kind(string tool) => string.Join('-', tool.Trim().ToLowerInvariant().Split((char[])[' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries));

    private static string VesselName(CellIdentity? hello, string vesselId) =>
        hello?.Hardware.HostName ?? hello?.Position.Node ?? vesselId[..Math.Min(8, vesselId.Length)];

    private static Dictionary<string, string> ActorNames(WorkLedger ledger) => ledger.Entries.GroupBy(e => e.Cell).ToDictionary(
        g => g.Key,
        g => $"{KindOf(g)} on {VesselName(g.LastOrDefault(e => e.Kind == EntryKind.Hello)?.Body<HelloPayload>().Cell, g.Last().Endorsement.VesselId)}",
        StringComparer.Ordinal);

    private static JsonObject Activity(SignedEntry e, IReadOnlyDictionary<string, string> names, IReadOnlyDictionary<string, OfferState> offers, IReadOnlyDictionary<string, string> vessels)
    {
        var who = names[e.Cell];
        var activity = new JsonObject
        {
            ["id"] = $"urn:creatura:entry:{e.Id}",
            ["type"] = "",
            ["actor"] = ActorId(e.Cell),
            ["published"] = Stamp(e.Timestamp),
            ["summary"] = "",
        };

        switch (e.Kind)
        {
            case EntryKind.Hello:
                var cell = e.Body<HelloPayload>().Cell;
                activity["type"] = "Join";
                activity["summary"] = $"{who} joined the fleet";
                activity["object"] = new JsonObject { ["type"] = "Group", ["name"] = "the fleet" };
                activity["creatura:hardware"] = cell.Hardware.Render();
                activity["creatura:position"] = cell.Position.Render();
                break;

            case EntryKind.Claim:
                var claim = e.Body<ClaimPayload>();
                var where = Where(claim);
                var thing = new JsonObject { ["id"] = ClaimId(e.Id), ["type"] = "creatura:Claim", ["name"] = claim.Goal };
                if (claim.Repo is not null) thing["creatura:repo"] = claim.Repo;
                if (claim.Branch is not null) thing["creatura:branch"] = claim.Branch;
                if (claim.Folder is not null) thing["creatura:folder"] = claim.Folder;
                if (claim.Tool is not null) thing["creatura:tool"] = claim.Tool;
                if (claim.Session is not null) thing["creatura:session"] = claim.Session;
                activity["type"] = "Create";
                activity["summary"] = $"{who} {(claim.Offer is null ? "" : "took the owner's offer and ")}started '{claim.Goal}'{(where.Length > 0 ? $" in {where}" : "")}";
                activity["object"] = thing;
                activity["context"] = ClaimId(e.Id);
                if (claim.Offer is not null)
                {
                    activity["creatura:offer"] = OfferId(claim.Offer);
                    if (offers.TryGetValue(claim.Offer, out var taken) && taken.Status == OfferStatus.Conflict) activity["creatura:conflict"] = true;
                }
                break;

            case EntryKind.Progress:
                var progress = e.Body<ProgressPayload>();
                activity["type"] = "Update";
                activity["summary"] = progress.WaitingOn is { } waitingOn ? $"{who} waits on {waitingOn}: {progress.Row}"
                    : progress.Answers is { } answered ? $"{who} {(progress.Verdict == AnswerVerdict.Accept ? "accepted" : "objected to")} {answered}: {progress.Row}"
                    : progress.Topic is { } topic ? $"{who} proposed on '{topic}': {progress.Row}"
                    : $"{who}: {progress.Row}{(progress.Blocked == true ? " (blocked)" : "")}";
                activity["object"] = ClaimId(progress.Claim);
                activity["content"] = progress.Row;
                activity["context"] = ClaimId(progress.Claim);
                if (progress.Stress is { } stress) activity["creatura:stress"] = stress;
                if (progress.Blocked is { } blocked) activity["creatura:blocked"] = blocked;
                // Turns (docs/design-turns.md): the wait, the topic and what was seen, the file, the answer and its verdict.
                if (progress.WaitingOn is not null) activity["creatura:waitingOn"] = progress.WaitingOn;
                if (progress.Topic is not null) activity["creatura:topic"] = progress.Topic;
                if (progress.Seen is not null) activity["creatura:seen"] = new JsonArray([.. progress.Seen.Select(id => (JsonNode?)JsonValue.Create($"urn:creatura:entry:{id}"))]);
                if (progress.Artifact is not null) activity["creatura:artifact"] = $"urn:creatura:entry:{progress.Artifact}";
                if (progress.Answers is not null)
                {
                    activity["creatura:answers"] = $"urn:creatura:entry:{progress.Answers}";
                    activity["creatura:verdict"] = progress.Verdict?.ToString().ToLowerInvariant();
                }
                break;

            case EntryKind.HandOff when e.Body<HandOffPayload>().Offer is { } declined:
                activity["type"] = "Reject";
                activity["summary"] = $"{who} declined the offer: {e.Body<HandOffPayload>().Reason}";
                activity["object"] = OfferId(declined);
                activity["content"] = e.Body<HandOffPayload>().Reason;
                break;

            case EntryKind.HandOff:
                var handOff = e.Body<HandOffPayload>();
                activity["type"] = "Leave";
                activity["summary"] = $"{who} stopped: {handOff.Reason}";
                activity["object"] = ClaimId(handOff.Claim);
                activity["content"] = handOff.Reason;
                activity["context"] = ClaimId(handOff.Claim);
                break;

            case EntryKind.Release:
                var release = e.Body<ReleasePayload>();
                activity["type"] = "creatura:Release";
                activity["summary"] = $"{who} finished: {release.Evidence}";
                activity["object"] = ClaimId(release.Claim);
                activity["result"] = new JsonObject { ["type"] = "Note", ["content"] = release.Evidence };
                activity["context"] = ClaimId(release.Claim);
                break;

            case EntryKind.Warning:
                var warning = e.Body<WarningPayload>();
                activity["type"] = "Flag";
                activity["summary"] = $"{who} flagged {warning.Subject}: {warning.Evidence}";
                activity["object"] = new JsonObject { ["type"] = "Object", ["name"] = warning.Subject };
                activity["content"] = warning.Evidence;
                break;

            case EntryKind.Artifact:
                // Only what is in the clear and says nothing of the content: the size and how many cells may open it.
                var artifact = e.Body<ArtifactPayload>();
                var recipients = artifact.Keys.Count;
                activity["type"] = "Create";
                activity["summary"] = $"{who} shared a file of {Size(artifact.Size)} with {recipients} cell{(recipients == 1 ? "" : "s")}";
                activity["object"] = new JsonObject { ["type"] = "Document", ["creatura:size"] = artifact.Size, ["creatura:recipients"] = recipients };
                if (artifact.Claim is not null) activity["context"] = ClaimId(artifact.Claim);
                break;

            case EntryKind.Offer:
                // Made by the owner: the owner is the actor; the cell that carried it is in the entry block below.
                var offer = e.Body<OfferPayload>().Offer;
                var target = offer.To == e.Cell || names.ContainsKey(offer.To) ? names.GetValueOrDefault(offer.To, offer.To)
                    : vessels.TryGetValue(offer.To, out var vesselName) ? $"any cell on {vesselName}" : offer.To;
                var goal = new JsonObject { ["id"] = OfferId(offer.Id), ["type"] = "creatura:Goal", ["name"] = offer.Goal };
                if (offer.Repo is not null) goal["creatura:repo"] = offer.Repo;
                if (offer.Branch is not null) goal["creatura:branch"] = offer.Branch;
                if (offer.Folder is not null) goal["creatura:folder"] = offer.Folder;
                activity["type"] = "Offer";
                activity["actor"] = OwnerId(offer.OwnerFingerprint);
                activity["summary"] = $"the owner offered '{offer.Goal}' to {target}";
                activity["object"] = goal;
                activity["target"] = names.ContainsKey(offer.To) ? ActorId(offer.To) : $"urn:creatura:vessel:{offer.To}";
                activity["endTime"] = Stamp(offer.ExpiresAt);
                activity["creatura:state"] = (offers.TryGetValue(offer.Id, out var state) ? state.Status : OfferStatus.Open).ToString().ToLowerInvariant();
                break;

            case EntryKind.Withdraw:
                var withdrawal = e.Body<WithdrawPayload>().Withdrawal;
                activity["type"] = "Undo";
                activity["actor"] = OwnerId(CellKey.FingerprintOf(withdrawal.OwnerPublicKey));
                activity["summary"] = $"the owner withdrew '{(offers.TryGetValue(withdrawal.Offer, out var gone) ? gone.Offer.Goal : withdrawal.Offer)}'";
                activity["object"] = OfferId(withdrawal.Offer);
                break;

            default:
                activity["type"] = "Activity";
                activity["summary"] = $"{who}: {e.Kind}";
                break;
        }

        activity["creatura:entry"] = new JsonObject
        {
            ["cell"] = e.Cell,
            ["sequence"] = e.Sequence,
            ["kind"] = e.Kind.ToString(),
            ["signature"] = e.Signature,
        };
        return activity;
    }

    private static string Where(ClaimPayload claim) => string.Join(", ", new[]
    {
        claim.Repo is null ? null : claim.Branch is null ? claim.Repo : $"{claim.Repo} ({claim.Branch})",
        claim.Repo is null && claim.Branch is not null ? $"branch {claim.Branch}" : null,
        claim.Folder,
    }.OfType<string>());

    private static string Size(long bytes) => bytes < 1024 ? $"{bytes} bytes"
        : bytes < 1024 * 1024 ? (bytes / 1024.0).ToString("0.#", CultureInfo.InvariantCulture) + " KB"
        : (bytes / (1024.0 * 1024)).ToString("0.#", CultureInfo.InvariantCulture) + " MB";
}
