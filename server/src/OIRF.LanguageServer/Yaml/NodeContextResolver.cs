namespace OIRF.LanguageServer.Yaml;

public static class NodeContextResolver
{
    /// <param name="isStyleType">
    /// Whether a prototype <c>type:</c> is the engine's StylePrototype - only then is a
    /// <c>rules:</c> list resolved as style rules. Decided by the caller, which has the schema.
    /// </param>
    public static NodeContext Resolve(PrototypeDocument document, LspPosition position, Func<string, bool>? isStyleType = null)
    {
        var item = FindEnclosing(document.Items, i => i.ItemRange.Start, position);
        return item is null ? new NodeContext.None() : ResolveWithinItem(item, position, isStyleType);
    }

    private static NodeContext ResolveWithinItem(PrototypeItem item, LspPosition position, Func<string, bool>? isStyleType)
    {
        var typeKey = item.TypeValue ?? string.Empty;

        if (IsOnValue(item.TypeValueRange, position))
            return new NodeContext.PrototypeTypeValue(item.TypeValue);

        if (IsOnValue(item.IdValueRange, position))
            return new NodeContext.PrototypeIdValue();

        if (IsOnValue(item.ParentValueRange, position) || IsWithinLines(item.ParentFullRange, position))
        {
            return new NodeContext.ParentValue(
                typeKey,
                item.IdValue,
                item.Parents.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase));
        }

        if (item.StyleRulesRange is { } rulesRange && isStyleType?.Invoke(typeKey) == true && IsInRulesRegion(item, rulesRange, position))
            return ResolveWithinStyleRules(item, position);

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
            if (IsOnValue(field.ValueRange, position) || IsInScalarSequence(field, position) || field.KeyRange.Contains(position))
                return new NodeContext.TopLevelFieldValue(typeKey, field.Name);
        }

        // Generic fallback: cursor is somewhere else inside the item (blank line, new key
        // position) - still offer top-level field completion.
        return new NodeContext.TopLevelFieldKey(typeKey, ExistingNames(item.TopLevelFields));
    }

    // From the first rule to just before the next top-level key written after "rules:" (if any).
    private static bool IsInRulesRegion(PrototypeItem item, LspRange rulesRange, LspPosition position)
    {
        if (IsBefore(position, rulesRange.Start))
            return false;

        var nextKey = item.TopLevelFields
            .Select(f => f.KeyRange.Start)
            .Where(start => IsBefore(rulesRange.Start, start))
            .OrderBy(start => start.Line).ThenBy(start => start.Character)
            .FirstOrDefault();

        return nextKey is null || IsBefore(position, nextKey);
    }

    /// <summary>
    /// Inside a <c>rules:</c> list. Each line belongs to the last rule-level key at or above it
    /// (class/properties): past that key on its own line, or indented deeper than the rule's keys
    /// on a following line, the cursor is in that key's value; otherwise it's at a new rule-level key.
    /// </summary>
    private static NodeContext ResolveWithinStyleRules(PrototypeItem item, LspPosition position)
    {
        var rule = FindEnclosing(item.StyleRules, r => r.ItemRange.Start, position);
        if (rule is null)
            return new NodeContext.StyleRuleKey(new HashSet<string>());

        var ruleKeys = rule.Fields.Select(f => f.Name).ToHashSet(StringComparer.Ordinal);
        var owner = rule.Fields.LastOrDefault(f => f.KeyRange.Start.Line <= position.Line);
        if (owner is null)
            return new NodeContext.StyleRuleKey(ruleKeys);

        var insideValue = position.Line == owner.KeyRange.Start.Line
            ? !IsBefore(position, owner.KeyRange.End) && !owner.KeyRange.Contains(position)
            : position.Character > rule.ItemRange.Start.Character;

        if (!insideValue)
            return new NodeContext.StyleRuleKey(ruleKeys);

        switch (owner.Name)
        {
            case "class":
                return new NodeContext.StyleSelector(
                    rule.Selector.Select(f => f.Name).ToHashSet(StringComparer.Ordinal),
                    EntryAt(rule.Selector, position));

            case "properties":
                var property = EntryAt(rule.Properties, position);
                if (property is not null && (property.KeyRange.Start.Line == position.Line || IsInScalarSequence(property, position)))
                    return new NodeContext.StylePropertyValue(rule.Control, property.Name);

                return new NodeContext.StylePropertyKey(rule.Control, rule.Properties.Select(f => f.Name).ToHashSet(StringComparer.Ordinal));

            default:
                return new NodeContext.None();
        }
    }

    /// <summary>
    /// The entry the cursor is on: the last one starting at or before it on the cursor's line
    /// (a flow mapping holds several per line), or one whose list value spans the cursor's line.
    /// </summary>
    private static FieldSpan? EntryAt(IReadOnlyList<FieldSpan> entries, LspPosition position) =>
        entries.LastOrDefault(e => e.KeyRange.Start.Line == position.Line && !IsBefore(position, e.KeyRange.Start))
        ?? entries.FirstOrDefault(e => IsInScalarSequence(e, position));

    /// <summary>Within the range's lines - from its start column on the first line, anywhere after.</summary>
    private static bool IsWithinLines(LspRange? range, LspPosition position) =>
        range is { } r &&
        position.Line >= r.Start.Line && position.Line <= r.End.Line &&
        (position.Line != r.Start.Line || !IsBefore(position, r.Start));

    private static NodeContext ResolveWithinComponent(string prototypeTypeKey, ComponentItem component, LspPosition position)
    {
        if (IsOnValue(component.TypeValueRange, position))
            return new NodeContext.ComponentTypeValue(prototypeTypeKey, component.TypeValue);

        var componentName = component.TypeValue;

        foreach (var field in component.Fields)
        {
            if (IsOnValue(field.ValueRange, position) || IsInScalarSequence(field, position) || field.KeyRange.Contains(position))
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

    // Line-based like IsOnValue: any line from the sequence's first item to its last, at or
    // after the column the items start at (so a dedented new key after the list isn't caught).
    private static bool IsInScalarSequence(FieldSpan field, LspPosition position) =>
        field.ScalarSequenceRange is { } r &&
        position.Line >= r.Start.Line && position.Line <= r.End.Line &&
        position.Character >= r.Start.Character;

    private static IReadOnlySet<string> ExistingNames(IReadOnlyList<FieldSpan> fields) =>
        fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
