# Creatura.Worlds.Web

Web worlds for Creatura: a persona uses a real web app through Playwright and is judged by the app's own data, never by the page or the model. The persona sees only the visible text and the numbered controls; each control is a tool; a control that writes promises the task's done fact, which only a declared check of the app's data grants, so a save the page shows and the data does not is caught as a lie. Barriers sit between the browser and the app as Playwright routes.

```sh
dotnet add package Creatura.Worlds.Web --prerelease
```

Run the bundled offline demo (a Chromium for Playwright is needed):

```csharp
using Creatura.Worlds.Web;

await using var app = DemoApp.Start();
await using var browser = await WebBrowser.LaunchAsync();
var adapter = app.Adapter();
var cell = await UxRunner.RunAsync(browser, adapter, adapter.Task(null), adapter.Persona("hurried"), adapter.Environment("phone"),
    world => new WebScriptPolicy(DemoFiles.Script, () => world.Current),
    options: new WebWorldOptions { Barriers = ["save-lies"] });
Console.WriteLine(cell.Summary());   // the data says NOT DONE ...; 1 lie caught; the page said it was saved
```

More: [docs/design-web-worlds.md](https://github.com/egarim/creatura/blob/main/docs/design-web-worlds.md).

Free for noncommercial use under the [PolyForm Noncommercial License 1.0.0](https://github.com/egarim/creatura/blob/main/LICENSE.md). Commercial use needs a separate license from the author.
