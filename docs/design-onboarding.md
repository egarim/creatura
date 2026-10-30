# Design: getting started

Status: decided by the owner on 2026-10-29 ("do 3 with your recommendation"). Built the same day; results at the end.

## The problem

The repo is public, and the articles and reels will bring visitors, but there is no on-ramp. The binaries exist (the
v0.2.0-preview.1 release); the packages are built but not on nuget.org; and a visitor has to read the README and the
samples to write their first agent. Creatura is a library, not a server, so the on-ramp is the usual .NET one: a
project made from a template, a way to try a design without writing code, and a check that says what is missing.

## What gets built

**Three templates**, one source in `templates/content/`, used two ways:

| Template | `dotnet new` | `creatura new` | What it is |
|---|---|---|---|
| an agent | `creatura-agent` | `agent` | the loop with each design, then your own regulator, on the toy world with one tool that lies; offline |
| a world | `creatura-world` | `world` | your own world: facts, tools that require and grant them, one that lies, and the designs run on it; offline |
| Semantic Kernel | `creatura-sk` | `sk` | the control layer on Semantic Kernel; offline until a model key is set, then that model, through the OpenAI chat API |

- The packaged form is `Creatura.Templates` (`dotnet new install Creatura.Templates`). The `creatura` tool embeds
  the same files, so `creatura new` needs nothing else installed.
- A made project references the packages by the exact version of the tool or template package that made it.
  Until the packages are on nuget.org, `--source <folder>` writes a `nuget.config` that points at a folder of
  `.nupkg` files (the release's assets, or a local `dotnet pack`).
- The `sk` project holds only the *name* of the environment variable with the key (default `OPENAI_API_KEY`), the
  base URL and the model name; never a key.

**Three commands** in the `creatura` tool, beside `creatura cell`:

- `creatura new [agent|world|sk] [--name <n>] [--output <dir>] [--version <v>] [--source <dir>]`: makes the project.
  With no template and a terminal, it asks which one. It never writes into a folder that has files in it.
- `creatura run --design <name> --world <name> [--barriers none|all|<ids>] [--seat bfs|scripted:<path>|model:<name>]
  [--ticks <n>] [--seed <n>] [--trace]`: tries a design with no code, from the catalogs the runners already use.
  `creatura run --list` names the designs, the worlds with their barriers, and the catalogued models. The default
  seat is the planner, so nothing leaves the machine unless `--seat model:<name>` asks for it.
- `creatura doctor`: what is installed and what is missing, one line each, with the fix. It checks the .NET SDK
  (needed by `new`), the cell home and its endorsement, the Claude Code hooks, the hub if one is configured, the
  model keys by the names of their variables (set or not, never their values), and whether the packages can be
  found. It exits 0 when nothing fails (a warning is not a failure) and 1 otherwise.

**The packages** get a README, an icon, SourceLink and symbol packages.

## Pre-registered tests

- **O1.** `creatura new <template>` writes the template's files with the project's name and the package version in
  place, leaves no template token and no `.template.config`, and refuses a folder that has files.
- **O2.** For each template, a project made by `creatura new` against packages packed from this commit builds and
  runs offline, and says the goal was reached; the same project made by `dotnet new` from the packed
  `Creatura.Templates` has the same files. (Slow: a script, `tools/try-onboarding.sh`, not the unit suite.)
- **O3.** `creatura run --design bach --world restore --barriers patchlies` reaches the goal and catches the lie, and
  prints the same lines twice; an unknown design or world exits 2 and names the known ones.
- **O4.** `creatura doctor` reports each check; with a model key set in its environment it says the key is set and
  never prints its value; a failed check makes it exit 1.
- **O5.** The `sk` template holds no key, and without one set it runs offline and says how to turn a model on.

## What this note does not cover

Publishing to nuget.org (it needs an API key the owner creates, added as the repository's `NUGET_API_KEY` secret),
reserving the `Creatura.` prefix there, and a container image for the hub.

## Results (2026-10-29)

Built as written: `templates/` (three templates and the `Creatura.Templates` package, which writes its version into
each template's default when it packs), `creatura new`, `creatura run` and `creatura doctor` in `Creatura.Cli`
(which now references Creatura.Worlds, Creatura.Seats and Creatura.Bench, all package-free), and the packages'
README, icon, SourceLink and `.snupkg`.

- **O1, O3, O4 and O5 hold** (`OnboardingTests`, 12 tests). Three deliberate breaks: `doctor` printing a key's value
  and `run` formatting in the machine's culture each failed a test on the first pass; `new` leaving the version
  token in place passed at first, because every test gave `--version`, so a test of the default was added and the
  break then failed it.
- **Found by O3:** under a German culture the trace's own text read "100 %" where en-US reads "100%"; it is written
  by the core, not by the command. `creatura run` now runs under the invariant culture with the percent sign against
  the number, so its output is the same on every machine. The core's own texts still followed the machine's culture;
  on 2026-10-30 the owner chose one fixed culture for every library's texts (`TraceText.Culture`, issue #5).
- **O2 holds** (`tools/try-onboarding.sh`): every package packed at a throwaway version into a scratch folder; for
  each template `creatura new` and `dotnet new` (from the packed `Creatura.Templates`, in a throwaway hive) made the
  same files; each project restored from that folder with a fresh package cache, built, and ran offline to the goal.
- On the owner's laptop, `creatura doctor` reported one warning (the packages are not on nuget.org yet) and no
  failure: the SDK, the home and its endorsed `claude-code` cell, the ledger, the hooks, the hub, and the DeepSeek key
  set (by name).
- Two README snippets did not compile as written and were fixed: `WorldCatalog.Permit.Set("all")` is
  `BarrierSet.All(permit)`, and the sync classes are `LedgerSyncNode` and `LedgerHub`.
