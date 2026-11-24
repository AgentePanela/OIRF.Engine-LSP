using LspPosition = OmniSharp.Extensions.LanguageServer.Protocol.Models.Position;
using OIRF.LanguageServer.Assets;
using OIRF.LanguageServer.Features;
using OIRF.LanguageServer.Schema;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Tests;

/// <summary>
/// Engine 0.5 additions: shaders referenced by ShaderPrototype id, [AudioKey] audio keys, and
/// SoundSpecifier values (bare key or a path/collection map, see SoundSpecifier.FromYaml).
/// </summary>
public class AudioAndShaderTests
{
    private static readonly AssetClassification Sound = new(AssetKind.Sound, ["Audios"], HighConfidence: true);
    private static readonly AssetClassification Audio = new(AssetKind.Audio, ["Audios"], HighConfidence: true);
    private static readonly AssetClassification Shader = new(AssetKind.Shader, ["Shaders"], HighConfidence: true);

    private static readonly ResourceIndex Resources = new()
    {
        SpriteKeys = [],
        AnimationKeys = [],
        ShaderKeys = ["WallCut", "outline"],
        AudioKeys = ["Mobs/Voice/speak_1", "alarm"],
    };

    private static readonly PrototypeIdIndex Ids = new()
    {
        IdsByTypeKey = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase)
        {
            ["shader"] = ["Unshaded", "Outline"],
            ["soundCollection"] = ["MobHumanSpeak"],
        },
    };

    private static EngineSchema BuildSchema(bool withShaderPrototype = true)
    {
        var entityFields = new List<DataFieldInfo>
        {
            new("type", false, true, false, "string", "", null, null, null),
            new("id", false, true, false, "string", "", null, null, null),
            new("components", false, false, true, "List<ComponentEntry>", "", null, null, null),
        };
        var collectionFields = new List<DataFieldInfo>
        {
            new("type", false, true, false, "string", "", null, null, null),
            new("id", false, true, false, "string", "", null, null, null),
            new("files", true, false, false, "List<string>", "", null, Audio, null),
        };

        var prototypes = new Dictionary<string, PrototypeTypeInfo>(StringComparer.OrdinalIgnoreCase)
        {
            ["entity"] = new("entity", 0, "TestGame.EntityPrototype", "", true, null, entityFields, null),
            ["soundCollection"] = new("soundCollection", 0, "Engine.Shared.Audio.SoundCollectionPrototype", "", false, null, collectionFields, null),
        };
        if (withShaderPrototype)
            prototypes["shader"] = new("shader", 0, "Engine.Shared.Graphics.ShaderPrototype", "", false, null, [], null);

        var speech = new ComponentTypeInfo("Speech", "SpeechComponent", "TestGame.SpeechComponent", "", null,
            [
                new MemberInfo("saySound", "SoundSpecifier", "", null, Sound, null),
                new MemberInfo("shader", "string", "", null, Shader, null),
            ], null);

        return new EngineSchema(prototypes,
            new Dictionary<string, ComponentTypeInfo>(StringComparer.OrdinalIgnoreCase) { ["Speech"] = speech });
    }

    private static List<string> Complete(string text, int line, string linePrefix, EngineSchema? schema = null) =>
        CompletionHandler.Handle(text, new LspPosition(line, linePrefix.Length), schema ?? BuildSchema(), Resources, Ids)
            .Select(i => i.Label).ToList();

    private const string Header = "- type: entity\n  id: Human\n  components:\n  - type: Speech\n";

    [Fact]
    public void Shader_reference_offers_shader_prototype_ids()
    {
        const string line = "    shader: ";
        Assert.Equal(["Unshaded", "Outline"], Complete(Header + line + "\n", 4, line));
    }

    [Fact]
    public void Shader_reference_falls_back_to_fx_files_on_engines_without_ShaderPrototype()
    {
        const string line = "    shader: ";
        Assert.Equal(["WallCut", "outline"], Complete(Header + line + "\n", 4, line, BuildSchema(withShaderPrototype: false)));
    }

    [Fact]
    public void Bare_sound_value_offers_audio_keys()
    {
        const string line = "    saySound: ";
        Assert.Equal(["Mobs/Voice/speak_1", "alarm"], Complete(Header + line + "\n", 4, line));
    }

    [Fact]
    public void Sound_map_offers_its_keys_after_the_brace()
    {
        const string line = "    saySound: { ";
        Assert.Equal(["path", "collection", "volume", "pitch"], Complete(Header + line + "}\n", 4, line));
    }

    [Fact]
    public void Sound_map_collection_offers_soundCollection_ids()
    {
        const string line = "    saySound: { collection: ";
        Assert.Equal(["MobHumanSpeak"], Complete(Header + line + "}\n", 4, line));
    }

    [Fact]
    public void Sound_map_path_offers_audio_keys()
    {
        const string line = "    saySound: { volume: 0.5, path: ";
        Assert.Equal(["Mobs/Voice/speak_1", "alarm"], Complete(Header + line + "}\n", 4, line));
    }

    [Fact]
    public void Block_style_sound_map_collection_offers_soundCollection_ids()
    {
        const string line = "      collection: ";
        var text = Header + "    saySound:\n" + line + "x\n";
        Assert.Equal(["MobHumanSpeak"], Complete(text, 5, line));
    }

    [Fact]
    public void Every_line_of_a_block_list_of_audio_keys_offers_audio_keys()
    {
        var text = "- type: soundCollection\n  id: X\n  files:\n  - a\n  - b\n";
        // Third line of the item list, not just the first one.
        Assert.Equal(["Mobs/Voice/speak_1", "alarm"], Complete(text, 4, "  - "));
    }

    private static List<Diagnostic> Validate(string soundValue) =>
        PrototypeValidator.Validate(Header + "    saySound: " + soundValue + "\n", BuildSchema()).ToList();

    [Theory]
    [InlineData("Mobs/Voice/speak_1")]
    [InlineData("{ collection: MobHumanSpeak }")]
    [InlineData("{ path: alarm, volume: 0.5, pitch: 0.1 }")]
    public void Valid_sounds_have_no_diagnostics(string value) => Assert.Empty(Validate(value));

    [Fact]
    public void Sound_map_without_path_or_collection_is_an_error()
    {
        var diagnostic = Assert.Single(Validate("{ volume: 1 }"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Equal("invalid-sound", diagnostic.Code!.Value.String);
    }

    [Fact]
    public void Sound_map_with_both_path_and_collection_is_an_error()
    {
        var diagnostic = Assert.Single(Validate("{ path: alarm, collection: MobHumanSpeak }"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    [Fact]
    public void Unknown_sound_map_key_is_a_warning()
    {
        var diagnostics = Validate("{ path: alarm, volum: 1 }");
        var diagnostic = Assert.Single(diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("unknown-sound-field", diagnostic.Code!.Value.String);
    }
}
