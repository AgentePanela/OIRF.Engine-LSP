using Microsoft.CodeAnalysis;

namespace OIRF.LanguageServer.Schema;

/// <summary>
/// Classifies a field/member as an asset-path reference. Highest confidence: the engine's own
/// dedicated wrapper types (<c>SpriteKey</c>/<c>ShaderPath</c>) or an explicit marker attribute
/// (<c>Engine.Shared.Assets.TextureKeyAttribute</c>/<c>AnimationKeyAttribute</c>/
/// <c>ShaderKeyAttribute</c>) on a plain `string` field - the deterministic alternative to
/// name-guessing for fields that can't use a dedicated wrapper type directly (e.g.
/// <c>TilePrototype.Sprite</c>, <c>AnimationComponent.Key</c>). Anything else falls back to a
/// low-confidence name-keyword heuristic, since not every asset-path field is marked yet.
///
/// This table is intentionally NOT a reflection of Engine.Editor's IQuickResolveField/
/// QuickResolveRegistry - not every downstream solution includes the Editor project - so keep
/// it in sync with that registry by convention when either one changes.
/// </summary>
public static class AssetFieldHeuristics
{
    private const string SpriteKeyFqn = "Engine.Client.Assets.SpriteKey";
    private const string ShaderPathFqn = "Engine.Client.Graphics.Shaders.ShaderPath";
    private const string TextureKeyAttributeFqn = "Engine.Shared.Assets.TextureKeyAttribute";
    private const string AnimationKeyAttributeFqn = "Engine.Shared.Assets.AnimationKeyAttribute";
    private const string ShaderKeyAttributeFqn = "Engine.Shared.Assets.ShaderKeyAttribute";

    // ProtoId<T> (Engine.Shared/Types.cs) declares no namespace, so its open-generic display is
    // just "ProtoId<T>" - no namespace prefix to include, unlike the FQNs above.
    private const string ProtoIdOpenGenericDisplay = "ProtoId<T>";

    /// <param name="member">
    /// The field/property symbol itself, so an explicit marker attribute can be checked directly
    /// (attributes live on the member, not its type).
    /// </param>
    /// <param name="containingTypeDisplayName">
    /// The declaring type's full display name (e.g. "Engine.Client.Graphics.AnimationComponent").
    /// Used only for the low-confidence "Key"-named field fallback below.
    /// </param>
    public static AssetClassification? Classify(ISymbol member, ITypeSymbol type, string fieldName, string containingTypeDisplayName)
    {
        var typeName = type.ToDisplayString();

        if (typeName == SpriteKeyFqn)
            return new AssetClassification(AssetKind.Sprite, ["Textures"], HighConfidence: true);

        if (typeName == ShaderPathFqn)
            return new AssetClassification(AssetKind.Shader, ["Shaders"], HighConfidence: true);

        if (type.SpecialType != SpecialType.System_String)
            return null;

        var attributeFqns = member.GetAttributes()
            .Select(a => a.AttributeClass?.ToDisplayString())
            .ToHashSet();

        if (attributeFqns.Contains(TextureKeyAttributeFqn))
            return new AssetClassification(AssetKind.Sprite, ["Textures"], HighConfidence: true);

        if (attributeFqns.Contains(AnimationKeyAttributeFqn))
            return new AssetClassification(AssetKind.Animation, ["Textures"], HighConfidence: true);

        if (attributeFqns.Contains(ShaderKeyAttributeFqn))
            return new AssetClassification(AssetKind.Shader, ["Shaders"], HighConfidence: true);

        if (fieldName.Contains("shader", StringComparison.OrdinalIgnoreCase))
            return new AssetClassification(AssetKind.Shader, ["Shaders"], HighConfidence: false);

        // Fallback for a downstream Animation-like component that doesn't use [AnimationKey]:
        // a plain string named just "Key" is too generic to match on its own, but combined with
        // "the declaring type looks like an animation component" it's a safe, narrow signal.
        if (string.Equals(fieldName, "Key", StringComparison.OrdinalIgnoreCase) &&
            containingTypeDisplayName.Contains("Animation", StringComparison.OrdinalIgnoreCase))
        {
            return new AssetClassification(AssetKind.Animation, ["Textures"], HighConfidence: false);
        }

        return null;
    }

    /// <summary>
    /// Deterministic, type-only classification (no name-guessing needed, unlike the low-confidence
    /// fallbacks above): a field typed <c>ProtoId&lt;T&gt;</c> should offer completion from every
    /// known <c>id:</c> value of prototypes registered as <c>T</c>. Checked against
    /// <see cref="UnwrapToElementType"/>'s result so it still matches when <c>ProtoId&lt;T&gt;</c>
    /// is wrapped in <c>Nullable&lt;T&gt;</c>, an array (any rank), or a single-type-argument
    /// collection (<c>List&lt;T&gt;</c>, <c>HashSet&lt;T&gt;</c>, ...) - all real shapes it's
    /// declared in across the engine (e.g. <c>HashSet&lt;ProtoId&lt;TagPrototype&gt;&gt;</c>,
    /// <c>ProtoId&lt;TilePrototype&gt;?[,]</c>).
    /// </summary>
    public static ProtoIdClassification? ClassifyProtoId(ITypeSymbol type)
    {
        var element = UnwrapToElementType(type);

        if (element is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } named &&
            named.OriginalDefinition.ToDisplayString() == ProtoIdOpenGenericDisplay)
        {
            return new ProtoIdClassification(named.TypeArguments[0].ToDisplayString());
        }

        return null;
    }

    private static ITypeSymbol UnwrapToElementType(ITypeSymbol type)
    {
        while (true)
        {
            if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable)
            {
                type = nullable.TypeArguments[0];
                continue;
            }

            if (type is IArrayTypeSymbol array)
            {
                type = array.ElementType;
                continue;
            }

            // Guard against unwrapping ProtoId<T> itself - it's also "a generic type with one
            // type argument", but T there is the prototype type, not another wrapper layer.
            if (type is INamedTypeSymbol { IsGenericType: true, TypeArguments.Length: 1 } collection &&
                collection.OriginalDefinition.ToDisplayString() != ProtoIdOpenGenericDisplay)
            {
                type = collection.TypeArguments[0];
                continue;
            }

            return type;
        }
    }
}
