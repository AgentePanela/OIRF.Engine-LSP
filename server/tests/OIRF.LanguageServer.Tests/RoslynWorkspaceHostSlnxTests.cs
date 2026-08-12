using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using OIRF.LanguageServer.Workspace;

namespace OIRF.LanguageServer.Tests;

/// <summary>
/// Regression test for https://github.com/dotnet/roslyn/issues/78097: MSBuildWorkspace threw
/// "No file format header found" on .slnx solutions on Microsoft.CodeAnalysis.Workspaces.MSBuild
/// 4.x (fixed in 5.0.0, which migrated solution parsing to vs-solutionpersistence) - even though
/// EngineWorkspaceLocator already found the file correctly. RoslynWorkspaceHost.LoadAsync swallows
/// that exception and just logs a warning (see its catch block), so a fake/in-memory workspace
/// can't catch this class of bug; this exercises the real MSBuildWorkspace against an on-disk
/// .slnx + .csproj instead.
/// </summary>
public class RoslynWorkspaceHostSlnxTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("OIRF.LanguageServer.Tests.Slnx.").FullName;

    public RoslynWorkspaceHostSlnxTests()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public async Task Opens_slnx_solution_and_loads_its_project()
    {
        var projectDir = Path.Combine(_root, "App");
        Directory.CreateDirectory(projectDir);
        File.WriteAllText(Path.Combine(projectDir, "App.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net9.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(_root, "App.slnx"), """
            <Solution>
              <Project Path="App/App.csproj" />
            </Solution>
            """);

        var entryPoint = EngineWorkspaceLocator.Find(_root);
        Assert.Equal(Path.Combine(_root, "App.slnx"), entryPoint.SolutionPath);

        using var host = new RoslynWorkspaceHost(NullLogger<RoslynWorkspaceHost>.Instance);
        await host.LoadAsync(entryPoint, CancellationToken.None);

        var loadedProject = Assert.Single(host.CurrentSolution?.Projects ?? []);
        Assert.Equal("App", loadedProject.AssemblyName);
    }
}
