namespace OIRF.LanguageServer.Schema;

public enum AssetKind
{
    /// <summary>Atlas frame key, e.g. "Entities/Player/idle-0" (SpriteComponent.Key).</summary>
    Sprite,

    /// <summary>
    /// A shader reference (<c>ShaderPath</c>/<c>[ShaderKey]</c>, e.g. SpriteComponent.Shader).
    /// Since engine 0.5 this is a <c>ShaderPrototype</c> id (<c>type: shader</c>); on older engines
    /// with no ShaderPrototype it's the bare .fx file name. Which one is offered is decided at
    /// completion time against the schema, see CompletionHandler.
    /// </summary>
    Shader,

    /// <summary>Bare .fx file name under a "Shaders" root, no extension (ShaderPrototype.Path).</summary>
    ShaderFile,

    /// <summary>
    /// Audio key: path of an .ogg relative to an "Audios" resource root, no extension, e.g.
    /// "Mobs/Voice/speak_1" (<c>[AudioKey]</c>, mirrors SharedAudioManifest).
    /// </summary>
    Audio,

    /// <summary>
    /// A <c>SoundSpecifier</c>: either a bare audio key, or a map with exactly one of
    /// <c>path</c> (audio key) / <c>collection</c> (soundCollection id) plus optional
    /// <c>volume</c>/<c>pitch</c> (mirrors SoundSpecifier.FromYaml).
    /// </summary>
    Sound,

    /// <summary>
    /// A .ttf under a "Fonts" root: relative path without extension, e.g. "Minecraft/macs-minecraft"
    /// (FontFamilyPrototype.Regular/Bold/..., resolved by FontManager against ResFile.Relative).
    /// </summary>
    Font,

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
/// A field/member's classification as a <c>ProtoId&lt;T&gt;</c>-shaped prototype-id reference
/// (bare, nullable, or as a collection/array element - see AssetFieldHeuristics.ClassifyProtoId).
/// Only <c>T</c>'s CLR type name is captured here rather than its registered
/// <see cref="PrototypeTypeInfo.TypeKey"/>: schema-build time walks types in no particular order,
/// so <c>T</c>'s own <c>[Prototype]</c> registration may not have been discovered yet. Resolving
/// <see cref="PrototypeClrTypeName"/> to a <c>TypeKey</c> - and from there to actual prototype id:
/// values via a workspace-wide id index - happens later, at completion time.
/// </summary>
public sealed record ProtoIdClassification(string PrototypeClrTypeName);

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
    IReadOnlyList<string>? ValueChoices = null,
    ProtoIdClassification? ProtoId = null);

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
    IReadOnlyList<string>? ValueChoices = null,
    ProtoIdClassification? ProtoId = null);

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
/// One <c>[StyleField("name", default)]</c> backing field on a UI control - a key a
/// <c>type: style</c> rule may set under <c>properties:</c>. Names are matched case-sensitively,
/// exactly like the engine (<c>StyleRule.Properties</c> is a plain <c>Dictionary</c>).
/// </summary>
public sealed record StylePropertyInfo(
    string Name,
    string ClrTypeDisplay,
    string Signature,
    string? DefaultDisplay,
    string? DocMarkdown,
    SymbolLocation? Location,
    IReadOnlyList<string>? ValueChoices,
    AssetClassification? Asset,
    ProtoIdClassification? ProtoId);

/// <summary>
/// A class deriving from (or being) <c>Engine.Client.UI.Control</c>. <see cref="Aliases"/> are the
/// extra names it adds with <c>StyleAliasses.Add("...")</c> (inherited by subclasses at runtime,
/// since they're added in a constructor); <see cref="StyleProperties"/> are only the ones declared
/// on this class - see <see cref="EngineSchema.ResolveStyleProperties"/> for the inherited set.
/// </summary>
public sealed record UiControlInfo(
    string Name,
    string ClrTypeName,
    string? BaseClrTypeName,
    IReadOnlyList<string> Aliases,
    IReadOnlyList<StylePropertyInfo> StyleProperties,
    string Signature,
    string? ClassDocMarkdown,
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

    /// <summary>The registered <c>type:</c> key of the prototype class <paramref name="clrTypeName"/>, if any.</summary>
    public string? TypeKeyOf(string clrTypeName) =>
        PrototypesByTypeKey.Values.FirstOrDefault(p => p.ClrTypeName == clrTypeName)?.TypeKey;

    public const string StylePrototypeFqn = "Engine.Client.UI.StylePrototype";
    public const string ShaderPrototypeFqn = "Engine.Shared.Graphics.ShaderPrototype";
    public const string SoundCollectionPrototypeFqn = "Engine.Shared.Audio.SoundCollectionPrototype";
    public const string FontFamilyPrototypeFqn = "Engine.Client.Graphics.Fonts.FontFamilyPrototype";

    /// <summary>Whether <paramref name="typeKey"/> is the engine's UI stylesheet prototype.</summary>
    public bool IsStylePrototype(string typeKey) =>
        PrototypesByTypeKey.GetValueOrDefault(typeKey)?.ClrTypeName == StylePrototypeFqn;

    /// <summary>The keys StyleClass reads from a rule's <c>class:</c> (case-sensitive, like the engine).</summary>
    public static readonly string[] StyleSelectorKeys = ["control", "styleClass", "pseudoClass", "id"];

    /// <summary>The keys StyleRule reads from each <c>rules:</c> entry.</summary>
    public static readonly string[] StyleRuleKeys = ["class", "properties"];

    /// <summary>UI controls keyed by CLR full name.</summary>
    public IReadOnlyDictionary<string, UiControlInfo> UiControls { get; init; } = new Dictionary<string, UiControlInfo>();

    /// <summary>Every literal passed to <c>PseudoClasses.Add("...")</c> anywhere in the workspace.</summary>
    public IReadOnlyList<string> PseudoClasses { get; init; } = [];

    /// <summary>The control and its base classes, most-derived first.</summary>
    public IEnumerable<UiControlInfo> ControlChain(UiControlInfo control)
    {
        for (UiControlInfo? current = control; current is not null;
             current = current.BaseClrTypeName is { } b ? UiControls.GetValueOrDefault(b) : null)
        {
            yield return current;
        }
    }

    /// <summary>Every name a <c>control:</c> selector can use: class names plus aliases.</summary>
    public IEnumerable<string> StyleControlNames =>
        UiControls.Values.Select(c => c.Name)
            .Concat(UiControls.Values.SelectMany(c => c.Aliases))
            .Distinct(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The controls a <c>control:</c> selector matches, mirroring StyleClass.MatchesControlType:
    /// the class name of the control or of any of its bases, or an alias the control or a base
    /// added - all case-insensitive.
    /// </summary>
    public IEnumerable<UiControlInfo> MatchStyleControl(string control) =>
        UiControls.Values.Where(c => ControlChain(c).Any(t =>
            string.Equals(t.Name, control, StringComparison.OrdinalIgnoreCase) ||
            t.Aliases.Contains(control, StringComparer.OrdinalIgnoreCase)));

    /// <summary>
    /// The style properties a rule can set: for a <c>control:</c> selector, everything declared on
    /// the matched controls and their bases; for a rule with no control (universal), every
    /// property of every control. The first declaration of each name wins.
    /// </summary>
    public IReadOnlyList<StylePropertyInfo> ResolveStyleProperties(string? control)
    {
        var controls = string.IsNullOrEmpty(control) ? UiControls.Values : MatchStyleControl(control);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<StylePropertyInfo>();
        foreach (var property in controls.SelectMany(ControlChain).SelectMany(c => c.StyleProperties))
        {
            if (seen.Add(property.Name))
                result.Add(property);
        }

        return result;
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
