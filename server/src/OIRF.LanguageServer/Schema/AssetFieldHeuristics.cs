using Microsoft.CodeAnalysis;

namespace OIRF.LanguageServer.Schema;

/// <summary>
/// Classifies a field/member as an asset-path reference. Two of these are the engine's own
/// dedicated wrapper types (high confidence); everything else falls back to a name-keyword
/// heuristic on plain `string` fields, since not every asset-path field is consistently typed
/// (e.g. `TilePrototype.Sprite` is a plain string, not a `SpriteKey`).
///
/// This table is intentionally NOT a reflection of Engine.Editor's IQuickResolveField/
/// QuickResolveRegistry - not every downstream solution includes the Editor project - so keep
/// it in sync with that registry by convention when either one changes.
/// </summary>
public static class AssetFieldHeuristics
{
    private const string SpriteKeyFqn = "Engine.Client.Assets.SpriteKey";
    private const string ShaderPathFqn = "Engine.Client.Graphics.Shaders.ShaderPath";

    private static readonly (string Keyword, AssetKind Kind)[] NameKeywords =
    [
        ("sprite", AssetKind.Sprite),
        ("texture", AssetKind.Sprite),
        ("icon", AssetKind.Sprite),
        ("shader", AssetKind.Shader),
    ];

    /// <param name="containingTypeDisplayName">
    /// The declaring type's full display name (e.g. "Engine.Client.Graphics.AnimationComponent").
    /// Used only for the low-confidence "Key"-named field fallback below.
    /// </param>
    public static AssetClassification? Classify(ITypeSymbol type, string fieldName, string containingTypeDisplayName)
    {
        var typeName = type.ToDisplayString();

        if (typeName == SpriteKeyFqn)
            return new AssetClassification(AssetKind.Sprite, ["Textures"], HighConfidence: true);

        if (typeName == ShaderPathFqn)
            return new AssetClassification(AssetKind.Shader, ["Shaders"], HighConfidence: true);

        if (type.SpecialType != SpecialType.System_String)
            return null;

        foreach (var (keyword, kind) in NameKeywords)
        {
            if (fieldName.Contains(keyword, StringComparison.OrdinalIgnoreCase))
            {
                var roots = kind == AssetKind.Sprite ? new[] { "Textures" } : new[] { "Shaders" };
                return new AssetClassification(kind, roots, HighConfidence: false);
            }
        }

        // AnimationComponent.Key is a plain string named just "Key" - too generic a name to
        // match on its own, but combined with "the declaring type looks like an animation
        // component" it's a safe, narrow signal (confirmed against the engine's own
        // AnimationComponent.Key, which references info.yml animation ids, not atlas frame keys).
        if (string.Equals(fieldName, "Key", StringComparison.OrdinalIgnoreCase) &&
            containingTypeDisplayName.Contains("Animation", StringComparison.OrdinalIgnoreCase))
        {
            return new AssetClassification(AssetKind.Animation, ["Textures"], HighConfidence: false);
        }

        return null;
    }
}
