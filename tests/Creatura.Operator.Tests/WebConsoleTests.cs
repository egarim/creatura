using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Creatura.Operator;
using Creatura.Scenarios;
using Creatura.Worlds.Web;
using Microsoft.Playwright;

namespace Creatura.Operator.Tests;

/// <summary>A Chromium for the page's tests: CREATURA_CHROMIUM, or Playwright's own; the test is skipped without one.</summary>
public sealed class PageFactAttribute : FactAttribute
{
    public PageFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("CREATURA_CHROMIUM") is { Length: > 0 } path)
        {
            if (!File.Exists(path)) Skip = $"CREATURA_CHROMIUM names {path}, which does not exist";
            return;
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] roots =
        [
            Environment.GetEnvironmentVariable("PLAYWRIGHT_BROWSERS_PATH") ?? "",
            Path.Combine(home, ".cache", "ms-playwright"),
            Path.Combine(home, "Library", "Caches", "ms-playwright"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ms-playwright"),
        ];
        if (!roots.Any(r => r.Length > 0 && Directory.Exists(r) && Directory.EnumerateDirectories(r, "chromium*").Any()))
            Skip = "no Chromium for Playwright: set CREATURA_CHROMIUM or install one";
    }
}

/// <summary>The console as a page (docs/design-console.md, slice U5): its API and guards, then the page in a browser with the network off.</summary>
[Collection("page")]
public class WebConsoleTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>A creatura command that answers from fixed documents and keeps every call: two cells, one blocked.</summary>
    internal sealed class FakeCli : ICellCommand
    {
        public List<string[]> Calls { get; } = [];

        public static string Fleet => new JsonObject
        {
            ["format"] = "creatura.fleet/1",
            ["at"] = Now.UtcDateTime.ToString("o"),
            ["quietMinutes"] = 15,
            ["cells"] = new JsonArray(
                new JsonObject
                {
                    ["cell"] = "cell-aaaa", ["name"] = "claude-code on laptop", ["kind"] = "claude-code", ["creatura"] = "cr-1", ["vessel"] = "v-1",
                    ["vesselName"] = "laptop", ["ring"] = "#2f6f8f", ["lastSeen"] = Now.AddMinutes(-1).UtcDateTime.ToString("o"), ["quiet"] = false,
                    ["claim"] = "migration step 16", ["claimId"] = "cell-aaaa#2", ["claimedAt"] = Now.AddMinutes(-30).UtcDateTime.ToString("o"),
                    ["progress"] = "golden diff empty", ["stress"] = 0.2,
                },
                new JsonObject
                {
                    ["cell"] = "cell-bbbb", ["name"] = "codex on second", ["kind"] = "codex", ["creatura"] = "cr-2", ["vessel"] = "v-2",
                    ["vesselName"] = "second", ["ring"] = "#b5683a", ["lastSeen"] = Now.AddMinutes(-40).UtcDateTime.ToString("o"), ["quiet"] = true,
                    ["claim"] = "rotate the logs", ["claimId"] = "cell-bbbb#2", ["claimedAt"] = Now.AddHours(-2).UtcDateTime.ToString("o"),
                    ["progress"] = "disk full", ["stress"] = 0.9, ["blocked"] = true,
                }),
        }.ToJsonString();

        public static string Activity => new JsonObject
        {
            ["type"] = "OrderedCollection",
            ["orderedItems"] = new JsonArray(
                new JsonObject { ["actor"] = "urn:creatura:cell:cell-bbbb", ["type"] = "Update", ["published"] = Now.AddMinutes(-40).UtcDateTime.ToString("o"), ["summary"] = "codex on second: disk full (blocked)", ["creatura:blocked"] = true, ["creatura:stress"] = 0.9 },
                new JsonObject { ["actor"] = "urn:creatura:cell:cell-aaaa", ["type"] = "Update", ["published"] = Now.AddMinutes(-1).UtcDateTime.ToString("o"), ["summary"] = "claude-code on laptop: golden diff empty" },
                new JsonObject { ["actor"] = "urn:creatura:cell:cell-bbbb", ["type"] = "Create", ["published"] = Now.AddHours(-2).UtcDateTime.ToString("o"), ["summary"] = "codex on second started 'rotate the logs'" }),
        }.ToJsonString();

        public (int Code, string Output, string Error) Run(IReadOnlyList<string> args)
        {
            Calls.Add([.. args]);
            return args[1] switch
            {
                "fleet" => (0, Fleet, ""),
                "activity" => (0, Activity, ""),
                "offer" => (0, $"offered      of-123 to cell {args[3]}: {args[5]}\nopen until   2026-11-04T12:00Z", ""),
                _ => (2, "", "unknown"),
            };
        }
    }

    private static async Task<(string Dir, string Replay)> Runs()
    {
        var dir = Path.Combine(Path.GetTempPath(), "creatura-web-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var text = new StringWriter();
        await ConvoyScenario.Create(ConvoyArm.E, 0).RunAsync(new JsonlReplayWriter(text));
        var replay = Path.Combine(dir, "convoy-e0.jsonl");
        await File.WriteAllTextAsync(replay, text.ToString());
        await File.WriteAllTextAsync(Path.Combine(dir, "notes.jsonl"), "{\"not\":\"a replay\"}\n");
        return (dir, replay);
    }

    [Fact]
    public async Task TheApiServesTheFleetTheRunsAndACellAndGuardsTheOffer()
    {
        var (dir, replay) = await Runs();
        var cli = new FakeCli();
        try
        {
            await using var web = await WebConsole.StartAsync(new FleetModel(cli, ["--home", "/h"]), new RunCatalog([dir]));
            Assert.Equal("127.0.0.1", web.Address.Host);
            using var http = new HttpClient { BaseAddress = web.Address };

            // The page carries its token; the headers keep it to itself.
            var page = await http.GetAsync("/");
            var html = await page.Content.ReadAsStringAsync();
            Assert.Contains(web.Token, html);
            Assert.DoesNotContain("__CREATURA_TOKEN__", html);
            Assert.Contains("default-src 'none'", page.Headers.GetValues("Content-Security-Policy").Single());
            Assert.Equal("image/svg+xml", (await http.GetAsync("/icon.svg")).Content.Headers.ContentType!.MediaType);

            // The state: the Fleet as the command gave it, the runs found (the file that is not a replay left out).
            var state = JsonNode.Parse(await http.GetStringAsync("/api/state"))!;
            Assert.Equal(["claude-code on laptop", "codex on second"], state["cells"]!.AsArray().Select(c => (string)c!["name"]!));
            Assert.Equal("alarm", (string)state["cells"]![1]!["look"]!);
            var run = Assert.Single(state["runs"]!.AsArray());
            Assert.Equal(("convoy", "Convoy, arm E, seed 0"), ((string)run!["kind"]!, (string)run["title"]!));
            Assert.DoesNotContain(dir, state.ToJsonString());                          // a path never reaches the page

            // A run by its id: the same rows as the model reads from the file; an id it did not list is not found.
            var view = JsonNode.Parse(await http.GetStringAsync($"/api/run/{(string)run["id"]!}"))!["views"]![0]!;
            var model = RunModel.ReadFile(replay);
            Assert.Equal(model.Rows.Select(r => r.Act), view["rows"]!.AsArray().Select(r => (string)r!["act"]!));
            Assert.Equal(model.Count(RowMark.Refused), (int)view["counts"]!["refused"]!);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/run/0000000000000000")).StatusCode);

            // A cell's activity, oldest first, its blocked step marked.
            var cell = JsonNode.Parse(await http.GetStringAsync("/api/cell/cell-bbbb"))!["views"]![0]!;
            Assert.Equal(["claim", "progress"], cell["rows"]!.AsArray().Select(r => (string)r!["act"]!));
            Assert.Equal("Started 'rotate the logs'", (string)cell["rows"]![0]!["answer"]!);
            Assert.Equal("blocked", (string)cell["rows"]![1]!["mark"]!);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/api/cell/cell-zzzz")).StatusCode);

            // An offer: refused without the token or from another origin; with both, cell offer's own call, on the home.
            var offers = () => cli.Calls.Count(c => c[1] == "offer");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsJsonAsync("/api/offer", new { cell = "cell-aaaa", goal = "x" })).StatusCode);
            using (var foreign = new HttpRequestMessage(HttpMethod.Post, "/api/offer") { Content = JsonContent.Create(new { cell = "cell-aaaa", goal = "x" }) })
            {
                foreign.Headers.Add("X-Creatura-Token", web.Token);
                foreign.Headers.Add("Origin", "https://evil.example");
                Assert.Equal(HttpStatusCode.Forbidden, (await http.SendAsync(foreign)).StatusCode);
            }

            Assert.Equal(0, offers());
            using (var mine = new HttpRequestMessage(HttpMethod.Post, "/api/offer") { Content = JsonContent.Create(new { cell = "cell-aaaa", goal = "check the disk" }) })
            {
                mine.Headers.Add("X-Creatura-Token", web.Token);
                mine.Headers.Add("Origin", $"http://127.0.0.1:{web.Address.Port}");
                var answer = JsonNode.Parse(await (await http.SendAsync(mine)).Content.ReadAsStringAsync())!;
                Assert.True((bool)answer["ok"]!);
                Assert.StartsWith("offered", (string)answer["said"]!);
            }

            Assert.Equal(["cell", "offer", "--to", "cell-aaaa", "--goal", "check the disk", "--home", "/h"], cli.Calls.Last(c => c[1] == "offer"));
            using (var stranger = new HttpRequestMessage(HttpMethod.Post, "/api/offer") { Content = JsonContent.Create(new { cell = "cell-nobody", goal = "x" }) })
            {
                stranger.Headers.Add("X-Creatura-Token", web.Token);
                Assert.False((bool)JsonNode.Parse(await (await http.SendAsync(stranger)).Content.ReadAsStringAsync())!["ok"]!);
            }

            Assert.Equal(1, offers());

            // Another name for this address (DNS rebinding) is refused.
            using var rebound = new HttpRequestMessage(HttpMethod.Get, "/api/state");
            rebound.Headers.Host = "attacker.example";
            Assert.Equal(HttpStatusCode.MisdirectedRequest, (await http.SendAsync(rebound)).StatusCode);

            // The Fleet is read at most every couple of seconds, however often it is asked for.
            var reads = cli.Calls.Count(c => c[1] == "fleet");
            for (var i = 0; i < 5; i++) await http.GetStringAsync("/api/state");
            Assert.True(cli.Calls.Count(c => c[1] == "fleet") <= reads + 1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// U5: U1.1 and U3.1 hold against the page's rendered text, with the network off: the Fleet's cells on the page, a run's
    /// steps in the same order as the model's, and not one request to anywhere but the console. (U4.1, Compare, is not
    /// built: the owner held it until the paid runs give it rows.)
    /// </summary>
    [PageFact]
    public async Task U5_ThePageShowsTheFleetAndARunInOrderWithTheNetworkOff()
    {
        var (dir, replay) = await Runs();
        try
        {
            await using var web = await WebConsole.StartAsync(new FleetModel(new FakeCli()), new RunCatalog([dir]));
            await using var browser = await WebBrowser.LaunchAsync();
            var context = await browser.Browser.NewContextAsync();
            var outside = new List<string>();
            await context.RouteAsync("**/*", route =>
            {
                var url = new Uri(route.Request.Url);
                if (url.Host == "127.0.0.1" && url.Port == web.Address.Port) return route.ContinueAsync();
                outside.Add(route.Request.Url);
                return route.AbortAsync();
            });
            var page = await context.NewPageAsync();
            await page.GotoAsync(web.Address.ToString());
            await page.WaitForSelectorAsync(".item .name");

            // U3.1 on the page: every cell of the Fleet, in its order; the blocked one marked.
            var names = await page.Locator(".side .item .name").AllInnerTextsAsync();
            Assert.Equal(["claude-code on laptop", "codex on second", "Convoy, arm E, seed 0"], names);
            Assert.Equal(1, await page.Locator(".dot.alarm").CountAsync());
            Assert.Contains("in trouble", await page.InnerTextAsync(".stats"));

            // U1.1 on the page: the run's steps in the model's order, each as the page draws it: a message as its words,
            // any other act as a tool block.
            await page.ClickAsync("text=Convoy, arm E, seed 0");
            await page.WaitForSelectorAsync(".tool .act");
            var model = RunModel.ReadFile(replay);
            var steps = await page.Locator(".turn .tool .act, .turn .say").AllInnerTextsAsync();
            Assert.Equal(model.Rows.Where(r => r.Cell is not (RunModel.World or RunModel.Judge)).Select(r => r.Kind == "message" ? r.Answer : r.Act), steps);
            Assert.Equal(model.Rows.Count(r => r.Kind == "message"), await page.Locator(".turn .say").CountAsync());
            Assert.Equal(model.Count(RowMark.Refused), await page.Locator(".tool.refused").CountAsync());
            Assert.Contains(model.Outcome[0], await page.InnerTextAsync(".outcome"));
            Assert.Contains("read-only", await page.InnerTextAsync("#composer"));

            // A cell's page: its activity and the composer that offers it a goal.
            await page.ClickAsync("text=codex on second");
            await page.WaitForSelectorAsync(".event");
            Assert.Equal(["claim", "progress"], await page.Locator(".event .chip:first-child").AllInnerTextsAsync());
            Assert.Equal("Offer a goal to codex on second…", await page.GetAttributeAsync("#goal", "placeholder"));

            Assert.Empty(outside);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
