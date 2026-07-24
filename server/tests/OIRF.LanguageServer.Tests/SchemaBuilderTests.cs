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

        var friction = Assert.Single(component.Members, m => m.Name == "Friction");
        Assert.Equal("Linear drag applied every frame.", friction.DocMarkdown);

        Assert.DoesNotContain(component.Members, m => m.Name == "Internal");
        Assert.DoesNotContain(component.Members, m => m.Name == "Owner");
        Assert.DoesNotContain(component.Members, m => m.Name == "Deleted");
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
        var friction = Assert.Single(component!.Members, m => m.Name == "Friction");

        Assert.Equal("Linear drag applied every frame.", friction.DocMarkdown);
    }
}
