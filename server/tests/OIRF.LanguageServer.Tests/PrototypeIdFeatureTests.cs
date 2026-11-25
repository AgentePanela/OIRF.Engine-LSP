using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using LspRange = OmniSharp.Extensions.LanguageServer.Protocol.Models.Range;
using OIRF.LanguageServer.Assets;
using OIRF.LanguageServer.Features;
using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Tests;

/// <summary>
/// Prototype ids across files: go-to-definition, hover, inherited required fields, inheritance
/// cycles, unknown ids, missing assets, find references, rename and symbol search.
/// </summary>
public class PrototypeIdFeatureTests
{
    private static readonly string OtherFile = Path.GetFullPath("other/Prototypes/base.yml");
    private static readonly string ThisFile = Path.GetFullPath("this/Prototypes/mine.yml");
    private static readonly DocumentUri ThisUri = DocumentUri.FromFileSystemPath(ThisFile);
    private static readonly DocumentUri OtherUri = DocumentUri.FromFileSystemPath(OtherFile);

    private static EngineSchema BuildSchema()
    {
        var entityFields = new List<DataFieldInfo>
        {
            new("type", true, true, false, "string", "", null, null, null),
            new("id", true, true, false, "string", "", null, null, null),
            new("parent", false, true, false, "string[]", "", null, null, null),
            new("abstract", false, true, false, "bool", "", null, null, null),
            new("name", true, false, false, "string", "", null, null, null),
            new("icon", false, false, false, "string", "", null,
                new AssetClassification(AssetKind.Sprite, ["Textures"], HighConfidence: true), null),
            new("guess", false, false, false, "string", "", null,
                new AssetClassification(AssetKind.Sprite, ["Textures"], HighConfidence: false), null),
            new("tags", false, false, false, "List<ProtoId<TagPrototype>>", "", null, null, null,
                ProtoId: new ProtoIdClassification("Game.TagPrototype")),
        };

        var prototypes = new Dictionary<string, PrototypeTypeInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["entity"] = new("entity", 0, "Game.EntityPrototype", "", true, null, entityFields, null),
            ["tag"] = new("tag", 0, "Game.TagPrototype", "", false, null,
                [new("id", true, true, false, "string", "", null, null, null)], null),
        };

        var components = new Dictionary<string, ComponentTypeInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["Tagged"] = new("Tagged", "TaggedComponent", "Game.TaggedComponent", "", null,
                [new MemberInfo("Main", "ProtoId<TagPrototype>", "", null, null, null, ProtoId: new ProtoIdClassification("Game.TagPrototype"))], null),
        };

        return new EngineSchema(prototypes, components);
    }

    private static LspRange R(int line, int start, int end) => new(new LspPosition(line, start), new LspPosition(line, end));

    // base.yml, as saved on disk.
    private const string OtherText =
        "- type: entity\n  id: Base\n  name: The base\n" +
        "- type: entity\n  id: Nameless\n  abstract: true\n" +
        "- type: tag\n  id: Shiny\n";

    private static PrototypeIdIndex BuildIndex(params (string Path, string Text)[] files)
    {
        var definitions = new List<PrototypeDefinition>();
        foreach (var (path, text) in files)
        {
            foreach (var item in PrototypeYamlParser.Parse(text).Items)
            {
                definitions.Add(new PrototypeDefinition(
                    item.TypeValue!, item.IdValue!, path, item.IdValueRange!,
                    item.Parents.Select(p => p.Id).ToList(), item.SetFieldNames.ToList()));
            }
        }

        return new PrototypeIdIndex
        {
            IdsByTypeKey = definitions
                .GroupBy(d => d.TypeKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, IReadOnlyList<string> (g) => g.Select(d => d.Id).ToList(), StringComparer.OrdinalIgnoreCase),
            Definitions = definitions,
            Files = files.Select(f => f.Path).ToList(),
        };
    }

    private static readonly PrototypeIdIndex Index = BuildIndex((OtherFile, OtherText));

    private static readonly ResourceIndex Resources = new()
    {
        SpriteKeys = ["Items/coin"],
        AnimationKeys = [],
        ShaderKeys = [],
    };

    private static List<Diagnostic> Validate(string text, PrototypeIdIndex? ids = null, ResourceIndex? resources = null) =>
        PrototypeValidator.Validate(text, BuildSchema(), ids ?? Index, resources ?? Resources, ThisFile).ToList();

    private static List<string> Codes(string text) => Validate(text).Select(d => d.Code!.Value.String).ToList();

    // ── go-to-definition / hover ───────────────────────────────────────────

    [Fact]
    public void Definition_of_a_parent_opens_the_other_file()
    {
        const string text = "- type: entity\n  id: Mine\n  parent: Base\n";
        var location = DefinitionHandler.Handle(text, new LspPosition(2, 11), BuildSchema(), Index, ThisUri)!.Single().Location!;

        Assert.Equal(OtherUri, location.Uri);
        Assert.Equal(R(1, 6, 10), location.Range);
    }

    [Fact]
    public void Definition_of_a_proto_id_field_finds_an_id_in_this_file_first()
    {
        const string text = "- type: tag\n  id: Local\n- type: entity\n  id: Mine\n  name: x\n  tags: [Shiny, local]\n";
        var location = DefinitionHandler.Handle(text, new LspPosition(5, 17), BuildSchema(), Index, ThisUri)!.Single().Location!;

        Assert.Equal(ThisUri, location.Uri);
        Assert.Equal(R(1, 6, 11), location.Range);
    }

    [Fact]
    public void Definition_of_a_component_field_proto_id()
    {
        const string text = "- type: entity\n  id: Mine\n  name: x\n  components:\n  - type: Tagged\n    main: Shiny\n";
        var location = DefinitionHandler.Handle(text, new LspPosition(5, 12), BuildSchema(), Index, ThisUri)!.Single().Location!;

        Assert.Equal(OtherUri, location.Uri);
    }

    [Fact]
    public void Hover_on_a_parent_shows_where_it_is_defined_and_what_it_sets()
    {
        const string text = "- type: entity\n  id: Mine\n  parent: Base\n";
        var hover = HoverHandler.Handle(text, new LspPosition(2, 11), BuildSchema(), Index, ThisFile)!;
        var markdown = hover.Contents.MarkupContent!.Value;

        Assert.Contains("id: Base", markdown);
        Assert.Contains("`name`", markdown);
        Assert.Contains("base.yml:2", markdown);
    }

    // ── inheritance ────────────────────────────────────────────────────────

    [Fact]
    public void Required_field_set_by_a_parent_in_another_file_is_not_missing()
    {
        Assert.Empty(Validate("- type: entity\n  id: Mine\n  parent: Base\n"));
    }

    [Fact]
    public void Required_field_set_by_a_grandparent_in_this_file_is_not_missing()
    {
        Assert.Empty(Validate("- type: entity\n  id: Mid\n  parent: Base\n- type: entity\n  id: Mine\n  parent: Mid\n"));
    }

    [Fact]
    public void Required_field_missing_from_the_whole_chain_is_an_error()
    {
        var diagnostic = Assert.Single(Validate("- type: entity\n  id: Mine\n  parent: Nameless\n"));
        Assert.Equal("missing-req-field", diagnostic.Code!.Value.String);
        Assert.Contains("not set here or by any parent", diagnostic.Message);
    }

    [Fact]
    public void Required_check_is_skipped_when_a_parent_is_missing()
    {
        Assert.Equal(["missing-parent"], Codes("- type: entity\n  id: Mine\n  parent: [Base, Gone]\n"));
    }

    [Fact]
    public void Parents_match_ids_ignoring_case_like_the_engine()
    {
        Assert.Empty(Validate("- type: entity\n  id: Mine\n  parent: base\n"));
    }

    [Fact]
    public void Inheritance_cycle_is_an_error()
    {
        var codes = Codes("- type: entity\n  id: A\n  name: a\n  parent: B\n- type: entity\n  id: B\n  name: b\n  parent: A\n");
        Assert.Equal(["cyclic-parent", "cyclic-parent"], codes);
    }

    [Fact]
    public void The_open_files_saved_copy_does_not_count()
    {
        // Saved on disk as Old; renamed to New in the editor, so a child of Old is now orphaned.
        var index = BuildIndex((OtherFile, OtherText), (ThisFile, "- type: entity\n  id: Old\n  name: x\n"));
        var codes = Validate("- type: entity\n  id: New\n  name: x\n- type: entity\n  id: Child\n  parent: Old\n", index).Select(d => d.Code!.Value.String);
        Assert.Equal(["missing-parent"], codes);
    }

    // ── unknown ids / missing assets ───────────────────────────────────────

    [Fact]
    public void Unknown_proto_id_is_a_warning()
    {
        var diagnostic = Assert.Single(Validate("- type: entity\n  id: Mine\n  name: x\n  tags: [Shiny, Dull]\n"));
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("unknown-proto-id", diagnostic.Code!.Value.String);
        Assert.Equal(R(3, 16, 20), diagnostic.Range);
    }

    [Fact]
    public void Unknown_proto_id_is_skipped_for_a_type_with_no_known_ids()
    {
        var index = BuildIndex((OtherFile, "- type: entity\n  id: Base\n  name: x\n"));
        Assert.Empty(Validate("- type: entity\n  id: Mine\n  name: x\n  tags: [Dull]\n", index));
    }

    [Fact]
    public void Missing_texture_is_a_warning_only_for_marked_fields()
    {
        var diagnostic = Assert.Single(Validate("- type: entity\n  id: Mine\n  name: x\n  icon: Items/gem\n  guess: Items/gem\n"));
        Assert.Equal("missing-asset", diagnostic.Code!.Value.String);
        Assert.Equal(3, diagnostic.Range.Start.Line);
        Assert.Empty(Validate("- type: entity\n  id: Mine\n  name: x\n  icon: Items/coin\n"));
    }

    [Fact]
    public void Texture_names_are_case_sensitive_like_the_atlas()
    {
        var diagnostic = Assert.Single(Validate("- type: entity\n  id: Mine\n  name: x\n  icon: items/COIN\n"));
        Assert.Contains("did you mean 'Items/coin'", diagnostic.Message);
    }

    [Fact]
    public void A_texture_field_also_takes_an_animation_key()
    {
        var resources = new ResourceIndex { SpriteKeys = ["Items/coin"], AnimationKeys = ["Items/spin"], ShaderKeys = [] };
        Assert.Empty(Validate("- type: entity\n  id: Mine\n  name: x\n  icon: Items/spin\n", resources: resources));
    }

    [Fact]
    public void Any_frame_of_an_animation_with_an_unknown_frame_count_is_accepted()
    {
        var resources = new ResourceIndex { SpriteKeys = ["Items/coin"], AnimationKeys = ["Items/spin"], OpenEndedAnimationKeys = ["Items/spin"], ShaderKeys = [] };

        Assert.Empty(Validate("- type: entity\n  id: Mine\n  name: x\n  icon: Items/spin-7\n", resources: resources));
    }

    [Fact]
    public void Frames_are_still_checked_for_an_animation_with_a_known_frame_count()
    {
        var resources = new ResourceIndex { SpriteKeys = ["Items/coin", "Items/spin-0"], AnimationKeys = ["Items/spin"], ShaderKeys = [] };

        var diagnostic = Assert.Single(Validate("- type: entity\n  id: Mine\n  name: x\n  icon: Items/spin-7\n", resources: resources));
        Assert.Equal("missing-asset", diagnostic.Code!.Value.String);
    }

    [Fact]
    public void Missing_asset_check_is_skipped_when_no_file_of_that_kind_was_found()
    {
        Assert.Empty(Validate("- type: entity\n  id: Mine\n  name: x\n  icon: Items/gem\n", resources: ResourceIndex.Empty));
    }

    [Fact]
    public void Cross_file_checks_wait_for_the_index()
    {
        Assert.Empty(Validate("- type: entity\n  id: Mine\n  parent: Gone\n  tags: [Dull]\n", PrototypeIdIndex.Empty));
    }

    // ── references / rename / symbols ──────────────────────────────────────

    private const string MineText = "- type: entity\n  id: Mine\n  parent: Base\n  tags: [Shiny]\n";

    private static IReadOnlyList<(DocumentUri, string)> Documents(string mine = MineText) =>
        [(ThisUri, mine), (OtherUri, OtherText)];

    [Fact]
    public void References_cover_every_file_and_the_declaration()
    {
        var locations = PrototypeIdHandler.References(MineText, new LspPosition(3, 11), BuildSchema(), Documents(), includeDeclaration: true).ToList();

        Assert.Equal(2, locations.Count);
        Assert.Contains(locations, l => l.Uri == ThisUri && l.Range == R(3, 9, 14));
        Assert.Contains(locations, l => l.Uri == OtherUri && l.Range == R(7, 6, 11));
    }

    [Fact]
    public void References_from_the_id_itself()
    {
        var locations = PrototypeIdHandler.References(OtherText, new LspPosition(1, 7), BuildSchema(), Documents(), includeDeclaration: false).ToList();
        Assert.Equal(ThisUri, Assert.Single(locations).Uri);
    }

    [Fact]
    public void Rename_edits_the_id_and_every_reference()
    {
        var edit = PrototypeIdHandler.Rename(OtherText, new LspPosition(1, 7), "Root", BuildSchema(), Documents())!;

        Assert.Equal(R(2, 10, 14), Assert.Single(edit.Changes![ThisUri]).Range);
        Assert.Equal(R(1, 6, 10), Assert.Single(edit.Changes[OtherUri]).Range);
        Assert.All(edit.Changes.Values.SelectMany(e => e), e => Assert.Equal("Root", e.NewText));
    }

    [Fact]
    public void Rename_keeps_the_quotes_of_a_quoted_id()
    {
        const string text = "- type: tag\n  id: \"Odd\"\n";
        Assert.Equal(R(1, 7, 10), PrototypeIdHandler.PrepareRename(text, new LspPosition(1, 8), BuildSchema()));
    }

    [Fact]
    public void Rename_refuses_a_name_that_would_need_quoting()
    {
        Assert.Null(PrototypeIdHandler.Rename(OtherText, new LspPosition(1, 7), "a: b", BuildSchema(), Documents()));
    }

    [Fact]
    public void Symbols_match_the_query_in_order_ignoring_case()
    {
        var names = PrototypeIdHandler.Symbols("sny", Documents()).Select(s => (s.Name, s.ContainerName)).ToList();
        Assert.Equal([("Shiny", "tag")], names);
    }
}
