using Microsoft.Extensions.Logging;
using OIRF.LanguageServer.Assets;
using OIRF.LanguageServer.Schema;

namespace OIRF.LanguageServer.Workspace;

/// <summary>
/// Ties together workspace resolution, the Roslyn schema, and the resource index for one LSP
/// session, and owns the debounced rescan triggers for both. Every feature handler
/// (Completion/Hover/diagnostics) reads <see cref="Schema"/>/<see cref="Resources"/> off this.
/// </summary>
public sealed class EngineWorkspaceManager(ILoggerFactory loggerFactory) : IDisposable
{
    private readonly ILogger<EngineWorkspaceManager> _logger = loggerFactory.CreateLogger<EngineWorkspaceManager>();
    private readonly RoslynWorkspaceHost _roslynHost = new(loggerFactory.CreateLogger<RoslynWorkspaceHost>());
    private readonly SchemaBuilder _schemaBuilder = new(loggerFactory.CreateLogger<SchemaBuilder>());

    private IReadOnlyList<string> _workspaceRoots = [];
    private DebouncedRescanQueue? _csharpRescanQueue;
    private DebouncedRescanQueue? _resourceRescanQueue;

    public EngineSchema Schema { get; private set; } = EngineSchema.Empty;

    public ResourceIndex Resources { get; private set; } = ResourceIndex.Empty;

    /// <summary>
    /// Defense-in-depth check re-evaluated after every schema/resource rebuild: every feature
    /// handler short-circuits to a no-op when this is false, so an irrelevant workspace never
    /// gets diagnostics/completions even if client-side detection let the server spawn.
    /// </summary>
    public bool IsEngineWorkspace { get; private set; }

    /// <summary>Raised after every schema rebuild, so already-open documents can be re-validated.</summary>
    public event Action? SchemaChanged;

    public async Task InitializeAsync(IReadOnlyList<string> workspaceRoots, CancellationToken cancellationToken)
    {
        _workspaceRoots = workspaceRoots;

        foreach (var root in workspaceRoots)
        {
            var entryPoint = EngineWorkspaceLocator.Find(root);
            if (entryPoint.IsEmpty)
                continue;

            await _roslynHost.LoadAsync(entryPoint, cancellationToken);
            // First workspace folder with a usable .sln/.csproj wins for M1; aggregating a true
            // multi-root workspace across several distinct solutions is a fast-follow, not
            // needed for the single-repo scenarios this milestone targets.
            break;
        }

        await RebuildSchemaAsync(cancellationToken);
        RebuildResourceIndex();
        DetermineIsEngineWorkspace();

        _csharpRescanQueue = new DebouncedRescanQueue(TimeSpan.FromMilliseconds(750), RebuildSchemaAsync);
        _resourceRescanQueue = new DebouncedRescanQueue(TimeSpan.FromMilliseconds(500), _ =>
        {
            RebuildResourceIndex();
            return Task.CompletedTask;
        });
    }

    public void NotifyCSharpFileChanged() => _csharpRescanQueue?.Trigger();

    public void NotifyResourceFileChanged() => _resourceRescanQueue?.Trigger();

    private async Task RebuildSchemaAsync(CancellationToken cancellationToken)
    {
        try
        {
            Schema = await _schemaBuilder.BuildAsync(_roslynHost.EngineRelevantProjects, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Schema rebuild failed.");
        }

        DetermineIsEngineWorkspace();
        SchemaChanged?.Invoke();
    }

    private void RebuildResourceIndex()
    {
        Resources = ResourceIndexer.Build(_workspaceRoots, loggerFactory.CreateLogger("ResourceIndexer"));
    }

    private void DetermineIsEngineWorkspace()
    {
        if (Schema.PrototypesByTypeKey.Count > 0 || _roslynHost.EngineRelevantProjects.Count > 0)
        {
            IsEngineWorkspace = true;
            return;
        }

        IsEngineWorkspace = _workspaceRoots.Any(HasPrototypesFolder);
    }

    private static bool HasPrototypesFolder(string root)
    {
        try
        {
            return Directory.Exists(root) &&
                   Directory.EnumerateDirectories(root, "Prototypes", SearchOption.AllDirectories).Any();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose() => _roslynHost.Dispose();
}
