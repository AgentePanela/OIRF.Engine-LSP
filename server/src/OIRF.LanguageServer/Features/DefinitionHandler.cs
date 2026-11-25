using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Features;

/// <summary>
/// Go-to-definition. A prototype id (a <c>parent:</c> entry or a field naming one) jumps to the
/// <c>id:</c> that defines it, in this file or any other. Prototype types, component types, and
/// prototype/component fields jump straight to the C# declaration the schema was built from,
/// using the <see cref="SymbolLocation"/> captured once at schema-build time (see SchemaBuilder).
/// </summary>
public static class DefinitionHandler
{
    public static LocationOrLocationLinks? Handle(
        string text,
        LspPosition position,
        EngineSchema schema,
        PrototypeIdIndex? prototypeIds = null,
        DocumentUri? documentUri = null)
    {
        var document = PrototypeYamlParser.Parse(text);

        if (prototypeIds is not null && documentUri is not null &&
            PrototypeReferences.ReferenceAt(document, schema, position) is { } reference)
        {
            var lookup = new PrototypeLookup(document, prototypeIds, documentUri.GetFileSystemPath());
            if (lookup.Find(reference.TypeKey, reference.Id) is not { } entry)
                return null;

            return new LocationOrLocationLinks(new Location
            {
                Uri = entry.FilePath is null ? documentUri : DocumentUri.FromFileSystemPath(entry.FilePath),
                Range = entry.Range,
            });
        }

        var context = NodeContextResolver.Resolve(document, position, schema.IsStylePrototype);

        var location = context switch
        {
            NodeContext.PrototypeTypeValue ctx when ctx.CurrentValue is not null =>
                schema.PrototypesByTypeKey.GetValueOrDefault(ctx.CurrentValue)?.Location,

            NodeContext.ComponentTypeValue ctx when ctx.CurrentValue is not null =>
                schema.ResolveComponent(ctx.CurrentValue)?.Component.Location,

            NodeContext.TopLevelFieldValue ctx =>
                schema.PrototypesByTypeKey.GetValueOrDefault(ctx.PrototypeTypeKey)
                    ?.DataFields.FirstOrDefault(f => string.Equals(f.YamlName, ctx.FieldName, StringComparison.OrdinalIgnoreCase))
                    ?.Location,

            NodeContext.ComponentFieldValue ctx =>
                schema.ResolveComponent(ctx.ComponentName)?.Component
                    .Members.FirstOrDefault(m => string.Equals(m.Name, ctx.FieldName, StringComparison.OrdinalIgnoreCase))
                    ?.Location,

            NodeContext.StylePropertyValue ctx =>
                schema.ResolveStyleProperties(ctx.Control).FirstOrDefault(p => p.Name == ctx.PropertyName)?.Location,

            NodeContext.StyleSelector { Entry: { Name: "control", ScalarValue: { } control } } =>
                schema.UiControls.Values.FirstOrDefault(c => string.Equals(c.Name, control, StringComparison.OrdinalIgnoreCase))?.Location
                ?? schema.UiControls.Values.FirstOrDefault(c => c.Aliases.Contains(control, StringComparer.OrdinalIgnoreCase))?.Location,

            _ => null,
        };

        if (location is null)
            return null;

        return new LocationOrLocationLinks(new Location
        {
            Uri = DocumentUri.FromFileSystemPath(location.FilePath),
            Range = location.Range,
        });
    }
}
