using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Features;

public static class HoverHandler
{
    public static Hover? Handle(string text, LspPosition position, EngineSchema schema)
    {
        var document = PrototypeYamlParser.Parse(text);
        var context = NodeContextResolver.Resolve(document, position);

        var markdown = context switch
        {
            NodeContext.PrototypeTypeValue ctx when ctx.CurrentValue is not null =>
                RenderType(schema.PrototypesByTypeKey.GetValueOrDefault(ctx.CurrentValue)),

            NodeContext.ComponentTypeValue ctx when ctx.CurrentValue is not null =>
                RenderType(schema.ComponentsByName.GetValueOrDefault(ctx.CurrentValue)),

            NodeContext.TopLevelFieldValue ctx =>
                RenderField(schema.PrototypesByTypeKey.GetValueOrDefault(ctx.PrototypeTypeKey)
                    ?.DataFields.FirstOrDefault(f => string.Equals(f.YamlName, ctx.FieldName, StringComparison.OrdinalIgnoreCase))),

            NodeContext.ComponentFieldValue ctx =>
                RenderField(schema.ComponentsByName.GetValueOrDefault(ctx.ComponentName)
                    ?.Members.FirstOrDefault(m => string.Equals(m.Name, ctx.FieldName, StringComparison.OrdinalIgnoreCase))),

            _ => null,
        };

        if (markdown is null)
            return null;

        return new Hover
        {
            Contents = new MarkedStringsOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = markdown }),
        };
    }

    private static string? RenderType(PrototypeTypeInfo? proto)
    {
        if (proto is null)
            return null;

        var header = $"**{proto.TypeKey}** — `{proto.ClrTypeName}`";
        return proto.ClassDocMarkdown is null ? header : $"{header}\n\n{proto.ClassDocMarkdown}";
    }

    private static string? RenderType(ComponentTypeInfo? component)
    {
        if (component is null)
            return null;

        var header = $"**{component.Name}** — `{component.ClrTypeName}`";
        return component.ClassDocMarkdown is null ? header : $"{header}\n\n{component.ClassDocMarkdown}";
    }

    private static string? RenderField(DataFieldInfo? field)
    {
        if (field is null)
            return null;

        var badge = field.Required ? " *(required)*" : string.Empty;
        var header = $"**{field.YamlName}**: `{field.ClrTypeDisplay}`{badge}";
        return field.DocMarkdown is null ? header : $"{header}\n\n{field.DocMarkdown}";
    }

    private static string? RenderField(MemberInfo? member)
    {
        if (member is null)
            return null;

        var header = $"**{member.Name}**: `{member.ClrTypeDisplay}`";
        return member.DocMarkdown is null ? header : $"{header}\n\n{member.DocMarkdown}";
    }
}
