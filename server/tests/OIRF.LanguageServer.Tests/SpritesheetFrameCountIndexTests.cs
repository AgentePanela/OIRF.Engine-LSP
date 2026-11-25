using Microsoft.Extensions.Logging.Abstractions;
using OIRF.LanguageServer.Assets;

namespace OIRF.LanguageServer.Tests;

/// <summary>
/// A spritesheet whose info.yml omits `frameCount` gets every cell of the sheet, like
/// AssetManager.SliceAndQueue in the engine.
/// </summary>
public class SpritesheetFrameCountIndexTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("OIRF.LanguageServer.Tests.FrameCount.").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string TexturesDir()
    {
        var dir = Path.Combine(_root, "Textures");
        Directory.CreateDirectory(dir);
        return dir;
    }

    // Signature + IHDR header is all PngDimensions reads.
    private static void WritePng(string path, int width, int height)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A });
        writer.Write(new byte[] { 0, 0, 0, 13 });
        writer.Write("IHDR"u8.ToArray());
        writer.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(width));
        writer.Write(System.Buffers.Binary.BinaryPrimitives.ReverseEndianness(height));
    }

    private ResourceIndex Index() => ResourceIndexer.Build([_root], NullLogger.Instance);

    [Fact]
    public void Omitted_frame_count_uses_every_cell_of_the_sheet()
    {
        var dir = TexturesDir();
        WritePng(Path.Combine(dir, "sheet.png"), 64, 32);
        File.WriteAllText(Path.Combine(dir, "info.yml"), "animations:\n  - id: walk\n    spritesheet: true\n    files: sheet\n    frameWidth: 16\n    frameHeight: 16\n");

        var index = Index();

        // 4 columns x 2 rows
        Assert.Contains("walk", index.AnimationKeys);
        for (var i = 0; i < 8; i++)
            Assert.Contains($"walk-{i}", index.SpriteKeys);
        Assert.DoesNotContain("walk-8", index.SpriteKeys);
        Assert.DoesNotContain("sheet", index.SpriteKeys);
    }

    [Fact]
    public void Unreadable_sheet_without_frame_count_is_open_ended()
    {
        var dir = TexturesDir();
        File.WriteAllText(Path.Combine(dir, "info.yml"), "animations:\n  - id: walk\n    spritesheet: true\n    files: ghost\n    frameWidth: 16\n    frameHeight: 16\n");

        var index = Index();

        Assert.Contains("walk", index.OpenEndedAnimationKeys);
    }

    [Fact]
    public void Readable_sheet_is_not_open_ended()
    {
        var dir = TexturesDir();
        WritePng(Path.Combine(dir, "sheet.png"), 64, 32);
        File.WriteAllText(Path.Combine(dir, "info.yml"), "animations:\n  - id: walk\n    spritesheet: true\n    files: sheet\n    frameWidth: 16\n    frameHeight: 16\n");

        Assert.Empty(Index().OpenEndedAnimationKeys);
    }

    [Fact]
    public void Explicit_frame_count_still_wins()
    {
        var dir = TexturesDir();
        WritePng(Path.Combine(dir, "sheet.png"), 64, 32);
        File.WriteAllText(Path.Combine(dir, "info.yml"), "animations:\n  - id: walk\n    spritesheet: true\n    files: sheet\n    frameWidth: 16\n    frameHeight: 16\n    frameCount: 3\n");

        var index = Index();

        Assert.Contains("walk-2", index.SpriteKeys);
        Assert.DoesNotContain("walk-3", index.SpriteKeys);
    }
}
