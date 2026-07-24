namespace OIRF.LanguageServer.Yaml;

/// <summary>What a cursor position resolves to inside a Prototype YAML document.</summary>
public abstract record NodeContext
{
    private NodeContext() { }

    public sealed record PrototypeTypeValue(string? CurrentValue) : NodeContext;

    public sealed record PrototypeIdValue : NodeContext;

    public sealed record ParentValue(string PrototypeTypeKey, string? CurrentItemId) : NodeContext;

    public sealed record TopLevelFieldKey(string PrototypeTypeKey, IReadOnlySet<string> ExistingFieldNames) : NodeContext;

    public sealed record TopLevelFieldValue(string PrototypeTypeKey, string FieldName) : NodeContext;

    public sealed record ComponentTypeValue(string PrototypeTypeKey, string? CurrentValue) : NodeContext;

    public sealed record ComponentFieldKey(string PrototypeTypeKey, string ComponentName, IReadOnlySet<string> ExistingFieldNames) : NodeContext;

    public sealed record ComponentFieldValue(string PrototypeTypeKey, string ComponentName, string FieldName) : NodeContext;

    /// <summary>Cursor isn't inside any recognized prototype item.</summary>
    public sealed record None : NodeContext;
}
