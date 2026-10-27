using Creatura.Worlds.Samples;

namespace Creatura.Worlds;

/// <summary>
/// The worlds by name. Seeded with the toy service-restore domain and the six sample worlds; a consumer registers
/// its own. A page, a runner or a golden scenario asks here instead of knowing each world's type.
/// </summary>
public static class WorldCatalog
{
    private static readonly object Gate = new();
    private static readonly List<WorldManifest> Order = [];
    private static readonly Dictionary<string, WorldManifest> ById = new(StringComparer.Ordinal);

    /// <summary>The toy suite (<see cref="RestoreServiceDomain"/>): its three barriers are the demo's, all off by default.</summary>
    public static readonly WorldManifest Restore = new()
    {
        Id = "restore",
        Task = RestoreServiceDomain.Task,
        Barriers =
        [
            new("patchrevoked", false, BarrierRole.Revoked),
            new("patchlies", false, BarrierRole.Lie),
            new("deployrevoked", false, BarrierRole.Revoked),
        ],
        Create = b =>
        {
            var world = RestoreServiceDomain.NewWorld();
            if (b.On("patchrevoked")) world.Revoke("apply-patch");
            if (b.On("patchlies")) world.MakeLiar("apply-patch");
            if (b.On("deployrevoked")) world.Revoke("deploy");
            return world;
        },
        GoalEvidence = ["alert.resolved"],
        Steps = ["logs.inspected", "component.identified", "patch.applied", "tests.green", "deployed", "alert.resolved"],
    };

    public static readonly WorldManifest Permit = new()
    {
        Id = "permit",
        Task = PermitOfficeWorld.Task,
        Barriers =
        [
            new("deed", true, BarrierRole.HonestFailure),
            new("pay", true, BarrierRole.Lie),
            new("zone", true, BarrierRole.ChangedRule),
            new("insp", true, BarrierRole.HonestFailure),
            new("short", true, BarrierRole.Shortcut),
        ],
        Create = b => new PermitOfficeWorld(b),
        GoalEvidence = ["permit.issued", "citizen.notified"],
        Steps = PermitOfficeWorld.Steps,
        Approvable = true,
    };

    public static readonly WorldManifest Irrigation = new()
    {
        Id = "irrigation",
        Task = IrrigationWorld.Task,
        Barriers =
        [
            new("sensor", true, BarrierRole.Lie),
            new("pump", true, BarrierRole.HonestFailure),
            new("drought", true, BarrierRole.ChangedRule),
            new("shortcut", true, BarrierRole.Shortcut),
        ],
        Create = b => new IrrigationWorld(b),
        GoalEvidence = ["zone-a.wet", "zone-b.wet", "zone-c.wet"],
        Steps = IrrigationWorld.Steps,
        Approvable = true,
    };

    public static readonly WorldManifest Livestock = new()
    {
        Id = "livestock",
        Task = LivestockWorld.Task,
        Barriers =
        [
            new("outbreak", true, BarrierRole.ChangedRule),
            new("expired", true, BarrierRole.Lie),
            new("scale", true, BarrierRole.Lie),
            new("vet", true, BarrierRole.HonestFailure),
            new("shortcut", true, BarrierRole.Shortcut),
        ],
        Create = b => new LivestockWorld(b),
        GoalEvidence = ["lot.vaccinated", "weights.recorded", "movement.permitted", "inspection.certified"],
        Steps = LivestockWorld.Steps,
        Approvable = true,
    };

    /// <summary>The flood world. Only the ferry's lie is a switch; the clinic's late lie is set by the battery that wants it (<c>clinic-lies</c>).</summary>
    public static readonly WorldManifest Flood = new()
    {
        Id = "flood",
        Task = FloodReliefWorld.Task,
        Barriers = [new("ferry-lies", true, BarrierRole.Lie)],
        Create = b => new FloodReliefWorld(b),
        GoalEvidence = ["supplies.delivered", "clinic.confirmed"],
        Steps = FloodReliefWorld.Steps,
        Approvable = true,
    };

    public static readonly WorldManifest ThreeSources = new()
    {
        Id = "three-sources",
        Task = ThreeSourcesWorld.Task,
        Create = _ => new ThreeSourcesWorld(),
        GoalEvidence = ["address.verified", "letter.sent"],
        Steps = ["address.verified", "letter.sent"],
    };

    public static readonly WorldManifest Licence = new()
    {
        Id = "licence",
        Task = LicenceWorld.Task,
        Create = _ => new LicenceWorld(),
        GoalEvidence = ["licence.issued", "citizen.notified"],
        Steps = ["file.ok", "fee.paid", "licence.issued", "citizen.notified"],
    };

    static WorldCatalog()
    {
        foreach (var m in new[] { Restore, Permit, Irrigation, Livestock, Flood, ThreeSources, Licence }) Register(m);
    }

    /// <summary>Adds or replaces a manifest under its id.</summary>
    public static void Register(WorldManifest manifest)
    {
        lock (Gate)
        {
            if (ById.TryGetValue(manifest.Id, out var old)) Order.Remove(old);
            ById[manifest.Id] = manifest;
            Order.Add(manifest);
        }
    }

    public static WorldManifest? TryFind(string id)
    {
        lock (Gate) return ById.GetValueOrDefault(id);
    }

    public static WorldManifest Find(string id) => TryFind(id) ?? throw new ArgumentException($"unknown world '{id}'; known: {string.Join(", ", Names)}");

    public static IReadOnlyList<WorldManifest> All
    {
        get { lock (Gate) return Order.ToList(); }
    }

    public static IReadOnlyList<string> Names => All.Select(m => m.Id).ToList();
}
