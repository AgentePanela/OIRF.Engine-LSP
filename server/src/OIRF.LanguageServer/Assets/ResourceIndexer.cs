using Microsoft.Extensions.Logging;
using OIRF.LanguageServer.Schema;

namespace OIRF.LanguageServer.Assets;

public sealed class ResourceIndex
{
    public required IReadOnlyList<string> SpriteKeys { get; init; }
    public required IReadOnlyList<string> AnimationKeys { get; init; }
    public required IReadOnlyList<string> ShaderKeys { get; init; }

    public static readonly ResourceIndex Empty = new() { SpriteKeys = [], AnimationKeys = [], ShaderKeys = [] };

    public IReadOnlyList<string> Get(AssetKind kind) => kind switch
    {
        AssetKind.Sprite => SpriteKeys,
        AssetKind.Animation => AnimationKeys,
        AssetKind.Shader => ShaderKeys,
        _ => [],
    };
}

/// <summary>
/// Scans the workspace for "Textures"/"Shaders" folders (by name, anywhere - not assumed to sit
/// under a folder literally named "Resources", since the engine supports multiple,
/// arbitrarily-named resource roots) and derives the exact sprite/animation/shader keys the
/// engine's own asset pipeline would produce.
/// </summary>
public static class ResourceIndexer
{
    private static readonly string[] ExcludedDirNames = ["bin", "obj", "node_modules", ".git"];

    public static ResourceIndex Build(IEnumerable<string> workspaceRoots, ILogger logger)
    {
        var textureRoots = new List<string>();
        var shaderRoots = new List<string>();

        foreach (var root in workspaceRoots)
            FindNamedFolders(root, textureRoots, shaderRoots);

        var spriteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var animationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shaderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var texturesRoot in textureRoots)
            IndexTexturesRoot(texturesRoot, spriteKeys, animationKeys, logger);

        foreach (var shadersRoot in shaderRoots)
            IndexShadersRoot(shadersRoot, shaderKeys);

        return new ResourceIndex
        {
            SpriteKeys = spriteKeys.ToList(),
            AnimationKeys = animationKeys.ToList(),
            ShaderKeys = shaderKeys.ToList(),
        };
    }

    private static void FindNamedFolders(string root, List<string> textureRoots, List<string> shaderRoots)
    {
        if (!Directory.Exists(root))
            return;

        void Walk(string dir)
        {
            var name = Path.GetFileName(dir);
            if (string.Equals(name, "Textures", StringComparison.OrdinalIgnoreCase))
                textureRoots.Add(dir);
            if (string.Equals(name, "Shaders", StringComparison.OrdinalIgnoreCase))
                shaderRoots.Add(dir);

            IEnumerable<string> subdirectories;
            try
            {
                subdirectories = Directory.EnumerateDirectories(dir);
            }
            catch (Exception)
            {
                return;
            }

            foreach (var subdirectory in subdirectories)
            {
                var subName = Path.GetFileName(subdirectory);
                if (ExcludedDirNames.Contains(subName, StringComparer.OrdinalIgnoreCase))
                    continue;

                Walk(subdirectory);
            }
        }

        Walk(root);
    }

    /// <summary>Mirrors ShaderManager.Scan: flat filename (no folder prefix), no extension.</summary>
    private static void IndexShadersRoot(string shadersRoot, HashSet<string> shaderKeys)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(shadersRoot, "*.fx", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var file in files)
            shaderKeys.Add(Path.GetFileNameWithoutExtension(file));
    }

    private static void IndexTexturesRoot(string texturesRoot, HashSet<string> spriteKeys, HashSet<string> animationKeys, ILogger logger)
    {
        var consumedRelativeKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        IEnumerable<string> infoFiles;
        try
        {
            infoFiles = Directory.EnumerateFiles(texturesRoot, "info.yml", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            infoFiles = [];
        }

        foreach (var infoFile in infoFiles)
        {
            var folderRelative = ToRelativeForwardSlash(texturesRoot, Path.GetDirectoryName(infoFile)!);

            SpriteInfoParseResult? parsed;
            try
            {
                parsed = SpriteInfoYamlReader.TryParse(infoFile, folderRelative);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to parse animation info {File}", infoFile);
                continue;
            }

            if (parsed is null)
            {
                logger.LogWarning("Malformed animation info file: {File}", infoFile);
                continue;
            }

            foreach (var animation in parsed.Animations)
            {
                animationKeys.Add(animation.AnimKey);

                foreach (var consumed in animation.ConsumedRelativePngKeys)
                    consumedRelativeKeys.Add(consumed);

                for (var i = 0; i < animation.FrameCount; i++)
                    spriteKeys.Add($"{animation.AnimKey}-{i}");
            }
        }

        IEnumerable<string> pngFiles;
        try
        {
            pngFiles = Directory.EnumerateFiles(texturesRoot, "*.png", SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            pngFiles = [];
        }

        foreach (var png in pngFiles)
        {
            var relative = ToRelativeForwardSlash(texturesRoot, png);
            relative = relative[..^Path.GetExtension(png).Length];

            if (!consumedRelativeKeys.Contains(relative))
                spriteKeys.Add(relative);
        }
    }

    private static string ToRelativeForwardSlash(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
