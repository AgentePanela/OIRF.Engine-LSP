using OIRF.LanguageServer.Workspace;

namespace OIRF.LanguageServer.Tests;

public class EngineWorkspaceLocatorTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("OIRF.LanguageServer.Tests.").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Finds_sln_at_root()
    {
        File.WriteAllText(Path.Combine(_root, "Game.sln"), "");

        var entryPoint = EngineWorkspaceLocator.Find(_root);

        Assert.Equal(Path.Combine(_root, "Game.sln"), entryPoint.SolutionPath);
    }

    [Fact]
    public void Finds_slnx_at_root()
    {
        File.WriteAllText(Path.Combine(_root, "Game.slnx"), "");

        var entryPoint = EngineWorkspaceLocator.Find(_root);

        Assert.Equal(Path.Combine(_root, "Game.slnx"), entryPoint.SolutionPath);
    }

    [Fact]
    public void Prefers_slnx_over_sln_when_otherwise_tied()
    {
        File.WriteAllText(Path.Combine(_root, "Game.sln"), "Engine.Shared");
        File.WriteAllText(Path.Combine(_root, "Game.slnx"), "Engine.Shared");

        var entryPoint = EngineWorkspaceLocator.Find(_root);

        Assert.Equal(Path.Combine(_root, "Game.slnx"), entryPoint.SolutionPath);
    }

    [Fact]
    public void Prefers_solution_referencing_more_engine_projects_over_format()
    {
        File.WriteAllText(Path.Combine(_root, "Game.slnx"), "");
        File.WriteAllText(Path.Combine(_root, "Engine.sln"), "Engine.Shared Engine.Client Engine.Server");

        var entryPoint = EngineWorkspaceLocator.Find(_root);

        Assert.Equal(Path.Combine(_root, "Engine.sln"), entryPoint.SolutionPath);
    }

    [Fact]
    public void Falls_back_to_loose_csproj_when_no_solution_file_exists()
    {
        File.WriteAllText(Path.Combine(_root, "Game.csproj"), "");

        var entryPoint = EngineWorkspaceLocator.Find(_root);

        Assert.Null(entryPoint.SolutionPath);
        Assert.Equal([Path.Combine(_root, "Game.csproj")], entryPoint.LooseProjectPaths);
    }
}
