using OIRF.LanguageServer.Assets;
using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Features;

/// <summary>
/// Diagnostics for a Prototype YAML document. Severities mirror the engine's own
/// PrototypeManager/DataFieldConverter exactly (verified against source): unknown prototype
/// field is a Warning (matches Log.Warn), everything else here is an Error (matches a thrown
/// PrototypeLoadException).
/// </summary>
public static class PrototypeValidator
{
    /// <param name="prototypeIds">
    /// Workspace-wide ids, for the parent/inheritance/unknown-id checks. Null (or not built yet)
    /// skips them rather than flagging every id defined in another file.
    /// </param>
    /// <param name="resources">Indexed asset files, for the missing-asset check. Null skips it.</param>
    /// <param name="documentPath">This document's path, so its own on-disk index entries are ignored.</param>
    public static IEnumerable<Diagnostic> Validate(
        string text,
        EngineSchema schema,
        PrototypeIdIndex? prototypeIds = null,
        ResourceIndex? resources = null,
        string? documentPath = null)
    {
        var document = PrototypeYamlParser.Parse(text);

        if (document.HasParseError)
        {
            yield return Error(
                document.ParseErrorRange ?? new LspRange(new LspPosition(0, 0), new LspPosition(0, 0)),
                "syntax-error",
                document.ParseErrorMessage!);
            yield break;
        }

        // Null when the cross-file checks can't run yet: no index, or nothing indexed.
        var lookup = new PrototypeLookup(document, prototypeIds ?? PrototypeIdIndex.Empty, documentPath);
        var crossFile = prototypeIds is null || lookup.IsEmpty ? null : lookup;

        foreach (var item in document.Items)
        {
            foreach (var diagnostic in ValidateItem(item, schema, crossFile, resources))
                yield return diagnostic;
        }

        if (crossFile is null)
            yield break;

        foreach (var diagnostic in ValidateFieldReferences(document, schema, crossFile, resources))
            yield return diagnostic;
    }

    /// <summary>
    /// A field naming a prototype id that doesn't exist. Only a Warning: the engine stores a
    /// ProtoId&lt;T&gt; as a plain string on load and only fails when it's indexed at runtime. A type
    /// with no known ids at all is skipped - its prototypes most likely just aren't in the workspace.
    /// </summary>
    private static IEnumerable<Diagnostic> ValidateFieldReferences(PrototypeDocument document, EngineSchema schema, PrototypeLookup lookup, ResourceIndex? resources)
    {
        foreach (var reference in PrototypeReferences.Collect(document, schema))
        {
            if (reference.Kind != PrototypeReferenceKind.Field || !lookup.HasAny(reference.TypeKey) || lookup.Exists(reference.TypeKey, reference.Id))
                continue;

            if (reference.AllowsLooseFont && resources is not null && IsKnownFont(resources, reference.Id))
                continue;

            yield return Warning(reference.Range, "unknown-proto-id",
                $"No '{reference.TypeKey}' prototype with id '{reference.Id}'. The engine doesn't check this on load, it fails when the id is used.");
        }
    }

    private static IEnumerable<Diagnostic> ValidateItem(PrototypeItem item, EngineSchema schema, PrototypeLookup? lookup, ResourceIndex? resources)
    {
        if (item.TypeValue is null || item.TypeValueRange is null)
        {
            yield return Error(item.ItemRange, "missing-req-field", "Prototype item is missing a 'type:' key.");
            yield break;
        }

        if (string.IsNullOrEmpty(item.IdValue))
            yield return Error(item.ItemRange, "missing-req-field", "Prototype item is missing an 'id:' key.");

        if (!schema.PrototypesByTypeKey.TryGetValue(item.TypeValue, out var proto))
        {
            yield return Error(item.TypeValueRange, "unknown-proto-type", $"Unknown prototype type '{item.TypeValue}'.");
            yield break;
        }

        // PrototypeManager.GetMergedFields throws for both. Parents resolve within the same
        // prototype type, case-insensitively (its raw-prototype buckets use OrdinalIgnoreCase).
        if (lookup is not null)
        {
            foreach (var (parentId, range) in item.Parents)
            {
                if (!lookup.Exists(item.TypeValue, parentId))
                    yield return Error(range, "missing-parent", $"Prototype '{item.IdValue}' references missing parent '{parentId}'.");
                else if (item.IdValue is { Length: > 0 } selfId && Reaches(lookup, item.TypeValue, parentId, selfId))
                    yield return Error(range, "cyclic-parent", $"Cyclic inheritance: '{parentId}' inherits back from '{selfId}'.");
            }
        }

        if (schema.IsStylePrototype(item.TypeValue))
        {
            foreach (var diagnostic in ValidateStyleRules(item, schema))
                yield return diagnostic;
        }

        // PrototypeManager.PopulateInstance checks required fields against the merged fields -
        // the item's own plus everything its parents (recursively) set - and it does so for
        // abstract prototypes too. Without the index, or with a parent that can't be resolved,
        // the merged set isn't known, so the check is skipped rather than guessed.
        var setFields = item.Parents.Count == 0
            ? item.SetFieldNames.ToHashSet(StringComparer.OrdinalIgnoreCase)
            : lookup is null ? null : MergedFieldNames(lookup, item);

        if (setFields is not null)
        {
            foreach (var field in proto.DataFields)
            {
                if (field.IsMeta || !field.Required || setFields.Contains(field.YamlName))
                    continue;

                var where = item.Parents.Count == 0 ? "" : " (not set here or by any parent)";
                yield return Error(
                    item.ItemRange,
                    "missing-req-field",
                    $"Missing required field '{field.YamlName}' on prototype '{item.IdValue}'{where}.");
            }
        }

        foreach (var field in item.TopLevelFields)
        {
            var dataField = proto.DataFields.FirstOrDefault(f => string.Equals(f.YamlName, field.Name, StringComparison.OrdinalIgnoreCase));
            if (dataField is null)
            {
                yield return Warning(field.KeyRange, "unknown-proto-field", $"Unknown field '{field.Name}' on prototype '{item.IdValue}'.");
                continue;
            }

            foreach (var diagnostic in ValidateValue(field, dataField.Asset, schema, resources))
                yield return diagnostic;
        }

        foreach (var component in item.Components)
        {
            foreach (var diagnostic in ValidateComponent(component, schema, resources))
                yield return diagnostic;
        }
    }

    /// <summary>
    /// Every field name the item ends up with after inheritance, or null when some ancestor
    /// can't be resolved (its own missing-parent error covers that) or the chain loops.
    /// </summary>
    private static HashSet<string>? MergedFieldNames(PrototypeLookup lookup, PrototypeItem item)
    {
        var result = item.SetFieldNames.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (item.IdValue is { Length: > 0 } selfId)
            visited.Add(selfId);

        var pending = new Stack<string>(item.Parents.Select(p => p.Id));
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (!visited.Add(id))
                continue;

            if (lookup.Find(item.TypeValue!, id) is not { } entry)
                return null;

            result.UnionWith(entry.SetFieldNames);
            foreach (var parent in entry.Parents)
                pending.Push(parent);
        }

        return result;
    }

    // Whether following parents up from `fromId` arrives back at `targetId`.
    private static bool Reaches(PrototypeLookup lookup, string typeKey, string fromId, string targetId)
    {
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>([fromId]);
        while (pending.Count > 0)
        {
            var id = pending.Pop();
            if (string.Equals(id, targetId, StringComparison.OrdinalIgnoreCase))
                return true;

            if (!visited.Add(id) || lookup.Find(typeKey, id) is not { } entry)
                continue;

            foreach (var parent in entry.Parents)
                pending.Push(parent);
        }

        return false;
    }

    private static IEnumerable<Diagnostic> ValidateComponent(ComponentItem component, EngineSchema schema, ResourceIndex? resources)
    {
        if (component.TypeValue is null || component.TypeValueRange is null)
        {
            yield return Error(component.ItemRange, "unknown-component", "Component entry is missing a 'type:' key.");
            yield break;
        }

        var resolution = schema.ResolveComponent(component.TypeValue);
        if (resolution is null)
        {
            yield return Error(component.TypeValueRange, "unknown-component", $"Unknown component type '{component.TypeValue}'.");
            yield break;
        }

        if (resolution.MatchedByClassName)
        {
            // Valid (the engine's loader falls back to the raw class name - see
            // EngineSchema.ComponentsByClassName), but not the convention - nudge toward the
            // registered name without blocking the user.
            yield return Warning(
                component.TypeValueRange,
                "comp-ref-by-class-name",
                $"'{component.TypeValue}' is the component class name. Consider using the registered name '{resolution.Component.Name}' instead.");
        }

        var comp = resolution.Component;

        foreach (var field in component.Fields)
        {
            var member = comp.Members.FirstOrDefault(m => string.Equals(m.Name, field.Name, StringComparison.OrdinalIgnoreCase));
            if (member is null)
            {
                yield return Error(
                    field.KeyRange,
                    "unknown-comp-field",
                    $"Component '{component.TypeValue}' does not contain field/property '{field.Name}'.");
                continue;
            }

            foreach (var diagnostic in ValidateValue(field, member.Asset, schema, resources))
                yield return diagnostic;
        }
    }

    /// <summary>
    /// <c>type: style</c> rules. The engine checks none of this on load and ignores what it can't
    /// use (StyleRule/StyleClass are filled by DataFieldConverter.ApplyFields, which skips unknown
    /// keys and doesn't enforce nested required fields; Control.TryGetStyleProperty only looks up
    /// exact, case-sensitive names), so mistakes are Warnings - except a rule with properties but
    /// no class, which throws a NullReferenceException in Control.BestRuleIn the first time it's checked.
    /// </summary>
    private static IEnumerable<Diagnostic> ValidateStyleRules(PrototypeItem item, EngineSchema schema)
    {
        var knowsControls = schema.UiControls.Count > 0;

        foreach (var rule in item.StyleRules)
        {
            if (rule.Fields.Count == 0)
                continue;

            var anchor = rule.Fields[0].KeyRange;

            foreach (var field in rule.Fields.Where(f => !EngineSchema.StyleRuleKeys.Contains(f.Name)))
            {
                yield return Warning(field.KeyRange, "unknown-style-field",
                    $"Unknown style rule key '{field.Name}', it is ignored. Expected 'class' or 'properties'.");
            }

            if (rule.ClassField is null)
            {
                if (rule.Properties.Count > 0)
                {
                    yield return Error(anchor, "style-rule-no-class",
                        "Style rule has no 'class'. The engine doesn't check this on load, but throws a NullReferenceException the first time it matches this rule.");
                }
            }
            else
            {
                foreach (var entry in rule.Selector.Where(e => !EngineSchema.StyleSelectorKeys.Contains(e.Name)))
                {
                    yield return Warning(entry.KeyRange, "unknown-style-selector",
                        $"Unknown selector key '{entry.Name}', it is ignored. Expected one of: {string.Join(", ", EngineSchema.StyleSelectorKeys)}.");
                }
            }

            if (rule.PropertiesField is null)
                yield return Warning(anchor, "style-rule-no-properties", "Style rule has no 'properties', so it never applies.");

            if (!knowsControls)
                continue;

            var control = rule.Control;
            // An empty "control: " is a value still being typed, not worth a warning yet.
            if (control is { Length: > 0 } && !schema.MatchStyleControl(control).Any())
            {
                var controlRange = rule.Selector.First(e => e.Name == "control").ValueRange;
                yield return Warning(controlRange, "unknown-style-control",
                    $"No UI control or alias named '{control}', so this rule never matches.");
                continue;
            }

            var allowed = schema.ResolveStyleProperties(control);
            foreach (var property in rule.Properties)
            {
                if (allowed.Any(p => p.Name == property.Name))
                    continue;

                var target = control is null ? "any control" : $"'{control}'";
                var caseMatch = allowed.FirstOrDefault(p => string.Equals(p.Name, property.Name, StringComparison.OrdinalIgnoreCase));
                var message = caseMatch is not null
                    ? $"Style property names are case-sensitive: '{property.Name}' is ignored, did you mean '{caseMatch.Name}'?"
                    : $"'{property.Name}' is not a style property of {target}, it is ignored.";

                yield return Warning(property.KeyRange, "unknown-style-property", message);
            }
        }
    }

    private static IEnumerable<Diagnostic> ValidateValue(FieldSpan field, AssetClassification? asset, EngineSchema schema, ResourceIndex? resources)
    {
        if (asset is null)
            return [];

        var sound = asset.Kind == AssetKind.Sound && field.MappingKeys is not null ? ValidateSoundMap(field) : [];
        return resources is null ? sound : sound.Concat(ValidateAssetFiles(field, asset, schema, resources));
    }

    /// <summary>
    /// An asset value naming a file that isn't in any resource folder. Only for fields known for
    /// sure to hold one (HighConfidence: a marker attribute or dedicated type, not a name guess),
    /// and only once that kind of file was found at all. A Warning: nothing fails on load (a
    /// missing texture, for one, logs "Sprite ... does not exist!" and draws a placeholder).
    /// </summary>
    private static IEnumerable<Diagnostic> ValidateAssetFiles(FieldSpan field, AssetClassification asset, EngineSchema schema, ResourceIndex resources)
    {
        if (!asset.HighConfidence)
            yield break;

        var kind = asset.Kind;
        var values = field.Scalars;

        switch (kind)
        {
            // Engine 0.5+: a shader id, checked as a prototype reference instead.
            case AssetKind.Shader when schema.TypeKeyOf(EngineSchema.ShaderPrototypeFqn) is not null:
                yield break;

            // A sound is a bare audio key, or a map whose "path:" is one.
            case AssetKind.Sound:
                kind = AssetKind.Audio;
                if (field.Entries is { } entries)
                {
                    values = entries
                        .Where(e => string.Equals(e.Name, "path", StringComparison.OrdinalIgnoreCase))
                        .SelectMany(e => e.Scalars)
                        .ToList();
                }
                break;
        }

        // AssetManager.GetSprite also takes a bare animation key and shows its first frame.
        var keys = kind == AssetKind.Sprite
            ? resources.SpriteKeys.Concat(resources.AnimationKeys).ToList()
            : resources.Get(kind);

        if (keys.Count == 0)
            yield break;

        foreach (var (value, range) in values)
        {
            // Fonts are looked up ignoring case (FontManager); the atlas, animations and the audio
            // manifest are plain dictionaries, so there a wrong case is as missing as a wrong name.
            if (kind == AssetKind.Font ? IsKnownFont(resources, value) : keys.Contains(value, StringComparer.Ordinal))
                continue;

            if (kind == AssetKind.Sprite && IsFrameOfOpenEndedAnimation(resources, value))
                continue;

            var caseMatch = kind == AssetKind.Font ? null : keys.FirstOrDefault(k => string.Equals(k, value, StringComparison.OrdinalIgnoreCase));
            var message = caseMatch is not null
                ? $"Asset names are case-sensitive: no {AssetName(kind)} '{value}', did you mean '{caseMatch}'?"
                : $"No {AssetName(kind)} '{value}' in any {string.Join("/", asset.ResourceRoots)} folder.";

            yield return Warning(range, "missing-asset", message);
        }
    }

    private static string AssetName(AssetKind kind) => kind switch
    {
        AssetKind.Sprite => "texture",
        AssetKind.Animation => "animation",
        AssetKind.Shader or AssetKind.ShaderFile => "shader file",
        AssetKind.Audio or AssetKind.Sound => "audio file",
        AssetKind.Font => "font file",
        _ => "file",
    };

    // "walk-3" of a spritesheet animation "walk" whose frame count the indexer couldn't determine.
    private static bool IsFrameOfOpenEndedAnimation(ResourceIndex resources, string value)
    {
        var dash = value.LastIndexOf('-');
        if (dash <= 0 || dash == value.Length - 1 || value.AsSpan(dash + 1).ContainsAnyExceptInRange('0', '9'))
            return false;

        var animKey = value[..dash];
        return resources.OpenEndedAnimationKeys.Contains(animKey, StringComparer.Ordinal);
    }

    // FontManager: a key relative to a Fonts folder, or else just the file name of a .ttf.
    private static bool IsKnownFont(ResourceIndex resources, string value) =>
        resources.FontKeys.Any(k =>
            string.Equals(k, value, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(k[(k.LastIndexOf('/') + 1)..], value, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Map-form SoundSpecifier ("{ path: ... }" / "{ collection: ... }"). The two Errors mirror the
    /// PrototypeLoadExceptions thrown by SoundSpecifier.FromYaml; an unknown key is only a Warning,
    /// since FromYaml silently ignores it (so a typo like "colection" just loses the value).
    /// </summary>
    private static IEnumerable<Diagnostic> ValidateSoundMap(FieldSpan field)
    {
        if (field.MappingKeys is not { } keys)
            yield break;

        bool Has(string name) => keys.Any(k => string.Equals(k.Key, name, StringComparison.OrdinalIgnoreCase));

        var hasPath = Has("path");
        var hasCollection = Has("collection");

        if (!hasPath && !hasCollection)
            yield return Error(field.KeyRange, "invalid-sound", "A sound needs either 'path' or 'collection'.");
        else if (hasPath && hasCollection)
            yield return Error(field.KeyRange, "invalid-sound", "A sound takes 'path' or 'collection', not both.");

        foreach (var (key, range) in keys)
        {
            if (!CompletionHandler.SoundMapKeys.Contains(key, StringComparer.OrdinalIgnoreCase))
                yield return Warning(range, "unknown-sound-field", $"Unknown sound key '{key}', expected one of: {string.Join(", ", CompletionHandler.SoundMapKeys)}. It is ignored.");
        }
    }

    private static Diagnostic Error(LspRange range, string code, string message) => Build(range, DiagnosticSeverity.Error, code, message);

    private static Diagnostic Warning(LspRange range, string code, string message) => Build(range, DiagnosticSeverity.Warning, code, message);

    private static Diagnostic Build(LspRange range, DiagnosticSeverity severity, string code, string message) => new()
    {
        Range = range,
        Severity = severity,
        Source = "oirf-engine",
        Code = new DiagnosticCode(code),
        Message = message,
    };
}
