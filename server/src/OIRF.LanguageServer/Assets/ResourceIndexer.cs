using Microsoft.Extensions.Logging;
using OIRF.LanguageServer.Schema;

namespace OIRF.LanguageServer.Assets;

public sealed class ResourceIndex
{
    public required IReadOnlyList<string> SpriteKeys { get; init; }
    public required IReadOnlyList<string> AnimationKeys { get; init; }
    /// <summary>.fx file names (no extension, no folder) - see IndexShadersRoot.</summary>
    public required IReadOnlyList<string> ShaderKeys { get; init; }
    /// <summary>
    /// Spritesheet animations whose frame count the LSP could not work out (no frameCount and an
    /// unreadable sheet or missing frame size). Any "{key}-{n}" sprite is accepted for these.
    /// </summary>
    public IReadOnlyList<string> OpenEndedAnimationKeys { get; init; } = [];
    public IReadOnlyList<string> AudioKeys { get; init; } = [];
    public IReadOnlyList<string> FontKeys { get; init; } = [];

    public static readonly ResourceIndex Empty = new() { SpriteKeys = [], AnimationKeys = [], ShaderKeys = [] };

    public IReadOnlyList<string> Get(AssetKind kind) => kind switch
    {
        AssetKind.Sprite => SpriteKeys,
        AssetKind.Animation => AnimationKeys,
        AssetKind.Shader or AssetKind.ShaderFile => ShaderKeys,
        AssetKind.Audio or AssetKind.Sound => AudioKeys,
        AssetKind.Font => FontKeys,
        _ => [],
    };
}

/// <summary>
/// Scans the workspace for "Textures"/"Shaders"/"Audios"/"Fonts" folders (by name, anywhere - not assumed to sit
/// under a folder literally named "Resources", since the engine supports multiple,
/// arbitrarily-named resource roots) and derives the exact sprite/animation/shader keys the
/// engine's own asset pipeline would produce.
/// </summary>
public static class ResourceIndexer
{
    // "publish": dotnet publish output, a copy of every resource and prototype file.
    private static readonly string[] ExcludedDirNames = ["bin", "obj", "node_modules", ".git", "publish"];

    public static ResourceIndex Build(IEnumerable<string> workspaceRoots, ILogger logger)
    {
        var textureRoots = new List<string>();
        var shaderRoots = new List<string>();
        var audioRoots = new List<string>();
        var fontRoots = new List<string>();

        foreach (var root in workspaceRoots)
            FindNamedFolders(root, textureRoots, shaderRoots, audioRoots, fontRoots);

        var spriteKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var animationKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var shaderKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var audioKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var fontKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var openEndedKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var texturesRoot in textureRoots)
            IndexTexturesRoot(texturesRoot, spriteKeys, animationKeys, openEndedKeys, logger);

        foreach (var shadersRoot in shaderRoots)
            IndexShadersRoot(shadersRoot, shaderKeys);

        foreach (var audioRoot in audioRoots)
            IndexKeysByRelativePath(audioRoot, "*.ogg", audioKeys);

        foreach (var fontRoot in fontRoots)
            IndexKeysByRelativePath(fontRoot, "*.ttf", fontKeys);

        return new ResourceIndex
        {
            SpriteKeys = spriteKeys.ToList(),
            AnimationKeys = animationKeys.ToList(),
            OpenEndedAnimationKeys = openEndedKeys.ToList(),
            ShaderKeys = shaderKeys.ToList(),
            AudioKeys = audioKeys.ToList(),
            FontKeys = fontKeys.ToList(),
        };
    }

    private static void FindNamedFolders(string root, List<string> textureRoots, List<string> shaderRoots, List<string> audioRoots, List<string> fontRoots)
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
            // SharedAudioManifest.ResPath is literally "Audios" (plural), not "Audio".
            if (string.Equals(name, "Audios", StringComparison.OrdinalIgnoreCase))
                audioRoots.Add(dir);
            if (string.Equals(name, "Fonts", StringComparison.OrdinalIgnoreCase))
                fontRoots.Add(dir);

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

    /// <summary>
    /// Every file matching <paramref name="pattern"/> under the root, recursively, keyed by its
    /// path relative to the root without extension
    /// </summary>
    private static void IndexKeysByRelativePath(string resourceRoot, string pattern, HashSet<string> keys)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(resourceRoot, pattern, SearchOption.AllDirectories);
        }
        catch (Exception)
        {
            return;
        }

        foreach (var file in files)
        {
            var relative = ToRelativeForwardSlash(resourceRoot, file);
            keys.Add(relative[..^Path.GetExtension(file).Length]);
        }
    }

    private static void IndexTexturesRoot(string texturesRoot, HashSet<string> spriteKeys, HashSet<string> animationKeys, HashSet<string> openEndedKeys, ILogger logger)
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
            // GetRelativePath returns "." for the root itself; the engine uses an empty folder there.
            if (folderRelative == ".")
                folderRelative = string.Empty;

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

                var frameCount = animation.FrameCount;
                if (animation.IsSpritesheet && frameCount <= 0)
                    frameCount = CountSheetCells(texturesRoot, animation);

                // Can't tell how many frames the engine will cut: don't flag any "{key}-{n}" later.
                if (animation.IsSpritesheet && frameCount <= 0)
                    openEndedKeys.Add(animation.AnimKey);

                for (var i = 0; i < frameCount; i++)
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

    /// <summary>
    /// Mirrors AssetManager.SliceAndQueue: with no frameCount the engine uses every cell of the
    /// sheet (columns * rows). Returns 0 if the PNG or the frame size is unusable.
    /// </summary>
    private static int CountSheetCells(string texturesRoot, AnimationEntry animation)
    {
        if (animation.FrameWidth <= 0 || animation.FrameHeight <= 0 || animation.ConsumedRelativePngKeys.Count == 0)
            return 0;

        var pngPath = Path.Combine(texturesRoot, animation.ConsumedRelativePngKeys[0] + ".png");
        if (!PngDimensions.TryRead(pngPath, out var width, out var height))
            return 0;

        var columns = Math.Max(1, width / animation.FrameWidth);
        var rows = Math.Max(1, height / animation.FrameHeight);
        return columns * rows;
    }

    private static string ToRelativeForwardSlash(string root, string path) =>
        Path.GetRelativePath(root, path).Replace('\\', '/');
}
