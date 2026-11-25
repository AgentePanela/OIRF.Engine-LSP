using OIRF.LanguageServer.Yaml;

namespace OIRF.LanguageServer.Features;

/// <summary>
/// Prototype ids as the user currently sees them: the open document's items (possibly unsaved)
/// plus every other file's, as last indexed from disk. The open file's own on-disk entries are
/// left out when its path is known, so an id just renamed or deleted in the editor is gone here
/// too. Ids match case-insensitively, like the engine's PrototypeManager.
/// </summary>
public sealed class PrototypeLookup(PrototypeDocument document, PrototypeIdIndex index, string? documentPath = null)
{
    /// <summary>A prototype as far as inheritance cares: what it inherits from, and what it sets.</summary>
    public sealed record Entry(IReadOnlyList<string> Parents, IEnumerable<string> SetFieldNames, string? FilePath, LspRange Range);

    /// <summary>Nothing indexed yet (startup, or no Prototypes folder): cross-file checks can't tell missing from unknown.</summary>
    public bool IsEmpty => index.IdsByTypeKey.Count == 0;

    private string? FullDocumentPath { get; } = documentPath is null ? null : Path.GetFullPath(documentPath);

    private bool IsOtherFile(PrototypeDefinition definition) =>
        FullDocumentPath is null || !string.Equals(definition.FilePath, FullDocumentPath, StringComparison.OrdinalIgnoreCase);

    /// <summary>Every id of <paramref name="typeKey"/>: other files first, then this document's.</summary>
    public IEnumerable<string> Ids(string typeKey)
    {
        var indexed = index.Definitions.Count > 0
            ? index.Definitions.Where(d => IsOtherFile(d) && SameType(d.TypeKey, typeKey)).Select(d => d.Id)
            : index.Get(typeKey);

        return indexed
            .Concat(DocumentItems(typeKey).Select(i => i.IdValue!))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    public bool Exists(string typeKey, string id) => Find(typeKey, id) is not null || Ids(typeKey).Contains(id, StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether any id of this type is known - if none is, its prototypes probably just aren't indexed.</summary>
    public bool HasAny(string typeKey) => Ids(typeKey).Any();

    /// <summary>The definition of an id; a null <see cref="Entry.FilePath"/> means this document.</summary>
    public Entry? Find(string typeKey, string id)
    {
        var item = DocumentItems(typeKey).FirstOrDefault(i => string.Equals(i.IdValue, id, StringComparison.OrdinalIgnoreCase));
        if (item is not null)
            return new Entry(item.Parents.Select(p => p.Id).ToList(), item.SetFieldNames, null, item.IdValueRange!);

        var definition = index.Definitions.FirstOrDefault(d =>
            IsOtherFile(d) && SameType(d.TypeKey, typeKey) && string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));

        return definition is null
            ? null
            : new Entry(definition.Parents, definition.SetFieldNames, definition.FilePath, definition.IdRange);
    }

    private IEnumerable<PrototypeItem> DocumentItems(string typeKey) =>
        document.Items.Where(i => SameType(i.TypeValue, typeKey) && i.IdValue is { Length: > 0 } && i.IdValueRange is not null);

    private static bool SameType(string? a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
