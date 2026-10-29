using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Creatura.Cli;

/// <summary>
/// <c>creatura new</c>: a project from one of the templates under <c>templates/content/</c>, which this tool embeds
/// (docs/design-onboarding.md). It reads each template's <c>.template.config/template.json</c> as <c>dotnet new</c>
/// does, for the source name and the symbols it replaces, so both make the same files; the package version is this
/// tool's own. It never writes into a folder that has files in it. Exit codes: 0 made, 2 a usage error.
/// </summary>
public static class NewCommand
{
    public const string Usage = """
        usage: creatura new [agent|world|sk] [options]

          agent                        the designs, then your own regulator, on the toy world with one tool that lies; offline
          world                        your own world: facts, tools that require and grant them, one that lies; offline
          sk                           the control layer on Semantic Kernel; offline until a model key is set

          --name <name>                the project's name (default MyAgent, MyWorld or MyKernel)
          --output <dir>               where to make it (default ./<name>); it must be empty or absent
          --version <v>                the Creatura packages' version (default this tool's)
          --source <dir>               a folder of .nupkg files to restore from as well as nuget.org (writes nuget.config)
          sk only: --key-env <name>    the variable that holds the model key (default OPENAI_API_KEY); the key is never written
                   --base-url <url>    an OpenAI-compatible base URL (default https://api.openai.com/v1)
                   --model <name>      the model (default gpt-4.1-mini)

        With no template and a terminal, it asks.
        """;

    /// <summary>Each template: its folder under templates/content, what it is, and the default project name.</summary>
    public static readonly IReadOnlyList<(string Id, string What, string DefaultName)> Templates =
    [
        ("agent", "an agent: the designs, then your own regulator, on the toy world (offline)", "MyAgent"),
        ("world", "your own world: facts, tools, one tool that lies (offline)", "MyWorld"),
        ("sk", "the control layer on Semantic Kernel, with a real model once a key is set", "MyKernel"),
    ];

    /// <summary>The options that set a template symbol, by the symbol's name in template.json.</summary>
    private static readonly Dictionary<string, string> SymbolOptions = new(StringComparer.Ordinal)
    {
        ["keyEnv"] = "--key-env",
        ["baseUrl"] = "--base-url",
        ["model"] = "--model",
        ["creaturaVersion"] = "--version",
    };

    /// <param name="interactive">True when a person can answer (stdin is a terminal); then a missing template is asked for.</param>
    public static int Run(string[] args, TextWriter output, TextWriter error, TextReader input, bool interactive, string? cwd = null)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            output.WriteLine(Usage);
            return 0;
        }

        cwd ??= Directory.GetCurrentDirectory();
        string? template = null;
        var options = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            if (args[i].StartsWith("--", StringComparison.Ordinal))
            {
                if (i + 1 >= args.Length) return Fail(error, $"{args[i]} needs a value");
                options[args[i]] = args[++i];
            }
            else if (template is null) template = args[i];
            else return Fail(error, $"unexpected argument: {args[i]}");
        }

        string? name = options.GetValueOrDefault("--name");
        if (template is null)
        {
            if (!interactive) return Fail(error, "which template? agent, world or sk");
            output.WriteLine("What do you want to make?");
            for (var i = 0; i < Templates.Count; i++) output.WriteLine($"  {i + 1}. {Templates[i].What}");
            output.Write("Choose 1, 2 or 3 [1]: ");
            var answer = input.ReadLine()?.Trim();
            var pick = string.IsNullOrEmpty(answer) ? 1 : int.TryParse(answer, out var n) ? n : 0;
            if (pick < 1 || pick > Templates.Count) return Fail(error, $"not one of the choices: {answer}");
            template = Templates[pick - 1].Id;
            if (name is null)
            {
                var suggested = Templates[pick - 1].DefaultName;
                output.Write($"Project name [{suggested}]: ");
                var typed = input.ReadLine()?.Trim();
                name = string.IsNullOrEmpty(typed) ? suggested : typed;
            }
        }

        var entry = Templates.FirstOrDefault(t => t.Id == template);
        if (entry.Id is null) return Fail(error, $"unknown template '{template}'; known: {string.Join(", ", Templates.Select(t => t.Id))}");
        name ??= options.TryGetValue("--output", out var outputDir) ? Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(outputDir, cwd))) : entry.DefaultName;
        if (!Regex.IsMatch(name, "^[A-Za-z][A-Za-z0-9_.]*$")) return Fail(error, $"'{name}' cannot be a project name: start with a letter; letters, digits, '.' and '_' only");

        var target = Path.GetFullPath(options.GetValueOrDefault("--output") ?? name, cwd);
        if (Directory.Exists(target) && Directory.EnumerateFileSystemEntries(target).Any()) return Fail(error, $"{target} has files in it; choose an empty or new folder (--output)");
        if (File.Exists(target)) return Fail(error, $"{target} is a file");

        string? source = null;
        if (options.TryGetValue("--source", out var s))
        {
            source = Path.GetFullPath(s, cwd);
            if (!Directory.Exists(source)) return Fail(error, $"no folder at {source}");
        }

        var files = Files(template);
        var config = JsonDocument.Parse(files[".template.config/template.json"]).RootElement;
        var replacements = new List<(string From, string To)>();
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "--name", "--output", "--source" };
        if (config.TryGetProperty("sourceName", out var sourceName)) replacements.Add((sourceName.GetString()!, name));
        if (config.TryGetProperty("symbols", out var symbols))
        {
            foreach (var symbol in symbols.EnumerateObject())
            {
                if (SymbolOptions.TryGetValue(symbol.Name, out var takes)) allowed.Add(takes);
                var replaces = symbol.Value.GetProperty("replaces").GetString()!;
                var value = SymbolOptions.TryGetValue(symbol.Name, out var option) && options.TryGetValue(option, out var given) ? given
                    : symbol.Name == "creaturaVersion" ? ToolVersion()
                    : symbol.Value.GetProperty("defaultValue").GetString()!;
                replacements.Add((replaces, value));
            }
        }
        var unused = options.Keys.Where(k => !allowed.Contains(k)).ToList();
        if (unused.Count > 0) return Fail(error, $"{string.Join(", ", unused)} {(unused.Count == 1 ? "does" : "do")} not apply to the {template} template");

        Directory.CreateDirectory(target);
        var written = new List<string>();
        foreach (var (path, text) in files.Where(f => !f.Key.StartsWith(".template.config/", StringComparison.Ordinal)).OrderBy(f => f.Key, StringComparer.Ordinal))
        {
            var relative = Replace(path, replacements);
            var full = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, Replace(text, replacements), new UTF8Encoding(false));
            written.Add(relative);
        }
        if (source is not null)
        {
            File.WriteAllText(Path.Combine(target, "nuget.config"), NuGetConfig(source), new UTF8Encoding(false));
            written.Add("nuget.config");
        }

        var version = replacements.First(r => r.From == "CREATURA_VERSION").To;
        output.WriteLine($"made {name} from the {template} template in {target}, on the Creatura packages {version}:");
        foreach (var file in written) output.WriteLine($"  {file}");
        output.WriteLine();
        output.WriteLine("next:");
        output.WriteLine($"  cd {Path.GetRelativePath(cwd, target)}");
        output.WriteLine("  dotnet run");
        if (template == "sk") output.WriteLine($"  (offline until {replacements.First(r => r.From == "MODEL_KEY_ENV").To} holds a key)");
        if (source is null) output.WriteLine("if the packages are not found, they are not on nuget.org yet: download the .nupkg files from the release and pass --source <that folder>");
        return 0;
    }

    /// <summary>The version of this tool, without the commit it was built from: the packages' version of the same release.</summary>
    public static string ToolVersion()
    {
        var version = typeof(NewCommand).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";
        var plus = version.IndexOf('+');
        return plus >= 0 ? version[..plus] : version;
    }

    /// <summary>A template's files, by their path inside the template folder ('/' separated), from this tool's resources.</summary>
    public static IReadOnlyDictionary<string, string> Files(string template)
    {
        var assembly = typeof(NewCommand).Assembly;
        var prefix = $"templates/{template}/";
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var path = resource.Replace('\\', '/');
            if (!path.StartsWith(prefix, StringComparison.Ordinal)) continue;
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            files[path[prefix.Length..]] = reader.ReadToEnd();
        }
        return files;
    }

    private static string Replace(string text, List<(string From, string To)> replacements)
    {
        foreach (var (from, to) in replacements) text = text.Replace(from, to, StringComparison.Ordinal);
        return text;
    }

    private static string NuGetConfig(string source) => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <!-- Written by creatura new: the Creatura packages from a local folder, everything else from nuget.org. -->
        <configuration>
          <packageSources>
            <add key="creatura-local" value="{System.Security.SecurityElement.Escape(source)}" />
            <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
          </packageSources>
        </configuration>

        """;

    private static int Fail(TextWriter error, string message)
    {
        error.WriteLine(message);
        error.WriteLine("creatura new --help for the options");
        return 2;
    }
}
