using Creatura.Worlds.Web;

namespace Creatura.Worlds.Web.Tests;

/// <summary>Accounts and the declared sign-in, as the adapter reads them: no browser.</summary>
public class SignInAdapterTests
{
    [Fact]
    public void TheDemoSignsInOneAccountByDeclaredSteps()
    {
        var adapter = DemoFiles.Adapter();
        var ana = adapter.Account("ana");
        Assert.Equal(DemoApp.User, ana.UserValue);
        Assert.Equal("ana", adapter.Task("private-bread").Account);
        Assert.Equal(["type Email: {user}", "type Password: {password}", "click Sign in"], adapter.SignIn!.Steps);
        Assert.True(adapter.SignIn.SessionStorage);
    }

    [Fact]
    public void CredentialsNamedByVariableAreReadWhenAsked()
    {
        var name = "CREATURA_TEST_PASSWORD_" + Guid.NewGuid().ToString("N")[..6];
        var account = new UxAccount("x", "someone", "env:" + name);
        Assert.Equal("", account.PasswordValue);
        Environment.SetEnvironmentVariable(name, "s3cret");
        try
        {
            Assert.Equal("s3cret", account.PasswordValue);
            Assert.DoesNotContain("s3cret", DemoFiles.Adapter().ToJson());
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void ATaskNeedsAnAccountTheAdapterHasAndASignInThatSaysHowToTell()
    {
        var e = Assert.Throws<FormatException>(() => UxAdapter.Parse("""
            { "app": "x", "personas": [{"id":"p","description":"d"}], "environments": [{"id":"e","width":400,"height":800}],
              "tasks": [{"id":"t","prompt":"p","account":"bob","check":{"url":"/a","exists":true}}],
              "signIn": { "steps": [] } }
            """));
        Assert.Contains("task t: no account 'bob'", e.Message);
        Assert.Contains("signIn has no steps", e.Message);
        Assert.Contains("signIn has no signedInText", e.Message);
    }

    [Fact]
    public void TheSingularTemplateParsesAndKeepsNoCredential()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "singular-starter.adapter.json");
        var adapter = UxAdapter.Load(path);
        Assert.Equal("env:UX_MEMBER_PASSWORD", adapter.Account("member").Password);
        Assert.True(adapter.SignIn!.SessionStorage);
        Assert.Equal(["phone", "tablet", "desktop"], adapter.Environments.Select(e => e.Id));
        Assert.Equal("env:UX_SERVICE_AUTHORIZATION", adapter.Tasks[0].Check!.Headers["Authorization"]);
    }

    [Theory]
    [InlineData("type Email: {user}", "type", "Email", "{user}")]
    [InlineData("click Sign in", "click", "Sign in", "")]
    [InlineData("choose Plan: Pro: yearly", "choose", "Plan", "Pro: yearly")]
    public void ScriptStepsParse(string line, string verb, string name, string text) =>
        Assert.Equal(new ScriptStep(verb, name, text), ScriptStep.Parse(line));
}

/// <summary>Sign in once per account, keep the session, put sessionStorage back, sign in again when it expires.</summary>
[Trait("Category", "Browser")]
[Collection("browser")]
public class SessionTests(DemoFixture demo) : IClassFixture<DemoFixture>
{
    private static readonly UxMatrixSpec Private = new() { Tasks = ["private-bread"] };

    [BrowserFact]
    public async Task AMatrixSignsEachAccountInOnceAndReusesTheSession()
    {
        var before = demo.App.SignIns;
        var report = await UxMatrix.RunAsync(demo.Browser, demo.App.Adapter(), Private, new UxSeats());
        Assert.Equal(4, report.Cells.Count);
        Assert.All(report.Cells, c => Assert.True(c.DataDone, c.Summary()));
        Assert.Equal((1, 3), (report.SignIns, report.SessionsReused));
        Assert.Equal(1, demo.App.SignIns - before);
        Assert.Contains("1 sign-in, 3 sessions reused", report.Totals());
        Assert.Equal(["buy bread"], demo.App.PrivateNotes(DemoApp.User));
    }

    [BrowserFact]
    public async Task WithoutSessionStorageTheSessionDoesNotCarryOver()
    {
        // The demo keeps its token where the Singular starter keeps its Keycloak refresh token: Playwright's storage state
        // alone does not carry it, so the cell cannot open signed in, and says why.
        var adapter = demo.App.Adapter();
        adapter = adapter with { SignIn = adapter.SignIn! with { SessionStorage = false } };
        var report = await UxMatrix.RunAsync(demo.Browser, adapter, Private with { Personas = ["hurried"], Environments = ["desktop"] }, new UxSeats());
        var cell = Assert.Single(report.Cells);
        Assert.Contains("the session did not carry over", cell.Error);
        Assert.Contains("signIn.sessionStorage is off", cell.Error);
    }

    [BrowserFact]
    public async Task AnExpiredSessionIsDroppedAndTheAccountSignsInAgain()
    {
        var adapter = demo.App.Adapter();
        var sessions = new UxSessions();
        var spec = Private with { Personas = ["hurried"], Environments = ["desktop"] };
        Assert.True(Assert.Single((await UxMatrix.RunAsync(demo.Browser, adapter, spec, new UxSeats(), sessions: sessions)).Cells).DataDone);
        demo.App.ExpireSessions();
        var again = await UxMatrix.RunAsync(demo.Browser, adapter, spec, new UxSeats(), sessions: sessions);
        Assert.True(Assert.Single(again.Cells).DataDone, again.Cells[0].Summary());
        Assert.Equal(2, sessions.SignIns);
        Assert.Equal(1, again.SignIns);
    }

    [BrowserFact]
    public async Task ASessionKeptOnDiskIsOwnerOnlyAndReusedByTheNextRun()
    {
        var folder = Path.Combine(Path.GetTempPath(), "creatura-sessions-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var adapter = demo.App.Adapter();
            var first = new UxSessions(folder);
            await first.GetAsync(demo.Browser, adapter, adapter.Account("ana"));
            var file = Assert.Single(Directory.GetFiles(folder));
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file));
            Assert.DoesNotContain(DemoApp.Password, File.ReadAllText(file));

            var before = demo.App.SignIns;
            var next = new UxSessions(folder);
            await using var world = await WebWorld.OpenAsync(demo.Browser, adapter, adapter.Task("private-bread"), adapter.Environment("phone"), new WebWorldOptions { Sessions = next });
            Assert.Equal("ana", world.SignedInAs);
            Assert.Contains("Signed in as ana@example.test", world.Current.Text);
            Assert.Equal((0, 1), (next.SignIns, next.Reused));
            Assert.Equal(before, demo.App.SignIns);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [BrowserFact]
    public async Task AFailedSignInNeverShowsThePassword()
    {
        var adapter = demo.App.Adapter() with { Accounts = [new UxAccount("ana", DemoApp.User, "wrong-but-secret")] };
        adapter = adapter with { SignIn = adapter.SignIn! with { TimeoutMs = 1500 } };
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => UxSessions.SignInAsync(demo.Browser, adapter, adapter.Account("ana")));
        Assert.Contains("no page showed \"Signed in as\"", e.Message);
        Assert.DoesNotContain("wrong-but-secret", e.Message);
    }

    [BrowserFact]
    public async Task ALieOnThePrivateListIsCaughtLikeAnyOther()
    {
        var report = await UxMatrix.RunAsync(demo.Browser, demo.App.Adapter(), Private with { Personas = ["careful"], Environments = ["phone"], Barriers = ["save-lies"] }, new UxSeats());
        var cell = Assert.Single(report.Cells);
        Assert.False(cell.DataDone);
        Assert.Equal(1, cell.LiesCaught);
        Assert.True(cell.PageSaidNotDone);
    }
}
