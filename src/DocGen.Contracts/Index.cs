namespace DocGen.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// index.json — produced by DocGen.Indexer (Roslyn), consumed by DocGen.Cards.
// Deterministic: no LLM involved. Locations are "<path relative to repo root>:<1-based line>".
// ─────────────────────────────────────────────────────────────────────────────

public sealed record CodeIndex(
    string Commit,
    List<IndexEntry> Entries,
    List<EntityInfo> Entities,
    List<ConfigKey> Config);

/// <summary>One entry point: a place where business logic starts.</summary>
/// <param name="Id">Documentation comment id ("T:Ns.Type") or "VERB /route" for endpoints.</param>
/// <param name="Kind">request_handler | event_handler | validator | behavior | endpoint</param>
/// <param name="Module">From project name: "Payments.Application" → "Payments".</param>
/// <param name="Layer">From project name: "Payments.Application" → "Application"; "" when absent.</param>
/// <param name="Dependencies">Constructor parameter types of the entry class, display form ("IOptions&lt;PayoutOptions&gt;"); empty for endpoints.</param>
/// <param name="Guards">In execution order (validator rules first for request handlers). Ids "G1".."Gn" are unique per entry.</param>
/// <param name="Chunks">Source of the entry method and every method in its call closure.</param>
/// <param name="Hash">SHA-256 (hex) over all chunk texts + guards + edges; changes whenever anything relevant changes.</param>
public sealed record IndexEntry(
    string Id,
    string Kind,
    string Module,
    string Layer,
    string Location,
    List<string> Dependencies,
    List<Guard> Guards,
    List<Edge> Edges,
    List<CodeChunk> Chunks,
    string Hash);

/// <param name="Kind">if | rule | filter | switch | throw_expr | guard_call</param>
/// <param name="Exit">
/// fail:&lt;ErrorType&gt; | silent_success | return | return_value | propagate | validation | filter | throw:&lt;ExceptionType&gt;
/// </param>
/// <param name="Via">"Type.Method" where the guard lives.</param>
/// <param name="Structured">Filled for simple "member op constant" comparisons, else null.</param>
public sealed record Guard(
    string Id,
    string Kind,
    string Condition,
    string Exit,
    string Location,
    string Via,
    GuardExpr? Structured = null);

/// <param name="Left">"Entity.Property" or parameter path, e.g. "CustomerAccount.KycStatus".</param>
/// <param name="Op">== | != | &lt; | &lt;= | &gt; | &gt;= | is_null | is_not_null | is_empty | is_not_empty | is_true | is_false</param>
/// <param name="Right">Constant text ("KycStatus.Verified", "0") or null for unary ops.</param>
public sealed record GuardExpr(string Left, string Op, string? Right);

/// <param name="Type">
/// handles | validates | sends | publishes | reads | writes | insert | delete | update | reads_config | behavior | maps | calls_external
/// </param>
/// <param name="Detail">
/// sends: propagated | logged | http | used | ignored;
/// reads: Target "Entity.Property" (entity property read anywhere in the closure, incl. LINQ predicates);
/// writes: assigned constant ("PayoutStatus.Failed") or null;
/// reads_config: "default &lt;value&gt;" or null;
/// maps (endpoint input → command): expression text, Target = command property name;
/// calls_external: HttpClient / typed client name.
/// </param>
public sealed record Edge(string Type, string Target, string? Detail = null)
{
    public override string ToString() => Detail is null ? $"{Type} {Target}" : $"{Type} {Target} [{Detail}]";
}

/// <param name="Symbol">"Type.Method".</param>
/// <param name="Location">Start "path:line".</param>
public sealed record CodeChunk(string Symbol, string Location, int EndLine, string Text);

/// <summary>Domain entity (type in a "*.Domain" namespace, or configured).</summary>
/// <param name="Id">"T:Ns.Type".</param>
/// <param name="Table">From EF Fluent config ToTable(...) or naming convention; null if unknown.</param>
public sealed record EntityInfo(
    string Id,
    string Name,
    string Module,
    string Location,
    string? Table,
    List<EntityField> Fields,
    List<StatusTransition> Transitions,
    List<string> RaisedEvents);

/// <param name="EnumValues">Member names when Type is an enum, else null.</param>
public sealed record EntityField(string Name, string Type, string? Column, List<string>? EnumValues);

/// <param name="From">Required current value if the method guards on it (e.g. "Pending"), else null.</param>
/// <param name="To">Assigned enum member name without type prefix (e.g. "Completed").</param>
/// <param name="Method">"Type.Method" doing the assignment.</param>
/// <param name="Condition">Guard text in that method that must pass, else null.</param>
public sealed record StatusTransition(string Field, string? From, string To, string Method, string Location, string? Condition);

/// <param name="Key">"Payouts.Enabled" (feature flag) or "Payouts:MaxAmount" (options).</param>
/// <param name="Kind">feature_flag | option</param>
public sealed record ConfigKey(string Key, string Kind, string? Default, string Location);
