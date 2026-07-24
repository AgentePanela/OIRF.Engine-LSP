using System.Globalization;
using YamlDotNet.RepresentationModel;

namespace OIRF.LanguageServer.Assets;

/// <summary>One `animations:` entry from an info.yml sidecar.</summary>
public sealed record AnimationEntry(
    string AnimKey,
    int FrameCount,
    bool IsSpritesheet,
    IReadOnlyList<string> ConsumedRelativePngKeys);

public sealed class SpriteInfoParseResult
{
    public required IReadOnlyList<AnimationEntry> Animations { get; init; }

    public static readonly SpriteInfoParseResult Empty = new() { Animations = [] };
}

/// <summary>
/// Reimplements Engine.Client.Assets.AssetManager.Animation.cs's info.yml parsing
/// (ParseInfoFile/ParseAnimationEntry) purely from source text, so the LSP can derive the exact
/// same sprite/animation keys the engine's asset pipeline produces at runtime, without loading
/// or running any engine code.
/// </summary>
public static class SpriteInfoYamlReader
{
    /// <param name="folderRelativeToTexturesRoot">
    /// The info.yml's containing folder, relative to the "Textures" root, forward-slash
    /// separated (empty string if the info.yml sits directly in the Textures root).
    /// </param>
    public static SpriteInfoParseResult? TryParse(string filePath, string folderRelativeToTexturesRoot)
    {
        var stream = new YamlStream();
        using (var reader = new StreamReader(filePath))
        {
            try
            {
                stream.Load(reader);
            }
            catch (Exception)
            {
                return null;
            }
        }

        if (stream.Documents.Count == 0)
            return SpriteInfoParseResult.Empty;

        if (stream.Documents[0].RootNode is not YamlMappingNode root)
            return null;

        if (!root.Children.TryGetValue(new YamlScalarNode("animations"), out var animationsNode))
            return SpriteInfoParseResult.Empty;

        if (animationsNode is not YamlSequenceNode animationsSeq)
            return null;

        var entries = new List<AnimationEntry>();
        foreach (var node in animationsSeq.Children)
        {
            if (node is YamlMappingNode map && ParseEntry(map, folderRelativeToTexturesRoot) is { } entry)
                entries.Add(entry);
        }

        return new SpriteInfoParseResult { Animations = entries };
    }

    private static AnimationEntry? ParseEntry(YamlMappingNode map, string folder)
    {
        var id = GetScalar(map, "id");
        if (id is null)
            return null;

        var animKey = string.IsNullOrEmpty(folder) ? id : $"{folder}/{id}";
        var isSpritesheet = GetBool(map, "spritesheet", fallback: false);

        if (!map.Children.TryGetValue(new YamlScalarNode("files"), out var filesNode))
            return null;

        if (isSpritesheet)
        {
            if (filesNode is not YamlScalarNode { Value: { } fileName })
                return null;

            var frameCount = GetInt(map, "frameCount");
            if (frameCount is null)
                return null;

            var sourceKey = string.IsNullOrEmpty(folder) ? fileName : $"{folder}/{fileName}";
            return new AnimationEntry(animKey, frameCount.Value, IsSpritesheet: true, [sourceKey]);
        }

        if (filesNode is not YamlSequenceNode fileSeq)
            return null;

        var names = fileSeq.Children.OfType<YamlScalarNode>().Select(n => n.Value).Where(v => v is not null).Cast<string>().ToList();
        var consumed = names.Select(n => string.IsNullOrEmpty(folder) ? n : $"{folder}/{n}").ToList();
        return new AnimationEntry(animKey, names.Count, IsSpritesheet: false, consumed);
    }

    private static string? GetScalar(YamlMappingNode map, string key) =>
        map.Children.TryGetValue(new YamlScalarNode(key), out var node) && node is YamlScalarNode scalar
            ? scalar.Value
            : null;

    private static bool GetBool(YamlMappingNode map, string key, bool fallback)
    {
        var value = GetScalar(map, key);
        return value is not null && bool.TryParse(value, out var parsed) ? parsed : fallback;
    }

    private static int? GetInt(YamlMappingNode map, string key)
    {
        var value = GetScalar(map, key);
        return value is not null && int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }
}
