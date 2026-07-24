using OIRF.LanguageServer.Assets;
using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Features;

public static class CompletionHandler
{
    public static IEnumerable<CompletionItem> Handle(string text, LspPosition position, EngineSchema schema, ResourceIndex resources)
    {
        var document = PrototypeYamlParser.Parse(text);
        var context = NodeContextResolver.Resolve(document, position);

        return context switch
        {
            NodeContext.PrototypeTypeValue => schema.PrototypesByTypeKey.Values.Select(PrototypeTypeItem),

            // "type"/"id" are excluded: they're structural (always the first two lines, and
            // completed via their own dedicated value-completion path), not generic authorable
            // fields. "parent"/"abstract" ARE meta (skipped by the required-field check, see
            // SchemaBuilder.MetaFieldNames) but are still fields the user types like any other,
            // so they must stay in this list.
            NodeContext.TopLevelFieldKey ctx => ResolvePrototype(schema, ctx.PrototypeTypeKey) is { } proto
                ? proto.DataFields
                    .Where(f => !IsStructuralKey(f.YamlName) && !ctx.ExistingFieldNames.Contains(f.YamlName))
                    .Select(DataFieldItem)
                : [],

            NodeContext.ComponentTypeValue ctx => schema.ComponentsByName.Values.Select(c => ComponentTypeItem(c, needsTypeKeyPrefix: ctx.CurrentValue is null)),

            NodeContext.ComponentFieldKey ctx => ResolveComponent(schema, ctx.ComponentName) is { } comp
                ? comp.Members
                    .Where(m => !ctx.ExistingFieldNames.Contains(m.Name))
                    .Select(MemberItem)
                : [],

            NodeContext.TopLevelFieldValue ctx => ValueCompletion(
                ResolvePrototype(schema, ctx.PrototypeTypeKey)?.DataFields.FirstOrDefault(f => string.Equals(f.YamlName, ctx.FieldName, StringComparison.OrdinalIgnoreCase)),
                resources),

            NodeContext.ComponentFieldValue ctx => ValueCompletion(
                ResolveComponent(schema, ctx.ComponentName)?.Members.FirstOrDefault(m => string.Equals(m.Name, ctx.FieldName, StringComparison.OrdinalIgnoreCase)),
                resources),

            // Local-document scope only: siblings in the same file with the same prototype type,
            // excluding the item being edited. Cross-file parent lookup would need a workspace-wide
            // id index, which doesn't exist yet - a fast-follow, not needed for this to be useful.
            NodeContext.ParentValue ctx => document.Items
                .Where(i => string.Equals(i.TypeValue, ctx.PrototypeTypeKey, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.IdValue)
                .Where(id => id is { Length: > 0 } && !string.Equals(id, ctx.CurrentItemId, StringComparison.Ordinal))
                .Distinct()
                .Select(id => new CompletionItem { Label = id!, Kind = CompletionItemKind.Reference }),

            _ => [],
        };
    }

    private static bool IsStructuralKey(string yamlName) =>
        string.Equals(yamlName, "type", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(yamlName, "id", StringComparison.OrdinalIgnoreCase);

    private static PrototypeTypeInfo? ResolvePrototype(EngineSchema schema, string typeKey) =>
        schema.PrototypesByTypeKey.GetValueOrDefault(typeKey);

    private static ComponentTypeInfo? ResolveComponent(EngineSchema schema, string name) =>
        schema.ResolveComponent(name)?.Component;

    private static CompletionItem PrototypeTypeItem(PrototypeTypeInfo p) => new()
    {
        Label = p.TypeKey,
        Kind = CompletionItemKind.EnumMember,
        Detail = p.ClrTypeName,
        Documentation = ToMarkup(p.ClassDocMarkdown),
    };

    private static CompletionItem ComponentTypeItem(ComponentTypeInfo c, bool needsTypeKeyPrefix) => new()
    {
        Label = c.Name,
        // A fresh "- " bullet has no "type:" key on it yet, so accepting a bare component name
        // would insert invalid YAML ("- Tag" instead of "- type: Tag"). Once a "type:" key
        // already exists and the cursor is in its value, only the bare name is needed.
        InsertText = needsTypeKeyPrefix ? $"type: {c.Name}" : c.Name,
        Kind = CompletionItemKind.Class,
        Detail = c.ClrTypeName,
        Documentation = ToMarkup(c.ClassDocMarkdown),
    };

    private static CompletionItem DataFieldItem(DataFieldInfo f) => new()
    {
        Label = f.YamlName,
        Kind = CompletionItemKind.Field,
        Detail = (f.Required ? "required " : string.Empty) + f.ClrTypeDisplay,
        Documentation = ToMarkup(f.DocMarkdown),
    };

    private static CompletionItem MemberItem(MemberInfo m) => new()
    {
        Label = m.Name,
        Kind = CompletionItemKind.Field,
        Detail = m.ClrTypeDisplay,
        Documentation = ToMarkup(m.DocMarkdown),
    };

    private static IEnumerable<CompletionItem> ValueCompletion(DataFieldInfo? field, ResourceIndex resources) =>
        field is null ? [] : ValueCompletion(field.Asset, field.ValueChoices, resources);

    private static IEnumerable<CompletionItem> ValueCompletion(MemberInfo? member, ResourceIndex resources) =>
        member is null ? [] : ValueCompletion(member.Asset, member.ValueChoices, resources);

    // A field's value is either an asset-path reference or a fixed set of literal choices
    // (enum member names, "true"/"false" for bool) - never both, so no need to merge/dedupe.
    private static IEnumerable<CompletionItem> ValueCompletion(AssetClassification? asset, IReadOnlyList<string>? choices, ResourceIndex resources)
    {
        if (asset is not null)
            return AssetCompletion(asset, resources);

        if (choices is not null)
            return choices.Select(c => new CompletionItem { Label = c, Kind = CompletionItemKind.EnumMember });

        return [];
    }

    private static IEnumerable<CompletionItem> AssetCompletion(AssetClassification? asset, ResourceIndex resources)
    {
        if (asset is null)
            yield break;

        foreach (var key in resources.Get(asset.Kind))
        {
            yield return new CompletionItem
            {
                Label = key,
                Kind = CompletionItemKind.File,
            };
        }
    }

    private static MarkupContent? ToMarkup(string? markdown) =>
        markdown is null ? null : new MarkupContent { Kind = MarkupKind.Markdown, Value = markdown };
}
