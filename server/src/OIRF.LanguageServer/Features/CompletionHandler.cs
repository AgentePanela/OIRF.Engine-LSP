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

            NodeContext.TopLevelFieldKey ctx => ResolvePrototype(schema, ctx.PrototypeTypeKey) is { } proto
                ? proto.DataFields
                    .Where(f => !f.IsMeta && !ctx.ExistingFieldNames.Contains(f.YamlName))
                    .Select(DataFieldItem)
                : [],

            NodeContext.ComponentTypeValue => schema.ComponentsByName.Values.Select(ComponentTypeItem),

            NodeContext.ComponentFieldKey ctx => ResolveComponent(schema, ctx.ComponentName) is { } comp
                ? comp.Members
                    .Where(m => !ctx.ExistingFieldNames.Contains(m.Name))
                    .Select(MemberItem)
                : [],

            NodeContext.TopLevelFieldValue ctx => AssetCompletion(
                ResolvePrototype(schema, ctx.PrototypeTypeKey)?.DataFields.FirstOrDefault(f => string.Equals(f.YamlName, ctx.FieldName, StringComparison.OrdinalIgnoreCase))?.Asset,
                resources),

            NodeContext.ComponentFieldValue ctx => AssetCompletion(
                ResolveComponent(schema, ctx.ComponentName)?.Members.FirstOrDefault(m => string.Equals(m.Name, ctx.FieldName, StringComparison.OrdinalIgnoreCase))?.Asset,
                resources),

            _ => [],
        };
    }

    private static PrototypeTypeInfo? ResolvePrototype(EngineSchema schema, string typeKey) =>
        schema.PrototypesByTypeKey.GetValueOrDefault(typeKey);

    private static ComponentTypeInfo? ResolveComponent(EngineSchema schema, string name) =>
        schema.ComponentsByName.GetValueOrDefault(name);

    private static CompletionItem PrototypeTypeItem(PrototypeTypeInfo p) => new()
    {
        Label = p.TypeKey,
        Kind = CompletionItemKind.EnumMember,
        Detail = p.ClrTypeName,
        Documentation = ToMarkup(p.ClassDocMarkdown),
    };

    private static CompletionItem ComponentTypeItem(ComponentTypeInfo c) => new()
    {
        Label = c.Name,
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
