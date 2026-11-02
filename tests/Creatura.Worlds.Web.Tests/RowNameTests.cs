using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>W35: an unlabelled checkbox is named by its row, so a script can tick an item by its name.</summary>
[Trait("Category", "Browser")]
[Collection("browser")]
public class RowNameTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    [BrowserFact]
    public async Task AnUnlabelledCheckboxTakesItsRowsTextAndALabelledOneKeepsItsLabel()
    {
        var page = await demo.Browser.Browser.NewPageAsync();
        try
        {
            await page.SetContentAsync("""
                <!doctype html><html><body>
                <h1>Shopping</h1>
                <ul>
                  <li><input type="checkbox"> milk <button>Delete</button></li>
                  <li><input type="checkbox" checked> bread <a href="#">edit</a></li>
                  <li><label><input type="checkbox"> eggs</label> <button>Delete</button></li>
                  <li><input type="checkbox" aria-label="Butter, salted"> butter</li>
                </ul>
                <table><tr><td><input type="radio" name="size"></td><td>Large</td><td><button>Pick</button></td></tr></table>
                </body></html>
                """);
            var screen = await WebWorld.ObserveAsync(page);
            var boxes = screen.Controls.Where(c => c.Role is "checkbox" or "radio").ToList();
            Assert.Equal(["milk", "bread", "eggs", "Butter, salted", "Large"], boxes.Select(c => c.Name));
            Assert.Equal(["checkbox-milk", "checkbox-bread", "checkbox-eggs", "checkbox-butter-salted", "radio-large"], boxes.Select(c => c.Id));
            Assert.EndsWith(". [checkbox] milk (not checked)", boxes[0].Line());
            Assert.EndsWith(". [checkbox] bread (checked)", boxes[1].Line());
            Assert.Equal(["Delete", "edit", "Delete", "Pick"], screen.Controls.Where(c => c.Role is "button" or "link").Select(c => c.Name));

            // A script finds the box by the item's name, and ticking it lands on that very element.
            var milk = screen.ById("checkbox-milk")!;
            await WebWorld.DoAsync(page, milk, null, 2000);
            var after = await WebWorld.ObserveAsync(page);
            Assert.True(after.ById("checkbox-milk")!.Checked);
            Assert.False(after.ById("checkbox-eggs")!.Checked);
        }
        finally
        {
            await page.CloseAsync();
        }
    }
}
