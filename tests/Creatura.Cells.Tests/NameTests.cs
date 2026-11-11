namespace Creatura.Cells.Tests;

/// <summary>The name generator (issue #52): N1 to N4.</summary>
public class NameTests
{
    private static (int Code, string Out, string Err) Cell(string home, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CellCommand.Run(["cell", .. args, "--home", home, "--no-mesh"], output, error, new FakeVessel("raw-vessel-names", "studio"));
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void N1_TheSameVesselAndRoleSuggestTheSameNameEveryTime()
    {
        var first = CreaturaNames.Suggest("vessel-1", "renderer", []);
        Assert.Equal(first, CreaturaNames.Suggest("vessel-1", "renderer", []));
        Assert.StartsWith("renderer-", first[0]);
        Assert.Contains(first[0]["renderer-".Length..], CreaturaNames.Creatures);
        Assert.Equal(first, CreaturaNames.Suggest("vessel-1", " Renderer ", []));               // the role is normalised first

        using var dir = new TempHome();
        var once = Cell(dir.Path, "name", "--role", "renderer", "--vessel", "vessel-1");
        Assert.Equal(0, once.Code);
        Assert.Equal(first[0], once.Out.Trim());
        Assert.Equal(once.Out, Cell(dir.Path, "name", "--role", "renderer", "--vessel", "vessel-1").Out);
        Assert.False(Directory.Exists(Path.Combine(dir.Path, "cells")));                         // it suggested; it made nothing
    }

    [Fact]
    public void N2_ANameAlreadyTakenIsSkippedForAnUnusedOne()
    {
        var first = CreaturaNames.Suggest("vessel-1", "critic", [])[0];
        var next = CreaturaNames.Suggest("vessel-1", "critic", [first.ToUpperInvariant()])[0];
        Assert.NotEqual(first, next);
        Assert.StartsWith("critic-", next);

        // Every creature taken: the same order again, numbered, still unique.
        var all = CreaturaNames.Creatures.Select(c => $"critic-{c}").ToList();
        var more = CreaturaNames.Suggest("vessel-1", "critic", all, count: 3);
        Assert.All(more, n => Assert.EndsWith("-2", n));
        Assert.Equal(3, more.Distinct().Count());

        // Through the command: a cell this home already holds, and a name passed with --taken, are both skipped.
        using var dir = new TempHome();
        Assert.Equal(0, Cell(dir.Path, "init", "--creatura", "placeholder").Code);
        var suggested = Cell(dir.Path, "name", "--role", "publisher").Out.Trim();
        Assert.Equal(0, Cell(dir.Path, "init", "--creatura", suggested).Code);
        var after = Cell(dir.Path, "name", "--role", "publisher", "--count", "2").Out.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(2, after.Length);
        Assert.DoesNotContain(suggested, after);
        var skipped = Cell(dir.Path, "name", "--role", "publisher", "--taken", after[0]).Out.Trim();
        Assert.Equal(after[1], skipped);
    }

    [Fact]
    public void N3_EverySuggestedNamePassesTheHomesRulesUnchanged()
    {
        using var dir = new TempHome();
        var home = new CellHome(dir.Path);
        foreach (var role in new[] { "renderer", "Reel Publisher", "it_keeper", "critic!!", "a very long role name that goes past thirty two characters" })
        {
            foreach (var name in CreaturaNames.Suggest("vessel-1", role, [], count: CreaturaNames.Creatures.Count + 2))
            {
                Assert.EndsWith(Path.DirectorySeparatorChar + name + ".key", home.KeyPathFor("vessel-1", name));   // SafeName throws on anything it would refuse
                Assert.Matches("^[a-z0-9]+(-[a-z0-9]+)+$", name);
            }
        }

        Assert.Throws<ArgumentException>(() => CreaturaNames.Suggest("vessel-1", "!!!", []));
        Assert.Equal(CreaturaNames.Creatures.Count, CreaturaNames.Creatures.Distinct().Count());
    }

    [Fact]
    public void N4_InitWithACreaturaNeverConsultsTheGenerator()
    {
        using var dir = new TempHome();
        var named = Cell(dir.Path, "init", "--creatura", "my-own-name");
        Assert.Equal(0, named.Code);
        Assert.DoesNotContain("cell name", named.Out);
        Assert.Contains("creatura cell name --role", Cell(dir.Path, "init").Out);                   // without it, a name is offered, not chosen
        var cells = Directory.GetFiles(Path.Combine(dir.Path, "cells"), "*.key", SearchOption.AllDirectories).Select(Path.GetFileName).ToList();
        Assert.Contains("my-own-name.key", cells);
        Assert.Contains("creatura.key", cells);
    }

    private static (int Code, string Out, string Err) On(string home, FakeVessel vessel, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CellCommand.Run(["cell", .. args, "--home", home, "--no-mesh"], output, error, vessel);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void N5_ANameAnotherVesselSaidInTheLedgerIsTakenAndWarnedAbout()
    {
        using var air = new TempHome();
        using var surface = new TempHome();
        using var hand = new TempHome();
        var airVessel = new FakeVessel("raw-air-names", "air");
        var surfaceVessel = new FakeVessel("raw-surface-names", "surface");
        Assert.Equal(0, On(air.Path, airVessel, "init").Code);
        var wanted = On(air.Path, airVessel, "name", "--role", "renderer").Out.Trim();

        // The surface joins and takes that name; its hello says it, and the air reads the surface's ledger.
        var request = Path.Combine(hand.Path, "request.json");
        var endorsement = Path.Combine(hand.Path, "endorsement.json");
        Assert.Equal(0, On(surface.Path, surfaceVessel, "request", "--creatura", wanted, "--out", request).Code);
        Assert.Equal(0, On(air.Path, airVessel, "endorse", "--request", request, "--out", endorsement).Code);
        Assert.Equal(0, On(surface.Path, surfaceVessel, "accept", "--creatura", wanted, "--endorsement", endorsement).Code);
        File.Copy(new CellHome(surface.Path).LedgerPath, new CellHome(air.Path).LedgerPath, overwrite: true);

        Assert.DoesNotContain(wanted, CreaturaNames.InHome(new CellHome(air.Path).CellsDirectory));        // the air holds no key by that name
        var now = On(air.Path, airVessel, "name", "--role", "renderer");
        Assert.Equal(0, now.Code);
        Assert.NotEqual(wanted, now.Out.Trim());                                                             // the ledger said it is taken

        // Taking it anyway on the air is allowed, with a warning naming the other creatura.
        var clash = On(air.Path, airVessel, "init", "--creatura", wanted);
        Assert.Equal(0, clash.Code);
        Assert.Contains($"another creatura already said the name '{wanted}'", clash.Err);

        // The same creatura on a second vessel of one home is one creatura that moved: no warning.
        var moved = On(surface.Path, surfaceVessel, "accept", "--creatura", wanted, "--endorsement", endorsement);
        Assert.DoesNotContain("another creatura", moved.Err);
    }

    [Fact]
    public void N6_TheHelloSaysTheNameAndTheStreamShowsIt()
    {
        using var dir = new TempHome();
        var vessel = new FakeVessel("raw-stream-names", "studio");
        Assert.Equal(0, On(dir.Path, vessel, "init", "--creatura", "critic-sphinx").Code);
        Assert.Equal(0, On(dir.Path, vessel, "init").Code);
        var home = new CellHome(dir.Path);
        using var owner = home.LoadOwner()!;
        var ledger = new WorkLedger([owner.PublicKey]);
        home.Ledger.LoadInto(ledger);
        var names = CreaturaNames.InLedger(ledger);
        Assert.Single(names["critic-sphinx"]);
        Assert.Single(names["creatura"]);
        var actors = ActivityStream.Actors(ledger).Select(a => (string)a["name"]!).ToList();
        Assert.Contains("critic-sphinx on studio", actors);                                                 // the name, when the owner gave one
        Assert.DoesNotContain(actors, a => a.StartsWith("creatura on", StringComparison.Ordinal));          // the default name keeps the kind
    }

    [Fact]
    public void WithoutAHomeTheVesselMustBeNamed()
    {
        using var dir = new TempHome();
        var missing = Cell(Path.Combine(dir.Path, "nothing"), "name", "--role", "renderer");
        Assert.Equal(1, missing.Code);
        Assert.Contains("--vessel", missing.Err);
        Assert.Equal(2, Cell(dir.Path, "name").Code);                                              // --role is required
    }
}
