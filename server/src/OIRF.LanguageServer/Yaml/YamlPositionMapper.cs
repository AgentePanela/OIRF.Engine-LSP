using YamlDotNet.Core;

namespace OIRF.LanguageServer.Yaml;

public static class YamlPositionMapper
{
    // YamlDotNet Marks are 1-based; LSP positions are 0-based.
    public static LspRange ToRange(Mark start, Mark end) =>
        new(
            new LspPosition((int)start.Line - 1, (int)start.Column - 1),
            new LspPosition((int)end.Line - 1, (int)end.Column - 1));

    public static bool Contains(this LspRange range, LspPosition position)
    {
        if (position.Line < range.Start.Line || position.Line > range.End.Line)
            return false;
        if (position.Line == range.Start.Line && position.Character < range.Start.Character)
            return false;
        if (position.Line == range.End.Line && position.Character > range.End.Character)
            return false;
        return true;
    }
}
