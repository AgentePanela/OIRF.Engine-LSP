using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;

namespace OIRF.LanguageServer.Schema;

/// <summary>
/// Walks the Roslyn semantic model of every engine-relevant project to discover
/// <c>[Prototype]</c>/<c>[RegisterComponent]</c> types and their <c>[DataField]</c>/public
/// members - built-in engine types and any custom ones a downstream project defines, picked up
/// identically since both come from the same source-level reflection, no compiled-assembly
/// distinction is made.
/// </summary>
public sealed class SchemaBuilder(ILogger<SchemaBuilder> logger)
{
    private const string IPrototypeFqn = "Engine.Shared.Prototypes.IPrototype";
    private const string IInheritingPrototypeFqn = "Engine.Shared.Prototypes.IInheritingPrototype";
    private const string PrototypeAttributeFqn = "Engine.Shared.Prototypes.PrototypeAttribute";
    private const string DataFieldAttributeFqn = "Engine.Shared.Prototypes.DataFieldAttribute";
    private const string ComponentsDataFieldAttributeFqn = "Engine.Shared.Prototypes.ComponentsDataFieldAttribute";
    private const string ComponentFqn = "Engine.Shared.GameObjects.Component";
    private const string RegisterComponentAttributeFqn = "Engine.Shared.GameObjects.RegisterComponentAttribute";

    // Mirrors PrototypeManager.PopulateInstance's special-casing of these four keys: they're
    // structural/meta, not generic [DataField] members, so the "missing required field" check
    // must skip them.
    private static readonly HashSet<string> MetaFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "type", "id", "abstract", "parent",
    };

    public async Task<EngineSchema> BuildAsync(IReadOnlyList<Project> projects, CancellationToken cancellationToken)
    {
        var prototypes = new Dictionary<string, PrototypeTypeInfo>(StringComparer.OrdinalIgnoreCase);
        var components = new Dictionary<string, ComponentTypeInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessProjectAsync(project, prototypes, components, cancellationToken);
        }

        logger.LogInformation(
            "Schema built: {PrototypeCount} prototype type(s), {ComponentCount} component type(s)",
            prototypes.Count,
            components.Count);

        return new EngineSchema(prototypes, components);
    }

    private async Task ProcessProjectAsync(
        Project project,
        Dictionary<string, PrototypeTypeInfo> prototypes,
        Dictionary<string, ComponentTypeInfo> components,
        CancellationToken cancellationToken)
    {
        Compilation? compilation;
        try
        {
            compilation = await project.GetCompilationAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to get compilation for project {Project}", project.Name);
            return;
        }

        if (compilation is null)
            return;

        foreach (var document in project.Documents)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var syntaxTree = await document.GetSyntaxTreeAsync(cancellationToken);
            if (syntaxTree is null)
                continue;

            var root = await syntaxTree.GetRootAsync(cancellationToken);
            var semanticModel = compilation.GetSemanticModel(syntaxTree);

            foreach (var classDecl in root.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (semanticModel.GetDeclaredSymbol(classDecl, cancellationToken) is not INamedTypeSymbol symbol)
                    continue;

                TryAddPrototype(symbol, compilation, prototypes);
                TryAddComponent(symbol, compilation, components);
            }
        }
    }

    private void TryAddPrototype(INamedTypeSymbol symbol, Compilation compilation, Dictionary<string, PrototypeTypeInfo> prototypes)
    {
        var prototypeAttr = FindAttribute(symbol, PrototypeAttributeFqn);
        if (prototypeAttr is null)
            return;

        if (!ImplementsInterface(symbol, IPrototypeFqn))
        {
            logger.LogWarning("{Type} has [Prototype] but does not implement IPrototype; skipping.", symbol.ToDisplayString());
            return;
        }

        if (GetCtorArg<string>(prototypeAttr, 0) is not { Length: > 0 } typeKey)
            return;

        var loadPriority = GetCtorArg<int?>(prototypeAttr, 1) ?? 0;
        var isInheriting = ImplementsInterface(symbol, IInheritingPrototypeFqn);
        var classDoc = XmlDocToMarkdown.Convert(symbol, compilation);

        var dataFields = new List<DataFieldInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in WalkMembers(symbol))
        {
            var dataFieldAttr = FindAttribute(member, DataFieldAttributeFqn);
            if (dataFieldAttr is null)
                continue;

            var yamlName = GetCtorArg<string>(dataFieldAttr, 0) ?? member.Name;
            if (!seenNames.Add(yamlName))
                continue; // a more-derived override already supplied this field

            var required = GetCtorArg<bool?>(dataFieldAttr, 1) ?? false;
            var isComponentsField = FindAttribute(member, ComponentsDataFieldAttributeFqn) is not null;
            var isMeta = MetaFieldNames.Contains(yamlName);
            var memberType = GetMemberType(member);
            var doc = XmlDocToMarkdown.Convert(member, compilation);
            var asset = memberType is not null
                ? AssetFieldHeuristics.Classify(memberType, yamlName, symbol.ToDisplayString())
                : null;

            dataFields.Add(new DataFieldInfo(
                yamlName,
                required,
                isMeta,
                isComponentsField,
                memberType?.ToDisplayString() ?? "object",
                doc,
                asset));
        }

        var info = new PrototypeTypeInfo(typeKey, loadPriority, symbol.ToDisplayString(), isInheriting, classDoc, dataFields);

        if (!prototypes.TryAdd(typeKey, info))
        {
            logger.LogWarning(
                "Duplicate [Prototype(\"{TypeKey}\")]: {New} ignored, {Existing} kept.",
                typeKey, symbol.ToDisplayString(), prototypes[typeKey].ClrTypeName);
        }
    }

    private void TryAddComponent(INamedTypeSymbol symbol, Compilation compilation, Dictionary<string, ComponentTypeInfo> components)
    {
        var registerAttr = FindAttribute(symbol, RegisterComponentAttributeFqn);
        if (registerAttr is null)
            return;

        if (!DerivesFrom(symbol, ComponentFqn))
        {
            logger.LogWarning("{Type} has [RegisterComponent] but does not derive from Component; skipping.", symbol.ToDisplayString());
            return;
        }

        if (GetCtorArg<string>(registerAttr, 0) is not { Length: > 0 } name)
            return;

        var classDoc = XmlDocToMarkdown.Convert(symbol, compilation);

        var members = new List<MemberInfo>();
        var seenNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var member in WalkMembers(symbol))
        {
            // Lifecycle/identity members declared directly on Component itself (Owner, Deleted,
            // State) aren't meant to be authored from YAML - exclude them, but keep members
            // declared on intermediate subclasses.
            if (member.ContainingType.ToDisplayString() == ComponentFqn)
                continue;

            if (!IsPublicSettable(member))
                continue;

            if (!seenNames.Add(member.Name))
                continue;

            var memberType = GetMemberType(member);
            var doc = XmlDocToMarkdown.Convert(member, compilation);
            var asset = memberType is not null
                ? AssetFieldHeuristics.Classify(memberType, member.Name, symbol.ToDisplayString())
                : null;

            members.Add(new MemberInfo(member.Name, memberType?.ToDisplayString() ?? "object", doc, asset));
        }

        var info = new ComponentTypeInfo(name, symbol.ToDisplayString(), classDoc, members);

        if (!components.TryAdd(name, info))
        {
            logger.LogWarning(
                "Duplicate [RegisterComponent(\"{Name}\")]: {New} ignored, {Existing} kept.",
                name, symbol.ToDisplayString(), components[name].ClrTypeName);
        }
    }

    private static IEnumerable<ISymbol> WalkMembers(INamedTypeSymbol symbol)
    {
        for (var current = symbol; current is not null && current.SpecialType != SpecialType.System_Object; current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member is IPropertySymbol or IFieldSymbol)
                    yield return member;
            }
        }
    }

    private static bool IsPublicSettable(ISymbol member) => member switch
    {
        IPropertySymbol { DeclaredAccessibility: Accessibility.Public, SetMethod.DeclaredAccessibility: Accessibility.Public } => true,
        IFieldSymbol { DeclaredAccessibility: Accessibility.Public, IsReadOnly: false, IsConst: false } => true,
        _ => false,
    };

    private static ITypeSymbol? GetMemberType(ISymbol member) => member switch
    {
        IPropertySymbol p => p.Type,
        IFieldSymbol f => f.Type,
        _ => null,
    };

    private static AttributeData? FindAttribute(ISymbol symbol, string attributeFqn) =>
        symbol.GetAttributes().FirstOrDefault(a => a.AttributeClass?.ToDisplayString() == attributeFqn);

    private static T? GetCtorArg<T>(AttributeData attribute, int index)
    {
        if (index >= attribute.ConstructorArguments.Length)
            return default;

        return attribute.ConstructorArguments[index].Value is T value ? value : default;
    }

    private static bool ImplementsInterface(INamedTypeSymbol symbol, string interfaceFqn) =>
        symbol.AllInterfaces.Any(i => i.ToDisplayString() == interfaceFqn);

    private static bool DerivesFrom(INamedTypeSymbol symbol, string baseTypeFqn)
    {
        for (var current = symbol.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString() == baseTypeFqn)
                return true;
        }

        return false;
    }
}
