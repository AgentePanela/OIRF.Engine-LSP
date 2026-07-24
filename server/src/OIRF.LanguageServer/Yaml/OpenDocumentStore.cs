using System.Collections.Concurrent;
using OmniSharp.Extensions.LanguageServer.Protocol;

namespace OIRF.LanguageServer.Yaml;

/// <summary>
/// Tracks the current full text of every open Prototype YAML document (Full text-document sync
/// - simplest correct option for M1's document sizes; no incremental patching needed). Keeps the
/// original <see cref="DocumentUri"/> alongside the text so callers can re-publish diagnostics
/// for every open document after a schema rebuild without needing to reparse URIs from strings.
/// </summary>
public sealed class OpenDocumentStore
{
    private readonly ConcurrentDictionary<string, (DocumentUri Uri, string Text)> _documents = new();

    public void Set(DocumentUri uri, string text) => _documents[uri.ToString()] = (uri, text);

    public void Remove(DocumentUri uri) => _documents.TryRemove(uri.ToString(), out _);

    public bool TryGet(DocumentUri uri, out string text)
    {
        if (_documents.TryGetValue(uri.ToString(), out var entry))
        {
            text = entry.Text;
            return true;
        }

        text = string.Empty;
        return false;
    }

    public IReadOnlyList<(DocumentUri Uri, string Text)> Snapshot() => _documents.Values.ToList();
}
