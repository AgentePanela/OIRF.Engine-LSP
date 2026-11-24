using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
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
    private const string ControlFqn = "Engine.Client.UI.Control";
    private const string StyleFieldAttributeFqn = "Engine.Client.UI.StyleFieldAttribute";
    private const string FontFamilyPrototypeFqn = "Engine.Client.Graphics.Fonts.FontFamilyPrototype";

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
        var uiControls = new Dictionary<string, UiControlInfo>(StringComparer.Ordinal);
        var pseudoClasses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var aliasesByControl = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            cancellationToken.ThrowIfCancellationRequested();
            await ProcessProjectAsync(project, prototypes, components, uiControls, pseudoClasses, aliasesByControl, cancellationToken);
        }

        foreach (var (clrName, aliases) in aliasesByControl)
        {
            if (uiControls.TryGetValue(clrName, out var control))
                uiControls[clrName] = control with { Aliases = aliases.Order(StringComparer.OrdinalIgnoreCase).ToList() };
        }

        logger.LogInformation(
            "Schema built: {PrototypeCount} prototype type(s), {ComponentCount} component type(s), {ControlCount} UI control(s)",
            prototypes.Count,
            components.Count,
            uiControls.Count);

        return new EngineSchema(prototypes, components)
        {
            UiControls = uiControls,
            PseudoClasses = pseudoClasses.Order(StringComparer.OrdinalIgnoreCase).ToList(),
        };
    }

    private async Task ProcessProjectAsync(
        Project project,
        Dictionary<string, PrototypeTypeInfo> prototypes,
        Dictionary<string, ComponentTypeInfo> components,
        Dictionary<string, UiControlInfo> uiControls,
        HashSet<string> pseudoClasses,
        Dictionary<string, HashSet<string>> aliasesByControl,
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
                TryAddUiControl(symbol, compilation, uiControls);
            }

            // Both are added from anywhere, so every file is scanned, not just control classes.
            // Most aliases go on a *child* control, not the class adding them - Button tags its own
            // inner Label with `_label.StyleAliasses.Add("button")`, which is why `control: button`
            // rules may set Label properties like color/fontSize - so an alias belongs to the
            // receiver's type, resolved through the semantic model.
            foreach (var invocation in root.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (GetAddCall(invocation, "PseudoClasses") is { } pseudo)
                    pseudoClasses.Add(pseudo.Literal);

                if (GetAddCall(invocation, "StyleAliasses") is { } alias &&
                    ResolveAliasOwner(alias.Owner, invocation, semanticModel, cancellationToken) is { } owner)
                {
                    if (!aliasesByControl.TryGetValue(owner, out var set))
                        aliasesByControl[owner] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    set.Add(alias.Literal);
                }
            }
        }
    }

    /// <summary>
    /// A UI control and its <c>[StyleField]</c> backing fields - the keys a <c>type: style</c>
    /// rule's <c>properties:</c> can set (Engine.Generators' StylePropertyGenerator builds the
    /// runtime property from exactly these). Controls are partial classes, so the first
    /// declaration seen is enough: the symbol already merges every part.
    /// </summary>
    private static void TryAddUiControl(INamedTypeSymbol symbol, Compilation compilation, Dictionary<string, UiControlInfo> controls)
    {
        var clrName = symbol.ToDisplayString();
        if (controls.ContainsKey(clrName))
            return;

        if (clrName != ControlFqn && !DerivesFrom(symbol, ControlFqn))
            return;

        var properties = new List<StylePropertyInfo>();
        foreach (var field in symbol.GetMembers().OfType<IFieldSymbol>())
        {
            var attr = FindAttribute(field, StyleFieldAttributeFqn);
            if (attr is null || GetCtorArg<string>(attr, 0) is not { Length: > 0 } name)
                continue;

            // Backing fields are nullable ("null = not set locally, ask the style") - the value a
            // stylesheet writes is the underlying type.
            var type = UnwrapNullable(field.Type);
            var defaultDisplay = attr.ConstructorArguments.Length > 1 ? FormatStyleDefault(attr.ConstructorArguments[1], type) : null;

            var asset = AssetFieldHeuristics.Classify(field, type, name, clrName);
            // e.g. CheckBox "checkedKey" (default "EngineInternal/Interface/CheckboxChecked"):
            // textures resolved by key, with no [TextureKey] marker on the backing field.
            if (asset is null && type.SpecialType == SpecialType.System_String && name.EndsWith("Key", StringComparison.Ordinal))
                asset = new AssetClassification(AssetKind.Sprite, ["Textures"], HighConfidence: false);

            // A fontFamily prototype id (FontManager.TryGetSystem also accepts a loose .ttf name,
            // offered alongside at completion time).
            var protoId = name == "fontFamily" && type.SpecialType == SpecialType.System_String
                ? new ProtoIdClassification(FontFamilyPrototypeFqn)
                : AssetFieldHeuristics.ClassifyProtoId(type);

            var attributeText = defaultDisplay is null ? $"[StyleField(\"{name}\")]" : $"[StyleField(\"{name}\", {defaultDisplay})]";

            properties.Add(new StylePropertyInfo(
                name,
                type.ToDisplayString(ShortTypeFormat),
                attributeText + "\n" + BuildMemberSignature(field),
                defaultDisplay,
                XmlDocToMarkdown.Convert(field, compilation),
                GetLocation(field),
                GetValueChoices(type),
                asset,
                protoId));
        }

        var baseName = symbol.BaseType is { } baseType && (baseType.ToDisplayString() == ControlFqn || DerivesFrom(baseType, ControlFqn))
            ? baseType.ToDisplayString()
            : null;

        controls[clrName] = new UiControlInfo(
            symbol.Name,
            clrName,
            baseName,
            [], // filled in BuildAsync, see ProcessProjectAsync
            properties,
            BuildTypeSignature(symbol),
            XmlDocToMarkdown.Convert(symbol, compilation),
            GetLocation(symbol));
    }

    /// <summary>
    /// A <c>&lt;owner&gt;.&lt;setName&gt;.Add("literal")</c> call, e.g.
    /// <c>_label.StyleAliasses.Add("button")</c>. <c>Owner</c> is null for the enclosing control
    /// itself (bare <c>StyleAliasses</c> or <c>this.StyleAliasses</c>).
    /// </summary>
    private static (string Literal, ExpressionSyntax? Owner)? GetAddCall(InvocationExpressionSyntax invocation, string setName)
    {
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name.Identifier.Text: "Add" } add)
            return null;

        if (invocation.ArgumentList.Arguments is not [{ Expression: LiteralExpressionSyntax literal }] ||
            !literal.IsKind(SyntaxKind.StringLiteralExpression))
        {
            return null;
        }

        return add.Expression switch
        {
            IdentifierNameSyntax { Identifier.Text: var name } when name == setName => (literal.Token.ValueText, null),
            MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax, Name.Identifier.Text: var name } when name == setName => (literal.Token.ValueText, null),
            MemberAccessExpressionSyntax { Name.Identifier.Text: var name } other when name == setName => (literal.Token.ValueText, other.Expression),
            // owner?.StyleAliasses.Add("...") - e.g. Window's `_closeButton.Content?.StyleAliasses.Add`
            MemberBindingExpressionSyntax { Name.Identifier.Text: var name } when name == setName &&
                invocation.Parent is ConditionalAccessExpressionSyntax conditional => (literal.Token.ValueText, conditional.Expression),
            _ => null,
        };
    }

    private static string? ResolveAliasOwner(ExpressionSyntax? owner, InvocationExpressionSyntax invocation, SemanticModel model, CancellationToken cancellationToken)
    {
        var type = owner is null
            ? invocation.Ancestors().OfType<ClassDeclarationSyntax>().FirstOrDefault() is { } classDecl
                ? model.GetDeclaredSymbol(classDecl, cancellationToken)
                : null
            : model.GetTypeInfo(owner, cancellationToken).Type;

        return type?.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString();
    }

    private static ITypeSymbol UnwrapNullable(ITypeSymbol type) =>
        type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);

    /// <summary>
    /// The <c>[StyleField]</c> default as it would be written in YAML-ish form: an enum member
    /// name, a colour as its 0xRRGGBBAA literal (the engine packs colour defaults as uint), a
    /// number in invariant culture, a quoted string.
    /// </summary>
    private static string? FormatStyleDefault(TypedConstant value, ITypeSymbol fieldType)
    {
        if (value.IsNull)
            return null;

        if (value.Type?.TypeKind == TypeKind.Enum)
        {
            var member = value.Type.GetMembers().OfType<IFieldSymbol>()
                .FirstOrDefault(f => f.HasConstantValue && Equals(f.ConstantValue, value.Value));
            return member?.Name ?? value.ToCSharpString();
        }

        return value.Value switch
        {
            uint packed when fieldType.Name == "Color" => $"0x{packed:X8}",
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => value.ToCSharpString(),
        };
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

            var yamlName = GetCtorArg<string>(dataFieldAttr, 0) ?? ToCamelCase(member.Name);
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
            var protoId = memberType is not null ? AssetFieldHeuristics.ClassifyProtoId(memberType) : null;

            dataFields.Add(new DataFieldInfo(
                yamlName,
                required,
                isMeta,
                isComponentsField,
                memberType?.ToDisplayString() ?? "object",
                BuildMemberSignature(member),
                doc,
                asset,
                GetLocation(member),
                memberType is not null ? GetValueChoices(memberType) : null,
                protoId));
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
            var protoId = memberType is not null ? AssetFieldHeuristics.ClassifyProtoId(memberType) : null;

            members.Add(new MemberInfo(
                ToCamelCase(member.Name),
                memberType?.ToDisplayString() ?? "object",
                BuildMemberSignature(member),
                doc,
                asset,
                GetLocation(member),
                memberType is not null ? GetValueChoices(memberType) : null,
                protoId));
        }

        var info = new ComponentTypeInfo(name, symbol.Name, symbol.ToDisplayString(), classSignature, classDoc, members, classLocation);

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

    /// <summary>
    /// The valid literal values for a field's value-completion, if the field's type is an enum
    /// (member names) or bool ("true"/"false") - unwraps a nullable wrapper first, since
    /// <c>FalloffMode?</c> should offer the same choices as <c>FalloffMode</c>.
    /// </summary>
    private static IReadOnlyList<string>? GetValueChoices(ITypeSymbol type)
    {
        var target = type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            ? nullable.TypeArguments[0]
            : type;

        if (target.SpecialType == SpecialType.System_Boolean)
            return ["true", "false"];

        if (target.TypeKind != TypeKind.Enum)
            return null;

        var values = target.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(f => f.HasConstantValue)
            .Select(f => f.Name)
            .ToList();

        return values.Count > 0 ? values : null;
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

    private static string ToCamelCase(string name)
    {
        if (string.IsNullOrEmpty(name))
            return name;

        if (name.Length == 1)
            return char.ToLowerInvariant(name[0]).ToString();

        return char.ToLowerInvariant(name[0]) + name[1..];
    }
}
