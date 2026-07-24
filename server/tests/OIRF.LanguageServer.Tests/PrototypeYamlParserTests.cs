using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using OIRF.LanguageServer.Yaml;

namespace OIRF.LanguageServer.Tests;

public class PrototypeYamlParserTests
{
    // Built with explicit "\n" (not a raw string literal) so every line/column used in
    // assertions below is unambiguous and doesn't depend on indentation-stripping rules.
    // Line 0: "- type: entity"
    // Line 1: "  id: MobPlayer"
    // Line 2: "  components:"
    // Line 3: "  - type: Transform"
    // Line 4: "  - type: Physics"
    // Line 5: "    friction: 15"
    private const string Sample =
        "- type: entity\n" +
        "  id: MobPlayer\n" +
        "  components:\n" +
        "  - type: Transform\n" +
        "  - type: Physics\n" +
        "    friction: 15\n";

    [Fact]
    public void Parses_top_level_fields_and_components()
    {
        var document = PrototypeYamlParser.Parse(Sample);

        Assert.False(document.HasParseError);
        var item = Assert.Single(document.Items);
        Assert.Equal("entity", item.TypeValue);
        Assert.Equal("MobPlayer", item.IdValue);
        Assert.Equal(2, item.Components.Count);
        Assert.Equal("Transform", item.Components[0].TypeValue);
        Assert.Equal("Physics", item.Components[1].TypeValue);

        var frictionField = Assert.Single(item.Components[1].Fields);
        Assert.Equal("friction", frictionField.Name);
        Assert.Equal("15", frictionField.ScalarValue);
    }

    [Fact]
    public void Item_range_spans_the_whole_entry_not_just_its_start()
    {
        // Regression test: YamlDotNet reports a degenerate (Start == End) mark for block
        // mappings/sequences, since there's no explicit closing token. PrototypeYamlParser must
        // recompute a true end from the last contained leaf, or every multi-line item's range
        // would collapse to a single point and every context resolution beyond line 0 would fail.
        var document = PrototypeYamlParser.Parse(Sample);
        var item = Assert.Single(document.Items);

        Assert.True(item.ItemRange.End.Line > item.ItemRange.Start.Line);

        var physics = item.Components[1];
        Assert.True(physics.ItemRange.End.Line > physics.ItemRange.Start.Line);
    }

    [Fact]
    public void Resolves_prototype_type_value_context()
    {
        var document = PrototypeYamlParser.Parse(Sample);

        var context = NodeContextResolver.Resolve(document, new LspPosition(0, 10));

        var typeContext = Assert.IsType<NodeContext.PrototypeTypeValue>(context);
        Assert.Equal("entity", typeContext.CurrentValue);
    }

    [Fact]
    public void Resolves_component_type_value_context()
    {
        var document = PrototypeYamlParser.Parse(Sample);

        var context = NodeContextResolver.Resolve(document, new LspPosition(4, 12));

        var componentContext = Assert.IsType<NodeContext.ComponentTypeValue>(context);
        Assert.Equal("entity", componentContext.PrototypeTypeKey);
        Assert.Equal("Physics", componentContext.CurrentValue);
    }

    [Theory]
    [InlineData(5, 6)]  // hovering the "friction" key itself
    [InlineData(5, 16)] // hovering the "15" value
    public void Resolves_component_field_value_context_for_both_key_and_value_hover(int line, int character)
    {
        var document = PrototypeYamlParser.Parse(Sample);

        var context = NodeContextResolver.Resolve(document, new LspPosition(line, character));

        var fieldContext = Assert.IsType<NodeContext.ComponentFieldValue>(context);
        Assert.Equal("Physics", fieldContext.ComponentName);
        Assert.Equal("friction", fieldContext.FieldName);
    }

    [Fact]
    public void Reports_syntax_error_for_malformed_yaml()
    {
        var document = PrototypeYamlParser.Parse("- type: entity\n  id: [unterminated\n");

        Assert.True(document.HasParseError);
        Assert.Empty(document.Items);
    }

    [Fact]
    public void Reports_error_when_root_is_not_a_sequence()
    {
        var document = PrototypeYamlParser.Parse("type: entity\nid: MobPlayer\n");

        Assert.True(document.HasParseError);
    }
}
