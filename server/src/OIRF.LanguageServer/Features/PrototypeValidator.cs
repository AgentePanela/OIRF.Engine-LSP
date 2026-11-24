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
    /// Workspace-wide ids, for the missing-parent check. Null (or not built yet) skips that check
    /// rather than flagging every parent defined in another file.
    /// </param>
    public static IEnumerable<Diagnostic> Validate(string text, EngineSchema schema, PrototypeIdIndex? prototypeIds = null)
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

        foreach (var item in document.Items)
        {
            foreach (var diagnostic in ValidateItem(item, schema, document, prototypeIds))
                yield return diagnostic;
        }
    }

    private static IEnumerable<Diagnostic> ValidateItem(PrototypeItem item, EngineSchema schema, PrototypeDocument document, PrototypeIdIndex? prototypeIds)
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

        // PrototypeManager.GetMergedFields throws for this. Parents resolve within the same
        // prototype type, case-sensitively (the raw-prototype buckets are plain dictionaries).
        if (prototypeIds is { IdsByTypeKey.Count: > 0 } && item.Parents.Count > 0)
        {
            var known = CompletionHandler.KnownIds(item.TypeValue, document, prototypeIds).ToHashSet(StringComparer.Ordinal);
            foreach (var (parentId, range) in item.Parents)
            {
                if (!known.Contains(parentId))
                    yield return Error(range, "missing-parent", $"Prototype '{item.IdValue}' references missing parent '{parentId}'.");
            }
        }

        if (schema.IsStylePrototype(item.TypeValue))
        {
            foreach (var diagnostic in ValidateStyleRules(item, schema))
                yield return diagnostic;
        }

        // M1 stopgap: prototype inheritance merges parent fields before the engine's own
        // required-field check runs, so a child validly omitting a field its parent supplies
        // must not be flagged. Full parent-chain resolution is a documented fast-follow (see
        // plan) - for now, only run this check on prototypes with no `parent:` at all.
        var hasParent = item.ParentValueRange is not null;
        if (!hasParent)
        {
            foreach (var field in proto.DataFields)
            {
                if (field.IsMeta || !field.Required)
                    continue;

                var present = item.TopLevelFields.Any(f => string.Equals(f.Name, field.YamlName, StringComparison.OrdinalIgnoreCase));
                if (!present)
                {
                    yield return Error(
                        item.ItemRange,
                        "missing-req-field",
                        $"Missing required field '{field.YamlName}' on prototype '{item.IdValue}'.");
                }
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

            foreach (var diagnostic in ValidateValue(field, dataField.Asset))
                yield return diagnostic;
        }

        foreach (var component in item.Components)
        {
            foreach (var diagnostic in ValidateComponent(component, schema))
                yield return diagnostic;
        }
    }

    private static IEnumerable<Diagnostic> ValidateComponent(ComponentItem component, EngineSchema schema)
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

            foreach (var diagnostic in ValidateValue(field, member.Asset))
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

    /// <summary>
    /// Map-form SoundSpecifier ("{ path: ... }" / "{ collection: ... }"). The two Errors mirror the
    /// PrototypeLoadExceptions thrown by SoundSpecifier.FromYaml; an unknown key is only a Warning,
    /// since FromYaml silently ignores it (so a typo like "colection" just loses the value).
    /// </summary>
    private static IEnumerable<Diagnostic> ValidateValue(FieldSpan field, AssetClassification? asset)
    {
        if (asset is not { Kind: AssetKind.Sound } || field.MappingKeys is not { } keys)
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
