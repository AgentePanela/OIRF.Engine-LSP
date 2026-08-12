using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using OIRF.LanguageServer.Assets;
using OIRF.LanguageServer.Features;
using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;

namespace OIRF.LanguageServer.Tests;

/// <summary>
/// Covers completion-context bugs reported against real usage: "parent"/"abstract" being
/// invisible in field-key completion (they were filtered out alongside "type"/"id" purely
/// because all four share the IsMeta flag), and a fresh "- " component bullet inserting a bare
/// component name instead of a valid "type: Name" entry.
/// </summary>
public class CompletionHandlerTests
{
    private static EngineSchema BuildSchema()
    {
        var dataFields = new List<DataFieldInfo>
        {
            new("type", Required: false, IsMeta: true, IsComponentsField: false, ClrTypeDisplay: "string", Signature: "string Type;", DocMarkdown: null, Asset: null, Location: null),
            new("id", Required: false, IsMeta: true, IsComponentsField: false, ClrTypeDisplay: "string", Signature: "string Id;", DocMarkdown: null, Asset: null, Location: null),
            new("parent", Required: false, IsMeta: true, IsComponentsField: false, ClrTypeDisplay: "string", Signature: "string Parent;", DocMarkdown: null, Asset: null, Location: null),
            new("abstract", Required: false, IsMeta: true, IsComponentsField: false, ClrTypeDisplay: "bool", Signature: "bool Abstract;", DocMarkdown: null, Asset: null, Location: null),
            new("weight", Required: true, IsMeta: false, IsComponentsField: false, ClrTypeDisplay: "float", Signature: "float Weight;", DocMarkdown: null, Asset: null, Location: null),
            new("primaryTag", Required: false, IsMeta: false, IsComponentsField: false, ClrTypeDisplay: "ProtoId<TagPrototype>", Signature: "ProtoId<TagPrototype> PrimaryTag;", DocMarkdown: null, Asset: null, Location: null, ValueChoices: null, ProtoId: new ProtoIdClassification("TestGame.TagPrototype")),
        };
        var prototype = new PrototypeTypeInfo("entity", 0, "TestGame.EntityPrototype", "class EntityPrototype", true, null, dataFields, null);
        var tagPrototype = new PrototypeTypeInfo("tag", 0, "TestGame.TagPrototype", "class TagPrototype", false, null, [], null);

        var tag = new ComponentTypeInfo("Tag", "TagComponent", "TestGame.TagComponent", "class TagComponent : Component", null, [], null);

        var light = new ComponentTypeInfo("PointLight", "PointLightComponent", "TestGame.PointLightComponent", "class PointLightComponent : Component", null,
            [
                new MemberInfo("falloff", "TestGame.FalloffMode", "FalloffMode Falloff;", null, null, null, ["Linear", "Quadratic", "InverseSquare"]),
                new MemberInfo("castShadows", "bool", "bool CastShadows;", null, null, null, ["true", "false"]),
            ], null);

        return new EngineSchema(
            new Dictionary<string, PrototypeTypeInfo>(StringComparer.OrdinalIgnoreCase) { ["entity"] = prototype, ["tag"] = tagPrototype },
            new Dictionary<string, ComponentTypeInfo>(StringComparer.OrdinalIgnoreCase) { ["Tag"] = tag, ["PointLight"] = light });
    }

    [Fact]
    public void Field_key_completion_offers_parent_and_abstract_but_not_type_or_id()
    {
        var text = "- type: entity\n  id: X\n  weight: 1\n  ";
        var items = CompletionHandler.Handle(text, new LspPosition(3, 2), BuildSchema(), ResourceIndex.Empty, PrototypeIdIndex.Empty).ToList();

        Assert.Contains(items, i => i.Label == "parent");
        Assert.Contains(items, i => i.Label == "abstract");
        Assert.DoesNotContain(items, i => i.Label == "type");
        Assert.DoesNotContain(items, i => i.Label == "id");
    }

    [Fact]
    public void Field_key_completion_excludes_parent_once_already_written()
    {
        var text = "- type: entity\n  id: X\n  parent: Base\n  weight: 1\n  ";
        var items = CompletionHandler.Handle(text, new LspPosition(4, 2), BuildSchema(), ResourceIndex.Empty, PrototypeIdIndex.Empty).ToList();

        Assert.DoesNotContain(items, i => i.Label == "parent");
    }

    [Fact]
    public void Component_completion_on_a_fresh_bullet_inserts_a_full_type_key()
    {
        var text = "- type: entity\n  id: X\n  weight: 1\n  components:\n  - \n";
        var items = CompletionHandler.Handle(text, new LspPosition(4, 4), BuildSchema(), ResourceIndex.Empty, PrototypeIdIndex.Empty).ToList();

        var tag = Assert.Single(items, i => i.Label == "Tag");
        Assert.Equal("type: Tag", tag.InsertText);
    }

    [Fact]
    public void Component_completion_on_an_existing_type_value_inserts_the_bare_name()
    {
        var text = "- type: entity\n  id: X\n  weight: 1\n  components:\n  - type: Ta\n";
        var items = CompletionHandler.Handle(text, new LspPosition(4, 12), BuildSchema(), ResourceIndex.Empty, PrototypeIdIndex.Empty).ToList();

        var tag = Assert.Single(items, i => i.Label == "Tag");
        Assert.Equal("Tag", tag.InsertText);
    }

    [Fact]
    public void Parent_value_completion_offers_sibling_ids_of_the_same_type_excluding_self()
    {
        var text =
            "- type: entity\n  id: Base\n  weight: 1\n" +
            "- type: entity\n  id: Child\n  weight: 1\n  parent: \n";

        var items = CompletionHandler.Handle(text, new LspPosition(6, 10), BuildSchema(), ResourceIndex.Empty, PrototypeIdIndex.Empty).ToList();

        Assert.Contains(items, i => i.Label == "Base");
        Assert.DoesNotContain(items, i => i.Label == "Child");
    }

    [Fact]
    public void Enum_typed_component_field_value_offers_its_member_names()
    {
        var text = "- type: entity\n  id: X\n  weight: 1\n  components:\n  - type: PointLight\n    falloff: \n";
        var items = CompletionHandler.Handle(text, new LspPosition(5, 13), BuildSchema(), ResourceIndex.Empty, PrototypeIdIndex.Empty).ToList();

        var labels = items.Select(i => i.Label).ToList();
        Assert.Equal(["Linear", "Quadratic", "InverseSquare"], labels);
        Assert.All(items, i => Assert.Equal(OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItemKind.EnumMember, i.Kind));
    }

    [Fact]
    public void Bool_typed_component_field_value_offers_true_and_false()
    {
        var text = "- type: entity\n  id: X\n  weight: 1\n  components:\n  - type: PointLight\n    castShadows: \n";
        var items = CompletionHandler.Handle(text, new LspPosition(5, 17), BuildSchema(), ResourceIndex.Empty, PrototypeIdIndex.Empty).ToList();

        Assert.Equal(["true", "false"], items.Select(i => i.Label));
    }

    [Fact]
    public void ProtoId_field_value_completion_offers_known_ids_of_the_referenced_prototype_type()
    {
        const string valueLine = "  primaryTag: ";
        var text = "- type: entity\n  id: X\n  weight: 1\n" + valueLine + "\n";
        var position = new LspPosition(3, valueLine.Length);

        var prototypeIds = new PrototypeIdIndex
        {
            IdsByTypeKey = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
            {
                ["tag"] = ["Friendly", "Hostile"],
            },
        };

        var items = CompletionHandler.Handle(text, position, BuildSchema(), ResourceIndex.Empty, prototypeIds).ToList();

        Assert.Equal(["Friendly", "Hostile"], items.Select(i => i.Label));
        Assert.All(items, i => Assert.Equal(OmniSharp.Extensions.LanguageServer.Protocol.Models.CompletionItemKind.Reference, i.Kind));
    }

    [Fact]
    public void ProtoId_field_with_no_matching_prototype_type_registered_offers_nothing()
    {
        // Defensive: the referenced T never got a [Prototype] registration in this workspace
        // (e.g. it's abstract, or its file failed to compile) - completion must degrade to
        // empty, not throw.
        const string valueLine = "  primaryTag: ";
        var text = "- type: entity\n  id: X\n  weight: 1\n" + valueLine + "\n";
        var position = new LspPosition(3, valueLine.Length);

        var schemaWithoutTagType = new EngineSchema(
            new Dictionary<string, PrototypeTypeInfo>(StringComparer.OrdinalIgnoreCase)
            {
                ["entity"] = BuildSchema().PrototypesByTypeKey["entity"],
            },
            new Dictionary<string, ComponentTypeInfo>(StringComparer.OrdinalIgnoreCase));

        var items = CompletionHandler.Handle(text, position, schemaWithoutTagType, ResourceIndex.Empty, PrototypeIdIndex.Empty).ToList();

        Assert.Empty(items);
    }
}
