using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OIRF.LanguageServer.Yaml;

/// <summary>
/// Parses Prototype YAML text (sequence-root grammar: a list of `type`/`id`/... mappings, each
/// optionally with a `components:` list) into a <see cref="PrototypeDocument"/> with LSP-ready
/// position ranges for every key/value. Uses the same library (YamlDotNet) and node-mark
/// mechanism the engine's own PrototypeLoader uses.
/// </summary>
public static class PrototypeYamlParser
{
    public static PrototypeDocument Parse(string text)
    {
        var stream = new YamlStream();
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            return new PrototypeDocument
            {
                Items = [],
                ParseErrorMessage = ex.Message,
                ParseErrorRange = YamlPositionMapper.ToRange(ex.Start, ex.End),
            };
        }

        if (stream.Documents.Count == 0)
            return PrototypeDocument.Empty;

        var rootNode = stream.Documents[0].RootNode;
        if (rootNode is not YamlSequenceNode sequence)
        {
            return new PrototypeDocument
            {
                Items = [],
                ParseErrorMessage = "Expected a YAML sequence at the document root (a list of prototype entries).",
                ParseErrorRange = YamlPositionMapper.ToRange(rootNode.Start, rootNode.End),
            };
        }

        var items = new List<PrototypeItem>();
        foreach (var node in sequence.Children)
        {
            if (node is YamlMappingNode mapping)
                items.Add(ParseItem(mapping));
        }

        return new PrototypeDocument { Items = items };
    }

    private static PrototypeItem ParseItem(YamlMappingNode mapping)
    {
        string? typeValue = null;
        LspRange? typeKeyRange = null;
        LspRange? typeValueRange = null;
        string? idValue = null;
        LspRange? idValueRange = null;
        LspRange? parentValueRange = null;
        LspRange? componentsListRange = null;
        var topLevelFields = new List<FieldSpan>();
        var components = new List<ComponentItem>();

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key } keyScalar)
                continue;

            var keyRange = YamlPositionMapper.ToRange(keyScalar.Start, keyScalar.End);
            var valueRange = YamlPositionMapper.ToRange(valueNode.Start, valueNode.End);

            switch (key)
            {
                case "type":
                    typeKeyRange = keyRange;
                    typeValueRange = valueRange;
                    typeValue = (valueNode as YamlScalarNode)?.Value;
                    continue;
                case "id":
                    idValueRange = valueRange;
                    idValue = (valueNode as YamlScalarNode)?.Value;
                    continue;
                case "parent":
                    parentValueRange = valueRange;
                    // Also tracked as a regular top-level field (in addition to the dedicated
                    // ParentValueRange used for hover/completion-value context) purely so
                    // "already present" checks - e.g. excluding it from key completion once
                    // written - see it like any other field.
                    topLevelFields.Add(new FieldSpan
                    {
                        Name = key,
                        KeyRange = keyRange,
                        ValueRange = valueRange,
                        ScalarValue = (valueNode as YamlScalarNode)?.Value,
                    });
                    continue;
                case "components" when valueNode is YamlSequenceNode componentsSeq:
                    componentsListRange = YamlPositionMapper.ToRange(componentsSeq.Start, GetTrueEnd(componentsSeq));
                    foreach (var componentNode in componentsSeq.Children)
                    {
                        // Every sequence child gets an entry, even a bare "- " bullet with
                        // nothing on it yet (not a YamlMappingNode) - otherwise that bullet has
                        // no ComponentItem of its own, and position-lookup would fall back to
                        // whichever real component happens to precede it, misresolving a
                        // still-empty bullet in the *middle* of the list as if it were inside
                        // that earlier component.
                        components.Add(componentNode is YamlMappingNode componentMapping
                            ? ParseComponent(componentMapping)
                            : EmptyComponent(componentNode));
                    }
                    continue;
            }

            topLevelFields.Add(new FieldSpan
            {
                Name = key,
                KeyRange = keyRange,
                ValueRange = valueRange,
                ScalarValue = (valueNode as YamlScalarNode)?.Value,
            });
        }

        return new PrototypeItem
        {
            ItemRange = YamlPositionMapper.ToRange(mapping.Start, GetTrueEnd(mapping)),
            TypeValue = typeValue,
            TypeKeyRange = typeKeyRange,
            TypeValueRange = typeValueRange,
            IdValue = idValue,
            IdValueRange = idValueRange,
            ParentValueRange = parentValueRange,
            TopLevelFields = topLevelFields,
            ComponentsListRange = componentsListRange,
            Components = components,
        };
    }

    private static ComponentItem ParseComponent(YamlMappingNode mapping)
    {
        string? typeValue = null;
        LspRange? typeKeyRange = null;
        LspRange? typeValueRange = null;
        var fields = new List<FieldSpan>();

        foreach (var (keyNode, valueNode) in mapping.Children)
        {
            if (keyNode is not YamlScalarNode { Value: { } key } keyScalar)
                continue;

            var keyRange = YamlPositionMapper.ToRange(keyScalar.Start, keyScalar.End);
            var valueRange = YamlPositionMapper.ToRange(valueNode.Start, valueNode.End);

            if (key == "type")
            {
                typeKeyRange = keyRange;
                typeValueRange = valueRange;
                typeValue = (valueNode as YamlScalarNode)?.Value;
                continue;
            }

            fields.Add(new FieldSpan
            {
                Name = key,
                KeyRange = keyRange,
                ValueRange = valueRange,
                ScalarValue = (valueNode as YamlScalarNode)?.Value,
            });
        }

        return new ComponentItem
        {
            ItemRange = YamlPositionMapper.ToRange(mapping.Start, GetTrueEnd(mapping)),
            TypeValue = typeValue,
            TypeKeyRange = typeKeyRange,
            TypeValueRange = typeValueRange,
            Fields = fields,
        };
    }

    private static ComponentItem EmptyComponent(YamlNode node) => new()
    {
        ItemRange = YamlPositionMapper.ToRange(node.Start, GetTrueEnd(node)),
        TypeValue = null,
        TypeKeyRange = null,
        TypeValueRange = null,
        Fields = [],
    };

    /// <summary>
    /// YamlDotNet reports <c>Start</c> correctly for block mappings/sequences, but <c>End</c> is
    /// degenerate (equal to Start) for block (non-flow) collections - there's no explicit closing
    /// token to mark it against. The true end is the end of the last leaf scalar contained
    /// anywhere within the node, found by recursing into the last child.
    /// </summary>
    private static Mark GetTrueEnd(YamlNode node) => node switch
    {
        YamlScalarNode scalar => scalar.End,
        YamlMappingNode { Children.Count: > 0 } mapping => GetTrueEnd(mapping.Children.Values.Last()),
        YamlSequenceNode { Children.Count: > 0 } sequence => GetTrueEnd(sequence.Children[^1]),
        _ => node.End,
    };
}
