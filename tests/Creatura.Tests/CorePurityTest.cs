using System.Reflection;

namespace Creatura.Tests;

/// <summary>
/// The four package-free projects stay package-free: no <c>PackageReference</c> in their project files, and no
/// reference from their assemblies to an agent framework, a model abstraction or a provider SDK. The Agent Framework
/// host (<c>Creatura.Hosts.AgentFramework</c>) is the one project in src/ allowed a package; nothing here may need it.
/// </summary>
public class CorePurityTest
{
    public static TheoryData<string> PackageFreeProjects => ["Creatura", "Creatura.Worlds", "Creatura.Bench", "Creatura.Seats"];

    private static readonly string[] ForbiddenPrefixes = ["Microsoft.Agents.", "Microsoft.Extensions.AI", "Microsoft.SemanticKernel"];

    private static string SourceRoot { get; } = Path.GetFullPath(Path.Combine(GoldenTraceTests.Directory_, "..", "..", "..", "src"));

    internal static bool Forbidden(string assemblyName) =>
        ForbiddenPrefixes.Any(p => assemblyName.StartsWith(p, StringComparison.Ordinal))
        || assemblyName == "OpenAI" || assemblyName.StartsWith("OpenAI.", StringComparison.Ordinal);

    private static Assembly AssemblyOf(string project) => project switch
    {
        "Creatura" => typeof(ReferenceLoop).Assembly,
        "Creatura.Worlds" => typeof(Worlds.AgentCatalog).Assembly,
        "Creatura.Bench" => typeof(Bench.Battery).Assembly,
        "Creatura.Seats" => typeof(Seats.ModelPolicy).Assembly,
        _ => throw new ArgumentException(project),
    };

    [Theory]
    [MemberData(nameof(PackageFreeProjects))]
    public void TheProjectFileHasNoPackageReference(string project)
    {
        var path = Path.Combine(SourceRoot, project, project + ".csproj");
        Assert.True(File.Exists(path), $"{path} not found");
        Assert.DoesNotContain("PackageReference", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(PackageFreeProjects))]
    public void TheAssemblyReferencesNoFrameworkModelAbstractionOrProviderSdk(string project)
    {
        var assembly = AssemblyOf(project);
        Assert.Equal(project, assembly.GetName().Name);

        var references = assembly.GetReferencedAssemblies().Select(a => a.Name ?? "").ToList();
        Assert.NotEmpty(references);
        Assert.DoesNotContain(references, Forbidden);
    }

    [Fact]
    public void TheRuleCatchesWhatItIsMeantToCatch()
    {
        // The host's own references, by name: a rule that let these through would make the theories above vacuous.
        Assert.All(["Microsoft.Agents.AI", "Microsoft.Agents.AI.OpenAI", "Microsoft.Extensions.AI", "Microsoft.Extensions.AI.Abstractions",
                    "Microsoft.SemanticKernel.Core", "OpenAI"], name => Assert.True(Forbidden(name), name));
        Assert.All(["System.Runtime", "System.Text.Json", "System.Net.Http", "Creatura", "Microsoft.Extensions.Logging.Abstractions", "OpenAIish"],
            name => Assert.False(Forbidden(name), name));
    }
}
