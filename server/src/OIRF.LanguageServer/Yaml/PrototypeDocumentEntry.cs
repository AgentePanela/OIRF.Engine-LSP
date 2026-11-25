namespace OIRF.LanguageServer.Yaml;

public sealed class FieldSpan
{
    public required string Name { get; init; }
    public required LspRange KeyRange { get; init; }
    public required LspRange ValueRange { get; init; }
    public string? ScalarValue { get; init; }

    /// <summary>
    /// Set when the value is a block/flow sequence of plain scalars (e.g. soundCollection
    /// <c>files:</c>): spans its first to last item, so every item line - not just the first -
    /// resolves to this field's value context.
    /// </summary>
    public LspRange? ScalarSequenceRange { get; init; }

    /// <summary>Set when the value is a mapping: its keys, with their ranges, in document order.</summary>
    public IReadOnlyList<(string Key, LspRange Range)>? MappingKeys { get; init; }

    /// <summary>Set when the value is a mapping: its entries, each a field of its own.</summary>
    public IReadOnlyList<FieldSpan>? Entries { get; init; }

    /// <summary>
    /// The value's scalar text(s): the scalar itself, or every scalar item of a sequence. Ranges
    /// cover the text only, without the quotes of a quoted scalar (what a rename replaces).
    /// </summary>
    public IReadOnlyList<(string Value, LspRange Range)> Scalars { get; init; } = [];
}

/// <summary>One entry of a prototype item's <c>components:</c> list.</summary>
public sealed class ComponentItem
{
    public required LspRange ItemRange { get; init; }
    public string? TypeValue { get; init; }
    public LspRange? TypeKeyRange { get; init; }
    public LspRange? TypeValueRange { get; init; }
    public required IReadOnlyList<FieldSpan> Fields { get; init; }
}

/// <summary>
/// One entry of a <c>type: style</c> prototype's <c>rules:</c> list (StyleRule): a
/// <c>class:</c> selector mapping and a <c>properties:</c> mapping, both kept as plain field spans.
/// </summary>
public sealed class StyleRuleItem
{
    public required LspRange ItemRange { get; init; }

    /// <summary>The rule's own keys (class, properties, and anything else written there).</summary>
    public required IReadOnlyList<FieldSpan> Fields { get; init; }

    /// <summary>Entries of the <c>class:</c> mapping (control/styleClass/pseudoClass/id).</summary>
    public required IReadOnlyList<FieldSpan> Selector { get; init; }

    /// <summary>Entries of the <c>properties:</c> mapping.</summary>
    public required IReadOnlyList<FieldSpan> Properties { get; init; }

    public FieldSpan? ClassField => Fields.FirstOrDefault(f => f.Name == "class");
    public FieldSpan? PropertiesField => Fields.FirstOrDefault(f => f.Name == "properties");

    /// <summary>The <c>control:</c> selector value, if any.</summary>
    public string? Control => Selector.FirstOrDefault(f => f.Name == "control")?.ScalarValue;
}

/// <summary>One item of the top-level YAML sequence in a Prototype file.</summary>
public sealed class PrototypeItem
{
    public required LspRange ItemRange { get; init; }
    public string? TypeValue { get; init; }
    public LspRange? TypeKeyRange { get; init; }
    public LspRange? TypeValueRange { get; init; }
    public string? IdValue { get; init; }
    public LspRange? IdValueRange { get; init; }
    public LspRange? ParentValueRange { get; init; }

    /// <summary>
    /// The whole <c>parent:</c> value, first to last line - a flow list like
    /// <c>[EngineDefault, OmicronStyleLabel,</c> often wraps onto following lines.
    /// </summary>
    public LspRange? ParentFullRange { get; init; }

    /// <summary>Every parent id, whether written as a scalar or as a list.</summary>
    public IReadOnlyList<(string Id, LspRange Range)> Parents { get; init; } = [];

    /// <summary>The <c>rules:</c> list (style prototypes), from its first to its last line.</summary>
    public LspRange? StyleRulesRange { get; init; }

    public IReadOnlyList<StyleRuleItem> StyleRules { get; init; } = [];
    public required IReadOnlyList<FieldSpan> TopLevelFields { get; init; }
    public LspRange? ComponentsListRange { get; init; }
    public required IReadOnlyList<ComponentItem> Components { get; init; }

    /// <summary>
    /// Every top-level key this item sets itself, <c>components</c> included (the parser keeps
    /// that list apart from <see cref="TopLevelFields"/>). What it passes on to its children.
    /// </summary>
    public IEnumerable<string> SetFieldNames =>
        ComponentsListRange is null
            ? TopLevelFields.Select(f => f.Name)
            : TopLevelFields.Select(f => f.Name).Append("components");
}

/// <summary>
/// The parsed shape of a Prototype YAML file (sequence-root grammar). On a parse failure,
/// <see cref="Items"/> is empty and <see cref="ParseErrorMessage"/> is set - callers publish a
/// syntax-error diagnostic and skip hover/most diagnostics until the document reparses cleanly.
/// </summary>
public sealed class PrototypeDocument
{
    public required IReadOnlyList<PrototypeItem> Items { get; init; }
    public string? ParseErrorMessage { get; init; }
    public LspRange? ParseErrorRange { get; init; }

    public bool HasParseError => ParseErrorMessage is not null;

    public static PrototypeDocument Empty { get; } = new() { Items = [] };
}
