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
    public static Hover? Handle(string text, LspPosition position, EngineSchema schema, PrototypeIdIndex? prototypeIds = null, string? documentPath = null)
    {
        var document = PrototypeYamlParser.Parse(text);

        if (prototypeIds is not null &&
            PrototypeReferences.ReferenceAt(document, schema, position) is { } reference &&
            new PrototypeLookup(document, prototypeIds, documentPath).Find(reference.TypeKey, reference.Id) is { } entry)
        {
            return new Hover
            {
                Contents = new MarkedStringsOrMarkupContent(new MarkupContent { Kind = MarkupKind.Markdown, Value = RenderPrototype(reference, entry) }),
                Range = reference.Range,
            };
        }

        var context = NodeContextResolver.Resolve(document, position, schema.IsStylePrototype);

        var markdown = context switch
        {
            NodeContext.PrototypeTypeValue ctx when ctx.CurrentValue is not null =>
                RenderType(schema.PrototypesByTypeKey.GetValueOrDefault(ctx.CurrentValue), yamlKey: ctx.CurrentValue),

            NodeContext.ComponentTypeValue ctx when ctx.CurrentValue is not null =>
                RenderType(schema.ResolveComponent(ctx.CurrentValue)?.Component, yamlKey: ctx.CurrentValue),

            NodeContext.TopLevelFieldValue ctx =>
                RenderField(schema.PrototypesByTypeKey.GetValueOrDefault(ctx.PrototypeTypeKey)
                    ?.DataFields.FirstOrDefault(f => string.Equals(f.YamlName, ctx.FieldName, StringComparison.OrdinalIgnoreCase))),

            NodeContext.ComponentFieldValue ctx =>
                RenderField(schema.ResolveComponent(ctx.ComponentName)?.Component
                    .Members.FirstOrDefault(m => string.Equals(m.Name, ctx.FieldName, StringComparison.OrdinalIgnoreCase))),

            NodeContext.StylePropertyValue ctx =>
                RenderStyleProperty(schema.ResolveStyleProperties(ctx.Control).FirstOrDefault(p => p.Name == ctx.PropertyName)),

            NodeContext.StyleSelector { Entry: { Name: "control", ScalarValue: { } control } } =>
                RenderControl(schema, control),

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

        //var badge = $"(`{yamlKey}`)";
        return Compose(proto.Signature, null, proto.ClassDocMarkdown);
    }

    private static string? RenderType(ComponentTypeInfo? component, string yamlKey)
    {
        if (component is null)
            return null;

        //var badge = $"(`{yamlKey}`)";
        return Compose(component.Signature, null, component.ClassDocMarkdown);
    }

    private static string? RenderField(DataFieldInfo? field)
    {
        if (field is null)
            return null;

        var badge = field.Required ? "*(required)*" : null;
        return Compose(field.Signature, badge, field.DocMarkdown);
    }

    private static string? RenderField(MemberInfo? member)
    {
        if (member is null)
            return null;

        return Compose(member.Signature, badge: null, member.DocMarkdown);
    }

    private static string? RenderStyleProperty(StylePropertyInfo? property)
    {
        if (property is null)
            return null;

        return Compose(property.Signature, badge: null, property.DocMarkdown);
    }

    // A class name shows that control; an alias lists every control that adds it.
    private static string? RenderControl(EngineSchema schema, string control)
    {
        var matches = schema.MatchStyleControl(control)
            .Where(c => string.Equals(c.Name, control, StringComparison.OrdinalIgnoreCase) ||
                        c.Aliases.Contains(control, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (matches.Count == 0)
            return null;

        var exact = matches.FirstOrDefault(c => string.Equals(c.Name, control, StringComparison.OrdinalIgnoreCase));
        if (exact is not null)
            return Compose(exact.Signature, badge: null, exact.ClassDocMarkdown);

        var names = string.Join(", ", matches.Select(c => $"`{c.Name}`"));
        return Compose(string.Join("\n", matches.Select(c => c.Signature)), $"*alias of {names} (and their subclasses)*", docMarkdown: null);
    }

    // The referenced prototype's header, what it inherits and sets, and where it's written.
    private static string RenderPrototype(PrototypeReference reference, PrototypeLookup.Entry entry)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("```yaml\n").Append("type: ").Append(reference.TypeKey).Append("\nid: ").Append(reference.Id);
        if (entry.Parents.Count > 0)
            sb.Append("\nparent: [").Append(string.Join(", ", entry.Parents)).Append(']');
        sb.Append("\n```");

        var fields = entry.SetFieldNames.Where(n => !string.Equals(n, "parent", StringComparison.OrdinalIgnoreCase)).ToList();
        if (fields.Count > 0)
            sb.Append("\n\nSets: ").Append(string.Join(", ", fields.Select(f => $"`{f}`")));

        var line = entry.Range.Start.Line + 1;
        if (entry.FilePath is null)
        {
            sb.Append("\n\nDefined in this file, line ").Append(line).Append('.');
        }
        else
        {
            var uri = OmniSharp.Extensions.LanguageServer.Protocol.DocumentUri.FromFileSystemPath(entry.FilePath);
            sb.Append("\n\nDefined in [").Append(Path.GetFileName(entry.FilePath)).Append(':').Append(line)
              .Append("](").Append(uri).Append("#L").Append(line).Append(").");
        }

        return sb.ToString();
    }

    private static string Compose(string signature, string? badge, string? docMarkdown)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append("```csharp\n").Append(signature).Append("\n```");

        if (badge is not null)
            sb.Append('\n').Append(badge);

        if (docMarkdown is not null)
            sb.Append("\n\n---\n\n").Append(docMarkdown);

        return sb.ToString();
    }
}
