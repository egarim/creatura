using System.Globalization;
using Creatura.Cli;

namespace Creatura.Cells.Tests;

/// <summary>Getting started (docs/design-onboarding.md): creatura new (O1, O5), creatura run (O3), creatura doctor (O4). O2 is tools/try-onboarding.sh.</summary>
public sealed class OnboardingTests
{
    private static readonly string[] Tokens = ["CreaturaApp", "CREATURA_VERSION", "__PACKAGE_VERSION__", "MODEL_KEY_ENV", "MODEL_BASE_URL", "MODEL_NAME"];

    private static string RepoRoot
    {
        get
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Creatura.slnx"))) directory = directory.Parent;
            return directory?.FullName ?? throw new DirectoryNotFoundException("Creatura.slnx not found above the test output");
        }
    }

    private static (int Code, string Out, string Err) New(string cwd, string input, bool interactive, params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = NewCommand.Run(["new", .. args], output, error, new StringReader(input), interactive, cwd);
        return (code, output.ToString(), error.ToString());
    }

    [Theory]
    [InlineData("agent", new[] { "Demo.csproj", "Program.cs" })]
    [InlineData("world", new[] { "Demo.csproj", "NewsletterWorld.cs", "Program.cs" })]
    [InlineData("sk", new[] { "Demo.csproj", "Program.cs" })]
    public void O1_NewWritesTheTemplateWithTheNameAndVersionAndNoToken(string template, string[] expected)
    {
        using var temp = new TempHome();
        var (code, _, err) = New(temp.Path, "", false, template, "--name", "Demo", "--version", "9.9.9-test");
        Assert.True(code == 0, err);

        var made = Path.Combine(temp.Path, "Demo");
        Assert.Equal(expected, Directory.EnumerateFiles(made, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(made, f)).Order(StringComparer.Ordinal));
        var text = string.Concat(expected.Select(f => File.ReadAllText(Path.Combine(made, f))));
        Assert.Contains("Version=\"9.9.9-test\"", text);
        foreach (var token in Tokens) Assert.DoesNotContain(token, text);
    }

    [Fact]
    public void O1_WithoutVersionTheProjectTakesTheToolsOwn()
    {
        using var temp = new TempHome();
        Assert.Equal(0, New(temp.Path, "", false, "agent", "--name", "Plain").Code);
        var project = File.ReadAllText(Path.Combine(temp.Path, "Plain", "Plain.csproj"));
        Assert.Contains($"Version=\"{NewCommand.ToolVersion()}\"", project);
        foreach (var token in Tokens) Assert.DoesNotContain(token, project);
    }

    [Fact]
    public void O1_TheToolCarriesTheSameFilesTheTemplatesPackageDoes()
    {
        foreach (var template in NewCommand.Templates.Select(t => t.Id))
        {
            var folder = Path.Combine(RepoRoot, "templates", "content", template);
            var onDisk = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
                .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") && !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}"))
                .ToDictionary(f => Path.GetRelativePath(folder, f).Replace('\\', '/'), File.ReadAllText);
            var embedded = NewCommand.Files(template);
            Assert.Equal(onDisk.Keys.Order(StringComparer.Ordinal), embedded.Keys.Order(StringComparer.Ordinal));
            foreach (var (path, text) in onDisk) Assert.Equal(text, embedded[path]);
        }
    }

    [Fact]
    public void O1_NewRefusesAFolderWithFilesAndOptionsThatDoNotApply()
    {
        using var temp = new TempHome();
        var busy = Path.Combine(temp.Path, "Busy");
        Directory.CreateDirectory(busy);
        File.WriteAllText(Path.Combine(busy, "keep.txt"), "mine");

        Assert.Equal(2, New(temp.Path, "", false, "agent", "--output", busy).Code);
        Assert.Equal(["keep.txt"], Directory.EnumerateFiles(busy).Select(Path.GetFileName));
        Assert.Equal(2, New(temp.Path, "", false, "agent", "--key-env", "X").Code);
        Assert.Equal(2, New(temp.Path, "", false, "nothing").Code);
        Assert.Equal(2, New(temp.Path, "", false).Code);                                   // no template and nobody to ask
        Assert.Equal(2, New(temp.Path, "", false, "agent", "--name", "1bad").Code);
        Assert.Equal(2, New(temp.Path, "", false, "agent", "--source", Path.Combine(temp.Path, "no-such-folder")).Code);
        Assert.False(Directory.Exists(Path.Combine(temp.Path, "MyAgent")));
    }

    [Fact]
    public void O1_NewAsksWhenItCanAndWritesANuGetConfigForASource()
    {
        using var temp = new TempHome();
        var feed = Directory.CreateDirectory(Path.Combine(temp.Path, "feed")).FullName;
        var (code, output, err) = New(temp.Path, "2\nValley\n", true, "--source", feed);
        Assert.True(code == 0, err);
        Assert.Contains("What do you want to make?", output);
        Assert.True(File.Exists(Path.Combine(temp.Path, "Valley", "NewsletterWorld.cs")));
        Assert.Contains($"value=\"{feed}\"", File.ReadAllText(Path.Combine(temp.Path, "Valley", "nuget.config")));
    }

    [Fact]
    public void O5_TheSkTemplateHoldsTheNameOfTheKeyNeverAKeyAndRunsOfflineWithoutOne()
    {
        using var temp = new TempHome();
        Assert.Equal(0, New(temp.Path, "", false, "sk", "--name", "Kernel", "--key-env", "MY_MODEL_KEY", "--model", "deepseek-chat", "--base-url", "https://api.deepseek.com").Code);
        var program = File.ReadAllText(Path.Combine(temp.Path, "Kernel", "Program.cs"));
        Assert.Contains("const string KeyVariable = \"MY_MODEL_KEY\";", program);
        Assert.Contains("Environment.GetEnvironmentVariable(KeyVariable)", program);
        Assert.Contains("ControlledKernelAgent.Offline()", program);
        Assert.DoesNotContain("sk-", program, StringComparison.Ordinal);
    }

    private static async Task<(int Code, string Out, string Err)> RunIt(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = await RunCommand.Run(["run", .. args], output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public async Task O3_RunTriesADesignWithNoCodeCatchesTheLieAndSaysTheSameEveryTime()
    {
        var first = await RunIt("--design", "bach", "--world", "restore", "--barriers", "patchlies");
        Assert.True(first.Code == 0, first.Err);
        Assert.Contains("barriers: patchlies; seat: bfs", first.Out);
        Assert.Contains("contradiction: reported 'reported success', no effect", first.Out);
        Assert.Contains("goal reached", first.Out);

        var culture = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("de-DE");                // a comma for a decimal point must not change a line
            Assert.Equal(first.Out, (await RunIt("--design", "bach", "--world", "restore", "--barriers", "patchlies")).Out);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public async Task O3_UnknownNamesExitTwoAndNameTheKnownOnes()
    {
        var design = await RunIt("--design", "nobody", "--world", "restore");
        Assert.Equal(2, design.Code);
        Assert.Contains("bach", design.Err);
        var world = await RunIt("--design", "bach", "--world", "atlantis");
        Assert.Equal(2, world.Code);
        Assert.Contains("restore", world.Err);
        var barrier = await RunIt("--design", "bach", "--world", "restore", "--barriers", "patchlies+volcano");
        Assert.Equal(2, barrier.Code);
        Assert.Contains("volcano", barrier.Err);
        Assert.Equal(0, (await RunIt("--list")).Code);
    }

    private static (int Code, string Out) Doctor(DoctorEnvironment env, params string[] args)
    {
        var output = new StringWriter();
        var code = DoctorCommand.Run(["doctor", .. args], output, env);
        return (code, output.ToString());
    }

    [Fact]
    public void O4_DoctorSaysAKeyIsSetWithoutShowingItAndFailsOnHooksThatCallNothing()
    {
        using var temp = new TempHome();
        const string secret = "sk-test-0123456789abcdef";
        var settings = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(settings, """{"hooks":{"Stop":[{"hooks":[{"type":"command","command":"/nowhere/creatura cell hook --creatura claude-code"}]}]}}""");
        var agents = Directory.CreateDirectory(Path.Combine(temp.Path, "LaunchAgents")).FullName;
        File.WriteAllText(Path.Combine(agents, "lat.creatura.sync.plist"), "<array><string>--hub</string><string>http://hub.test:5181</string></array>");
        var asked = new List<Uri>();
        var env = new DoctorEnvironment
        {
            Variable = _ => secret,
            Sdks = () => "8.0.400 [/dotnet/sdk]\n10.0.103 [/dotnet/sdk]\n",
            Get = uri => { asked.Add(uri); return uri.Host == "api.nuget.org" ? 404 : null; },
            Home = Path.Combine(temp.Path, "no-home"),
            ClaudeSettings = settings,
            LaunchAgents = agents,
        };

        var (code, output) = Doctor(env);
        Assert.Equal(1, code);
        Assert.DoesNotContain(secret, output);
        Assert.Contains("ok    .NET SDK 10.0.103", output);
        Assert.Contains("Creatura is not on nuget.org yet", output);
        Assert.Contains("fail  Claude Code: the hooks call /nowhere/creatura, which is not there", output);
        Assert.Contains("http://hub.test:5181 did not answer", output);
        Assert.Contains("model keys", output);
        Assert.DoesNotContain("none set", output);
        Assert.False(Directory.Exists(env.Home));                                          // it looked; it made nothing

        asked.Clear();
        File.Delete(settings);
        var offline = Doctor(env, "--offline");
        Assert.Equal(0, offline.Code);
        Assert.Empty(asked);
    }

    [Fact]
    public void O4_DoctorOnAnInitializedHomeChangesNothingInIt()
    {
        using var home = new TempHome();
        var output = new StringWriter();
        var vessel = new FakeVessel("11111111-2222-3333-4444-555555555555", "air");
        Assert.Equal(0, CellCommand.Run(["cell", "init", "--home", home.Path, "--no-mesh", "--creatura", "claude-code"], output, output, vessel));
        var before = Directory.EnumerateFiles(home.Path, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);

        var (code, said) = Doctor(new DoctorEnvironment { Home = home.Path, Offline = true, Sdks = () => "10.0.103 [/x]", ClaudeSettings = Path.Combine(home.Path, "none.json"), LaunchAgents = Path.Combine(home.Path, "none") });
        Assert.Equal(0, code);
        Assert.Contains("endorsed: claude-code", said);
        Assert.Contains("ledger: 1 entries", said);

        var after = Directory.EnumerateFiles(home.Path, "*", SearchOption.AllDirectories).ToDictionary(f => f, File.ReadAllBytes);
        Assert.Equal(before.Keys.Order(), after.Keys.Order());
        foreach (var (path, bytes) in before) Assert.Equal(bytes, after[path]);
    }
}
