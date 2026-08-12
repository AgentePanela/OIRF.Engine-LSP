using Microsoft.Extensions.Logging.Abstractions;
using OIRF.LanguageServer.Yaml;

namespace OIRF.LanguageServer.Tests;

public class PrototypeIdIndexerTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("OIRF.LanguageServer.Tests.PrototypeId.").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string CreatePrototypesFolder(params string[] segments)
    {
        var dir = Path.Combine([_root, .. segments]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Indexes_ids_by_type_across_files_and_folders()
    {
        var entities = CreatePrototypesFolder("Prototypes", "Entities");
        File.WriteAllText(Path.Combine(entities, "Player.yml"), "- type: entity\n  id: Player\n");

        var tags = CreatePrototypesFolder("Prototypes", "Tags");
        File.WriteAllText(Path.Combine(tags, "Tags.yml"), "- type: tag\n  id: Friendly\n- type: tag\n  id: Hostile\n");

        var index = PrototypeIdIndexer.Build([_root], NullLogger.Instance);

        Assert.Equal(["Player"], index.Get("entity"));
        Assert.Equal(["Friendly", "Hostile"], index.Get("tag"));
    }

    [Fact]
    public void Deduplicates_case_insensitively_matching_the_engine_s_own_lookup()
    {
        var dir = CreatePrototypesFolder("Prototypes");
        File.WriteAllText(Path.Combine(dir, "A.yml"), "- type: tag\n  id: Friendly\n");
        File.WriteAllText(Path.Combine(dir, "B.yml"), "- type: tag\n  id: friendly\n");

        var index = PrototypeIdIndexer.Build([_root], NullLogger.Instance);

        Assert.Equal(["Friendly"], index.Get("tag"));
    }

    [Fact]
    public void Ignores_items_missing_type_or_id()
    {
        var dir = CreatePrototypesFolder("Prototypes");
        File.WriteAllText(Path.Combine(dir, "A.yml"), "- type: tag\n- id: Orphan\n");

        var index = PrototypeIdIndexer.Build([_root], NullLogger.Instance);

        Assert.Empty(index.Get("tag"));
    }

    [Fact]
    public void Unknown_type_key_returns_empty_not_null()
    {
        var index = PrototypeIdIndexer.Build([_root], NullLogger.Instance);

        Assert.Empty(index.Get("doesNotExist"));
    }

    [Fact]
    public void Prototypes_folder_can_live_anywhere_not_just_at_the_workspace_root()
    {
        var nested = CreatePrototypesFolder("SomeMod", "Resources", "Prototypes");
        File.WriteAllText(Path.Combine(nested, "Item.yml"), "- type: item\n  id: Sword\n");

        var index = PrototypeIdIndexer.Build([_root], NullLogger.Instance);

        Assert.Equal(["Sword"], index.Get("item"));
    }
}
