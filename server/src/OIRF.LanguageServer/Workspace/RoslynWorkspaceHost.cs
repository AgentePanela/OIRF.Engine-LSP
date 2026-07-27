using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.CodeAnalysis.Text;
using Microsoft.Extensions.Logging;

namespace OIRF.LanguageServer.Workspace;

/// <summary>
/// Owns the Roslyn <see cref="MSBuildWorkspace"/> for one loaded engine workspace. Must only be
/// constructed after <c>MSBuildLocator.RegisterDefaults()</c> has run (see Program.cs).
/// </summary>
public sealed class RoslynWorkspaceHost(ILogger<RoslynWorkspaceHost> logger) : IDisposable
{
    private MSBuildWorkspace? _workspace;

    // Tracked separately from `_workspace.CurrentSolution`: TryRefreshDocument mutates this via
    // pure Solution.WithDocumentText transforms and never pushes the result back into the
    // Workspace (Workspace.TryApplyChanges is meant for edits flowing the other way, back out to
    // disk/the host - since our new text already came FROM disk, that would be backwards and
    // could trigger an unwanted write-back). A full LoadAsync resyncs both.
    private Solution? _currentSolution;

    public Solution? CurrentSolution => _currentSolution;

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
            logger.LogInformation("No .sln/.slnx/.csproj found for this workspace folder; Roslyn workspace not loaded.");
            return false;
        }

        _workspace?.Dispose();
        var workspace = MSBuildWorkspace.Create();
        workspace.RegisterWorkspaceFailedHandler(e =>
            logger.LogWarning("MSBuild workspace diagnostic ({Kind}): {Message}", e.Diagnostic.Kind, e.Diagnostic.Message));
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

        _currentSolution = workspace.CurrentSolution;
        RefreshEngineRelevantProjects();
        logger.LogInformation(
            "Loaded {ProjectCount} project(s), {RelevantCount} engine-relevant",
            _currentSolution.Projects.Count(),
            EngineRelevantProjects.Count);

        return EngineRelevantProjects.Count > 0;
    }

    /// <summary>
    /// Fast path for the common rescan trigger (a .cs save): swaps just that document's text into
    /// the tracked <see cref="Solution"/> snapshot via <c>Solution.WithDocumentText</c> - a cheap,
    /// structurally-shared, in-memory transform that lets Roslyn re-parse only the changed file
    /// instead of re-running MSBuild evaluation for the whole solution. Returns false (caller
    /// should fall back to a full <see cref="LoadAsync"/> reload) when the file isn't part of the
    /// currently-loaded solution at all - most commonly a brand new file, which genuinely does
    /// need MSBuild to re-evaluate the project's file list.
    /// </summary>
    public bool TryRefreshDocument(string filePath)
    {
        if (_currentSolution is not { } solution)
            return false;

        var documentIds = solution.GetDocumentIdsWithFilePath(filePath);
        if (documentIds.IsEmpty)
            return false;

        string text;
        try
        {
            text = File.ReadAllText(filePath);
        }
        catch (IOException ex)
        {
            logger.LogWarning(ex, "Failed to read changed file {Path}; falling back to a full reload.", filePath);
            return false;
        }

        var sourceText = SourceText.From(text);
        foreach (var documentId in documentIds)
            solution = solution.WithDocumentText(documentId, sourceText, PreservationMode.PreserveIdentity);

        _currentSolution = solution;
        RefreshEngineRelevantProjects();
        return true;
    }

    private void RefreshEngineRelevantProjects() =>
        EngineRelevantProjects = _currentSolution?.Projects.Where(IsEngineRelevant).ToList() ?? [];

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
