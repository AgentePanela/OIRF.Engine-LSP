namespace OIRF.LanguageServer.Yaml;

public static class NodeContextResolver
{
    public static NodeContext Resolve(PrototypeDocument document, LspPosition position)
    {
        var item = FindEnclosing(document.Items, i => i.ItemRange.Start, position);
        return item is null ? new NodeContext.None() : ResolveWithinItem(item, position);
    }

    private static NodeContext ResolveWithinItem(PrototypeItem item, LspPosition position)
    {
        var typeKey = item.TypeValue ?? string.Empty;

        if (IsOnValue(item.TypeValueRange, position))
            return new NodeContext.PrototypeTypeValue(item.TypeValue);

        if (IsOnValue(item.IdValueRange, position))
            return new NodeContext.PrototypeIdValue();

        if (IsOnValue(item.ParentValueRange, position))
            return new NodeContext.ParentValue(typeKey, item.IdValue);

        // Bounded by the next sibling's start (or open-ended for the last one) rather than a
        // strict Contains on ComponentsListRange's own end - the end mark only reaches as far as
        // the last *parsed* scalar, so a blank/in-progress line the user is actively typing past
        // that point (a brand new "- " bullet, or a new field under the last component) would
        // otherwise fall outside the range and misresolve to "no context" or "not in components".
        if (item.ComponentsListRange is { } componentsRange && !IsBefore(position, componentsRange.Start))
        {
            var component = FindEnclosing(item.Components, c => c.ItemRange.Start, position);
            return component is null
                ? new NodeContext.ComponentTypeValue(typeKey, null)
                : ResolveWithinComponent(typeKey, component, position);
        }

        foreach (var field in item.TopLevelFields)
        {
            // Hovering an existing field's key or its value both resolve to the same
            // "FieldValue" context - the doc lookup is identical either way. TopLevelFieldKey is
            // reserved for the generic "cursor on a blank/new key position" completion case below.
            if (IsOnValue(field.ValueRange, position) || field.KeyRange.Contains(position))
                return new NodeContext.TopLevelFieldValue(typeKey, field.Name);
        }

        // Generic fallback: cursor is somewhere else inside the item (blank line, new key
        // position) - still offer top-level field completion.
        return new NodeContext.TopLevelFieldKey(typeKey, ExistingNames(item.TopLevelFields));
    }

    private static NodeContext ResolveWithinComponent(string prototypeTypeKey, ComponentItem component, LspPosition position)
    {
        if (IsOnValue(component.TypeValueRange, position))
            return new NodeContext.ComponentTypeValue(prototypeTypeKey, component.TypeValue);

        var componentName = component.TypeValue;

        foreach (var field in component.Fields)
        {
            if (IsOnValue(field.ValueRange, position) || field.KeyRange.Contains(position))
                return new NodeContext.ComponentFieldValue(prototypeTypeKey, componentName ?? string.Empty, field.Name);
        }

        if (componentName is null)
            return new NodeContext.ComponentTypeValue(prototypeTypeKey, null);

        return new NodeContext.ComponentFieldKey(prototypeTypeKey, componentName, ExistingNames(component.Fields));
    }

    /// <summary>
    /// Finds the last item in <paramref name="items"/> whose start is at or before
    /// <paramref name="position"/> - i.e. the item <paramref name="position"/> is currently
    /// "inside", including any trailing blank/in-progress line past the last item's known
    /// content, up to (but not including) the next item's start. Items are assumed to be in
    /// document order and non-overlapping, which the parser already guarantees.
    /// </summary>
    private static T? FindEnclosing<T>(IReadOnlyList<T> items, Func<T, LspPosition> start, LspPosition position)
        where T : class
    {
        T? match = null;
        foreach (var item in items)
        {
            if (IsBefore(position, start(item)))
                break;
            match = item;
        }

        return match;
    }

    private static bool IsBefore(LspPosition a, LspPosition b) =>
        a.Line < b.Line || (a.Line == b.Line && a.Character < b.Character);

    /// <summary>
    /// True when <paramref name="position"/> is on the value's start line, at or after its start
    /// column - deliberately unbounded on the right (unlike a strict Contains check against
    /// <c>range.End</c>), since a value the user is actively typing (or hasn't typed at all yet,
    /// e.g. "key: |" with the cursor right after the trailing space) has a degenerate/empty End
    /// mark that would otherwise make the value position resolve to nothing.
    /// </summary>
    private static bool IsOnValue(LspRange? range, LspPosition position) =>
        range is { } r && position.Line == r.Start.Line && !IsBefore(position, r.Start);

    private static IReadOnlySet<string> ExistingNames(IReadOnlyList<FieldSpan> fields) =>
        fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
