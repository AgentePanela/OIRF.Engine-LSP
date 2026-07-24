using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;

namespace OIRF.LanguageServer.Schema;

/// <summary>
/// Renders a symbol's <c>///</c> XML doc comment (via <see cref="ISymbol.GetDocumentationCommentXml"/>)
/// as Markdown suitable for an LSP hover. Handles &lt;summary&gt;/&lt;remarks&gt;/&lt;para&gt;/&lt;c&gt;,
/// resolves &lt;see cref&gt; to a short inline-code name, and recursively resolves
/// &lt;inheritdoc cref="..."/&gt; chains (depth-capped) - confirmed necessary against
/// SpriteComponent.Shader / SpriteLayer.Shader in the engine source.
/// </summary>
public static class XmlDocToMarkdown
{
    private const int MaxInheritdocDepth = 5;
    private static readonly Regex WhitespaceRun = new(@"\s+", RegexOptions.Compiled);

    public static string? Convert(ISymbol? symbol, Compilation? compilation) =>
        Convert(symbol, compilation, depth: 0);

    private static string? Convert(ISymbol? symbol, Compilation? compilation, int depth)
    {
        if (symbol is null || depth > MaxInheritdocDepth)
            return null;

        var raw = symbol.GetDocumentationCommentXml(expandIncludes: true);
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        XElement root;
        try
        {
            // GetDocumentationCommentXml returns a <member> element without a single root some
            // versions omit the outer wrapper for members with only whitespace between tags;
            // wrapping defensively keeps XElement.Parse happy either way.
            root = XElement.Parse(raw);
        }
        catch (Exception)
        {
            return null;
        }

        var inheritdoc = root.Element("inheritdoc") ?? root.Descendants("inheritdoc").FirstOrDefault();
        if (inheritdoc is not null)
        {
            var cref = inheritdoc.Attribute("cref")?.Value;
            var target = cref is not null ? ResolveCref(cref, compilation) : null;
            return target is not null ? Convert(target, compilation, depth + 1) : null;
        }

        var parts = new List<string>();
        var summary = root.Element("summary");
        if (summary is not null)
        {
            var rendered = RenderInline(summary, compilation).Trim();
            if (rendered.Length > 0)
                parts.Add(rendered);
        }

        var remarks = root.Element("remarks");
        if (remarks is not null)
        {
            var rendered = RenderInline(remarks, compilation).Trim();
            if (rendered.Length > 0)
                parts.Add(rendered);
        }

        return parts.Count > 0 ? string.Join("\n\n", parts) : null;
    }

    private static string RenderInline(XElement element, Compilation? compilation)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(CollapseWhitespace(text.Value));
                    break;

                case XElement { Name.LocalName: "see" or "seealso" } el:
                    sb.Append('`').Append(FormatCrefTarget(el, compilation)).Append('`');
                    break;

                case XElement { Name.LocalName: "c" } el:
                    sb.Append('`').Append(CollapseWhitespace(el.Value)).Append('`');
                    break;

                case XElement { Name.LocalName: "para" } el:
                    sb.Append("\n\n").Append(RenderInline(el, compilation).Trim());
                    break;

                case XElement el:
                    sb.Append(RenderInline(el, compilation));
                    break;
            }
        }

        return sb.ToString();
    }

    private static string FormatCrefTarget(XElement seeElement, Compilation? compilation)
    {
        var cref = seeElement.Attribute("cref")?.Value;
        if (cref is null)
            return CollapseWhitespace(seeElement.Value);

        var symbol = ResolveCref(cref, compilation);
        if (symbol is not null)
            return symbol.Name;

        var id = StripDocIdPrefix(cref);
        var lastDot = id.LastIndexOf('.');
        return lastDot >= 0 ? id[(lastDot + 1)..] : id;
    }

    private static string CollapseWhitespace(string s) => WhitespaceRun.Replace(s, " ");

    private static string StripDocIdPrefix(string docId) =>
        docId.Length > 2 && docId[1] == ':' ? docId[2..] : docId;

    private static ISymbol? ResolveCref(string cref, Compilation? compilation)
    {
        if (compilation is null)
            return null;

        try
        {
            return DocumentationCommentId.GetFirstSymbolForDeclarationId(cref, compilation);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
