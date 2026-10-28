# The replay format: how a UI watches a run

Status: version 1, 2026-10-28. Written by `JsonlReplayWriter` (`src/Creatura.Scenarios/Replay.cs`); read by the
convoy viewer (`tools/convoy-viewer/index.html`).

A replay is a text file of JSON lines, one JSON object per line, UTF-8. A UI can read it whole, or follow a run live
by implementing `IScenarioObserver`, which gets the same three kinds of objects as the run goes:

```csharp
public interface IScenarioObserver
{
    void Start(ReplayHeader header);
    void Tick(ReplayTick tick);
    void End(ReplaySummary summary);
}
```

Property names are camelCase, and properties with no value are left out. A reader must ignore properties and event
kinds it does not know, so later versions can add them.

## Line 1: the header

```json
{"type":"header","format":"creatura.replay/1","scenario":"convoy","seed":1,"arm":"A",
 "armText":"plain loop, shared ledger, perfect link","maxTicks":60,
 "nodes":[{"id":"depot","name":"Depot","x":0,"y":0,"kind":"depot","demand":0}, ...],
 "roads":[{"id":"r01","a":"depot","b":"j1"}, ...],
 "trucks":[{"name":"Truck 1","cell":"cell-…","design":"plain","capacity":6,"fuelCapacity":10,"start":"depot"}, ...],
 "faults":["The road from North crossing to South crossing closes at tick 6, and its sensor keeps reporting it open.", ...]}
```

- `nodes`: every place, with map coordinates for drawing (`x` east, `y` north, in road-length units) and, for a
  village, its demand in units. `kind` is `depot`, `junction` or `village`.
- `roads`: every road between two nodes. Each takes one tick to drive.
- `trucks`: each truck's cell id (empty without a ledger), its design (`plain`, `levin`, `bach`, `peterson`) and its
  machine.
- `faults`: what the world will do, in sentences, for the viewer's opening.
- `goal`, `success` and `armPurpose`: what the trucks are trying to do, what counts as success and how the judge
  measures it, and what this arm is in the battery for. Sentences for a UI to show as they are.
- `trucks[].agent`: how each truck's agent is built, written from the same code that builds it:

  ```json
  {"design":"levin","title":"Levin's colony","regulator":"ColonyRegulator",
   "about":"Nested homeostatic controllers with no self-model: …",
   "seat":"convoy-planner: an offline planner that knows roads, fuel and loads, and believes its sensors",
   "model":"none: no language model is called in this scenario (decision D3)",
   "layer":"ControlLayerOptions.Design: … plus: quarantine is cleared when no route is left",
   "extensions":["StoryExtension: …","RoadDistrustExtension: …"],
   "cSharp":"var truck1 = new ReferenceLoop(\n    regulator: new ColonyRegulator(…), …"}
  ```

  `model` says whether a language model makes decisions; in the convoy none does. `cSharp` is the code that builds
  the agent, for a UI to show as written.

## One line per tick

```json
{"type":"tick","tick":12,
 "trucks":[{"name":"Truck 1","at":"j3","fuel":6,"fuelBelieved":6,"load":6,"state":"acting","claim":"ceiba"}, ...],
 "needs":{"alba":0,"brisa":3,"ceiba":9,"dalia":6},
 "closed":["r03","r09"],
 "events":[{"kind":"lie","text":"Truck 1 takes the road to Ceiba: its sensor said open, but the road is flooded. The truck's radio still reports it arrived.",
            "truck":"Truck 1","road":"r09","from":"j3","to":"ceiba"}, ...]}
```

- `trucks`: the truth at the end of the tick. `fuel` is the real tank, `fuelBelieved` what the gauge reads. `state` is
  `acting`, `finished`, `broken` or `stranded`. `claim` is the village the truck holds on the ledger.
- `needs`: what each village still needs. `closed`: the roads the flood has closed, the lying one included.
- `events`: what happened in the tick, in order, each with a sentence (`text`) and, where they apply, `truck`, `road`,
  `village`, `from`, `to` and `verdict`.

| kind | what happened |
|---|---|
| `drive` | a truck moved along `road` from `from` to `to` |
| `blocked` | a truck found a closed road and stayed |
| `lie` | a truck took the lying road: the radio reported arrival, the truck did not move |
| `caught` | the truck's control layer judged that action a lie and set the tool aside |
| `refused` | a failed action the control layer counted as a lie (judging by the registry alone) |
| `load`, `refuel`, `unload` | at the depot, or at `village` |
| `wasted` | a truck reached the village it was driving to, and it was already served |
| `claim`, `takeover`, `release` | ledger work: a claim, a claim on a quiet truck's village, a delivery report or a hand-off |
| `message`, `lost`, `delayed` | a claim, report or hand-off from `truck` to `to` on the mesh |
| `closure`, `breakdown`, `out-of-fuel` | the world: a road closes, a truck breaks down, a tank runs dry |
| `done` | every village has what it needs |
| `stopped` | a truck's loop ended, and why |

## The last line: the end

```json
{"type":"end","measures":{"delivered":30,"demand":30,"wastedTrips":0,"stranded":0,"liesBelieved":2,
 "lyingRoadRepeats":1,"ticksToServe":23,"ledgersIdentical":true,"idleWhileUnserved":0,
 "messagesSent":114,"messagesLost":0,"allServed":true},
 "outcome":["Every village was served by tick 23.", ...]}
```

`measures` is the judge's reading of the world (decision D6 of docs/design-machine-vessels.md); `outcome` says it in
sentences.

## Making a replay

```bash
dotnet run --project samples/Convoy -- --arm A --seed 1 --replay run.jsonl
```

Open `tools/convoy-viewer/index.html` in a browser and choose the file, or drop it on the page. The page needs no
server and no build; it loads three.js from cdnjs. It comes with four sample runs built in.

To rebuild it after changing `viewer.template.html`, or to build in other samples:

```bash
python3 tools/convoy-viewer/build.py tools/convoy-viewer/viewer.template.html docs/assets/creatura-icon.svg /tmp/convoy-replay.html tools/convoy-viewer/index.html "The road that lies (arm A, seed 0)=a0.jsonl" "A truck breaks down (arm E, seed 3)=e3.jsonl"
```
