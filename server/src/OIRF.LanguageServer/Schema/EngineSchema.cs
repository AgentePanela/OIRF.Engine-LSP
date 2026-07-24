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

/// <summary>A <c>[DataField]</c> member on a <see cref="PrototypeTypeInfo"/>.</summary>
public sealed record DataFieldInfo(
    string YamlName,
    bool Required,
    bool IsMeta,
    bool IsComponentsField,
    string ClrTypeDisplay,
    string? DocMarkdown,
    AssetClassification? Asset);

/// <summary>A public settable member on a <see cref="ComponentTypeInfo"/>.</summary>
public sealed record MemberInfo(
    string Name,
    string ClrTypeDisplay,
    string? DocMarkdown,
    AssetClassification? Asset);

/// <summary>A <c>[Prototype("type")]</c> class implementing <c>IPrototype</c>.</summary>
public sealed record PrototypeTypeInfo(
    string TypeKey,
    int LoadPriority,
    string ClrTypeName,
    bool IsInheriting,
    string? ClassDocMarkdown,
    IReadOnlyList<DataFieldInfo> DataFields);

/// <summary>A <c>[RegisterComponent("Name")]</c> class deriving from <c>Component</c>.</summary>
public sealed record ComponentTypeInfo(
    string Name,
    string ClrTypeName,
    string? ClassDocMarkdown,
    IReadOnlyList<MemberInfo> Members);

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

    public EngineSchema(
        IReadOnlyDictionary<string, PrototypeTypeInfo> prototypesByTypeKey,
        IReadOnlyDictionary<string, ComponentTypeInfo> componentsByName)
    {
        PrototypesByTypeKey = prototypesByTypeKey;
        ComponentsByName = componentsByName;
    }
}
