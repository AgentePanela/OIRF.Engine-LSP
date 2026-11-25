using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;

namespace OIRF.LanguageServer.Features;

public enum PrototypeReferenceKind
{
    /// <summary>A <c>parent:</c> entry: same prototype type as the item it's on.</summary>
    Parent,

    /// <summary>A field value naming a prototype (ProtoId&lt;T&gt;, shader, sound collection...).</summary>
    Field,
}

/// <summary>A prototype id written somewhere in a document, and the prototype type it names.</summary>
/// <param name="AllowsLooseFont">
/// A fontFamily reference also accepts a bare .ttf file name (FontManager.TryGetSystem falls back
/// to one), so a missing id there isn't necessarily wrong.
/// </param>
public sealed record PrototypeReference(string TypeKey, string Id, LspRange Range, PrototypeReferenceKind Kind, bool AllowsLooseFont = false);

/// <summary>An <c>id:</c> written in a document.</summary>
public sealed record PrototypeIdSite(string TypeKey, string Id, LspRange Range);

/// <summary>
/// Finds every prototype id a document defines or references. Shared by go-to-definition,
/// find references, rename and the validator, so they all agree on what counts as a reference.
/// </summary>
public static class PrototypeReferences
{
    public static IEnumerable<PrototypeIdSite> Definitions(PrototypeDocument document) =>
        document.Items
            .Where(i => i.TypeValue is { Length: > 0 } && i.IdValue is { Length: > 0 } && i.IdValueRange is not null)
            .Select(i => new PrototypeIdSite(i.TypeValue!, i.IdValue!, i.IdValueRange!));

    public static IEnumerable<PrototypeReference> Collect(PrototypeDocument document, EngineSchema schema)
    {
        foreach (var item in document.Items)
        {
            if (item.TypeValue is not { Length: > 0 } typeKey)
                continue;

            foreach (var (id, range) in item.Parents)
                yield return new PrototypeReference(typeKey, id, range, PrototypeReferenceKind.Parent);

            if (schema.PrototypesByTypeKey.GetValueOrDefault(typeKey) is not { } proto)
                continue;

            foreach (var field in item.TopLevelFields)
            {
                if (string.Equals(field.Name, "parent", StringComparison.OrdinalIgnoreCase))
                    continue;

                var dataField = proto.DataFields.FirstOrDefault(f => string.Equals(f.YamlName, field.Name, StringComparison.OrdinalIgnoreCase));
                foreach (var reference in FieldReferences(field, dataField?.Asset, dataField?.ProtoId, schema))
                    yield return reference;
            }

            foreach (var component in item.Components)
            {
                if (component.TypeValue is null || schema.ResolveComponent(component.TypeValue)?.Component is not { } comp)
                    continue;

                foreach (var field in component.Fields)
                {
                    var member = comp.Members.FirstOrDefault(m => string.Equals(m.Name, field.Name, StringComparison.OrdinalIgnoreCase));
                    foreach (var reference in FieldReferences(field, member?.Asset, member?.ProtoId, schema))
                        yield return reference;
                }
            }

            if (!schema.IsStylePrototype(typeKey))
                continue;

            foreach (var rule in item.StyleRules)
            {
                var properties = schema.ResolveStyleProperties(rule.Control);
                foreach (var field in rule.Properties)
                {
                    var property = properties.FirstOrDefault(p => p.Name == field.Name);
                    foreach (var reference in FieldReferences(field, property?.Asset, property?.ProtoId, schema))
                        yield return reference;
                }
            }
        }
    }

    /// <summary>
    /// The id under the cursor, whether it's a reference or an <c>id:</c> itself - what
    /// references/rename start from.
    /// </summary>
    public static PrototypeIdSite? TargetAt(PrototypeDocument document, EngineSchema schema, LspPosition position)
    {
        if (ReferenceAt(document, schema, position) is { } reference)
            return new PrototypeIdSite(reference.TypeKey, reference.Id, reference.Range);

        return Definitions(document).FirstOrDefault(d => Touches(d.Range, position));
    }

    public static PrototypeReference? ReferenceAt(PrototypeDocument document, EngineSchema schema, LspPosition position) =>
        Collect(document, schema).FirstOrDefault(r => Touches(r.Range, position));

    /// <summary>On the range, its end included: a cursor right after the last character still counts.</summary>
    public static bool Touches(LspRange range, LspPosition position) =>
        !IsBefore(position, range.Start) && !IsBefore(range.End, position);

    private static bool IsBefore(LspPosition a, LspPosition b) =>
        a.Line < b.Line || (a.Line == b.Line && a.Character < b.Character);

    private static IEnumerable<PrototypeReference> FieldReferences(FieldSpan field, AssetClassification? asset, ProtoIdClassification? protoId, EngineSchema schema)
    {
        // Map-form SoundSpecifier: only its "collection:" names a prototype.
        if (asset is { Kind: AssetKind.Sound })
        {
            if (field.Entries is null || schema.TypeKeyOf(EngineSchema.SoundCollectionPrototypeFqn) is not { } collectionType)
                yield break;

            foreach (var entry in field.Entries.Where(e => string.Equals(e.Name, "collection", StringComparison.OrdinalIgnoreCase)))
            {
                foreach (var (id, range) in entry.Scalars)
                    yield return new PrototypeReference(collectionType, id, range, PrototypeReferenceKind.Field);
            }

            yield break;
        }

        var typeKey = protoId is not null
            ? schema.TypeKeyOf(protoId.PrototypeClrTypeName)
            // Engine 0.5+: a shader reference is a ShaderPrototype id (older engines: a .fx name).
            : asset is { Kind: AssetKind.Shader } ? schema.TypeKeyOf(EngineSchema.ShaderPrototypeFqn) : null;

        if (typeKey is null)
            yield break;

        var allowsLooseFont = protoId?.PrototypeClrTypeName == EngineSchema.FontFamilyPrototypeFqn;
        foreach (var (id, range) in field.Scalars)
            yield return new PrototypeReference(typeKey, id, range, PrototypeReferenceKind.Field, allowsLooseFont);
    }
}
