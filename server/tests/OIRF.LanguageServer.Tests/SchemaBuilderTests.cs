using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.Extensions.Logging.Abstractions;
using OIRF.LanguageServer.Schema;

namespace OIRF.LanguageServer.Tests;

/// <summary>
/// Feeds SchemaBuilder small, self-contained C# source (via AdhocWorkspace, no real
/// MSBuildWorkspace) that defines minimal stand-ins for the engine's own
/// [Prototype]/[DataField]/[RegisterComponent]/Component/IPrototype types under their real
/// fully-qualified names - SchemaBuilder matches purely by FQN string, so this fixture doesn't
/// need to reference the actual engine assembly to exercise the real extraction logic.
/// </summary>
public class SchemaBuilderTests
{
    private const string FixtureSource = """
        using System;
        using System.Collections.Generic;

        // Global namespace, mirroring the real engine's Engine.Shared/Types.cs - no namespace
        // declaration wraps it there either.
        public readonly struct ProtoId<T> where T : Engine.Shared.Prototypes.IPrototype
        {
            public readonly string Id;
            public ProtoId(string id) { Id = id; }
            public override string ToString() => Id;
            public static implicit operator string(ProtoId<T> id) => id.Id;
            public static implicit operator ProtoId<T>(string id) => new(id);
        }

        namespace Engine.Shared.Prototypes
        {
            public interface IPrototype { string Type { get; } string ID { get; } }
            public interface IInheritingPrototype { string[]? Parents { get; } bool Abstract { get; } }

            [AttributeUsage(AttributeTargets.Class, Inherited = false)]
            public sealed class PrototypeAttribute : Attribute
            {
                public string Type { get; }
                public int LoadPriority { get; }
                public PrototypeAttribute(string type, int loadPriority = 0) { Type = type; LoadPriority = loadPriority; }
            }

            [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
            public sealed class DataFieldAttribute : Attribute
            {
                public string? Name { get; }
                public bool Required { get; }
                public DataFieldAttribute(string? name = null, bool required = false) { Name = name; Required = required; }
            }

            [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
            public sealed class ComponentsDataFieldAttribute : Attribute { }
        }

        namespace Engine.Shared.GameObjects
        {
            public class Component
            {
                public int Owner { get; set; }
                public bool Deleted { get; internal set; }
            }

            [AttributeUsage(AttributeTargets.Class, Inherited = false)]
            public sealed class RegisterComponentAttribute : Attribute
            {
                public string Name { get; }
                public RegisterComponentAttribute(string name) { Name = name; }
            }
        }

        namespace Engine.Shared.Assets
        {
            [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
            public sealed class AudioKeyAttribute : Attribute { }

            [AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
            public sealed class ShaderKeyAttribute : Attribute { }
        }

        // Stand-ins for the 0.5 engine's audio/shader prototypes, same FQNs as the real ones.
        namespace Engine.Shared.Audio
        {
            using Engine.Shared.Prototypes;

            public abstract class SoundSpecifier { public float Volume { get; set; } = 1f; }

            [Prototype("soundCollection")]
            public sealed class SoundCollectionPrototype : IPrototype
            {
                [DataField("type", required: true)]
                public string Type { get; set; } = "";

                [DataField("id", required: true)]
                public string ID { get; set; } = "";

                [Engine.Shared.Assets.AudioKey]
                [DataField("files", required: true)]
                public List<string> Files { get; set; } = new();
            }
        }

        namespace Engine.Shared.Graphics
        {
            using Engine.Shared.Prototypes;

            [Prototype("shader")]
            public sealed class ShaderPrototype : IPrototype
            {
                public string Type => "shader";

                [DataField("id", required: true)]
                public string ID { get; set; } = "";

                [DataField("path")]
                public string? Path { get; set; }
            }
        }

        // Stand-ins for the 0.5 engine's UI styling, same FQNs as the real ones.
        namespace Engine.Client.UI
        {
            using Engine.Shared.Prototypes;

            [AttributeUsage(AttributeTargets.Field)]
            public sealed class StyleFieldAttribute : Attribute
            {
                public StyleFieldAttribute(string name, object? @default = null) { }
            }

            public sealed class StyleSet { public bool Add(string item) => true; }
            public struct Color { }
            public enum TTransform { None, Uppercase }

            // Split over two declarations like the real one - must still be one control.
            public abstract partial class Control
            {
                public StyleSet StyleAliasses { get; } = new();
                public StyleSet PseudoClasses { get; } = new();

                [StyleField("width")]
                private float? _width;
            }

            public abstract partial class Control
            {
                [StyleField("zIndex", 0)]
                private int? _zIndex;
            }

            /// <summary>Plain text control.</summary>
            public class Label : Control
            {
                [StyleField("color", 0xFFFFFFFFu)]
                private Color? _color;

                [StyleField("textTransform", TTransform.None)]
                private TTransform? _textTransform;

                [StyleField("fontFamily")]
                private string? _fontFamily;
            }

            public class Button : Control
            {
                private readonly Label _label = new();
                public Label? Content { get; set; }

                public Button()
                {
                    StyleAliasses.Add("button");
                    // the alias goes on the inner label: `control: button` styles it too
                    _label.StyleAliasses.Add("button");
                    Content?.StyleAliasses.Add("buttonContent");
                    PseudoClasses.Add("hover");
                }
            }

            [Prototype("style")]
            public sealed class StylePrototype : IPrototype
            {
                public string Type => "style";

                [DataField("id", required: true)]
                public string ID { get; set; } = "";
            }
        }

        namespace Engine.Client.Graphics.Fonts
        {
            using Engine.Shared.Prototypes;

            [Prototype("fontFamily")]
            public sealed class FontFamilyPrototype : IPrototype
            {
                public string Type => "fontFamily";

                [DataField("id", required: true)]
                public string ID { get; set; } = "";

                [DataField("regular", required: true)]
                public string Regular { get; set; } = "";
            }
        }

        namespace TestGame
        {
            using Engine.Shared.Prototypes;
            using Engine.Shared.GameObjects;

            /// <summary>A test entity prototype.</summary>
            [Prototype("entity")]
            public sealed class EntityPrototype : IPrototype, IInheritingPrototype
            {
                public string Type => "entity";

                [DataField("id", required: true)]
                public string ID { get; set; } = "";

                public string[]? Parents { get; set; }
                public bool Abstract { get; set; }

                /// <summary>
                /// Friendly display name.
                /// </summary>
                [DataField("name")]
                public string? Name { get; set; }
            }

            /// <summary>A test physics component.</summary>
            [RegisterComponent("Physics")]
            public sealed class PhysicsComponent : Component
            {
                /// <summary>
                /// Linear drag applied every frame.
                /// </summary>
                public float Friction { get; set; } = 0.3f;

                // Non-public setter - must be excluded from the schema (narrower than the
                // runtime's ApplyByName binder, by design; see SchemaBuilder's comments).
                public int Internal { get; private set; }
            }

            [RegisterComponent("PhysicsLike")]
            public sealed class PhysicsLikeComponent : Component
            {
                /// <inheritdoc cref="PhysicsComponent.Friction"/>
                public float Friction { get; set; }
            }

            [Prototype("tag")]
            public sealed class TagPrototype : IPrototype
            {
                public string Type => "tag";

                [DataField("id", required: true)]
                public string ID { get; set; } = "";
            }

            /// <summary>Covers every ProtoId&lt;T&gt; shape the real engine declares fields as.</summary>
            [RegisterComponent("Tags")]
            public sealed class TagsComponent : Component
            {
                public ProtoId<TagPrototype> PrimaryTag { get; set; }
                public ProtoId<TagPrototype>? OptionalTag { get; set; }
                public HashSet<ProtoId<TagPrototype>> Tags { get; set; } = new();
                public ProtoId<TagPrototype>[] TagArray { get; set; } = [];
                public string PlainString { get; set; } = "";
            }

            [RegisterComponent("Noisy")]
            public sealed class NoisyComponent : Component
            {
                [Engine.Shared.Assets.AudioKey]
                public string Key { get; set; } = "";

                public Engine.Shared.Audio.SoundSpecifier? HitSound { get; set; }

                [Engine.Shared.Assets.ShaderKey]
                public string? Shader { get; set; }
            }
        }
        """;

    private static Project CreateFixtureProject()
    {
        var workspace = new AdhocWorkspace();
        var projectId = ProjectId.CreateNewId();
        var projectInfo = ProjectInfo.Create(
            projectId,
            VersionStamp.Create(),
            "TestGame",
            "TestGame",
            LanguageNames.CSharp,
            compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary),
            metadataReferences:
            [
                MetadataReference.CreateFromFile(typeof(object).Assembly.Location),
                MetadataReference.CreateFromFile(typeof(Attribute).Assembly.Location),
            ]);

        var project = workspace.AddProject(projectInfo);
        return project.AddDocument("Fixture.cs", FixtureSource).Project;
    }

    private static async Task<EngineSchema> BuildSchemaAsync()
    {
        var project = CreateFixtureProject();
        var builder = new SchemaBuilder(NullLogger<SchemaBuilder>.Instance);
        return await builder.BuildAsync([project], CancellationToken.None);
    }

    [Fact]
    public async Task Discovers_prototype_type_with_data_fields_and_doc()
    {
        var schema = await BuildSchemaAsync();

        Assert.True(schema.PrototypesByTypeKey.TryGetValue("entity", out var proto));
        Assert.Equal("TestGame.EntityPrototype", proto!.ClrTypeName);
        Assert.True(proto.IsInheriting);
        Assert.Equal("A test entity prototype.", proto.ClassDocMarkdown);

        var idField = Assert.Single(proto.DataFields, f => f.YamlName == "id");
        Assert.True(idField.Required);
        Assert.True(idField.IsMeta, "id is a structural/meta field mirroring PrototypeManager's special-casing.");

        var nameField = Assert.Single(proto.DataFields, f => f.YamlName == "name");
        Assert.False(nameField.Required);
        Assert.Equal("Friendly display name.", nameField.DocMarkdown);
    }

    [Fact]
    public async Task Discovers_component_with_public_settable_members_only()
    {
        var schema = await BuildSchemaAsync();

        Assert.True(schema.ComponentsByName.TryGetValue("Physics", out var component));
        Assert.Equal("TestGame.PhysicsComponent", component!.ClrTypeName);

        // Member names are exposed in camelCase (matching the engine's YAML convention, e.g.
        // "friction: 15" in real prototype files), not the PascalCase C# property name.
        var friction = Assert.Single(component.Members, m => m.Name == "friction");
        Assert.Equal("Linear drag applied every frame.", friction.DocMarkdown);

        Assert.DoesNotContain(component.Members, m => m.Name == "internal");
        Assert.DoesNotContain(component.Members, m => m.Name == "owner");
        Assert.DoesNotContain(component.Members, m => m.Name == "deleted");
    }

    [Fact]
    public async Task Resolves_component_by_registered_name_and_by_class_name_fallback()
    {
        // Mirrors the engine's own loader: ComponentFactory.CreateInstanceFromSanitized(name)
        // (registered name) ?? CreateInstance(name) (raw class name) - so both must resolve, but
        // only the registered-name path should be reported as the non-fallback match.
        var schema = await BuildSchemaAsync();

        var byRegisteredName = schema.ResolveComponent("Physics");
        Assert.NotNull(byRegisteredName);
        Assert.False(byRegisteredName!.MatchedByClassName);

        var byClassName = schema.ResolveComponent("PhysicsComponent");
        Assert.NotNull(byClassName);
        Assert.True(byClassName!.MatchedByClassName);
        Assert.Same(byRegisteredName!.Component, byClassName!.Component);

        Assert.Null(schema.ResolveComponent("NotAComponent"));
    }

    [Fact]
    public async Task Resolves_inheritdoc_cref_across_types()
    {
        var schema = await BuildSchemaAsync();

        Assert.True(schema.ComponentsByName.TryGetValue("PhysicsLike", out var component));
        var friction = Assert.Single(component!.Members, m => m.Name == "friction");

        Assert.Equal("Linear drag applied every frame.", friction.DocMarkdown);
    }

    [Theory]
    [InlineData("primaryTag")]
    [InlineData("optionalTag")]
    [InlineData("tags")]
    [InlineData("tagArray")]
    public async Task ProtoId_of_T_is_classified_regardless_of_nullable_or_collection_wrapping(string memberName)
    {
        var schema = await BuildSchemaAsync();

        Assert.True(schema.ComponentsByName.TryGetValue("Tags", out var component));
        var member = Assert.Single(component!.Members, m => m.Name == memberName);

        Assert.NotNull(member.ProtoId);
        Assert.Equal("TestGame.TagPrototype", member.ProtoId!.PrototypeClrTypeName);
    }

    [Fact]
    public async Task Plain_string_member_is_not_classified_as_protoid()
    {
        var schema = await BuildSchemaAsync();

        Assert.True(schema.ComponentsByName.TryGetValue("Tags", out var component));
        var member = Assert.Single(component!.Members, m => m.Name == "plainString");

        Assert.Null(member.ProtoId);
    }

    [Theory]
    [InlineData("key", AssetKind.Audio)]
    [InlineData("hitSound", AssetKind.Sound)]
    [InlineData("shader", AssetKind.Shader)]
    public async Task Engine_0_5_asset_members_are_classified(string memberName, AssetKind expected)
    {
        var schema = await BuildSchemaAsync();

        Assert.True(schema.ComponentsByName.TryGetValue("Noisy", out var component));
        var member = Assert.Single(component!.Members, m => m.Name == memberName);

        Assert.Equal(expected, member.Asset?.Kind);
    }

    [Fact]
    public async Task AudioKey_on_a_list_of_strings_classifies_each_element_as_audio()
    {
        var schema = await BuildSchemaAsync();

        var files = Assert.Single(schema.PrototypesByTypeKey["soundCollection"].DataFields, f => f.YamlName == "files");

        Assert.Equal(AssetKind.Audio, files.Asset?.Kind);
    }

    [Fact]
    public async Task UI_controls_are_discovered_once_with_style_fields_from_every_partial()
    {
        var schema = await BuildSchemaAsync();

        var control = schema.UiControls["Engine.Client.UI.Control"];
        Assert.Equal(["width", "zIndex"], control.StyleProperties.Select(p => p.Name).Order());

        var label = schema.UiControls["Engine.Client.UI.Label"];
        Assert.Equal("Engine.Client.UI.Control", label.BaseClrTypeName);
        Assert.Equal("Plain text control.", label.ClassDocMarkdown);
    }

    [Fact]
    public async Task Style_field_defaults_types_and_choices_are_captured()
    {
        var schema = await BuildSchemaAsync();
        var label = schema.UiControls["Engine.Client.UI.Label"].StyleProperties.ToDictionary(p => p.Name);

        Assert.Equal("0xFFFFFFFF", label["color"].DefaultDisplay);
        Assert.Equal("None", label["textTransform"].DefaultDisplay);
        Assert.Equal(["None", "Uppercase"], label["textTransform"].ValueChoices);
        Assert.Equal("Engine.Client.Graphics.Fonts.FontFamilyPrototype", label["fontFamily"].ProtoId?.PrototypeClrTypeName);
    }

    [Fact]
    public async Task Alias_added_to_a_child_control_belongs_to_the_child_type()
    {
        var schema = await BuildSchemaAsync();

        // Button adds "button" to itself and to its inner Label, so a `control: button` rule
        // reaches Label's properties as well as Button's (inherited) ones.
        Assert.Contains("button", schema.UiControls["Engine.Client.UI.Button"].Aliases);
        Assert.Contains("button", schema.UiControls["Engine.Client.UI.Label"].Aliases);
        Assert.Contains(schema.ResolveStyleProperties("button"), p => p.Name == "color");
        Assert.Contains(schema.ResolveStyleProperties("button"), p => p.Name == "width");
    }

    [Fact]
    public async Task Alias_added_through_a_null_conditional_is_found()
    {
        var schema = await BuildSchemaAsync();

        Assert.Contains("buttonContent", schema.UiControls["Engine.Client.UI.Label"].Aliases);
    }

    [Fact]
    public async Task Pseudo_classes_are_collected_from_literals()
    {
        var schema = await BuildSchemaAsync();

        Assert.Contains("hover", schema.PseudoClasses);
    }

    [Fact]
    public async Task FontFamily_variant_fields_are_font_files()
    {
        var schema = await BuildSchemaAsync();

        var regular = Assert.Single(schema.PrototypesByTypeKey["fontFamily"].DataFields, f => f.YamlName == "regular");

        Assert.Equal(AssetKind.Font, regular.Asset?.Kind);
    }

    [Fact]
    public async Task ShaderPrototype_path_is_the_fx_file_not_a_shader_reference()
    {
        var schema = await BuildSchemaAsync();

        var path = Assert.Single(schema.PrototypesByTypeKey["shader"].DataFields, f => f.YamlName == "path");

        Assert.Equal(AssetKind.ShaderFile, path.Asset?.Kind);
    }
}
