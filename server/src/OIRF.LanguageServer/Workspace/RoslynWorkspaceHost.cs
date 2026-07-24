using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;

namespace OIRF.LanguageServer.Workspace;

/// <summary>
/// Owns the Roslyn <see cref="MSBuildWorkspace"/> for one loaded engine workspace. Must only be
/// constructed after <c>MSBuildLocator.RegisterDefaults()</c> has run (see Program.cs).
/// </summary>
public sealed class RoslynWorkspaceHost(ILogger<RoslynWorkspaceHost> logger) : IDisposable
{
    private MSBuildWorkspace? _workspace;

    public Solution? CurrentSolution => _workspace?.CurrentSolution;

    /// <summary>
    /// Projects that either are "Engine.Shared" or reference it (directly or via a metadata
    /// reference) - the schema builder only needs to walk these, since that's the marker for
    /// "this project can plausibly define/consume [Prototype]/[RegisterComponent] types".
    /// </summary>
    public IReadOnlyList<Project> EngineRelevantProjects { get; private set; } = [];

    public async Task<bool> LoadAsync(EngineWorkspaceLocator.EntryPoint entryPoint, CancellationToken cancellationToken)
    {
        if (entryPoint.IsEmpty)
        {
            logger.LogInformation("No .sln/.csproj found for this workspace folder; Roslyn workspace not loaded.");
            return false;
        }

        _workspace?.Dispose();
        var workspace = MSBuildWorkspace.Create();
        workspace.WorkspaceFailed += (_, e) =>
            logger.LogWarning("MSBuild workspace diagnostic ({Kind}): {Message}", e.Diagnostic.Kind, e.Diagnostic.Message);
        _workspace = workspace;

        try
        {
            if (entryPoint.SolutionPath is not null)
            {
                logger.LogInformation("Opening solution {Path}", entryPoint.SolutionPath);
                await workspace.OpenSolutionAsync(entryPoint.SolutionPath, cancellationToken: cancellationToken);
            }
            else
            {
                foreach (var projectPath in entryPoint.LooseProjectPaths)
                {
                    logger.LogInformation("Opening project {Path}", projectPath);
                    await workspace.OpenProjectAsync(projectPath, cancellationToken: cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            // A downstream repo may have unrelated projects that fail to load (missing SDKs,
            // platform-specific tooling, etc.) - never let that abort the whole workspace load.
            logger.LogWarning(ex, "Failed to fully load workspace entry point {EntryPoint}", entryPoint.SolutionPath ?? string.Join(", ", entryPoint.LooseProjectPaths));
        }

        EngineRelevantProjects = workspace.CurrentSolution.Projects.Where(IsEngineRelevant).ToList();
        logger.LogInformation(
            "Loaded {ProjectCount} project(s), {RelevantCount} engine-relevant",
            workspace.CurrentSolution.Projects.Count(),
            EngineRelevantProjects.Count);

        return EngineRelevantProjects.Count > 0;
    }

    private static bool IsEngineRelevant(Project project)
    {
        if (IsEngineShared(project))
            return true;

        var referencesEngineShared = project.ProjectReferences
            .Select(reference => project.Solution.GetProject(reference.ProjectId))
            .Any(referenced => referenced is not null && IsEngineShared(referenced));
        if (referencesEngineShared)
            return true;

        return project.MetadataReferences.Any(reference =>
            reference.Display is not null &&
            Path.GetFileNameWithoutExtension(reference.Display).Equals("Engine.Shared", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsEngineShared(Project project) =>
        string.Equals(project.AssemblyName, "Engine.Shared", StringComparison.OrdinalIgnoreCase);

    public void Dispose() => _workspace?.Dispose();
}
