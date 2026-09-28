using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using DocGen.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace DocGen.Indexer;

/// <summary>Walks one loaded solution and builds the CodeIndex. Deterministic, no I/O besides Roslyn.</summary>
internal sealed partial class IndexBuilder(Solution solution, string root, IndexerOptions o)
{
    static readonly string[] LinqPredicates =
        ["Where", "Any", "AnyAsync", "All", "AllAsync", "First", "FirstAsync", "FirstOrDefault", "FirstOrDefaultAsync",
         "Single", "SingleAsync", "SingleOrDefault", "SingleOrDefaultAsync", "Count", "CountAsync"];

    static readonly SymbolDisplayFormat TypeFormat = SymbolDisplayFormat.MinimallyQualifiedFormat
        .AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // ponytail: options types keyed by simple name; collisions across modules would merge, key by symbol if that happens.
    readonly Dictionary<string, INamedTypeSymbol> optionsTypes = new();                  // read via IOptions<T>.Value
    readonly Dictionary<string, (INamedTypeSymbol Type, string Section)> configSections = new(); // Configure<T>(GetSection(x))
    readonly Dictionary<string, string> featureFlags = new();                             // name -> location

    public async Task<CodeIndex> BuildAsync(string commit, CancellationToken ct)
    {
        var entries = new List<IndexEntry>();
        var entityTypes = new Dictionary<INamedTypeSymbol, string>(SymbolEqualityComparer.Default); // type -> project

        foreach (var project in solution.Projects)
        foreach (var document in project.Documents)
        {
            var model = await document.GetSemanticModelAsync(ct);
            var syntaxRoot = await document.GetSyntaxRootAsync(ct);
            if (model is null || syntaxRoot is null)
                continue;

            foreach (var cls in syntaxRoot.DescendantNodes().OfType<ClassDeclarationSyntax>())
            {
                if (model.GetDeclaredSymbol(cls, ct) is not INamedTypeSymbol type)
                    continue;
                if (await ClassEntryAsync(project.Name, type) is { } entry)
                    entries.Add(entry);
                if (IsEntity(type))
                    entityTypes.TryAdd(type, project.Name);
            }

            foreach (var property in syntaxRoot.DescendantNodes().OfType<PropertyDeclarationSyntax>())
                if (model.GetDeclaredSymbol(property, ct) is IPropertySymbol { Type: INamedTypeSymbol { Name: "DbSet", TypeArguments: [var set] } } dbSet)
                    dbSets.TryAdd(set.GetDocumentationCommentId()!, dbSet.Name);

            foreach (var invocation in syntaxRoot.DescendantNodes().OfType<InvocationExpressionSyntax>())
            {
                if (model.GetOperation(invocation, ct) is not IInvocationOperation op)
                    continue;
                if (await EndpointEntryAsync(project.Name, op, model) is { } endpoint)
                    entries.Add(endpoint);
                if (ConfigBinding(op) is var (optionsType, section))
                    configSections[optionsType.Name] = (optionsType, section);
                if (FeatureFlag(op) is { } flag)
                    featureFlags.TryAdd(flag, Location(op.Syntax));
                EfMapping(op);
            }
        }

        entries = entries.DistinctBy(e => e.Id).ToList(); // partial classes / multi-targeted projects
        AttachValidatorsAndBehaviors(entries);
        ResolveConfigKeys(entries);

        var entities = new List<EntityInfo>();
        foreach (var (type, project) in entityTypes)
            entities.Add(await EntityAsync(type, project, ct));

        return new CodeIndex(
            commit,
            entries.Select(Finish).OrderBy(e => e.Id, StringComparer.Ordinal).ToList(),
            entities.OrderBy(e => e.Id, StringComparer.Ordinal).ToList(),
            ConfigKeys().OrderBy(c => c.Key, StringComparer.Ordinal).ToList());
    }

    // --- entry points -------------------------------------------------------------------------

    async Task<IndexEntry?> ClassEntryAsync(string project, INamedTypeSymbol type)
    {
        var (kind, edge, method) = Classify(type);
        if (kind is null)
            return null;

        var entry = NewEntry(type.GetDocumentationCommentId()!, kind, project, Location(type.DeclaringSyntaxReferences[0].GetSyntax()), Dependencies(type));
        if (edge is not null)
            entry.Edges.Add(edge);
        if (method is not null)
            await WalkMethodAsync(entry, method, 0, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        return entry;
    }

    (string?, Edge?, IMethodSymbol?) Classify(INamedTypeSymbol type)
    {
        if (FindInterface(type, o.RequestHandlerInterface) is { } request)
            return ("request_handler", Handles(request), HandleMethod(type, request));
        if (FindInterface(type, o.NotificationHandlerInterface) is { } notification)
            return ("event_handler", Handles(notification), HandleMethod(type, notification));
        if (FindInterface(type, o.PipelineBehaviorInterface) is { } behavior)
            return ("behavior", null, HandleMethod(type, behavior));

        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            if (baseType.Name == o.ValidatorBaseType && Ns(baseType) == o.ValidatorNamespace)
                return ("validator", new Edge("validates", baseType.TypeArguments[0].GetDocumentationCommentId()!), type.InstanceConstructors.FirstOrDefault());

        return (null, null, null);
    }

    async Task<IndexEntry?> EndpointEntryAsync(string project, IInvocationOperation op, SemanticModel model)
    {
        var method = op.TargetMethod;
        if (!method.Name.StartsWith(o.EndpointMethodPrefix) || !Ns(method).StartsWith(o.EndpointNamespace))
            return null;

        var route = op.Arguments.Select(a => a.Value.ConstantValue).Where(c => c is { HasValue: true, Value: string })
            .Select(c => (string)c.Value!).FirstOrDefault();
        var handler = op.Arguments.Select(a => Lambda(a.Value)).OfType<IAnonymousFunctionOperation>().FirstOrDefault();
        if (route is null || handler is null)
            return null;

        var via = model.GetEnclosingSymbol(op.Syntax.SpanStart) is IMethodSymbol enclosing ? Name(enclosing) : "endpoint";
        var entry = NewEntry($"{method.Name[o.EndpointMethodPrefix.Length..].ToUpperInvariant()} {route}", "endpoint", project, Location(op.Syntax), []);
        entry.Chunks.Add(Chunk(via, op.Syntax));
        Maps(entry, handler);
        await WalkAsync(entry, handler.Body, via, 0, new HashSet<ISymbol>(SymbolEqualityComparer.Default));
        return entry;
    }

    // Endpoint input -> command: every constructor/initializer value that is not a plain pass-through of a lambda parameter.
    void Maps(IndexEntry entry, IAnonymousFunctionOperation lambda)
    {
        var inputs = lambda.Symbol.Parameters;
        foreach (var creation in lambda.Body.Descendants().OfType<IObjectCreationOperation>())
        {
            if (creation.Type is not INamedTypeSymbol type || !type.AllInterfaces.Any(i => i.Name == o.RequestInterface && Ns(i) == o.MediatRNamespace))
                continue;
            var values = creation.Arguments.Where(a => a.ArgumentKind != ArgumentKind.DefaultValue && a.Parameter is not null)
                .Select(a => (a.Parameter!.Name, a.Value))
                .Concat(creation.Initializer?.Initializers.OfType<ISimpleAssignmentOperation>()
                    .Where(a => a.Target is IPropertyReferenceOperation)
                    .Select(a => (((IPropertyReferenceOperation)a.Target).Property.Name, a.Value)) ?? []);
            foreach (var (name, value) in values)
                if (!IsPassThrough(value, inputs))
                    Add(entry, new Edge("maps", $"{type.Name}.{name}", value.Syntax.ToString()));
        }
    }

    static bool IsPassThrough(IOperation value, ImmutableArray<IParameterSymbol> inputs) => Unwrap(value) switch
    {
        IParameterReferenceOperation p => inputs.Contains(p.Parameter, SymbolEqualityComparer.Default),
        IPropertyReferenceOperation { Instance: { } instance } => IsPassThrough(instance, inputs),
        _ => false
    };

    // --- walking ------------------------------------------------------------------------------

    async Task WalkMethodAsync(IndexEntry entry, IMethodSymbol method, int depth, HashSet<ISymbol> visited)
    {
        if (!visited.Add(method) || await BodyAsync(method) is not var (syntax, body))
            return;
        entry.Chunks.Add(Chunk(Name(method), syntax));
        await WalkAsync(entry, body, Name(method), depth, visited);
    }

    async Task WalkAsync(IndexEntry entry, IOperation body, string via, int depth, HashSet<ISymbol> visited)
    {
        foreach (var op in body.Descendants())
        {
            switch (op)
            {
                case IConditionalOperation { Syntax: IfStatementSyntax } cond when ExitOf(cond.WhenTrue) is { } exit:
                    AddGuard(entry, "if", cond.Condition.Syntax.ToString(), exit, cond.Syntax, via, Structured(cond.Condition));
                    break;

                case IConditionalOperation { Syntax: not IfStatementSyntax } cond when ThrowType(cond.WhenTrue) is { } thrown:
                    AddGuard(entry, "throw_expr", cond.Condition.Syntax.ToString(), $"throw:{thrown}", cond.Syntax, via, Structured(cond.Condition));
                    break;

                case IConditionalOperation { Syntax: not IfStatementSyntax } cond when ThrowType(cond.WhenFalse) is { } thrown:
                    AddGuard(entry, "throw_expr", $"!({cond.Condition.Syntax})", $"throw:{thrown}", cond.Syntax, via, Negate(Structured(cond.Condition)));
                    break;

                case ICoalesceOperation coalesce when ThrowType(coalesce.WhenNull) is { } thrown:
                    AddGuard(entry, "throw_expr", $"{coalesce.Value.Syntax} is null", $"throw:{thrown}", coalesce.Syntax, via,
                        Path(coalesce.Value) is { } left ? new GuardExpr(left, "is_null", null) : null);
                    break;

                case ISwitchOperation sw:
                    foreach (var section in sw.Cases)
                        if (ExitOf(section.Body.LastOrDefault()) is { } exit)
                            foreach (var clause in section.Clauses)
                                AddGuard(entry, "switch", ClauseText(sw.Value, clause), exit, clause.Syntax, via, ClauseExpr(sw.Value, clause));
                    break;

                case ISwitchExpressionOperation sw:
                    var returned = SkipConversionsUp(sw) is IReturnOperation;
                    foreach (var arm in sw.Arms)
                    {
                        var exit = ThrowType(arm.Value) is { } thrown ? $"throw:{thrown}"
                            : returned && ReturnKind(Unwrap(arm.Value)) is var kind && kind.StartsWith("fail:") ? kind
                            : null;
                        if (exit is not null)
                            AddGuard(entry, "switch", $"{sw.Value.Syntax} is {arm.Pattern.Syntax}{(arm.Guard is null ? "" : $" when {arm.Guard.Syntax}")}",
                                exit, arm.Syntax, via, arm.Guard is null ? PatternExpr(sw.Value, arm.Pattern) : null);
                    }
                    break;

                case IInvocationOperation inv:
                    await InvocationAsync(entry, inv, via, depth, visited);
                    break;

                case IObjectCreationOperation { Type: INamedTypeSymbol created } when IsEvent(created):
                    Add(entry, new Edge("publishes", created.GetDocumentationCommentId()!));
                    break;

                case ISimpleAssignmentOperation { Target: IPropertyReferenceOperation target } assignment
                    when IsEntity(target.Property.ContainingType):
                    Add(entry, new Edge("writes", $"{target.Property.ContainingType.Name}.{target.Property.Name}", ConstantText(assignment.Value)));
                    break;

                case IPropertyReferenceOperation { Instance: IPropertyReferenceOperation { Property.Name: "Value" } value } option
                    when value.Property.ContainingType.Name == "IOptions":
                    optionsTypes[option.Property.ContainingType.Name] = option.Property.ContainingType;
                    Add(entry, new Edge("reads_config", $"{option.Property.ContainingType.Name}.{option.Property.Name}"));
                    break;

                case IPropertyReferenceOperation read when IsEntity(read.Property.ContainingType)
                                                          && !(read.Parent is IAssignmentOperation a && a.Target == read):
                    Add(entry, new Edge("reads", $"{read.Property.ContainingType.Name}.{read.Property.Name}"));
                    break;
            }
        }
    }

    async Task InvocationAsync(IndexEntry entry, IInvocationOperation inv, string via, int depth, HashSet<ISymbol> visited)
    {
        var method = inv.TargetMethod;
        var ns = Ns(method);

        if (GuardCall(inv) is var (exit, structured))
        {
            AddGuard(entry, "guard_call", inv.Syntax.ToString(), exit, inv.Syntax, via, structured);
        }
        else if (method.Name == o.SendMethod && ns == o.MediatRNamespace)
        {
            var request = Unwrap(inv.Arguments[0].Value).Type;
            Add(entry, new Edge("sends", request?.GetDocumentationCommentId() ?? "?", ResultHandling(inv)));
        }
        else if (FeatureFlag(inv) is { } flag)
        {
            Add(entry, new Edge("reads_config", flag));
        }
        else if (method.Name == o.RuleMethod && method.ContainingType.Name == o.ValidatorBaseType)
        {
            var statement = inv.Syntax.FirstAncestorOrSelf<ExpressionStatementSyntax>() ?? inv.Syntax;
            AddGuard(entry, "rule", statement.ToString().TrimEnd(';'), "validation", statement, via, null);
        }
        else if (method.ContainingType.Name == "DbSet" && method.Name is "Add" or "AddAsync" or "Remove" or "Update")
        {
            var kind = method.Name switch { "Remove" => "delete", "Update" => "update", _ => "insert" };
            Add(entry, new Edge(kind, method.ContainingType.TypeArguments[0].Name));
        }
        else if (LinqPredicates.Contains(method.Name) && !IsInSource(method)
                 && inv.Arguments.Select(a => Lambda(a.Value)).OfType<IAnonymousFunctionOperation>().FirstOrDefault() is { } predicate)
        {
            AddGuard(entry, "filter", $"{method.Name}({predicate.Syntax})", "filter", predicate.Syntax, via,
                Returned(predicate) is { } value ? Structured(value) : null);
        }
        else if (ExternalClient(inv) is { } client)
        {
            Add(entry, new Edge("calls_external", client, method.Name));
        }
        else if (IsInSource(method) && depth < o.MaxDepth)
        {
            foreach (var target in await ImplementationsAsync(method.OriginalDefinition))
                await WalkMethodAsync(entry, target, depth + 1, visited);
        }
    }

    async Task<IEnumerable<IMethodSymbol>> ImplementationsAsync(IMethodSymbol method)
    {
        if (method.ContainingType.TypeKind != TypeKind.Interface && !method.IsAbstract)
            return [method];
        var found = await SymbolFinder.FindImplementationsAsync(method, solution);
        return found.OfType<IMethodSymbol>();
    }

    async Task<(SyntaxNode Syntax, IOperation Body)?> BodyAsync(IMethodSymbol method)
    {
        var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
        if (syntax is null || solution.GetDocument(syntax.SyntaxTree) is not { } document)
            return null;
        var model = await document.GetSemanticModelAsync();
        return model?.GetOperation(syntax) is { } body ? (syntax, body) : null;
    }

    // --- guards -------------------------------------------------------------------------------

    string? ExitOf(IOperation? branch)
    {
        var last = branch is IBlockOperation block ? block.Operations.LastOrDefault() : branch;
        if (last is IExpressionStatementOperation statement)
            last = statement.Operation;
        return last switch
        {
            IThrowOperation t => ThrowType(t) is { } type ? $"throw:{type}" : null,
            IReturnOperation { ReturnedValue: null } => "return",
            IReturnOperation { ReturnedValue: var value } => ReturnKind(Unwrap(value)),
            IBlockOperation nested => ExitOf(nested),
            _ => null
        };
    }

    string ReturnKind(IOperation value) => value switch
    {
        IInvocationOperation { TargetMethod: var m } when m.Name == o.OkMethod && m.ContainingType.Name == o.ResultType => "silent_success",
        IInvocationOperation { TargetMethod: var m } fail when m.Name == o.FailMethod && m.ContainingType.Name == o.ResultType =>
            $"fail:{Unwrap(fail.Arguments[0].Value).Type?.Name}",
        IInvocationOperation { TargetMethod.Name: "ToResult" } => "propagate",
        ILocalReferenceOperation or IParameterReferenceOperation => "propagate",
        _ => "return_value"
    };

    static string? ThrowType(IOperation? op)
    {
        var unwrapped = op is null ? null : Unwrap(op);
        if (unwrapped is IExpressionStatementOperation statement)
            unwrapped = statement.Operation;
        return unwrapped is IThrowOperation { Exception: { } exception } ? Unwrap(exception).Type?.Name : null;
    }

    (string Exit, GuardExpr? Structured)? GuardCall(IInvocationOperation inv)
    {
        var callee = (inv.Syntax as InvocationExpressionSyntax)?.Expression.ToString();
        if (callee is null || !o.GuardCallPrefixes.Any(p => callee.StartsWith(p) || callee.Contains("." + p)))
            return null;

        var name = inv.TargetMethod.Name;
        var type = inv.TargetMethod.ContainingType.Name;
        var empty = name.Contains("Empty") || name.Contains("WhiteSpace");
        var exception = type.EndsWith("Exception") ? type : name.Contains("Null") && !empty ? "ArgumentNullException" : "ArgumentException";

        var subject = inv.Arguments.ElementAtOrDefault(inv.TargetMethod.IsExtensionMethod ? 1 : 0)?.Value;
        var op = empty ? "is_empty" : name.Contains("Null") ? "is_null" : null;
        return ($"throw:{exception}", subject is not null && op is not null && Path(subject) is { } left ? new GuardExpr(left, op, null) : null);
    }

    static string ClauseText(IOperation governing, ICaseClauseOperation clause) => clause switch
    {
        ISingleValueCaseClauseOperation single => $"{governing.Syntax} == {single.Value.Syntax}",
        IPatternCaseClauseOperation pattern => $"{governing.Syntax} is {pattern.Pattern.Syntax}{(pattern.Guard is null ? "" : $" when {pattern.Guard.Syntax}")}",
        _ => $"{governing.Syntax}: default"
    };

    GuardExpr? ClauseExpr(IOperation governing, ICaseClauseOperation clause) => clause switch
    {
        ISingleValueCaseClauseOperation single => Compare(governing, "==", single.Value),
        IPatternCaseClauseOperation { Guard: null } pattern => PatternExpr(governing, pattern.Pattern),
        _ => null
    };

    GuardExpr? Structured(IOperation condition) => Unwrap(condition) switch
    {
        IUnaryOperation { OperatorKind: UnaryOperatorKind.Not } not => Negate(Structured(not.Operand)),
        IBinaryOperation binary when Op(binary.OperatorKind) is { } op =>
            Compare(binary.LeftOperand, op, binary.RightOperand) ?? Compare(binary.RightOperand, Flip(op), binary.LeftOperand),
        IIsPatternOperation isPattern => PatternExpr(isPattern.Value, isPattern.Pattern),
        IInvocationOperation { TargetMethod: { Name: "IsNullOrEmpty" or "IsNullOrWhiteSpace", ContainingType.SpecialType: SpecialType.System_String } } inv
            when Path(inv.Arguments[0].Value) is { } left => new GuardExpr(left, "is_empty", null),
        { Type.SpecialType: SpecialType.System_Boolean } member when Path(member) is { } left => new GuardExpr(left, "is_true", null),
        _ => null
    };

    GuardExpr? PatternExpr(IOperation value, IPatternOperation pattern) => pattern switch
    {
        IConstantPatternOperation constant => Compare(value, "==", constant.Value),
        INegatedPatternOperation { Pattern: IConstantPatternOperation constant } => Compare(value, "!=", constant.Value),
        IRelationalPatternOperation relational when Op(relational.OperatorKind) is { } op => Compare(value, op, relational.Value),
        _ => null
    };

    // "member op constant"; null comparisons become is_null / is_not_null.
    GuardExpr? Compare(IOperation left, string op, IOperation right)
    {
        if (Path(left) is not { } path)
            return null;
        if (Unwrap(right).ConstantValue is { HasValue: true, Value: null })
            return op switch { "==" => new(path, "is_null", null), "!=" => new(path, "is_not_null", null), _ => null };
        return ConstantText(right) is { } constant ? new GuardExpr(path, op, constant) : null;
    }

    // "Entity.Property" for entity members, else the source text of a member/variable path.
    string? Path(IOperation op) => Unwrap(op) switch
    {
        IPropertyReferenceOperation p when IsEntity(p.Property.ContainingType) => $"{p.Property.ContainingType.Name}.{p.Property.Name}",
        IPropertyReferenceOperation or ILocalReferenceOperation or IParameterReferenceOperation => op.Syntax.ToString(),
        IFieldReferenceOperation { Field.IsConst: false } => op.Syntax.ToString(),
        _ => null
    };

    static GuardExpr? Negate(GuardExpr? expr) => expr is null ? null : expr with
    {
        Op = expr.Op switch
        {
            "==" => "!=", "!=" => "==", "<" => ">=", ">=" => "<", ">" => "<=", "<=" => ">",
            "is_null" => "is_not_null", "is_not_null" => "is_null",
            "is_empty" => "is_not_empty", "is_not_empty" => "is_empty",
            "is_true" => "is_false", _ => "is_true"
        }
    };

    static string Flip(string op) => op switch { "<" => ">", ">" => "<", "<=" => ">=", ">=" => "<=", _ => op };

    static string? Op(BinaryOperatorKind kind) => kind switch
    {
        BinaryOperatorKind.Equals => "==",
        BinaryOperatorKind.NotEquals => "!=",
        BinaryOperatorKind.LessThan => "<",
        BinaryOperatorKind.LessThanOrEqual => "<=",
        BinaryOperatorKind.GreaterThan => ">",
        BinaryOperatorKind.GreaterThanOrEqual => ">=",
        _ => null
    };

    void AddGuard(IndexEntry entry, string kind, string condition, string exit, SyntaxNode node, string via, GuardExpr? structured) =>
        entry.Guards.Add(new Guard("", kind, condition, exit, Location(node), via, structured));

    // --- edges --------------------------------------------------------------------------------

    // What the caller does with the Result returned by ISender.Send.
    static string ResultHandling(IInvocationOperation send)
    {
        var parent = send.Parent is IAwaitOperation awaited ? awaited.Parent : send.Parent;
        if (parent is IReturnOperation)
            return "propagated";
        if (parent?.Parent is not IVariableDeclaratorOperation { Symbol: var local })
            return "ignored";

        var scope = send.Parent;
        while (scope is not null and not IAnonymousFunctionOperation and not IMethodBodyOperation)
            scope = scope.Parent;
        var ops = (scope ?? send).Descendants().ToList();

        bool References(IOperation o) => o.DescendantsAndSelf().OfType<ILocalReferenceOperation>()
            .Any(r => SymbolEqualityComparer.Default.Equals(r.Local, local));

        var branches = ops.OfType<IConditionalOperation>().Where(c => References(c.Condition))
            .SelectMany(c => new[] { c.WhenTrue, c.WhenFalse }).OfType<IOperation>()
            .SelectMany(b => b.DescendantsAndSelf()).OfType<IInvocationOperation>().ToList();

        if (branches.Any(i => i.TargetMethod.ContainingType.Name == "Results"))
            return "http";
        if (ops.OfType<IReturnOperation>().Any(r => r.ReturnedValue is not null && References(r.ReturnedValue)))
            return "propagated";
        if (branches.Any(i => i.TargetMethod.Name.StartsWith("Log")))
            return "logged";
        return ops.OfType<ILocalReferenceOperation>().Any(r => SymbolEqualityComparer.Default.Equals(r.Local, local))
            ? "used"
            : "ignored";
    }

    string? FeatureFlag(IInvocationOperation inv) =>
        inv.TargetMethod.Name == o.FeatureFlagMethod && Ns(inv.TargetMethod).StartsWith(o.FeatureManagementNamespace)
            ? inv.Arguments[0].Value.ConstantValue.Value?.ToString() ?? "?"
            : null;

    // HttpClient or a typed "*Client" from a package (in-source clients are walked instead).
    static string? ExternalClient(IInvocationOperation inv)
    {
        if (Receiver(inv)?.Type is not INamedTypeSymbol receiver)
            return null;
        if (receiver.Name == "HttpClient" && Ns(receiver) == "System.Net.Http")
            return receiver.Name;
        return receiver.Name.EndsWith("Client") && !receiver.Locations.Any(l => l.IsInSource) ? receiver.Name : null;
    }

    static (INamedTypeSymbol, string)? ConfigBinding(IInvocationOperation op)
    {
        if (op.TargetMethod is not { Name: "Configure", TypeArguments: [INamedTypeSymbol optionsType] })
            return null;
        var section = op.Descendants().OfType<IInvocationOperation>()
            .FirstOrDefault(i => i.TargetMethod.Name == "GetSection")?.Arguments[0].Value.ConstantValue;
        return section is { HasValue: true, Value: string name } ? (optionsType, name) : null;
    }

    // --- post-processing ----------------------------------------------------------------------

    static void AttachValidatorsAndBehaviors(List<IndexEntry> entries)
    {
        var validators = entries.Where(e => e.Kind == "validator")
            .ToLookup(e => e.Edges.First(x => x.Type == "validates").Target);
        var behaviors = entries.Where(e => e.Kind == "behavior").Select(e => e.Id).ToList();

        foreach (var handler in entries.Where(e => e.Kind == "request_handler"))
        {
            var request = handler.Edges.First(x => x.Type == "handles").Target;
            handler.Guards.InsertRange(0, validators[request].SelectMany(v => v.Guards));
            handler.Chunks.InsertRange(0, validators[request].SelectMany(v => v.Chunks));
            handler.Edges.AddRange(behaviors.Select(b => new Edge("behavior", b)));
        }
    }

    void ResolveConfigKeys(List<IndexEntry> entries)
    {
        foreach (var entry in entries)
            for (var i = 0; i < entry.Edges.Count; i++)
            {
                var edge = entry.Edges[i];
                var dot = edge.Target.IndexOf('.');
                if (edge.Type != "reads_config" || dot < 0 || !optionsTypes.TryGetValue(edge.Target[..dot], out var type))
                    continue;

                var property = edge.Target[(dot + 1)..];
                var key = configSections.TryGetValue(type.Name, out var binding) ? $"{binding.Section}:{property}" : edge.Target;
                var initializer = Initializer(type.GetMembers(property).FirstOrDefault());
                entry.Edges[i] = new Edge("reads_config", key, initializer is null ? null : $"default {initializer}");
            }
    }

    IEnumerable<ConfigKey> ConfigKeys() =>
        featureFlags.Select(f => new ConfigKey(f.Key, "feature_flag", null, f.Value))
            .Concat(configSections.Values.SelectMany(binding => binding.Type.GetMembers().OfType<IPropertySymbol>()
                .Where(p => p is { DeclaredAccessibility: Accessibility.Public, IsStatic: false } && p.DeclaringSyntaxReferences.Length > 0)
                .Select(p => new ConfigKey($"{binding.Section}:{p.Name}", "option", Initializer(p), Location(p.DeclaringSyntaxReferences[0].GetSyntax())))))
            .DistinctBy(c => c.Key);

    static string? Initializer(ISymbol? property) =>
        property?.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() is PropertyDeclarationSyntax { Initializer: { } init } ? init.Value.ToString() : null;

    static IndexEntry Finish(IndexEntry entry)
    {
        var guards = entry.Guards.Select((g, i) => g with { Id = $"G{i + 1}" }).ToList();
        var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { entry.Chunks, guards, entry.Edges }, DocGenJson.Options));
        return entry with { Guards = guards, Hash = Convert.ToHexStringLower(hash) };
    }

    // --- helpers ------------------------------------------------------------------------------

    IndexEntry NewEntry(string id, string kind, string project, string location, List<string> dependencies)
    {
        var (module, layer) = ModuleLayer(project);
        return new IndexEntry(id, kind, module, layer, location, dependencies, [], [], [], "");
    }

    // "Payments.Application" -> ("Payments", "Application"); "BuildingBlocks" -> ("BuildingBlocks", "").
    internal static (string Module, string Layer) ModuleLayer(string project) =>
        project.LastIndexOf('.') is var dot and >= 0 ? (project[..dot], project[(dot + 1)..]) : (project, "");

    static List<string> Dependencies(INamedTypeSymbol type) =>
        type.InstanceConstructors.OrderByDescending(c => c.Parameters.Length).FirstOrDefault()?.Parameters
            .Select(p => p.Type.ToDisplayString(TypeFormat)).ToList() ?? [];

    string Location(SyntaxNode node)
    {
        var span = node.GetLocation().GetLineSpan();
        return $"{System.IO.Path.GetRelativePath(root, span.Path).Replace('\\', '/')}:{span.StartLinePosition.Line + 1}";
    }

    CodeChunk Chunk(string symbol, SyntaxNode syntax) =>
        new(symbol, Location(syntax), syntax.GetLocation().GetLineSpan().EndLinePosition.Line + 1, syntax.ToString());

    INamedTypeSymbol? FindInterface(INamedTypeSymbol type, string name) =>
        type.AllInterfaces.FirstOrDefault(i => i.Name == name && Ns(i) == o.MediatRNamespace);

    static IMethodSymbol? HandleMethod(INamedTypeSymbol type, INamedTypeSymbol iface) =>
        iface.GetMembers("Handle").Select(type.FindImplementationForInterfaceMember).OfType<IMethodSymbol>().FirstOrDefault();

    static Edge Handles(INamedTypeSymbol iface) => new("handles", iface.TypeArguments[0].GetDocumentationCommentId()!);

    static bool IsInSource(IMethodSymbol method) => method.Locations.Any(l => l.IsInSource);

    bool IsEntity(ITypeSymbol? type) =>
        type is INamedTypeSymbol { TypeKind: TypeKind.Class, IsStatic: false } cls
        && o.EntityNamespaceSuffixes.Any(Ns(cls).EndsWith)
        && !IsEvent(cls)
        && !InheritsFrom(cls, o.ResultNamespace, o.ErrorBaseType);

    bool IsEvent(INamedTypeSymbol type) =>
        type.AllInterfaces.Any(i => i.Name == o.NotificationInterface && Ns(i) == o.MediatRNamespace);

    static bool InheritsFrom(INamedTypeSymbol type, string ns, string name)
    {
        for (var baseType = type.BaseType; baseType is not null; baseType = baseType.BaseType)
            if (baseType.Name == name && Ns(baseType) == ns)
                return true;
        return false;
    }

    static string Ns(ISymbol symbol) => symbol.ContainingNamespace?.ToDisplayString() ?? "";

    static IOperation Unwrap(IOperation op) => op is IConversionOperation c ? Unwrap(c.Operand) : op;

    static IOperation? SkipConversionsUp(IOperation op)
    {
        var parent = op.Parent;
        while (parent is IConversionOperation)
            parent = parent.Parent;
        return parent;
    }

    // Receiver of an instance call, or the `this` argument of an extension call.
    static IOperation? Receiver(IInvocationOperation inv)
    {
        var receiver = inv.TargetMethod.IsExtensionMethod ? inv.Arguments.FirstOrDefault()?.Value : inv.Instance;
        return receiver is null ? null : Unwrap(receiver);
    }

    // Lambda passed as Delegate, Func<> or Expression<Func<>> is wrapped in conversions and/or a delegate creation.
    static IAnonymousFunctionOperation? Lambda(IOperation op) => Unwrap(op) switch
    {
        IAnonymousFunctionOperation lambda => lambda,
        IDelegateCreationOperation creation => Lambda(creation.Target),
        _ => null
    };

    static IOperation? Returned(IAnonymousFunctionOperation lambda) =>
        lambda.Body.Operations.FirstOrDefault() is IReturnOperation { ReturnedValue: { } value } ? value : null;

    static string? ConstantText(IOperation value) => Unwrap(value) switch
    {
        IFieldReferenceOperation { Field.ContainingType.TypeKind: TypeKind.Enum } f => $"{f.Field.ContainingType.Name}.{f.Field.Name}",
        { ConstantValue: { HasValue: true, Value: var v } } => v switch
        {
            null => "null",
            string s => $"\"{s}\"",
            bool b => b ? "true" : "false",
            _ => Convert.ToString(v, CultureInfo.InvariantCulture)
        },
        _ => null
    };

    static string Name(IMethodSymbol method) => $"{method.ContainingType.Name}.{method.Name}";

    static void Add(IndexEntry entry, Edge edge)
    {
        if (!entry.Edges.Contains(edge))
            entry.Edges.Add(edge);
    }
}
