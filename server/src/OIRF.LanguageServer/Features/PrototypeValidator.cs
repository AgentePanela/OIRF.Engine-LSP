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
    public static IEnumerable<Diagnostic> Validate(string text, EngineSchema schema)
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
            foreach (var diagnostic in ValidateItem(item, schema))
                yield return diagnostic;
        }
    }

    private static IEnumerable<Diagnostic> ValidateItem(PrototypeItem item, EngineSchema schema)
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
            var known = proto.DataFields.Any(f => string.Equals(f.YamlName, field.Name, StringComparison.OrdinalIgnoreCase));
            if (!known)
                yield return Warning(field.KeyRange, "unknown-proto-field", $"Unknown field '{field.Name}' on prototype '{item.IdValue}'.");
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
            var known = comp.Members.Any(m => string.Equals(m.Name, field.Name, StringComparison.OrdinalIgnoreCase));
            if (!known)
            {
                yield return Error(
                    field.KeyRange,
                    "unknown-comp-field",
                    $"Component '{component.TypeValue}' does not contain field/property '{field.Name}'.");
            }
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
