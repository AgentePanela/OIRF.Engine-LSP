using System.Text.RegularExpressions;
using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Features;

/// <summary>
/// Workspace-wide prototype id features: find references, rename, and symbol search (Ctrl+T).
/// They work on <c>documents</c>: every prototype file, with the editor's text for the open ones.
/// References and rename reach only YAML - an id written as a string in C# isn't touched.
/// </summary>
public static class PrototypeIdHandler
{
    public static IEnumerable<Location> References(
        string text, LspPosition position, EngineSchema schema,
        IReadOnlyList<(DocumentUri Uri, string Text)> documents, bool includeDeclaration)
    {
        var target = PrototypeReferences.TargetAt(PrototypeYamlParser.Parse(text), schema, position);
        if (target is null)
            return [];

        return Occurrences(target, schema, documents, includeDeclaration)
            .Select(o => new Location { Uri = o.Uri, Range = o.Range });
    }

    /// <summary>The id under the cursor, for the editor to select before asking for a new name.</summary>
    public static LspRange? PrepareRename(string text, LspPosition position, EngineSchema schema) =>
        PrototypeReferences.TargetAt(PrototypeYamlParser.Parse(text), schema, position)?.Range;

    // Ids are written as plain YAML scalars; anything that would need quoting is refused.
    private static readonly Regex PlainId = new(@"^[A-Za-z0-9_][A-Za-z0-9_.\-/]*$", RegexOptions.Compiled);

    public static bool IsValidId(string id) => PlainId.IsMatch(id);

    /// <summary>The <c>id:</c> and every reference to it, across all documents. Null when nothing applies.</summary>
    public static WorkspaceEdit? Rename(
        string text, LspPosition position, string newName, EngineSchema schema,
        IReadOnlyList<(DocumentUri Uri, string Text)> documents)
    {
        if (!IsValidId(newName))
            return null;

        var target = PrototypeReferences.TargetAt(PrototypeYamlParser.Parse(text), schema, position);
        if (target is null)
            return null;

        var changes = Occurrences(target, schema, documents, includeDeclaration: true)
            .GroupBy(o => o.Uri)
            .ToDictionary(
                g => g.Key,
                g => (IEnumerable<TextEdit>)g.Select(o => new TextEdit { Range = o.Range, NewText = newName }).ToList());

        return changes.Count == 0 ? null : new WorkspaceEdit { Changes = changes };
    }

    /// <summary>
    /// Ctrl+T: every prototype id matching <paramref name="query"/> (its characters in order,
    /// ignoring case), labelled with its type.
    /// </summary>
    public static IEnumerable<WorkspaceSymbol> Symbols(string query, IReadOnlyList<(DocumentUri Uri, string Text)> documents, int limit = 1000)
    {
        return documents
            .SelectMany(d => PrototypeReferences.Definitions(PrototypeYamlParser.Parse(d.Text)).Select(site => (d.Uri, site)))
            .Where(x => IsSubsequence(query, x.site.Id))
            .Take(limit)
            .Select(x => new WorkspaceSymbol
            {
                Name = x.site.Id,
                ContainerName = x.site.TypeKey,
                Kind = SymbolKind.Object,
                Location = new Location { Uri = x.Uri, Range = x.site.Range },
            });
    }

    private static bool IsSubsequence(string query, string candidate)
    {
        var i = 0;
        foreach (var c in candidate)
        {
            if (i < query.Length && char.ToLowerInvariant(query[i]) == char.ToLowerInvariant(c))
                i++;
        }

        return i == query.Length;
    }

    private static IEnumerable<(DocumentUri Uri, LspRange Range)> Occurrences(
        PrototypeIdSite target, EngineSchema schema,
        IReadOnlyList<(DocumentUri Uri, string Text)> documents, bool includeDeclaration)
    {
        bool Matches(string typeKey, string id) =>
            string.Equals(typeKey, target.TypeKey, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(id, target.Id, StringComparison.OrdinalIgnoreCase);

        foreach (var (uri, text) in documents)
        {
            var document = PrototypeYamlParser.Parse(text);

            if (includeDeclaration)
            {
                foreach (var site in PrototypeReferences.Definitions(document).Where(s => Matches(s.TypeKey, s.Id)))
                    yield return (uri, site.Range);
            }

            foreach (var reference in PrototypeReferences.Collect(document, schema).Where(r => Matches(r.TypeKey, r.Id)))
                yield return (uri, reference.Range);
        }
    }
}
