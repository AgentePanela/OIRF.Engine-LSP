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

    public static readonly PrototypeIdIndex Empty = new()
    {
        IdsByTypeKey = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
    };

    public IReadOnlyList<string> Get(string typeKey) =>
        IdsByTypeKey.TryGetValue(typeKey, out var ids) ? ids : [];
}

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
    private static readonly string[] ExcludedDirNames = ["bin", "obj", "node_modules", ".git"];

    public static PrototypeIdIndex Build(IEnumerable<string> workspaceRoots, ILogger logger)
    {
        var prototypeRoots = new List<string>();
        foreach (var root in workspaceRoots)
            FindPrototypesFolders(root, prototypeRoots);

        var idsByTypeKey = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);

        foreach (var prototypesRoot in prototypeRoots)
            IndexPrototypesRoot(prototypesRoot, idsByTypeKey, logger);

        return new PrototypeIdIndex
        {
            IdsByTypeKey = idsByTypeKey.ToDictionary(
                kv => kv.Key,
                IReadOnlyList<string> (kv) => kv.Value,
                StringComparer.OrdinalIgnoreCase),
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

    private static void IndexPrototypesRoot(string prototypesRoot, Dictionary<string, List<string>> idsByTypeKey, ILogger logger)
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
            }
        }
    }
}
