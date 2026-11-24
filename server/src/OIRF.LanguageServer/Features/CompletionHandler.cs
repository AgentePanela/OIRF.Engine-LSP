using System.Text.RegularExpressions;
using OIRF.LanguageServer.Assets;
using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Features;

public static class CompletionHandler
{
    public static IEnumerable<CompletionItem> Handle(string text, LspPosition position, EngineSchema schema, ResourceIndex resources, PrototypeIdIndex prototypeIds)
    {
        var document = PrototypeYamlParser.Parse(text);
        var context = NodeContextResolver.Resolve(document, position, schema.IsStylePrototype);

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
                schema, resources, prototypeIds, LinePrefix(text, position)),

            NodeContext.ComponentFieldValue ctx => ValueCompletion(
                ResolveComponent(schema, ctx.ComponentName)?.Members.FirstOrDefault(m => string.Equals(m.Name, ctx.FieldName, StringComparison.OrdinalIgnoreCase)),
                schema, resources, prototypeIds, LinePrefix(text, position)),

            // Every id of the same prototype type: the workspace-wide index (other files, as last
            // saved) plus this document's own items (possibly unsaved), minus the item itself and
            // parents already listed.
            NodeContext.ParentValue ctx => KnownIds(ctx.PrototypeTypeKey, document, prototypeIds)
                .Where(id => !string.Equals(id, ctx.CurrentItemId, StringComparison.Ordinal) && !ctx.ExistingParents.Contains(id))
                .Select(id => new CompletionItem { Label = id, Kind = CompletionItemKind.Reference }),

            NodeContext.StyleRuleKey ctx => EngineSchema.StyleRuleKeys
                .Where(k => !ctx.ExistingKeys.Contains(k))
                .Select(k => new CompletionItem { Label = k, InsertText = $"{k}: ", Kind = CompletionItemKind.Property }),

            NodeContext.StyleSelector ctx => StyleSelectorCompletion(ctx, schema, LinePrefix(text, position)),

            NodeContext.StylePropertyKey ctx => schema.ResolveStyleProperties(ctx.Control)
                .Where(p => !ctx.ExistingNames.Contains(p.Name))
                .Select(StylePropertyItem),

            NodeContext.StylePropertyValue ctx => schema.ResolveStyleProperties(ctx.Control)
                .FirstOrDefault(p => p.Name == ctx.PropertyName) is { } property
                ? ValueCompletion(property.Asset, property.ProtoId, property.ValueChoices, schema, resources, prototypeIds, LinePrefix(text, position))
                : [],

            _ => [],
        };
    }

    /// <summary>Every id of <paramref name="typeKey"/>: workspace index plus this document's own items.</summary>
    public static IEnumerable<string> KnownIds(string typeKey, PrototypeDocument document, PrototypeIdIndex prototypeIds) =>
        prototypeIds.Get(typeKey)
            .Concat(document.Items
                .Where(i => string.Equals(i.TypeValue, typeKey, StringComparison.OrdinalIgnoreCase))
                .Select(i => i.IdValue)
                .OfType<string>()
                .Where(id => id.Length > 0))
            .Distinct(StringComparer.Ordinal);

    private static IEnumerable<CompletionItem> StyleSelectorCompletion(NodeContext.StyleSelector ctx, EngineSchema schema, string linePrefix)
    {
        var match = LastKeyOnLine.Match(linePrefix);
        var key = match.Success ? match.Groups[1].Value : null;

        switch (key)
        {
            case "control":
                return schema.StyleControlNames.Select(n => new CompletionItem { Label = n, Kind = CompletionItemKind.Class });
            case "pseudoClass":
                return schema.PseudoClasses.Select(p => new CompletionItem { Label = p, Kind = CompletionItemKind.EnumMember });
            case "styleClass" or "id":
                return [];
        }

        var keys = EngineSchema.StyleSelectorKeys.Where(k => !ctx.ExistingKeys.Contains(k));

        // "- class: |" with nothing written yet: open a flow mapping, the form every rule uses.
        if (key == "class")
        {
            return keys.Select(k => new CompletionItem
            {
                Label = k,
                InsertText = $"{{ {k}: $1 }}",
                InsertTextFormat = InsertTextFormat.Snippet,
                Kind = CompletionItemKind.Property,
            });
        }

        return keys.Select(k => new CompletionItem { Label = k, InsertText = $"{k}: ", Kind = CompletionItemKind.Property });
    }

    private static CompletionItem StylePropertyItem(StylePropertyInfo p) => new()
    {
        Label = p.Name,
        InsertText = $"{p.Name}: ",
        Kind = CompletionItemKind.Field,
        Detail = p.DefaultDisplay is null ? p.ClrTypeDisplay : $"{p.ClrTypeDisplay} = {p.DefaultDisplay}",
        Documentation = ToMarkup(p.DocMarkdown),
    };

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

    private static IEnumerable<CompletionItem> ValueCompletion(DataFieldInfo? field, EngineSchema schema, ResourceIndex resources, PrototypeIdIndex prototypeIds, string linePrefix) =>
        field is null ? [] : ValueCompletion(field.Asset, field.ProtoId, field.ValueChoices, schema, resources, prototypeIds, linePrefix);

    private static IEnumerable<CompletionItem> ValueCompletion(MemberInfo? member, EngineSchema schema, ResourceIndex resources, PrototypeIdIndex prototypeIds, string linePrefix) =>
        member is null ? [] : ValueCompletion(member.Asset, member.ProtoId, member.ValueChoices, schema, resources, prototypeIds, linePrefix);

    // A field's value is exactly one of: an asset-path reference, a ProtoId<T> prototype-id
    // reference, or a fixed set of literal choices (enum member names, "true"/"false" for bool) -
    // never more than one, so no need to merge/dedupe.
    private static IEnumerable<CompletionItem> ValueCompletion(
        AssetClassification? asset,
        ProtoIdClassification? protoId,
        IReadOnlyList<string>? choices,
        EngineSchema schema,
        ResourceIndex resources,
        PrototypeIdIndex prototypeIds,
        string linePrefix)
    {
        if (asset is { Kind: AssetKind.Sound })
            return SoundCompletion(linePrefix, schema, resources, prototypeIds);

        // Engine 0.5+: a shader reference is a ShaderPrototype id. Older engines (no
        // ShaderPrototype in the schema) still reference the .fx file name directly.
        if (asset is { Kind: AssetKind.Shader } && FindPrototypeTypeKey(schema, ShaderPrototypeFqn) is { } shaderTypeKey)
            return PrototypeIdItems(shaderTypeKey, prototypeIds);

        if (asset is not null)
            return AssetCompletion(asset, resources);

        // FontManager.TryGetSystem: a fontFamily id, or else a loose .ttf by bare file name.
        if (protoId is { PrototypeClrTypeName: FontFamilyPrototypeFqn })
        {
            return ProtoIdCompletion(protoId, schema, prototypeIds)
                .Concat(resources.FontKeys
                    .Select(k => k[(k.LastIndexOf('/') + 1)..])
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Select(name => new CompletionItem { Label = name, Kind = CompletionItemKind.File }))
                .DistinctBy(i => i.Label);
        }

        if (protoId is not null)
            return ProtoIdCompletion(protoId, schema, prototypeIds);

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

    private const string ShaderPrototypeFqn = "Engine.Shared.Graphics.ShaderPrototype";
    private const string SoundCollectionPrototypeFqn = "Engine.Shared.Audio.SoundCollectionPrototype";
    private const string FontFamilyPrototypeFqn = "Engine.Client.Graphics.Fonts.FontFamilyPrototype";

    /// <summary>The keys SoundSpecifier.FromYaml reads from a map-form sound.</summary>
    public static readonly string[] SoundMapKeys = ["path", "collection", "volume", "pitch"];

    // The innermost "key: partial-value" before the cursor on this line, e.g. "collection" in
    // "saySound: { collection: Mob". A '{' or ',' after the colon means a new map key is being
    // typed instead, so the value part can't contain those.
    private static readonly Regex LastKeyOnLine = new(@"([A-Za-z_]\w*)\s*:\s*[^:,{}\[\]]*$", RegexOptions.Compiled);

    /// <summary>
    /// A SoundSpecifier value is either a bare audio key ("saySound: Mobs/Voice/speak_1") or a
    /// map ("saySound: { collection: MobHumanSpeak }", or the same in block style). Which part
    /// the cursor is in is read off the current line, since the parser only knows the field.
    /// </summary>
    private static IEnumerable<CompletionItem> SoundCompletion(string linePrefix, EngineSchema schema, ResourceIndex resources, PrototypeIdIndex prototypeIds)
    {
        var match = LastKeyOnLine.Match(linePrefix);
        var key = match.Success ? match.Groups[1].Value : null;

        if (key is null)
        {
            // Right after "{"/"," or on the first line of a block map: a map key goes here.
            return SoundMapKeys.Select(k => new CompletionItem
            {
                Label = k,
                InsertText = $"{k}: ",
                Kind = CompletionItemKind.Property,
            });
        }

        if (string.Equals(key, "collection", StringComparison.OrdinalIgnoreCase))
        {
            return FindPrototypeTypeKey(schema, SoundCollectionPrototypeFqn) is { } collectionTypeKey
                ? PrototypeIdItems(collectionTypeKey, prototypeIds)
                : [];
        }

        if (string.Equals(key, "volume", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(key, "pitch", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        // "path:" inside the map, or the field's own key (bare scalar form).
        return AssetCompletion(new AssetClassification(AssetKind.Audio, ["Audios"], HighConfidence: true), resources);
    }

    private static string LinePrefix(string text, LspPosition position)
    {
        var lines = text.Split('\n');
        if (position.Line < 0 || position.Line >= lines.Length)
            return string.Empty;

        var line = lines[position.Line].TrimEnd('\r');
        return line[..Math.Clamp(position.Character, 0, line.Length)];
    }

    private static string? FindPrototypeTypeKey(EngineSchema schema, string clrTypeName) =>
        schema.PrototypesByTypeKey.Values.FirstOrDefault(p => p.ClrTypeName == clrTypeName)?.TypeKey;

    private static IEnumerable<CompletionItem> PrototypeIdItems(string typeKey, PrototypeIdIndex prototypeIds) =>
        prototypeIds.Get(typeKey).Select(id => new CompletionItem { Label = id, Kind = CompletionItemKind.Reference });

    // T's registered TypeKey isn't known at schema-build time (see ProtoIdClassification's doc
    // comment), so it's resolved here instead, against the now-complete schema.
    private static IEnumerable<CompletionItem> ProtoIdCompletion(ProtoIdClassification protoId, EngineSchema schema, PrototypeIdIndex prototypeIds)
    {
        return FindPrototypeTypeKey(schema, protoId.PrototypeClrTypeName) is { } typeKey
            ? PrototypeIdItems(typeKey, prototypeIds)
            : [];
    }

    private static MarkupContent? ToMarkup(string? markdown) =>
        markdown is null ? null : new MarkupContent { Kind = MarkupKind.Markdown, Value = markdown };
}
