using System.Globalization;
using OIRF.LanguageServer.Yaml;
using OmniSharp.Extensions.LanguageServer.Protocol.Models;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace OIRF.LanguageServer.Features;

/// <summary>
/// Diagnostics for an info.yml sidecar (the file next to a "Textures" folder's PNGs that the
/// engine's AssetManager/AnimationSystem use to slice spritesheets and stitch loose frames into
/// animations). Mirrors Engine.Client.Assets.AssetManager.Animation.cs's
/// ParseInfoFile/ParseAnimationEntry exactly for anything that would throw there (reported here
/// as an Error, since a throw there means that file's *entire* animations dictionary fails to
/// load - see LoadAnimationInfo's catch), plus a couple of Warning-level checks for mistakes the
/// engine doesn't throw on but silently misbehaves for (a missing texture file just never gets
/// sliced/queued; a duplicate id silently overwrites the earlier definition).
/// </summary>
public static class InfoYamlValidator
{
    public static IEnumerable<Diagnostic> Validate(string text, string infoYmlDirectory)
    {
        var stream = new YamlStream();
        Diagnostic? syntaxError = null;
        try
        {
            stream.Load(new StringReader(text));
        }
        catch (YamlException ex)
        {
            syntaxError = Error(YamlPositionMapper.ToRange(ex.Start, ex.End), "syntax-error", ex.Message);
        }

        if (syntaxError is not null)
        {
            yield return syntaxError;
            yield break;
        }

        if (stream.Documents.Count == 0)
            yield break;

        var rootNode = stream.Documents[0].RootNode;
        if (rootNode is not YamlMappingNode root)
        {
            yield return Error(ToRange(rootNode), "bad-root", "info.yml root must be a mapping.");
            yield break;
        }

        if (!root.Children.TryGetValue(new YamlScalarNode("animations"), out var animationsNode))
            yield break;

        if (animationsNode is not YamlSequenceNode animationsSeq)
        {
            yield return Error(ToRange(animationsNode), "bad-anims", "'animations' must be a list.");
            yield break;
        }

        var seenIds = new Dictionary<string, YamlNode>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in animationsSeq.Children)
        {
            if (entry is not YamlMappingNode map)
            {
                yield return Error(ToRange(entry), "bad-anim-entry", "Each entry under 'animations' must be a mapping.");
                continue;
            }

            foreach (var diagnostic in ValidateEntry(map, infoYmlDirectory, seenIds))
                yield return diagnostic;
        }
    }

    private static IEnumerable<Diagnostic> ValidateEntry(YamlMappingNode map, string infoYmlDirectory, Dictionary<string, YamlNode> seenIds)
    {
        if (!TryGetScalar(map, "id", out var idNode))
        {
            yield return Error(ToRange(map), "missing-req-field", "Missing required field 'id'.");
            yield break;
        }

        var id = idNode.Value ?? string.Empty;
        if (seenIds.TryGetValue(id, out var earlier))
        {
            yield return Warning(
                ToRange(idNode),
                "duplicate-anim-id",
                $"Duplicate animation id '{id}' - this entry overwrites the earlier definition at line {earlier.Start.Line}; only the last one is used at runtime.");
        }

        seenIds[id] = idNode;

        bool? spritesheet = null;
        if (TryGetScalar(map, "spritesheet", out var spritesheetNode))
        {
            if (bool.TryParse(spritesheetNode.Value, out var parsed))
                spritesheet = parsed;
            else
                yield return Error(ToRange(spritesheetNode), "bad-value", "'spritesheet' must be 'true' or 'false'.");
        }

        if (TryGetScalar(map, "speed", out var speedNode) && !float.TryParse(speedNode.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
            yield return Error(ToRange(speedNode), "bad-value", "'speed' must be a number.");

        if (TryGetScalar(map, "loop", out var loopNode) && !bool.TryParse(loopNode.Value, out _))
            yield return Error(ToRange(loopNode), "bad-value", "'loop' must be 'true' or 'false'.");

        if (map.Children.TryGetValue(new YamlScalarNode("frameSpeeds"), out var frameSpeedsNode))
        {
            if (frameSpeedsNode is not YamlSequenceNode frameSpeedsSeq)
            {
                yield return Error(ToRange(frameSpeedsNode), "bad-value", "'frameSpeeds' must be a list of numbers.");
            }
            else
            {
                foreach (var child in frameSpeedsSeq.Children)
                {
                    if (child is not YamlScalarNode { Value: { } value } || !float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                        yield return Error(ToRange(child), "bad-value", "Each entry in 'frameSpeeds' must be a number.");
                }
            }
        }

        if (!map.Children.TryGetValue(new YamlScalarNode("files"), out var filesNode))
        {
            yield return Error(ToRange(map), "missing-req-field", $"Animation '{id}' is missing 'files'.");
            yield break;
        }

        if (spritesheet == true)
        {
            foreach (var diagnostic in ValidateSpritesheetFiles(map, filesNode, id, infoYmlDirectory))
                yield return diagnostic;
        }
        else
        {
            foreach (var diagnostic in ValidateFrameFiles(filesNode, id, infoYmlDirectory))
                yield return diagnostic;
        }
    }

    private static IEnumerable<Diagnostic> ValidateSpritesheetFiles(YamlMappingNode map, YamlNode filesNode, string id, string infoYmlDirectory)
    {
        if (filesNode is not YamlScalarNode { Value: { } fileName })
        {
            yield return Error(ToRange(filesNode), "bad-value", $"Animation '{id}' is a spritesheet, 'files' must be a single file name (no extension).");
            yield break;
        }

        if (!TryGetRequiredInt(map, "frameWidth", out var frameWidthDiagnostic))
            yield return frameWidthDiagnostic!;

        if (!TryGetRequiredInt(map, "frameHeight", out var frameHeightDiagnostic))
            yield return frameHeightDiagnostic!;

        if (!TryGetRequiredInt(map, "frameCount", out var frameCountDiagnostic))
            yield return frameCountDiagnostic!;

        if (MissingTexture(infoYmlDirectory, fileName) is { } missing)
            yield return Warning(ToRange(filesNode), "missing-texture", missing);
    }

    private static IEnumerable<Diagnostic> ValidateFrameFiles(YamlNode filesNode, string id, string infoYmlDirectory)
    {
        if (filesNode is not YamlSequenceNode fileSeq)
        {
            yield return Error(ToRange(filesNode), "bad-value", $"Animation '{id}' is not a spritesheet, 'files' must be a list of file names (no extension).");
            yield break;
        }

        if (fileSeq.Children.Count == 0)
        {
            yield return Warning(ToRange(filesNode), "empty-files", $"Animation '{id}' has no entries in 'files' - it will have zero frames.");
            yield break;
        }

        foreach (var child in fileSeq.Children)
        {
            if (child is not YamlScalarNode { Value: { } fileName })
            {
                yield return Error(ToRange(child), "bad-value", "Each entry in 'files' must be a plain file name.");
                continue;
            }

            if (MissingTexture(infoYmlDirectory, fileName) is { } missing)
                yield return Warning(ToRange(child), "missing-texture", missing);
        }
    }

    private static string? MissingTexture(string infoYmlDirectory, string fileNameNoExtension)
    {
        var pngPath = Path.Combine(infoYmlDirectory, fileNameNoExtension + ".png");
        return File.Exists(pngPath) ? null : $"'{fileNameNoExtension}.png' was not found next to this info.yml.";
    }

    private static bool TryGetRequiredInt(YamlMappingNode map, string key, out Diagnostic? diagnostic)
    {
        if (!TryGetScalar(map, key, out var node))
        {
            diagnostic = Error(ToRange(map), "missing-req-field", $"Missing required field '{key}'.");
            return false;
        }

        if (!int.TryParse(node.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
        {
            diagnostic = Error(ToRange(node), "bad-value", $"'{key}' must be a whole number.");
            return false;
        }

        diagnostic = null;
        return true;
    }

    private static bool TryGetScalar(YamlMappingNode map, string key, out YamlScalarNode node)
    {
        if (map.Children.TryGetValue(new YamlScalarNode(key), out var raw) && raw is YamlScalarNode scalar)
        {
            node = scalar;
            return true;
        }

        node = null!;
        return false;
    }

    private static LspRange ToRange(YamlNode node) => YamlPositionMapper.ToRange(node.Start, node.End);

    private static Diagnostic Error(LspRange range, string code, string message) => Build(range, DiagnosticSeverity.Error, code, message);

    private static Diagnostic Warning(LspRange range, string code, string message) => Build(range, DiagnosticSeverity.Warning, code, message);

    private static Diagnostic Build(LspRange range, DiagnosticSeverity severity, string code, string message) => new()
    {
        Range = range,
        Severity = severity,
        Source = "oirf-engine",
        Code = new DiagnosticCode(code),
        Message = message,
    };
}
