using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Features;

/// <summary>
/// Go-to-definition for prototype types, component types, and prototype/component fields -
/// jumps straight to the C# declaration (class or member) the schema was built from, using the
/// <see cref="SymbolLocation"/> captured once at schema-build time (see SchemaBuilder).
/// </summary>
public static class DefinitionHandler
{
    public static LocationOrLocationLinks? Handle(string text, LspPosition position, EngineSchema schema)
    {
        var document = PrototypeYamlParser.Parse(text);
        var context = NodeContextResolver.Resolve(document, position);

        var location = context switch
        {
            NodeContext.PrototypeTypeValue ctx when ctx.CurrentValue is not null =>
                schema.PrototypesByTypeKey.GetValueOrDefault(ctx.CurrentValue)?.Location,

            NodeContext.ComponentTypeValue ctx when ctx.CurrentValue is not null =>
                schema.ComponentsByName.GetValueOrDefault(ctx.CurrentValue)?.Location,

            NodeContext.TopLevelFieldValue ctx =>
                schema.PrototypesByTypeKey.GetValueOrDefault(ctx.PrototypeTypeKey)
                    ?.DataFields.FirstOrDefault(f => string.Equals(f.YamlName, ctx.FieldName, StringComparison.OrdinalIgnoreCase))
                    ?.Location,

            NodeContext.ComponentFieldValue ctx =>
                schema.ComponentsByName.GetValueOrDefault(ctx.ComponentName)
                    ?.Members.FirstOrDefault(m => string.Equals(m.Name, ctx.FieldName, StringComparison.OrdinalIgnoreCase))
                    ?.Location,

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
