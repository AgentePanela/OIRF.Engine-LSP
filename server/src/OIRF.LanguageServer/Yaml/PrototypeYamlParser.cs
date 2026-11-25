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
                items.Add(ParseItem(mapping, text));
        }

        return new PrototypeDocument { Items = items };
    }

    private static PrototypeItem ParseItem(YamlMappingNode mapping, string text)
    {
        string? typeValue = null;
        LspRange? typeKeyRange = null;
        LspRange? typeValueRange = null;
        string? idValue = null;
        LspRange? idValueRange = null;
        LspRange? parentValueRange = null;
        LspRange? parentFullRange = null;
        var parents = new List<(string Id, LspRange Range)>();
        LspRange? styleRulesRange = null;
        var styleRules = new List<StyleRuleItem>();
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
                    idValueRange = valueNode is YamlScalarNode idScalar ? ScalarRange(idScalar) : valueRange;
                    idValue = (valueNode as YamlScalarNode)?.Value;
                    continue;
                case "parent":
                    parentValueRange = valueRange;
                    parentFullRange = ParentRange(valueNode, text);
                    parents.AddRange(ScalarsOf(valueNode));
                    // Also tracked as a regular top-level field (in addition to the dedicated
                    // ParentValueRange used for hover/completion-value context) purely so
                    // "already present" checks - e.g. excluding it from key completion once
                    // written - see it like any other field.
                    topLevelFields.Add(Field(key, keyRange, valueRange, valueNode));
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

            if (key == "rules" && valueNode is YamlSequenceNode rulesSeq)
            {
                styleRulesRange = YamlPositionMapper.ToRange(rulesSeq.Start, GetTrueEnd(rulesSeq));
                styleRules.AddRange(rulesSeq.Children.Select(ParseStyleRule));
            }

            topLevelFields.Add(Field(key, keyRange, valueRange, valueNode));
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
            ParentFullRange = parentFullRange,
            Parents = parents,
            StyleRulesRange = styleRulesRange,
            StyleRules = styleRules,
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

            fields.Add(Field(key, keyRange, valueRange, valueNode));
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

    private static StyleRuleItem ParseStyleRule(YamlNode node)
    {
        var range = YamlPositionMapper.ToRange(node.Start, GetTrueEnd(node));
        if (node is not YamlMappingNode mapping)
            return new StyleRuleItem { ItemRange = range, Fields = [], Selector = [], Properties = [] };

        var fields = FieldsOf(mapping);
        return new StyleRuleItem
        {
            ItemRange = range,
            Fields = fields,
            Selector = ValueMapping(mapping, "class") is { } selector ? FieldsOf(selector) : [],
            Properties = ValueMapping(mapping, "properties") is { } properties ? FieldsOf(properties) : [],
        };
    }

    private static YamlMappingNode? ValueMapping(YamlMappingNode mapping, string key) =>
        mapping.Children.FirstOrDefault(kv => kv.Key is YamlScalarNode { Value: { } k } && k == key).Value as YamlMappingNode;

    private static List<FieldSpan> FieldsOf(YamlMappingNode mapping) =>
        mapping.Children
            .Where(kv => kv.Key is YamlScalarNode { Value: not null })
            .Select(kv => Field(
                ((YamlScalarNode)kv.Key).Value!,
                YamlPositionMapper.ToRange(kv.Key.Start, kv.Key.End),
                YamlPositionMapper.ToRange(kv.Value.Start, kv.Value.End),
                kv.Value))
            .ToList();

    /// <summary>A scalar's own value, or every non-empty scalar of a sequence.</summary>
    private static IEnumerable<(string, LspRange)> ScalarsOf(YamlNode node) => node switch
    {
        YamlScalarNode { Value: { Length: > 0 } v } s => [(v, ScalarRange(s))],
        YamlSequenceNode seq => seq.Children
            .OfType<YamlScalarNode>()
            .Where(s => s.Value is { Length: > 0 })
            .Select(s => (s.Value!, ScalarRange(s))),
        _ => [],
    };

    // A quoted scalar's marks include its quotes; the text inside them is one column in from each.
    private static LspRange ScalarRange(YamlScalarNode scalar)
    {
        var range = YamlPositionMapper.ToRange(scalar.Start, scalar.End);
        if (scalar.Style is not (YamlDotNet.Core.ScalarStyle.SingleQuoted or YamlDotNet.Core.ScalarStyle.DoubleQuoted) ||
            range.Start.Line != range.End.Line || range.End.Character - range.Start.Character < 2)
        {
            return range;
        }

        return new LspRange(
            new LspPosition(range.Start.Line, range.Start.Character + 1),
            new LspPosition(range.End.Line, range.End.Character - 1));
    }

    private static Mark LaterOf(Mark a, Mark b) =>
        a.Line > b.Line || (a.Line == b.Line && a.Column >= b.Column) ? a : b;

    /// <summary>
    /// The parent value's full extent. YamlDotNet doesn't give a flow list's closing bracket a
    /// usable end mark either, so for "[...]" it's found in the text: a list wrapped onto several
    /// lines often ends with a "]" line of its own, which still belongs to the value.
    /// </summary>
    private static LspRange ParentRange(YamlNode valueNode, string text)
    {
        var end = LaterOf(valueNode.End, GetTrueEnd(valueNode));

        if (valueNode is YamlSequenceNode { Style: YamlDotNet.Core.Events.SequenceStyle.Flow } &&
            FindClosingBracket(text, (int)valueNode.Start.Index) is { } close)
        {
            var line = 1;
            var lineStart = 0;
            for (var i = 0; i < close; i++)
            {
                if (text[i] == '\n')
                {
                    line++;
                    lineStart = i + 1;
                }
            }

            end = LaterOf(end, new Mark(close + 1, line, close - lineStart + 2));
        }

        return YamlPositionMapper.ToRange(valueNode.Start, end);
    }

    // Index of the ']' matching the '[' at openIndex, skipping quoted ids.
    private static int? FindClosingBracket(string text, int openIndex)
    {
        if (openIndex < 0 || openIndex >= text.Length || text[openIndex] != '[')
            return null;

        var depth = 0;
        char? quote = null;
        for (var i = openIndex; i < text.Length; i++)
        {
            var c = text[i];
            if (quote is not null)
            {
                if (c == quote)
                    quote = null;
                continue;
            }

            switch (c)
            {
                case '"' or '\'':
                    quote = c;
                    break;
                case '[':
                    depth++;
                    break;
                case ']' when --depth == 0:
                    return i;
            }
        }

        return null;
    }

    private static FieldSpan Field(string key, LspRange keyRange, LspRange valueRange, YamlNode valueNode) => new()
    {
        Name = key,
        KeyRange = keyRange,
        ValueRange = valueRange,
        ScalarValue = (valueNode as YamlScalarNode)?.Value,
        ScalarSequenceRange = valueNode is YamlSequenceNode { Children.Count: > 0 } sequence && sequence.Children.All(c => c is YamlScalarNode)
            ? YamlPositionMapper.ToRange(sequence.Start, GetTrueEnd(sequence))
            : null,
        MappingKeys = valueNode is YamlMappingNode mapping
            ? mapping.Children.Keys
                .OfType<YamlScalarNode>()
                .Where(k => k.Value is not null)
                .Select(k => (k.Value!, YamlPositionMapper.ToRange(k.Start, k.End)))
                .ToList()
            : null,
        Entries = valueNode is YamlMappingNode entries ? FieldsOf(entries) : null,
        Scalars = ScalarsOf(valueNode).ToList(),
    };

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
