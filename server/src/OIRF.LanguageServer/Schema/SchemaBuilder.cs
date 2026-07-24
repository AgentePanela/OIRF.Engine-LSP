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
        var classSignature = BuildTypeSignature(symbol);
        var classLocation = GetLocation(symbol);

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
                ? AssetFieldHeuristics.Classify(member, memberType, yamlName, symbol.ToDisplayString())
                : null;

            dataFields.Add(new DataFieldInfo(
                yamlName,
                required,
                isMeta,
                isComponentsField,
                memberType?.ToDisplayString() ?? "object",
                BuildMemberSignature(member),
                doc,
                asset,
                GetLocation(member)));
        }

        var info = new PrototypeTypeInfo(typeKey, loadPriority, symbol.ToDisplayString(), classSignature, isInheriting, classDoc, dataFields, classLocation);

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
        var classSignature = BuildTypeSignature(symbol);
        var classLocation = GetLocation(symbol);

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
                ? AssetFieldHeuristics.Classify(member, memberType, member.Name, symbol.ToDisplayString())
                : null;

            members.Add(new MemberInfo(
                member.Name,
                memberType?.ToDisplayString() ?? "object",
                BuildMemberSignature(member),
                doc,
                asset,
                GetLocation(member)));
        }

        var info = new ComponentTypeInfo(name, symbol.ToDisplayString(), classSignature, classDoc, members, classLocation);

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

    private static readonly SymbolDisplayFormat ShortTypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat;

    /// <summary>
    /// A short, human/C#-looking one-line declaration for a hover code block - not a real
    /// round-trippable signature (no attributes, no full generic constraints), just enough for
    /// the hover to render like the C# extension's does via a ```csharp fenced block.
    /// </summary>
    private static string BuildTypeSignature(INamedTypeSymbol symbol)
    {
        var keyword = symbol.TypeKind switch
        {
            TypeKind.Interface => "interface",
            TypeKind.Struct => symbol.IsRecord ? "record struct" : "struct",
            _ => symbol.IsRecord ? "record" : "class",
        };

        var modifiers = new List<string> { "public" };
        if (symbol.IsSealed && symbol.TypeKind == TypeKind.Class)
            modifiers.Add("sealed");
        modifiers.Add(keyword);

        var baseList = new List<string>();
        if (symbol.BaseType is { SpecialType: not SpecialType.System_Object } baseType)
            baseList.Add(baseType.Name);
        baseList.AddRange(symbol.Interfaces.Select(i => i.Name));

        var baseClause = baseList.Count > 0 ? " : " + string.Join(", ", baseList) : string.Empty;
        return $"{string.Join(' ', modifiers)} {symbol.Name}{baseClause}";
    }

    private static string BuildMemberSignature(ISymbol member)
    {
        var accessibility = member.DeclaredAccessibility == Accessibility.Public ? "public " : string.Empty;

        return member switch
        {
            IPropertySymbol p => $"{accessibility}{p.Type.ToDisplayString(ShortTypeFormat)} {p.Name} {{ get;{(p.SetMethod is not null ? " set;" : string.Empty)} }}",
            IFieldSymbol f => $"{accessibility}{f.Type.ToDisplayString(ShortTypeFormat)} {f.Name};",
            _ => member.ToDisplayString(),
        };
    }

    private static SymbolLocation? GetLocation(ISymbol symbol)
    {
        var location = symbol.Locations.FirstOrDefault(l => l.IsInSource);
        if (location is null)
            return null;

        var span = location.GetLineSpan();
        if (!span.IsValid)
            return null;

        return new SymbolLocation(
            span.Path,
            new LspRange(
                new LspPosition(span.StartLinePosition.Line, span.StartLinePosition.Character),
                new LspPosition(span.EndLinePosition.Line, span.EndLinePosition.Character)));
    }

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
