# Creatura.Worlds.Terminal

Terminal worlds for Creatura: an agent drives a real terminal application — a TUI menu, a REPL, a CLI prompt — through Sentiora's PTY runtime, sees the rendered screen as a report, and is judged by an independent check of the application's outcome, never by the screen or by the runtime's word. The world's facts are only what it can prove (the session exists, the runtime's state/revision/geometry, the child's exit code), and the task's done fact is granted only while the injected check agrees, so an application that says "Saved!" and wrote nothing cannot make the world say the work landed. A scripted `SimulatedTerminal` runs the same world, tools, facts and bounds with no process at all.

```sh
dotnet add package Creatura.Worlds.Terminal --prerelease
```

```csharp
using Creatura.Worlds.Terminal;

var task = new TerminalTask { Name = "choose-settings" };
await using var world = await TerminalWorld.OpenAsync(task, new TerminalWorldOptions
{
    Driver = new SimulatedTerminal(
        [SimulatedStep.Line("2", "SETTINGS\n1) Sound\n2) Back\n")],
        screen: "MAIN MENU\n1) Notes\n2) Settings\n"),
});

var read = world.Act("read-screen");
Console.WriteLine(read.Observation);        // MAIN MENU ...
var chosen = world.Act("line", "2");        // the number, then Enter
Console.WriteLine(chosen.Observation);      // sent a line (1 character); running at revision 1; new: SETTINGS
Console.WriteLine(world.Has("state:running"));   // a fact the runtime reports; the screen is not a fact
```

For the real runtime, set `ServerPath` (the Sentiora checkout's `src/server.js`) and `Create` (the command the session spawns), or inject an already opened `SentioraClient` and `SessionHandle`. The client is vendored under `Client/` from Sentiora commit `302d71f`; it is to be replaced by a `PackageReference` once Sentiora publishes one.

**Not yet wired:** there is no `WorldManifest` in v1, following the `Creatura.Worlds.Web` precedent, so this package is not reachable from `creatura run --world terminal`; a manifest registered with `WorldCatalog` is what that command would need. A host is handed the world instance from `TerminalWorld.OpenAsync`.

More: [docs/design-terminal-worlds.md](https://github.com/egarim/creatura/blob/main/docs/design-terminal-worlds.md).

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
