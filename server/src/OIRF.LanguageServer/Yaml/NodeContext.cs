namespace OIRF.LanguageServer.Yaml;

/// <summary>What a cursor position resolves to inside a Prototype YAML document.</summary>
public abstract record NodeContext
{
    private NodeContext() { }

    public sealed record PrototypeTypeValue(string? CurrentValue) : NodeContext;

    public sealed record PrototypeIdValue : NodeContext;

    public sealed record ParentValue(string PrototypeTypeKey, string? CurrentItemId, IReadOnlySet<string> ExistingParents) : NodeContext;

    public sealed record TopLevelFieldKey(string PrototypeTypeKey, IReadOnlySet<string> ExistingFieldNames) : NodeContext;

    public sealed record TopLevelFieldValue(string PrototypeTypeKey, string FieldName) : NodeContext;

    public sealed record ComponentTypeValue(string PrototypeTypeKey, string? CurrentValue) : NodeContext;

    public sealed record ComponentFieldKey(string PrototypeTypeKey, string ComponentName, IReadOnlySet<string> ExistingFieldNames) : NodeContext;

    public sealed record ComponentFieldValue(string PrototypeTypeKey, string ComponentName, string FieldName) : NodeContext;

    /// <summary>A rule-level key position inside a style <c>rules:</c> entry (class/properties).</summary>
    public sealed record StyleRuleKey(IReadOnlySet<string> ExistingKeys) : NodeContext;

    /// <summary>
    /// Inside a rule's <c>class:</c> selector. Whether the cursor is on a key or a value is
    /// read off the line by the handler (flow form puts several entries on one line);
    /// <see cref="Entry"/> is the entry the cursor is on or right after, if any.
    /// </summary>
    public sealed record StyleSelector(IReadOnlySet<string> ExistingKeys, Yaml.FieldSpan? Entry) : NodeContext;

    /// <summary>A key position under a rule's <c>properties:</c>.</summary>
    public sealed record StylePropertyKey(string? Control, IReadOnlySet<string> ExistingNames) : NodeContext;

    /// <summary>On (the key or value of) an existing property under a rule's <c>properties:</c>.</summary>
    public sealed record StylePropertyValue(string? Control, string PropertyName) : NodeContext;

    /// <summary>Cursor isn't inside any recognized prototype item.</summary>
    public sealed record None : NodeContext;
}
