using Microsoft.Extensions.Logging;

namespace OIRF.LanguageServer.Yaml;

/// <summary>
/// Every known prototype <c>id:</c> value, keyed by its <c>type:</c> - a workspace-wide
/// complement to <see cref="Schema.EngineSchema.PrototypesByTypeKey"/>, which only knows about
/// the C# type *definitions* (fields, doc comments, ...), never the actual instances authored in
/// YAML. Powers completion for <c>ProtoId&lt;T&gt;</c>-classified fields (see
/// <see cref="Schema.AssetFieldHeuristics.ClassifyProtoId"/>).
/// </summary>
public sealed class PrototypeIdIndex
{
    public required IReadOnlyDictionary<string, IReadOnlyList<string>> IdsByTypeKey { get; init; }

    /// <summary>Where each id is written, with what it inherits from and sets itself.</summary>
    public IReadOnlyList<PrototypeDefinition> Definitions { get; init; } = [];

    /// <summary>Every prototype file scanned (full paths), for workspace-wide references/rename.</summary>
    public IReadOnlyList<string> Files { get; init; } = [];

    public static readonly PrototypeIdIndex Empty = new()
    {
        IdsByTypeKey = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
    };

    public IReadOnlyList<string> Get(string typeKey) =>
        IdsByTypeKey.TryGetValue(typeKey, out var ids) ? ids : [];

    /// <summary>
    /// The definition of <paramref name="id"/>, matched case-insensitively like the engine's
    /// PrototypeManager (its raw and typed buckets both use OrdinalIgnoreCase).
    /// </summary>
    public PrototypeDefinition? Find(string typeKey, string id) =>
        Definitions.FirstOrDefault(d =>
            string.Equals(d.TypeKey, typeKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
}

/// <summary>One <c>id:</c> as last saved on disk.</summary>
public sealed record PrototypeDefinition(
    string TypeKey,
    string Id,
    string FilePath,
    LspRange IdRange,
    IReadOnlyList<string> Parents,
    IReadOnlyList<string> SetFieldNames);

/// <summary>
/// Scans every "Prototypes"-named folder anywhere in the workspace (mirrors
/// <c>ResourceIndexer</c>'s "any folder named Textures/Shaders" convention - prototypes, like
/// resources, aren't assumed to live under a fixed "Resources/Prototypes" path) and reuses
/// <see cref="PrototypeYamlParser"/> - the same parser completion/diagnostics already run on open
/// documents - to pull every item's <c>type:</c>/<c>id:</c> pair, across every file, not just the
/// one currently open.
/// </summary>
public static class PrototypeIdIndexer
{
    // "publish": dotnet publish output, a copy of every resource and prototype file.
    private static readonly string[] ExcludedDirNames = ["bin", "obj", "node_modules", ".git", "publish"];

    public static PrototypeIdIndex Build(IEnumerable<string> workspaceRoots, ILogger logger)
    {
        var prototypeRoots = new List<string>();
        foreach (var root in workspaceRoots)
            FindPrototypesFolders(root, prototypeRoots);

        var idsByTypeKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        var definitions = new List<PrototypeDefinition>();
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var prototypesRoot in prototypeRoots)
            IndexPrototypesRoot(prototypesRoot, idsByTypeKey, definitions, files, logger);

        return new PrototypeIdIndex
        {
            IdsByTypeKey = idsByTypeKey.ToDictionary(
                kv => kv.Key,
                IReadOnlyList<string> (kv) => kv.Value,
                StringComparer.OrdinalIgnoreCase),
            Definitions = definitions,
            Files = files.ToList(),
        };
    }

    private static void FindPrototypesFolders(string root, List<string> prototypeRoots)
    {
        if (!Directory.Exists(root))
            return;

        void Walk(string dir)
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "Prototypes", StringComparison.OrdinalIgnoreCase))
                prototypeRoots.Add(dir);

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(dir);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var subdirectory in subdirectories)
            {
                var subName = Path.GetFileName(subdirectory);
                if (ExcludedDirNames.Contains(subName, StringComparer.OrdinalIgnoreCase))
                    continue;

                Walk(subdirectory);
            }
        }

        Walk(root);
    }

    private static void IndexPrototypesRoot(
        string prototypesRoot,
        Dictionary<string, List<string>> idsByTypeKey,
        List<PrototypeDefinition> definitions,
        HashSet<string> seenFiles,
        ILogger logger)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(prototypesRoot, "*.yml", SearchOption.AllDirectories)
                .Concat(Directory.EnumerateFiles(prototypesRoot, "*.yaml", SearchOption.AllDirectories));
        }
        catch (Exception)
        {
            return;
        }

        foreach (var file in files)
        {
            // A Prototypes folder nested in another one would be walked twice.
            if (!seenFiles.Add(Path.GetFullPath(file)))
                continue;

            string text;
            try
            {
                text = File.ReadAllText(file);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read prototype file {File}", file);
                continue;
            }

            var document = PrototypeYamlParser.Parse(text);
            foreach (var item in document.Items)
            {
                if (item.TypeValue is not { Length: > 0 } typeValue || item.IdValue is not { Length: > 0 } idValue)
                    continue;

                if (!idsByTypeKey.TryGetValue(typeValue, out var ids))
                {
                    ids = [];
                    idsByTypeKey[typeValue] = ids;
                }

                if (!ids.Contains(idValue, StringComparer.OrdinalIgnoreCase))
                    ids.Add(idValue);

                if (item.IdValueRange is { } idRange)
                {
                    definitions.Add(new PrototypeDefinition(
                        typeValue,
                        idValue,
                        Path.GetFullPath(file),
                        idRange,
                        item.Parents.Select(p => p.Id).ToList(),
                        item.SetFieldNames.ToList()));
                }
            }
        }
    }
}
