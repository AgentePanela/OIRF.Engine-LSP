using OmniSharp.Extensions.LanguageServer.Protocol.Models;

namespace OIRF.LanguageServer.Features;

/// <summary>
/// Completion for an info.yml sidecar's 'files:' value - the only place in the grammar worth
/// autocompleting, since that's where a typo/rename most commonly breaks the sprite/animation
/// (see InfoYamlValidator's "missing-texture" diagnostic for the other half of that safety net).
/// Deliberately text-based rather than a full YAML-tree resolver like NodeContextResolver: the
/// grammar here is small enough (one key, two possible value shapes) that line-scanning covers
/// it without needing position-tracked node ranges.
/// </summary>
public static class InfoYamlCompletionHandler
{
    public static IEnumerable<CompletionItem> Handle(string text, LspPosition position, string infoYmlDirectory)
    {
        var lines = text.Replace("\r\n", "\n").Split('\n');
        if (position.Line < 0 || position.Line >= lines.Length)
            return [];

        var line = lines[position.Line];
        var column = Math.Clamp(position.Character, 0, line.Length);
        var linePrefix = line[..column];

        var insideFilesValue = IsInsideFlowFilesValue(linePrefix) || IsInsideBlockFilesValue(lines, position.Line);
        return insideFilesValue ? PngCompletionItems(infoYmlDirectory) : [];
    }

    /// <summary>Same-line cases: a spritesheet scalar ("files: wal") or an inline flow list ("files: [idle, ").</summary>
    private static bool IsInsideFlowFilesValue(string linePrefix)
    {
        var keyIndex = linePrefix.LastIndexOf("files:", StringComparison.OrdinalIgnoreCase);
        if (keyIndex < 0)
            return false;

        var afterKey = linePrefix[(keyIndex + "files:".Length)..];
        if (afterKey.Contains(']') || afterKey.Contains('#'))
            return false;

        return afterKey.All(c => char.IsWhiteSpace(c) || c is '[' or ',' || IsFileNameChar(c));
    }

    /// <summary>
    /// Block-list case: cursor is on a "- " bullet line whose nearest less-indented ancestor line
    /// is a bare "files:" key.
    /// </summary>
    private static bool IsInsideBlockFilesValue(string[] lines, int lineIndex)
    {
        var line = lines[lineIndex];
        var trimmed = line.TrimStart();
        if (!trimmed.StartsWith('-'))
            return false;

        var indent = line.Length - trimmed.Length;
        for (var i = lineIndex - 1; i >= 0; i--)
        {
            var candidate = lines[i];
            var candidateTrimmed = candidate.TrimStart();
            if (candidateTrimmed.Length == 0)
                continue;

            var candidateIndent = candidate.Length - candidateTrimmed.Length;
            if (candidateIndent >= indent)
                continue;

            return candidateTrimmed.StartsWith("files:", StringComparison.OrdinalIgnoreCase);
        }

        return false;
    }

    private static bool IsFileNameChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '-' or '.' or '/';

    private static IEnumerable<CompletionItem> PngCompletionItems(string infoYmlDirectory)
    {
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(infoYmlDirectory, "*.png", SearchOption.TopDirectoryOnly);
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var file in files)
        {
            yield return new CompletionItem
            {
                Label = Path.GetFileNameWithoutExtension(file),
                Kind = CompletionItemKind.File,
            };
        }
    }
}
