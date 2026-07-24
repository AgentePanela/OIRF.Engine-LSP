using OIRF.LanguageServer.Features;
using OIRF.LanguageServer.Schema;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Tests;

/// <summary>
/// Diagnostic severities here must mirror the engine's own PrototypeManager/DataFieldConverter
/// exactly (verified against source, see docs/ARCHITECTURE.md): unknown prototype field is a
/// Warning, everything else is an Error.
/// </summary>
public class PrototypeValidatorTests
{
    private static EngineSchema BuildSchema()
    {
        var dataFields = new List<DataFieldInfo>
        {
            new("type", Required: false, IsMeta: true, IsComponentsField: false, ClrTypeDisplay: "string", DocMarkdown: null, Asset: null),
            new("id", Required: false, IsMeta: true, IsComponentsField: false, ClrTypeDisplay: "string", DocMarkdown: null, Asset: null),
            new("parent", Required: false, IsMeta: true, IsComponentsField: false, ClrTypeDisplay: "string", DocMarkdown: null, Asset: null),
            new("weight", Required: true, IsMeta: false, IsComponentsField: false, ClrTypeDisplay: "float", DocMarkdown: null, Asset: null),
            new("components", Required: false, IsMeta: false, IsComponentsField: true, ClrTypeDisplay: "List<ComponentEntry>", DocMarkdown: null, Asset: null),
        };
        var prototype = new PrototypeTypeInfo("entity", 0, "TestGame.EntityPrototype", true, null, dataFields);

        var physics = new ComponentTypeInfo("Physics", "TestGame.PhysicsComponent", null,
            [new MemberInfo("Friction", "float", null, null)]);

        return new EngineSchema(
            new Dictionary<string, PrototypeTypeInfo>(StringComparer.OrdinalIgnoreCase) { ["entity"] = prototype },
            new Dictionary<string, ComponentTypeInfo>(StringComparer.OrdinalIgnoreCase) { ["Physics"] = physics });
    }

    [Fact]
    public void Unknown_prototype_type_is_error()
    {
        var diagnostics = PrototypeValidator.Validate("- type: notreal\n  id: X\n  weight: 1\n", BuildSchema()).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Unknown prototype type", diagnostic.Message);
    }

    [Fact]
    public void Missing_required_field_without_parent_is_error()
    {
        var diagnostics = PrototypeValidator.Validate("- type: entity\n  id: X\n", BuildSchema()).ToList();

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("Missing required field 'weight'"));
    }

    [Fact]
    public void Missing_required_field_is_not_flagged_when_prototype_declares_a_parent()
    {
        // M1 stopgap: inheritance merges parent fields before the real required-field check
        // would run, so a child validly omitting an inherited field must not be flagged.
        var diagnostics = PrototypeValidator.Validate("- type: entity\n  id: X\n  parent: Base\n", BuildSchema()).ToList();

        Assert.DoesNotContain(diagnostics, d => d.Message.Contains("Missing required field"));
    }

    [Fact]
    public void Unknown_top_level_field_is_warning_not_error()
    {
        var diagnostics = PrototypeValidator
            .Validate("- type: entity\n  id: X\n  weight: 1\n  bogus: true\n", BuildSchema())
            .ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Contains("Unknown field 'bogus'", diagnostic.Message);
    }

    [Fact]
    public void Unknown_component_type_is_error()
    {
        var text = "- type: entity\n  id: X\n  weight: 1\n  components:\n  - type: NotReal\n";
        var diagnostics = PrototypeValidator.Validate(text, BuildSchema()).ToList();

        Assert.Contains(diagnostics, d =>
            d.Severity == DiagnosticSeverity.Error && d.Message.Contains("Unknown component type"));
    }

    [Fact]
    public void Unknown_component_field_is_error_not_warning()
    {
        // Confirms the severity asymmetry verified against DataFieldConverter.ApplyByName:
        // unlike unknown prototype fields (Warning), unknown component fields are an Error.
        var text = "- type: entity\n  id: X\n  weight: 1\n  components:\n  - type: Physics\n    frictoin: 1\n";
        var diagnostics = PrototypeValidator.Validate(text, BuildSchema()).ToList();

        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("does not contain field/property 'frictoin'", diagnostic.Message);
    }

    [Fact]
    public void Well_formed_prototype_produces_no_diagnostics()
    {
        var text = "- type: entity\n  id: X\n  weight: 1\n  components:\n  - type: Physics\n    friction: 15\n";
        var diagnostics = PrototypeValidator.Validate(text, BuildSchema()).ToList();

        Assert.Empty(diagnostics);
    }
}
