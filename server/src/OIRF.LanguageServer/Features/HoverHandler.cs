using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Features;

/// <summary>
/// Renders hovers as a ```csharp fenced signature block followed by the doc body - matching how
/// the C# extension's own hover looks (VSCode colorizes fenced code blocks using the target
/// language's grammar, so this gets semantic-looking coloring for free without a semantic tokens
/// provider for the YAML side).
/// </summary>
public static class HoverHandler
{
    public static Hover? Handle(string text, LspPosition position, EngineSchema schema)
    {
        var document = PrototypeYamlParser.Parse(text);
        var context = NodeContextResolver.Resolve(document, position);

        var markdown = context switch
        {
            NodeContext.PrototypeTypeValue ctx when ctx.CurrentValue is not null =>
                RenderType(schema.PrototypesByTypeKey.GetValueOrDefault(ctx.CurrentValue), yamlKey: ctx.CurrentValue),

            NodeContext.ComponentTypeValue ctx when ctx.CurrentValue is not null =>
                RenderType(schema.ComponentsByName.GetValueOrDefault(ctx.CurrentValue), yamlKey: ctx.CurrentValue),

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

    private static string? RenderType(PrototypeTypeInfo? proto, string yamlKey)
    {
        if (proto is null)
            return null;

        var badge = $"(`{yamlKey}`)";
        return Compose(proto.Signature, badge, proto.ClassDocMarkdown);
    }

    private static string? RenderType(ComponentTypeInfo? component, string yamlKey)
    {
        if (component is null)
            return null;

        var badge = $"(`{yamlKey}`)";
        return Compose(component.Signature, badge, component.ClassDocMarkdown);
    }

    private static string? RenderField(DataFieldInfo? field)
    {
        if (field is null)
            return null;

        var badge = field.Required ? "\n*(required)*" : null;
        return Compose(field.Signature, badge, field.DocMarkdown);
    }

    private static string? RenderField(MemberInfo? member)
    {
        if (member is null)
            return null;

        return Compose(member.Signature, badge: null, member.DocMarkdown);
    }

    private static string Compose(string signature, string? badge, string? docMarkdown)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("```csharp\n").Append(signature).Append("\n```");

        if (badge is not null)
            sb.Append(' ').Append(badge);

        if (docMarkdown is not null)
            sb.Append("\n\n---\n\n").Append(docMarkdown);

        return sb.ToString();
    }
}
