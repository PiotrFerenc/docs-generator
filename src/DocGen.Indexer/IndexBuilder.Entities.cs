using System.Text.RegularExpressions;
using DocGen.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace DocGen.Indexer;

// Entities: fields, EF table/column mapping (static read of Fluent config), status transitions, raised events.
internal sealed partial class IndexBuilder
{
    readonly Dictionary<string, string> tables = new();                           // entity id -> ToTable(...)
    readonly Dictionary<(string Entity, string Property), string> columns = new(); // HasColumnName(...)
    readonly Dictionary<string, string> dbSets = new();                           // entity id -> DbSet property name

    // ToTable / HasColumnName anywhere (OnModelCreating lambdas, IEntityTypeConfiguration<T>.Configure, chains):
    // the entity comes from the EntityTypeBuilder<T> receiver, the property from the Property(p => p.X) call in the chain.
    void EfMapping(IInvocationOperation inv)
    {
        if (inv.TargetMethod.Name is not ("ToTable" or "HasColumnName")
            || inv.Arguments.Select(a => a.Value.ConstantValue).FirstOrDefault(c => c is { HasValue: true, Value: string }) is not { Value: string name })
            return;

        if (inv.TargetMethod.Name == "ToTable")
        {
            if (BuilderEntity(Receiver(inv)) is { } entity)
                tables[entity] = name;
            return;
        }

        for (var receiver = Receiver(inv); receiver is IInvocationOperation call; receiver = Receiver(call))
            if (call.TargetMethod.Name == "Property" && BuilderEntity(Receiver(call)) is { } entity && PropertyName(call) is { } property)
            {
                columns[(entity, property)] = name;
                return;
            }
    }

    static string? BuilderEntity(IOperation? receiver) =>
        receiver?.Type is INamedTypeSymbol { Name: "EntityTypeBuilder", TypeArguments: [var entity] } ? entity.GetDocumentationCommentId() : null;

    static string? PropertyName(IInvocationOperation propertyCall) =>
        propertyCall.Arguments.Select(a => a.Value)
            .Select(v => Lambda(v) is { } lambda
                ? Returned(lambda) is { } body && Unwrap(body) is IPropertyReferenceOperation p ? p.Property.Name : null
                : v.ConstantValue is { HasValue: true, Value: string s } ? s : null)
            .FirstOrDefault(n => n is not null);

    async Task<EntityInfo> EntityAsync(INamedTypeSymbol type, string project, CancellationToken ct)
    {
        var id = type.GetDocumentationCommentId()!;
        var table = tables.GetValueOrDefault(id) ?? (dbSets.TryGetValue(id, out var set) ? Naming(set) : null);

        // Columns only for mapped entities (table known); a convention name for an unpersisted type would be a guess.
        var fields = type.GetMembers().OfType<IPropertySymbol>()
            .Where(p => p is { DeclaredAccessibility: Accessibility.Public, IsStatic: false, IsIndexer: false })
            .Select(p => new EntityField(p.Name, p.Type.ToDisplayString(TypeFormat),
                table is null ? null : columns.GetValueOrDefault((id, p.Name)) ?? Naming(p.Name), EnumValues(p.Type)))
            .ToList();

        var transitions = new List<StatusTransition>();
        var events = new List<string>();
        foreach (var method in type.GetMembers().OfType<IMethodSymbol>())
        {
            ct.ThrowIfCancellationRequested();
            if (method.MethodKind is not (MethodKind.Ordinary or MethodKind.Constructor) || await BodyAsync(method) is not var (_, body))
                continue;

            var ops = body.Descendants().ToList();
            var failGuards = ops.OfType<IConditionalOperation>()
                .Where(c => c.Syntax is IfStatementSyntax && ExitOf(c.WhenTrue) is { } exit && (exit.StartsWith("fail:") || exit.StartsWith("throw:")))
                .ToList();

            foreach (var assignment in ops.OfType<ISimpleAssignmentOperation>())
            {
                if (assignment.Target is not IPropertyReferenceOperation target
                    || !SymbolEqualityComparer.Default.Equals(target.Property.ContainingType, type)
                    || Unwrap(assignment.Value) is not IFieldReferenceOperation { Field: { ContainingType.TypeKind: TypeKind.Enum } member })
                    continue;

                // "if (Field != X) fail" in the same method => transition from X.
                var field = $"{type.Name}.{target.Property.Name}";
                var from = failGuards.Select(g => (Guard: g, Expr: Structured(g.Condition)))
                    .FirstOrDefault(g => g.Expr is { Op: "!=", Right: not null } e && e.Left == field);
                var condition = from.Guard ?? failGuards.FirstOrDefault();
                transitions.Add(new StatusTransition(target.Property.Name, from.Expr?.Right!.Split('.')[^1], member.Name,
                    Name(method), Location(assignment.Syntax), condition?.Condition.Syntax.ToString()));
            }

            events.AddRange(ops.OfType<IObjectCreationOperation>().Select(c => c.Type).OfType<INamedTypeSymbol>()
                .Where(IsEvent).Select(e => e.GetDocumentationCommentId()!));
        }

        return new EntityInfo(id, type.Name, ModuleLayer(project).Module, Location(type.DeclaringSyntaxReferences[0].GetSyntax()),
            table, fields, transitions, events.Distinct().ToList());
    }

    static List<string>? EnumValues(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T, TypeArguments: [var inner] })
            type = inner;
        return type.TypeKind == TypeKind.Enum ? type.GetMembers().OfType<IFieldSymbol>().Where(f => f.IsConst).Select(f => f.Name).ToList() : null;
    }

    string Naming(string name) => o.ColumnNaming == "snake_case"
        ? Regex.Replace(name, "(?<=[a-z0-9])([A-Z])|(?<=[A-Z])([A-Z][a-z])", "_$1$2").ToLowerInvariant()
        : name;
}
