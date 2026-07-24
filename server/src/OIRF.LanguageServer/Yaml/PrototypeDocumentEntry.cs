namespace OIRF.LanguageServer.Yaml;

public sealed class FieldSpan
{
    public required string Name { get; init; }
    public required LspRange KeyRange { get; init; }
    public required LspRange ValueRange { get; init; }
    public string? ScalarValue { get; init; }
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
    public required IReadOnlyList<FieldSpan> TopLevelFields { get; init; }
    public LspRange? ComponentsListRange { get; init; }
    public required IReadOnlyList<ComponentItem> Components { get; init; }
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
