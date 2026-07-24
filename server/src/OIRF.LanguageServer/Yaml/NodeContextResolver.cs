namespace OIRF.LanguageServer.Yaml;

public static class NodeContextResolver
{
    public static NodeContext Resolve(PrototypeDocument document, LspPosition position)
    {
        foreach (var item in document.Items)
        {
            if (item.ItemRange.Contains(position))
                return ResolveWithinItem(item, position);
        }

        return new NodeContext.None();
    }

    private static NodeContext ResolveWithinItem(PrototypeItem item, LspPosition position)
    {
        var typeKey = item.TypeValue ?? string.Empty;

        if (item.TypeValueRange?.Contains(position) == true)
            return new NodeContext.PrototypeTypeValue(item.TypeValue);

        if (item.IdValueRange?.Contains(position) == true)
            return new NodeContext.PrototypeIdValue();

        if (item.ParentValueRange?.Contains(position) == true)
            return new NodeContext.ParentValue();

        if (item.ComponentsListRange?.Contains(position) == true)
        {
            foreach (var component in item.Components)
            {
                if (component.ItemRange.Contains(position))
                    return ResolveWithinComponent(typeKey, component, position);
            }

            // Inside the components list but not within any parsed component item yet -
            // e.g. a fresh "- " bullet the user just typed with no `type:` on it yet.
            return new NodeContext.ComponentTypeValue(typeKey, null);
        }

        foreach (var field in item.TopLevelFields)
        {
            // Hovering an existing field's key or its value both resolve to the same
            // "FieldValue" context - the doc lookup is identical either way. TopLevelFieldKey is
            // reserved for the generic "cursor on a blank/new key position" completion case below.
            if (field.ValueRange.Contains(position) || field.KeyRange.Contains(position))
                return new NodeContext.TopLevelFieldValue(typeKey, field.Name);
        }

        // Generic fallback: cursor is somewhere else inside the item (blank line, new key
        // position) - still offer top-level field completion.
        return new NodeContext.TopLevelFieldKey(typeKey, ExistingNames(item.TopLevelFields));
    }

    private static NodeContext ResolveWithinComponent(string prototypeTypeKey, ComponentItem component, LspPosition position)
    {
        if (component.TypeValueRange?.Contains(position) == true)
            return new NodeContext.ComponentTypeValue(prototypeTypeKey, component.TypeValue);

        var componentName = component.TypeValue;

        foreach (var field in component.Fields)
        {
            if (field.ValueRange.Contains(position) || field.KeyRange.Contains(position))
                return new NodeContext.ComponentFieldValue(prototypeTypeKey, componentName ?? string.Empty, field.Name);
        }

        if (componentName is null)
            return new NodeContext.ComponentTypeValue(prototypeTypeKey, null);

        return new NodeContext.ComponentFieldKey(prototypeTypeKey, componentName, ExistingNames(component.Fields));
    }

    private static IReadOnlySet<string> ExistingNames(IReadOnlyList<FieldSpan> fields) =>
        fields.Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
}
