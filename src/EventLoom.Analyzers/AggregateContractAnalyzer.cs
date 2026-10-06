using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace EventLoom.Analyzers;

/// <summary>
/// Reports EventLoom aggregate, event, and snapshot rules that the C# type system cannot express.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class AggregateContractAnalyzer : DiagnosticAnalyzer
{
    public const string InvalidSelfTypeDiagnosticId = "EL0101";
    public const string RaiseInHandlerDiagnosticId = "EL0102";
    public const string RaiseInConstructorDiagnosticId = "EL0103";
    public const string DirectHandlerCallDiagnosticId = "EL0104";
    public const string NondeterministicHandlerDiagnosticId = "EL0105";
    public const string NonConstantIdentityDiagnosticId = "EL0106";
    public const string DuplicateIdentityDiagnosticId = "EL0107";
    public const string MutableContractDiagnosticId = "EL0108";
    public const string ForeignApplyHandlerDiagnosticId = "EL0109";
    public const string StateChangedOutsideApplyDiagnosticId = "EL0110";

    private const string Category = "EventLoom";

    private static readonly DiagnosticDescriptor InvalidSelfType = new(
        InvalidSelfTypeDiagnosticId,
        "Aggregate must pass itself as TSelf",
        "Aggregate '{0}' must derive from Aggregate<{0}, TId>, not Aggregate<{1}, TId>",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Concrete aggregates pass themselves as TSelf; abstract generic bases forward their own TSelf type parameter.");

    private static readonly DiagnosticDescriptor RaiseInHandler = new(
        RaiseInHandlerDiagnosticId,
        "Do not raise events from Apply or snapshot callbacks",
        "'{0}' raises an event from an Apply or snapshot callback",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Handlers run again on every replay. Raising from them duplicates events; raise only from command methods.");

    private static readonly DiagnosticDescriptor RaiseInConstructor = new(
        RaiseInConstructorDiagnosticId,
        "Do not raise events from aggregate constructors",
        "Constructor of '{0}' raises an event",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "The aggregate factory runs on every load. Raise creation events from a command method instead.");

    private static readonly DiagnosticDescriptor DirectHandlerCall = new(
        DirectHandlerCallDiagnosticId,
        "Do not call Apply or snapshot callbacks directly",
        "Direct call to '{0}' changes aggregate state without recording an event",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Use Raise to apply events. EventLoom invokes snapshot callbacks when saving and loading.");

    private static readonly DiagnosticDescriptor NondeterministicHandler = new(
        NondeterministicHandlerDiagnosticId,
        "Apply and snapshot callbacks must be deterministic",
        "'{0}' uses non-deterministic '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Replay must reproduce the same state. Put clocks, random values, identifiers, and I/O results in the event instead.");

    private static readonly DiagnosticDescriptor NonConstantIdentity = new(
        NonConstantIdentityDiagnosticId,
        "Persisted identity must be a valid constant",
        "'{0}.{1}' must return {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Event and snapshot names and versions are persisted; they must be compile-time constants with valid values.");

    private static readonly DiagnosticDescriptor DuplicateIdentity = new(
        DuplicateIdentityDiagnosticId,
        "Persisted identity is declared more than once",
        "{0} type '{1}' is declared by multiple types: {2}",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Each persisted event or snapshot name maps to exactly one type. Model schema changes with versions and upcasters.",
        customTags: WellKnownDiagnosticTags.CompilationEnd);

    private static readonly DiagnosticDescriptor MutableContract = new(
        MutableContractDiagnosticId,
        "Events and snapshots must be immutable",
        "'{0}' declares mutable member '{1}'",
        Category,
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        description: "Use init-only properties and readonly fields so persisted history cannot change after it is raised.");

    private static readonly DiagnosticDescriptor ForeignApplyHandler = new(
        ForeignApplyHandlerDiagnosticId,
        "Apply handler is never used",
        "Aggregate '{0}' handles '{1}', which it does not own",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: true,
        description: "EventLoom only dispatches events owned by the aggregate through IDomainEvent<TSelf, TAggregate>.");

    private static readonly DiagnosticDescriptor StateChangedOutsideApply = new(
        StateChangedOutsideApplyDiagnosticId,
        "Change aggregate state only in Apply handlers",
        "'{0}' assigns '{1}' outside an Apply or RestoreSnapshot callback",
        Category,
        DiagnosticSeverity.Info,
        isEnabledByDefault: false,
        description: "State changed outside handlers is lost on replay. Raise an event and change state in its handler.");

    private static readonly string[] NondeterministicTypes =
    [
        "System.Random",
        "System.TimeProvider",
        "System.Environment",
        "System.Diagnostics.Stopwatch",
        "System.Security.Cryptography.RandomNumberGenerator",
        "System.IO.File",
        "System.IO.Directory",
        "System.Net.Http.HttpClient",
        "System.Threading.Tasks.Task"
    ];

    private static readonly (string Type, string Member)[] NondeterministicMembers =
    [
        ("System.DateTime", "Now"),
        ("System.DateTime", "UtcNow"),
        ("System.DateTime", "Today"),
        ("System.DateTimeOffset", "Now"),
        ("System.DateTimeOffset", "UtcNow"),
        ("System.Guid", "NewGuid"),
        ("System.Guid", "CreateVersion7")
    ];

    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(
            InvalidSelfType,
            RaiseInHandler,
            RaiseInConstructor,
            DirectHandlerCall,
            NondeterministicHandler,
            NonConstantIdentity,
            DuplicateIdentity,
            MutableContract,
            ForeignApplyHandler,
            StateChangedOutsideApply);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(startContext =>
        {
            var symbols = KnownSymbols.Create(startContext.Compilation);
            if (symbols is null)
            {
                return;
            }

            var identities = new ConcurrentBag<DeclaredIdentity>();
            startContext.RegisterSymbolAction(value => AnalyzeType(value, symbols), SymbolKind.NamedType);
            startContext.RegisterSymbolAction(value => AnalyzeMethod(value, symbols), SymbolKind.Method);
            startContext.RegisterOperationAction(
                value => AnalyzeInvocation(value, symbols),
                OperationKind.Invocation);
            startContext.RegisterOperationAction(
                value => AnalyzeMemberUse(value, symbols),
                OperationKind.PropertyReference,
                OperationKind.FieldReference,
                OperationKind.MethodReference,
                OperationKind.ObjectCreation);
            startContext.RegisterOperationAction(
                value => AnalyzeAssignment(value, symbols),
                OperationKind.SimpleAssignment,
                OperationKind.CompoundAssignment,
                OperationKind.CoalesceAssignment,
                OperationKind.Increment,
                OperationKind.Decrement);
            startContext.RegisterOperationBlockAction(value => AnalyzeIdentityGetter(value, symbols, identities));
            startContext.RegisterOperationAction(
                value => AnalyzeIdentityInitializer(value, symbols, identities),
                OperationKind.PropertyInitializer);
            startContext.RegisterCompilationEndAction(value => ReportDuplicateIdentities(value, identities));
        });
    }

    private static void AnalyzeType(SymbolAnalysisContext context, KnownSymbols symbols)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        var location = type.Locations.FirstOrDefault(static value => value.IsInSource);
        if (location is null)
        {
            return;
        }

        var aggregateBase = symbols.FindAggregateBase(type);
        if (aggregateBase is not null && type.TypeKind == TypeKind.Class)
        {
            AnalyzeAggregate(context, symbols, type, aggregateBase, location);
        }

        foreach (var contract in symbols.GetIdentityContracts(type))
        {
            AnalyzeContractMutability(context, type);
            AnalyzeAutoPropertyIdentity(context, type, contract);
            break;
        }
    }

    private static void AnalyzeAggregate(
        SymbolAnalysisContext context,
        KnownSymbols symbols,
        INamedTypeSymbol type,
        INamedTypeSymbol aggregateBase,
        Location location)
    {
        var self = aggregateBase.TypeArguments[0];
        var validSelf = SymbolEqualityComparer.Default.Equals(self, type) ||
                        (type.IsAbstract && self is ITypeParameterSymbol);
        if (!validSelf)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                InvalidSelfType,
                location,
                type.Name,
                self.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
        }

        if (type.IsAbstract)
        {
            return;
        }

        var ownedContract = symbols.DomainEventOwner.Construct(type);
        foreach (var handler in type.AllInterfaces.Where(value =>
                     SymbolEqualityComparer.Default.Equals(value.OriginalDefinition, symbols.Apply)))
        {
            var eventType = handler.TypeArguments[0];
            if (!eventType.AllInterfaces.Contains(ownedContract, SymbolEqualityComparer.Default))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    ForeignApplyHandler,
                    location,
                    type.Name,
                    eventType.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            }
        }
    }

    private static void AnalyzeContractMutability(SymbolAnalysisContext context, INamedTypeSymbol type)
    {
        foreach (var member in type.GetMembers())
        {
            var mutable = member switch
            {
                IPropertySymbol property => !property.IsStatic &&
                                            property.SetMethod is { IsInitOnly: false },
                IFieldSymbol field => !field.IsStatic &&
                                      !field.IsReadOnly &&
                                      !field.IsConst &&
                                      !field.IsImplicitlyDeclared &&
                                      field.AssociatedSymbol is null,
                _ => false
            };
            if (!mutable)
            {
                continue;
            }

            var location = member.Locations.FirstOrDefault(static value => value.IsInSource) ??
                           type.Locations.First(static value => value.IsInSource);
            context.ReportDiagnostic(Diagnostic.Create(MutableContract, location, type.Name, member.Name));
        }
    }

    private static void AnalyzeAutoPropertyIdentity(
        SymbolAnalysisContext context,
        INamedTypeSymbol type,
        IdentityContract contract)
    {
        foreach (var role in contract.Roles)
        {
            if (type.FindImplementationForInterfaceMember(role.Member) is not IPropertySymbol implementation ||
                !SymbolEqualityComparer.Default.Equals(implementation.ContainingType, type))
            {
                continue;
            }

            foreach (var reference in implementation.DeclaringSyntaxReferences)
            {
                if (reference.GetSyntax(context.CancellationToken) is PropertyDeclarationSyntax
                    {
                        ExpressionBody: null, Initializer: null, AccessorList: { } accessors
                    } declaration &&
                    accessors.Accessors.All(static accessor =>
                        accessor.Body is null && accessor.ExpressionBody is null))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        NonConstantIdentity,
                        declaration.Identifier.GetLocation(),
                        type.Name,
                        role.Member.Name,
                        role.Expectation));
                }
            }
        }
    }

    private static void AnalyzeMethod(SymbolAnalysisContext context, KnownSymbols symbols)
    {
        var method = (IMethodSymbol)context.Symbol;
        if (method.IsAsync && symbols.IsHandlerMethod(method))
        {
            var location = method.Locations.FirstOrDefault(static value => value.IsInSource);
            if (location is not null)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    NondeterministicHandler,
                    location,
                    method.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
                    "async"));
            }
        }
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context, KnownSymbols symbols)
    {
        var invocation = (IInvocationOperation)context.Operation;
        var target = invocation.TargetMethod;
        var containing = context.ContainingSymbol as IMethodSymbol;

        if (target.Name == "Raise" &&
            SymbolEqualityComparer.Default.Equals(target.ContainingType?.OriginalDefinition, symbols.Aggregate) &&
            containing is not null)
        {
            if (containing.MethodKind == MethodKind.Constructor)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RaiseInConstructor,
                    invocation.Syntax.GetLocation(),
                    containing.ContainingType.Name));
            }
            else if (symbols.IsHandlerMethod(containing))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    RaiseInHandler,
                    invocation.Syntax.GetLocation(),
                    containing.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
            }
        }

        if (symbols.IsHandlerMember(target) || symbols.IsHandlerMethod(target))
        {
            context.ReportDiagnostic(Diagnostic.Create(
                DirectHandlerCall,
                invocation.Syntax.GetLocation(),
                target.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat)));
        }

        ReportNondeterminism(context, symbols, target);
    }

    private static void AnalyzeMemberUse(OperationAnalysisContext context, KnownSymbols symbols)
    {
        ISymbol? member = context.Operation switch
        {
            IPropertyReferenceOperation value => value.Property,
            IFieldReferenceOperation value => value.Field,
            IMethodReferenceOperation value => value.Method,
            IObjectCreationOperation value => value.Constructor,
            _ => null
        };
        if (member is not null)
        {
            ReportNondeterminism(context, symbols, member);
        }
    }

    private static void ReportNondeterminism(OperationAnalysisContext context, KnownSymbols symbols, ISymbol member)
    {
        if (context.ContainingSymbol is not IMethodSymbol containing ||
            !symbols.IsHandlerMethod(containing) ||
            !symbols.IsNondeterministic(member))
        {
            return;
        }

        var name = member is IMethodSymbol { MethodKind: MethodKind.Constructor }
            ? member.ContainingType.Name
            : $"{member.ContainingType.Name}.{member.Name}";
        context.ReportDiagnostic(Diagnostic.Create(
            NondeterministicHandler,
            context.Operation.Syntax.GetLocation(),
            containing.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
            name));
    }

    private static void AnalyzeAssignment(OperationAnalysisContext context, KnownSymbols symbols)
    {
        var target = context.Operation switch
        {
            IAssignmentOperation value => value.Target,
            IIncrementOrDecrementOperation value => value.Target,
            _ => null
        };
        ISymbol? member = target switch
        {
            IFieldReferenceOperation { Field.IsStatic: false, Instance: IInstanceReferenceOperation } value =>
                value.Field,
            IPropertyReferenceOperation { Property.IsStatic: false, Instance: IInstanceReferenceOperation } value =>
                value.Property,
            _ => null
        };
        if (member is null ||
            context.ContainingSymbol is not IMethodSymbol containing ||
            containing.MethodKind is MethodKind.Constructor or MethodKind.StaticConstructor ||
            symbols.FindAggregateBase(containing.ContainingType) is null ||
            symbols.IsHandlerMethod(containing))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            StateChangedOutsideApply,
            context.Operation.Syntax.GetLocation(),
            containing.ToDisplayString(SymbolDisplayFormat.CSharpShortErrorMessageFormat),
            member.Name));
    }

    private static void AnalyzeIdentityGetter(
        OperationBlockAnalysisContext context,
        KnownSymbols symbols,
        ConcurrentBag<DeclaredIdentity> identities)
    {
        if (context.OwningSymbol is not IMethodSymbol { MethodKind: MethodKind.PropertyGet } getter ||
            getter.AssociatedSymbol is not IPropertySymbol property ||
            !symbols.TryGetIdentityRole(property, out var contract, out var role))
        {
            return;
        }

        foreach (var block in context.OperationBlocks)
        {
            foreach (var returned in block.DescendantsAndSelf().OfType<IReturnOperation>())
            {
                ValidateIdentityValue(
                    context.ReportDiagnostic,
                    identities,
                    property,
                    contract,
                    role,
                    returned.ReturnedValue,
                    returned.Syntax.GetLocation());
            }
        }
    }

    private static void AnalyzeIdentityInitializer(
        OperationAnalysisContext context,
        KnownSymbols symbols,
        ConcurrentBag<DeclaredIdentity> identities)
    {
        var initializer = (IPropertyInitializerOperation)context.Operation;
        foreach (var property in initializer.InitializedProperties)
        {
            if (symbols.TryGetIdentityRole(property, out var contract, out var role))
            {
                ValidateIdentityValue(
                    context.ReportDiagnostic,
                    identities,
                    property,
                    contract,
                    role,
                    initializer.Value,
                    initializer.Syntax.GetLocation());
            }
        }
    }

    private static void ValidateIdentityValue(
        System.Action<Diagnostic> report,
        ConcurrentBag<DeclaredIdentity> identities,
        IPropertySymbol property,
        IdentityContract contract,
        IdentityRole role,
        IOperation? value,
        Location location)
    {
        var constant = value?.ConstantValue ?? default;
        var valid = constant.HasValue && (role.IsName
            ? constant.Value is string name && !string.IsNullOrWhiteSpace(name)
            : constant.Value is int version && version > 0);
        if (!valid)
        {
            report(Diagnostic.Create(
                NonConstantIdentity,
                location,
                property.ContainingType.Name,
                role.Member.Name,
                role.Expectation));
            return;
        }

        if (role.IsName)
        {
            identities.Add(new DeclaredIdentity(
                contract.Kind,
                (string)constant.Value!,
                property.ContainingType,
                property.ContainingType.Locations.FirstOrDefault(static item => item.IsInSource) ?? location));
        }
    }

    private static void ReportDuplicateIdentities(
        CompilationAnalysisContext context,
        ConcurrentBag<DeclaredIdentity> identities)
    {
        foreach (var group in identities.GroupBy(static value => (value.Kind, value.Name)))
        {
            var types = group
                .GroupBy(static value => value.Type, SymbolEqualityComparer.Default)
                .Select(static value => value.First())
                .OrderBy(static value => value.Type.ToDisplayString(), System.StringComparer.Ordinal)
                .ToArray();
            if (types.Length < 2)
            {
                continue;
            }

            var names = string.Join(
                ", ",
                types.Select(static value => value.Type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
            foreach (var duplicate in types)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    DuplicateIdentity,
                    duplicate.Location,
                    group.Key.Kind,
                    group.Key.Name,
                    names));
            }
        }
    }

    private sealed class KnownSymbols
    {
        private readonly ImmutableHashSet<INamedTypeSymbol> nondeterministicTypes;
        private readonly ImmutableArray<(INamedTypeSymbol Type, string Member)> nondeterministicMembers;

        private KnownSymbols(
            INamedTypeSymbol aggregate,
            INamedTypeSymbol apply,
            INamedTypeSymbol domainEventOwner,
            INamedTypeSymbol domainEvent,
            INamedTypeSymbol snapshot,
            INamedTypeSymbol snapshotable,
            ImmutableHashSet<INamedTypeSymbol> nondeterministicTypes,
            ImmutableArray<(INamedTypeSymbol Type, string Member)> nondeterministicMembers)
        {
            Aggregate = aggregate;
            Apply = apply;
            DomainEventOwner = domainEventOwner;
            DomainEvent = domainEvent;
            Snapshot = snapshot;
            Snapshotable = snapshotable;
            this.nondeterministicTypes = nondeterministicTypes;
            this.nondeterministicMembers = nondeterministicMembers;
        }

        public INamedTypeSymbol Aggregate { get; }

        public INamedTypeSymbol Apply { get; }

        public INamedTypeSymbol DomainEventOwner { get; }

        public INamedTypeSymbol DomainEvent { get; }

        public INamedTypeSymbol Snapshot { get; }

        public INamedTypeSymbol Snapshotable { get; }

        public static KnownSymbols? Create(Compilation compilation)
        {
            var aggregate = compilation.GetTypeByMetadataName("EventLoom.Aggregate`2");
            var apply = compilation.GetTypeByMetadataName("EventLoom.IApply`1");
            var owner = compilation.GetTypeByMetadataName("EventLoom.IDomainEvent`1");
            var domainEvent = compilation.GetTypeByMetadataName("EventLoom.IDomainEvent`2");
            var snapshot = compilation.GetTypeByMetadataName("EventLoom.IAggregateSnapshot`2");
            var snapshotable = compilation.GetTypeByMetadataName("EventLoom.ISnapshotable`1");
            if (aggregate is null || apply is null || owner is null || domainEvent is null ||
                snapshot is null || snapshotable is null)
            {
                return null;
            }

            var types = NondeterministicTypes
                .Select(compilation.GetTypeByMetadataName)
                .Where(static value => value is not null)
                .Select(static value => value!)
                .ToImmutableHashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
            var members = NondeterministicMembers
                .Select(value => (Type: compilation.GetTypeByMetadataName(value.Type), value.Member))
                .Where(static value => value.Type is not null)
                .Select(static value => (value.Type!, value.Member))
                .ToImmutableArray();
            return new KnownSymbols(aggregate, apply, owner, domainEvent, snapshot, snapshotable, types, members);
        }

        public INamedTypeSymbol? FindAggregateBase(INamedTypeSymbol? type)
        {
            for (var current = type?.BaseType; current is not null; current = current.BaseType)
            {
                if (SymbolEqualityComparer.Default.Equals(current.OriginalDefinition, Aggregate))
                {
                    return current;
                }
            }

            return null;
        }

        public IEnumerable<IdentityContract> GetIdentityContracts(INamedTypeSymbol type)
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            {
                yield break;
            }

            foreach (var contract in type.AllInterfaces)
            {
                if (SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, DomainEvent))
                {
                    yield return new IdentityContract("Event", contract, "EventType", "EventVersion");
                }
                else if (SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, Snapshot))
                {
                    yield return new IdentityContract("Snapshot", contract, "SnapshotType", "SnapshotVersion");
                }
            }
        }

        public bool TryGetIdentityRole(IPropertySymbol property, out IdentityContract contract, out IdentityRole role)
        {
            if (property.IsStatic)
            {
                foreach (var candidate in GetIdentityContracts(property.ContainingType))
                {
                    foreach (var candidateRole in candidate.Roles)
                    {
                        if (SymbolEqualityComparer.Default.Equals(
                                property.ContainingType.FindImplementationForInterfaceMember(candidateRole.Member),
                                property))
                        {
                            contract = candidate;
                            role = candidateRole;
                            return true;
                        }
                    }
                }
            }

            contract = null!;
            role = null!;
            return false;
        }

        public bool IsHandlerMember(IMethodSymbol method) =>
            method.ContainingType is { } type &&
            (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, Apply) ||
             SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, Snapshotable));

        public bool IsHandlerMethod(IMethodSymbol method)
        {
            if (method.ExplicitInterfaceImplementations.Any(IsHandlerMember))
            {
                return true;
            }

            if (method.MethodKind != MethodKind.Ordinary || method.IsStatic ||
                method.Name is not ("Apply" or "CreateSnapshot" or "RestoreSnapshot"))
            {
                return false;
            }

            foreach (var contract in method.ContainingType.AllInterfaces)
            {
                if (!SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, Apply) &&
                    !SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, Snapshotable))
                {
                    continue;
                }

                foreach (var member in contract.GetMembers(method.Name))
                {
                    if (SymbolEqualityComparer.Default.Equals(
                            method.ContainingType.FindImplementationForInterfaceMember(member),
                            method))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        public bool IsNondeterministic(ISymbol member)
        {
            var type = member.ContainingType;
            if (type is null)
            {
                return false;
            }

            if (nondeterministicTypes.Contains(type.OriginalDefinition))
            {
                return true;
            }

            foreach (var candidate in nondeterministicMembers)
            {
                if (candidate.Member == member.Name &&
                    SymbolEqualityComparer.Default.Equals(candidate.Type, type))
                {
                    return true;
                }
            }

            return false;
        }
    }

    private sealed class IdentityContract
    {
        public IdentityContract(string kind, INamedTypeSymbol contract, string nameMember, string versionMember)
        {
            Kind = kind;
            Roles = contract.GetMembers()
                .OfType<IPropertySymbol>()
                .Where(value => value.Name == nameMember || value.Name == versionMember)
                .Select(value => new IdentityRole(
                    value,
                    value.Name == nameMember,
                    value.Name == nameMember ? "a non-empty constant string" : "a positive constant integer"))
                .ToImmutableArray();
        }

        public string Kind { get; }

        public ImmutableArray<IdentityRole> Roles { get; }
    }

    private sealed class IdentityRole(IPropertySymbol member, bool isName, string expectation)
    {
        public IPropertySymbol Member { get; } = member;

        public bool IsName { get; } = isName;

        public string Expectation { get; } = expectation;
    }

    private readonly struct DeclaredIdentity(string kind, string name, INamedTypeSymbol type, Location location)
    {
        public string Kind { get; } = kind;

        public string Name { get; } = name;

        public INamedTypeSymbol Type { get; } = type;

        public Location Location { get; } = location;
    }
}
