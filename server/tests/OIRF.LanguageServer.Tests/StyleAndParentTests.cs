using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using OIRF.LanguageServer.Assets;
using OIRF.LanguageServer.Features;
using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Tests;

/// <summary>
/// <c>type: style</c> rules (StyleRule/StyleClass, Control [StyleField]s) and cross-file
/// <c>parent:</c> references. The style checks mirror what the engine silently ignores, so they
/// are Warnings; see PrototypeValidator.ValidateStyleRules.
/// </summary>
public class StyleAndParentTests
{
    private static StylePropertyInfo Prop(string name, IReadOnlyList<string>? choices = null) =>
        new(name, "float", $"float? _{name};", null, null, null, choices, null, null);

    private static EngineSchema BuildSchema()
    {
        var styleFields = new List<DataFieldInfo>
        {
            new("id", false, true, false, "string", "", null, null, null),
            new("parent", false, true, false, "string[]", "", null, null, null),
            new("rules", false, false, false, "List<StyleRule>", "", null, null, null),
        };

        var prototypes = new Dictionary<string, PrototypeTypeInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["style"] = new("style", 0, EngineSchema.StylePrototypeFqn, "", true, null, styleFields, null),
        };

        var controls = new Dictionary<string, UiControlInfo>
        {
            ["UI.Control"] = new("Control", "UI.Control", null, [], [Prop("width")], "", null, null),
            ["UI.Label"] = new("Label", "UI.Label", "UI.Control", ["button"], [Prop("fontSize"), Prop("textTransform", ["None", "Uppercase"])], "", null, null),
            ["UI.Button"] = new("Button", "UI.Button", "UI.Control", ["button"], [Prop("padding")], "", null, null),
        };

        return new EngineSchema(prototypes, new Dictionary<string, ComponentTypeInfo>())
        {
            UiControls = controls,
            PseudoClasses = ["hover", "pressed"],
        };
    }

    private static readonly PrototypeIdIndex Ids = new()
    {
        IdsByTypeKey = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["style"] = ["EngineDefault", "OtherFileStyle"],
        },
    };

    private static List<string> Complete(string text, int line, string linePrefix) =>
        CompletionHandler.Handle(text, new LspPosition(line, linePrefix.Length), BuildSchema(), ResourceIndex.Empty, Ids)
            .Select(i => i.Label).ToList();

    private static List<Diagnostic> Validate(string text, PrototypeIdIndex? ids = null) =>
        PrototypeValidator.Validate(text, BuildSchema(), ids ?? Ids).ToList();

    private const string Head = "- type: style\n  id: Mine\n  rules:\n";

    [Fact]
    public void Control_selector_offers_controls_and_aliases()
    {
        const string line = "    - class: { control: ";
        var labels = Complete(Head + line + "}\n      properties: {}\n", 3, line);

        Assert.Contains("Label", labels);
        Assert.Contains("Control", labels);
        // "button" and "Button" are the same selector (matched case-insensitively): one entry
        Assert.Single(labels, l => string.Equals(l, "button", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Pseudo_class_selector_offers_known_pseudo_classes()
    {
        const string line = "    - class: { control: Label, pseudoClass: ";
        Assert.Equal(["hover", "pressed"], Complete(Head + line + "}\n      properties: {}\n", 3, line));
    }

    [Fact]
    public void Block_style_selector_offers_controls()
    {
        const string line = "        control: ";
        var text = Head + "    - class:\n" + line + "x\n      properties: {}\n";
        Assert.Contains("Label", Complete(text, 4, line));
    }

    [Fact]
    public void Property_keys_are_the_controls_own_and_inherited_style_fields_minus_written_ones()
    {
        var text = Head + "    - class: { control: Label }\n      properties:\n        fontSize: 1\n        \n";
        Assert.Equal(["textTransform", "width"], Complete(text, 6, "        ").Order());
    }

    [Fact]
    public void Alias_property_keys_cover_every_control_carrying_the_alias()
    {
        var text = Head + "    - class: { control: button }\n      properties:\n        \n";
        Assert.Equal(["fontSize", "padding", "textTransform", "width"], Complete(text, 5, "        ").Order());
    }

    [Fact]
    public void Property_value_offers_its_choices()
    {
        const string line = "        textTransform: ";
        var text = Head + "    - class: { control: Label }\n      properties:\n" + line + "\n";
        Assert.Equal(["None", "Uppercase"], Complete(text, 5, line));
    }

    [Fact]
    public void Rule_level_key_offers_class_and_properties()
    {
        var text = Head + "    - class: { control: Label }\n      \n";
        Assert.Equal(["properties"], Complete(text, 4, "      "));
    }

    [Fact]
    public void Valid_rules_have_no_diagnostics()
    {
        var text = Head +
            "    - class: { control: button, pseudoClass: hover }\n      properties:\n        fontSize: 2\n        padding: 1\n" +
            "    - class: { styleClass: \"*\" }\n      properties: { width: 3 }\n";
        Assert.Empty(Validate(text));
    }

    [Fact]
    public void Unknown_property_is_a_warning()
    {
        var diagnostic = Assert.Single(Validate(Head + "    - class: { control: Label }\n      properties: { padding: 1 }\n"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("unknown-style-property", diagnostic.Code!.Value.String);
    }

    [Fact]
    public void Wrong_case_property_suggests_the_real_name()
    {
        var diagnostic = Assert.Single(Validate(Head + "    - class: { control: Label }\n      properties: { FontSize: 1 }\n"));
        Assert.Contains("did you mean 'fontSize'", diagnostic.Message);
    }

    [Fact]
    public void Unknown_control_is_a_warning()
    {
        var diagnostic = Assert.Single(Validate(Head + "    - class: { control: Nope }\n      properties: { width: 1 }\n"));
        Assert.Equal("unknown-style-control", diagnostic.Code!.Value.String);
    }

    [Fact]
    public void Rule_without_class_is_an_error()
    {
        var diagnostic = Assert.Single(Validate(Head + "    - properties: { width: 1 }\n"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("style-rule-no-class", diagnostic.Code!.Value.String);
    }

    [Fact]
    public void Rule_without_properties_and_unknown_keys_are_warnings()
    {
        var codes = Validate(Head + "    - class: { control: Label, pseudo: hover }\n").Select(d => d.Code!.Value.String).Order();
        Assert.Equal(["style-rule-no-properties", "unknown-style-selector"], codes);
    }

    [Fact]
    public void Parent_completion_offers_ids_from_other_files_and_this_one_minus_listed_ones()
    {
        const string line = "  parent: [EngineDefault, ";
        var text = "- type: style\n  id: Local\n- type: style\n  id: Mine\n" + line + "]\n";
        Assert.Equal(["OtherFileStyle", "Local"], Complete(text, 4, line));
    }

    [Fact]
    public void Parent_completion_works_on_a_wrapped_flow_list_line()
    {
        var text = "- type: style\n  id: Mine\n  parent: [EngineDefault,\n    ]\n";
        Assert.Equal(["OtherFileStyle"], Complete(text, 3, "    "));
    }

    [Fact]
    public void Missing_parent_is_an_error()
    {
        var diagnostic = Assert.Single(Validate("- type: style\n  id: Mine\n  parent: [EngineDefault, Gone]\n"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("missing-parent", diagnostic.Code!.Value.String);
        Assert.Contains("'Gone'", diagnostic.Message);
    }

    [Fact]
    public void Missing_parent_is_skipped_while_the_id_index_is_empty()
    {
        Assert.Empty(Validate("- type: style\n  id: Mine\n  parent: Gone\n", PrototypeIdIndex.Empty));
    }
}
