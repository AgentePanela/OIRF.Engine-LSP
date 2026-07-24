namespace OIRF.LanguageServer.Schema;

public enum AssetKind
{
    /// <summary>Atlas frame key, e.g. "Entities/Player/idle-0" (SpriteComponent.Key).</summary>
    Sprite,

    Shader,

    /// <summary>
    /// Bare animation-definition key from an info.yml `id` (no "-N" frame suffix), e.g.
    /// "Entities/Player/walking" (AnimationComponent.Key) - a different key namespace from
    /// Sprite, so it must not be offered the same completion list.
    /// </summary>
    Animation,
}

/// <summary>
/// A field/member's classification as an asset-path reference, e.g. a <c>SpriteKey</c> field
/// should offer completion from the workspace's <c>Textures</c> resource root(s).
/// </summary>
public sealed record AssetClassification(AssetKind Kind, IReadOnlyList<string> ResourceRoots, bool HighConfidence);

/// <summary>
/// Where a symbol is declared in C# source - captured once at schema-build time (via
/// <c>ISymbol.Locations</c>/<c>GetLineSpan()</c>) rather than keeping the <c>ISymbol</c> itself
/// around, since the schema is meant to be a plain snapshot decoupled from the Roslyn
/// workspace's lifetime. Powers go-to-definition.
/// </summary>
public sealed record SymbolLocation(string FilePath, LspRange Range);

/// <summary>A <c>[DataField]</c> member on a <see cref="PrototypeTypeInfo"/>.</summary>
public sealed record DataFieldInfo(
    string YamlName,
    bool Required,
    bool IsMeta,
    bool IsComponentsField,
    string ClrTypeDisplay,
    string Signature,
    string? DocMarkdown,
    AssetClassification? Asset,
    SymbolLocation? Location,
    IReadOnlyList<string>? ValueChoices = null);

/// <summary>
/// A public settable member on a <see cref="ComponentTypeInfo"/>. <see cref="ValueChoices"/>
/// (enum member names, or "true"/"false" for bool) is null for anything else, and offered as
/// value completion the same way an asset-classified field offers resource keys.
/// </summary>
public sealed record MemberInfo(
    string Name,
    string ClrTypeDisplay,
    string Signature,
    string? DocMarkdown,
    AssetClassification? Asset,
    SymbolLocation? Location,
    IReadOnlyList<string>? ValueChoices = null);

/// <summary>A <c>[Prototype("type")]</c> class implementing <c>IPrototype</c>.</summary>
public sealed record PrototypeTypeInfo(
    string TypeKey,
    int LoadPriority,
    string ClrTypeName,
    string Signature,
    bool IsInheriting,
    string? ClassDocMarkdown,
    IReadOnlyList<DataFieldInfo> DataFields,
    SymbolLocation? Location);

/// <summary>A <c>[RegisterComponent("Name")]</c> class deriving from <c>Component</c>.</summary>
public sealed record ComponentTypeInfo(
    string Name,
    string ClassName,
    string ClrTypeName,
    string Signature,
    string? ClassDocMarkdown,
    IReadOnlyList<MemberInfo> Members,
    SymbolLocation? Location);

/// <summary>
/// The result of resolving a <c>type:</c> value against a component's registered
/// <see cref="ComponentTypeInfo.Name"/> or, failing that, its raw <see cref="ComponentTypeInfo.ClassName"/>.
/// </summary>
public sealed record ComponentResolution(ComponentTypeInfo Component, bool MatchedByClassName);

/// <summary>
/// Everything the LSP features (completion/hover/diagnostics) need to know about the prototypes
/// and components discovered in the current workspace - built-in engine ones and any custom
/// ones a downstream project defines, indistinguishably (both come from the same Roslyn walk).
/// </summary>
public sealed class EngineSchema
{
    public static readonly EngineSchema Empty = new(
        new Dictionary<string, PrototypeTypeInfo>(StringComparer.OrdinalIgnoreCase),
        new Dictionary<string, ComponentTypeInfo>(StringComparer.OrdinalIgnoreCase));

    public IReadOnlyDictionary<string, PrototypeTypeInfo> PrototypesByTypeKey { get; }
    public IReadOnlyDictionary<string, ComponentTypeInfo> ComponentsByName { get; }

    /// <summary>
    /// Keyed by raw C# class name (e.g. "PointLightComponent"), not the registered name (e.g.
    /// "PointLight"). The engine's own loader accepts either
    /// (<c>EntityManager.Entity.cs</c>: <c>ComponentFactory.CreateInstanceFromSanitized(name) ??
    /// CreateInstance(name)</c>, where <c>CreateInstanceFromSanitized</c> looks up the registered
    /// name and the fallback <c>CreateInstance</c> looks up the raw class name) - so a prototype
    /// referencing a component by its class name is valid YAML, not an error, even though the
    /// registered name is the convention. See <see cref="ResolveComponent"/>.
    /// </summary>
    public IReadOnlyDictionary<string, ComponentTypeInfo> ComponentsByClassName { get; }

    public EngineSchema(
        IReadOnlyDictionary<string, PrototypeTypeInfo> prototypesByTypeKey,
        IReadOnlyDictionary<string, ComponentTypeInfo> componentsByName)
        : this(prototypesByTypeKey, componentsByName, BuildByClassName(componentsByName))
    {
    }

    public EngineSchema(
        IReadOnlyDictionary<string, PrototypeTypeInfo> prototypesByTypeKey,
        IReadOnlyDictionary<string, ComponentTypeInfo> componentsByName,
        IReadOnlyDictionary<string, ComponentTypeInfo> componentsByClassName)
    {
        PrototypesByTypeKey = prototypesByTypeKey;
        ComponentsByName = componentsByName;
        ComponentsByClassName = componentsByClassName;
    }

    /// <summary>
    /// Resolves a component <c>type:</c> value the same way the engine's runtime loader does:
    /// registered name first, raw class name as a fallback. Callers that need to distinguish the
    /// two (the validator, to warn on the fallback path) can check
    /// <see cref="ComponentResolution.MatchedByClassName"/>.
    /// </summary>
    public ComponentResolution? ResolveComponent(string name)
    {
        if (ComponentsByName.TryGetValue(name, out var byRegisteredName))
            return new ComponentResolution(byRegisteredName, MatchedByClassName: false);

        if (ComponentsByClassName.TryGetValue(name, out var byClassName))
            return new ComponentResolution(byClassName, MatchedByClassName: true);

        return null;
    }

    private static IReadOnlyDictionary<string, ComponentTypeInfo> BuildByClassName(
        IReadOnlyDictionary<string, ComponentTypeInfo> componentsByName)
    {
        var byClassName = new Dictionary<string, ComponentTypeInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var component in componentsByName.Values)
            byClassName.TryAdd(component.ClassName, component);

        return byClassName;
    }
}
