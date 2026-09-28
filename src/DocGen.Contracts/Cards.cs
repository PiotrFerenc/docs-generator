namespace DocGen.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// cards.json — produced by DocGen.Cards (deterministic parts + LLM prose), consumed by DocGen.Render.
// A card is the complete model of one documentation page; the renderer adds no content, only layout.
//
// Inline link tokens allowed in every prose/markdown string field:
//   [[page:<PageId>|<label>]]           → relative link to another generated page
//   [[page:<PageId>]]                   → same, label = target page's business title (card Title / Name)
//   [[code:<path>:<line>|<label>]]      → link to repository source (RepoUrlTemplate)
// Everything else is plain Markdown (inline `code`, *italic*, **bold**).
// ─────────────────────────────────────────────────────────────────────────────

public sealed record CardSet(
    string Commit,
    List<UseCaseCard> UseCases,
    List<EntityCard> Entities,
    List<FlowCard> Flows,
    List<GlobalRuleCard> GlobalRules,
    List<ModuleCard> Modules,
    SystemCard? System,
    GlossaryCard? Glossary);

/// <param name="Status">ok | needs_review | offline (generated without LLM)</param>
/// <param name="CardHash">Hash of index inputs + prompt version + model; cache key.</param>
public sealed record CardMeta(string SourceCommit, string CardHash, string Status, string PromptVersion, string Model, List<string> Warnings);

public sealed record KeyValue(string Key, string Value);

// ── Use case (request handler / event handler) ───────────────────────────────

/// <param name="PageId">Entry id, e.g. "T:Payments.Application.RequestPayoutHandler".</param>
/// <param name="Slug">File name without extension, PascalCase English, e.g. "RequestPayout".</param>
/// <param name="Title">Business title in Polish, e.g. "Zlecenie wypłaty dla klienta".</param>
/// <param name="Triggers">Bullets of "Kiedy się uruchamia".</param>
/// <param name="MainScenario">Text after "**Scenariusz główny:**" (single paragraph).</param>
/// <param name="Effects">Bullets of "Efekty".</param>
/// <param name="GlobalRules">PageIds of GlobalRuleCards that apply.</param>
/// <param name="Flows">PageIds of FlowCards this use case belongs to.</param>
public sealed record UseCaseCard(
    string PageId,
    string Module,
    string Slug,
    string Title,
    string Summary,
    List<string> Triggers,
    string? TriggersNote,
    string MainScenario,
    List<AltScenario> AlternativeScenarios,
    string? ScenariosNote,
    string? RulesIntro,
    List<BusinessRule> Rules,
    List<string> Effects,
    List<string> GlobalRules,
    List<string> Flows,
    UseCaseTechnical Technical,
    CardMeta Meta);

/// <param name="Title">Bold lead, e.g. "Brak numeru konta".</param>
/// <param name="RuleRef">Rendered in parentheses after title, e.g. "reguła 1" / "reguły 1–2"; null when none.</param>
/// <param name="Text">Consequence text after the colon.</param>
public sealed record AltScenario(string Title, string? RuleRef, string Text);

/// <param name="GuardIds">Index guard ids ("G3") this business rule is derived from (≥1).</param>
public sealed record BusinessRule(int No, string Rule, string OnFail, List<string> GuardIds);

/// <param name="Summary">Rows of the first technical key/value table (Handler, Komenda, Walidator, ...). Values are Markdown.</param>
/// <param name="CodeRules">"Reguły w kodzie" table; No matches BusinessRule.No.</param>
/// <param name="DismissedNote">Markdown sentence listing technical guards left out of business rules; null if none.</param>
/// <param name="ResultHandling">"Obsługa wyniku wywołania" rows; empty when not applicable.</param>
/// <param name="DataNote">Used instead of the data table when Data is empty (e.g. "brak bezpośrednich odczytów...").</param>
public sealed record UseCaseTechnical(
    List<KeyValue> Summary,
    List<CodeRule> CodeRules,
    string? DismissedNote,
    List<ResultHandlingRow> ResultHandling,
    List<DataRow> Data,
    string? DataNote,
    List<ConfigRow> Config,
    string? ConfigNote);

/// <param name="Locations">"path:line" list, rendered as code links.</param>
public sealed record CodeRule(int No, string Condition, string Exit, List<string> Locations);

public sealed record ResultHandlingRow(string Call, string WhenFailed, string Location);

/// <param name="Operation">odczyt | zapis (insert) | zapis (update) | zapis (delete)</param>
public sealed record DataRow(string Operation, string Table, string Columns);

public sealed record ConfigRow(string Key, string Meaning, string Default);

// ── Entity ───────────────────────────────────────────────────────────────────

/// <param name="Name">Polish business name, e.g. "Wypłata"; page title is "&lt;Module&gt; / Encja: &lt;Name&gt;".</param>
public sealed record EntityCard(
    string PageId,
    string Module,
    string Slug,
    string Name,
    string Description,
    List<StatusRow> Statuses,
    List<StateEdge> StateDiagram,
    List<string> Rules,
    List<ChangeRow> Changes,
    EntityTechnical Technical,
    CardMeta Meta);

/// <param name="Value">Enum member, e.g. "Pending".</param>
/// <param name="Label">Polish label, e.g. "Oczekująca".</param>
public sealed record StatusRow(string Value, string Label, string Meaning, bool IsFinal);

/// <param name="From">Status label or "[*]".</param>
/// <param name="To">Status label or "[*]".</param>
public sealed record StateEdge(string From, string To, string? Label);

/// <param name="Change">e.g. "*Oczekująca* → *Zrealizowana*".</param>
/// <param name="PageId">Use case performing the change.</param>
public sealed record ChangeRow(string Change, string PageId);

public sealed record EntityTechnical(
    List<KeyValue> Summary,
    List<FieldRow> Fields,
    List<TransitionRow> Transitions);

public sealed record FieldRow(string Field, string Column, string Type, string Description);

public sealed record TransitionRow(string To, string Method, string Condition, string Location);

// ── Flow (process across handlers) ───────────────────────────────────────────

/// <param name="PageId">"flow:&lt;root entry id&gt;".</param>
/// <param name="Title">Polish, e.g. "Wypłata po rozliczeniu zlecenia skupu"; page title is "Proces: &lt;Title&gt;".</param>
public sealed record FlowCard(
    string PageId,
    string Slug,
    string Title,
    string Description,
    List<FlowNode> Nodes,
    List<FlowEdge> Edges,
    List<FlowStep> Steps,
    string? SilentStopsIntro,
    List<SilentStop> SilentStops,
    List<FlowTechnicalRow> Technical,
    CardMeta Meta);

/// <param name="Shape">step | decision | end</param>
public sealed record FlowNode(string Id, string Label, string Shape);

public sealed record FlowEdge(string From, string To, string? Label, bool Dashed);

/// <param name="PageId">Null for steps outside the indexed code (label rendered as plain text).</param>
/// <param name="Mode">e.g. "na żądanie specjalisty", "w tle, po zapisie kroku 1".</param>
public sealed record FlowStep(int No, string? PageId, string Label, string Module, string Mode);

public sealed record SilentStop(string Place, string Reason, string Trace);

public sealed record FlowTechnicalRow(int Step, string Input, string Connection);

// ── Global rule (pipeline behavior) ─────────────────────────────────────────

public sealed record GlobalRuleCard(
    string PageId,
    string Slug,
    string Title,
    string Summary,
    List<string> Behaviour,
    List<AppliesToRow> AppliesTo,
    List<KeyValue> TechnicalSummary,
    List<LogicRow> Logic,
    CardMeta Meta);

/// <param name="PageId">Use case page.</param>
public sealed record AppliesToRow(string PageId, string Rules);

public sealed record LogicRow(string Condition, string Exit, string Location);

// ── Module / system / glossary ──────────────────────────────────────────────

public sealed record ModuleCard(
    string PageId,
    string Module,
    string Title,
    string Summary,
    List<string> UseCases,
    List<string> Entities,
    List<string> Flows,
    List<string> DependsOn,
    CardMeta Meta);

public sealed record SystemCard(
    string PageId,
    string Title,
    string Summary,
    List<ModuleRow> Modules,
    CardMeta Meta);

public sealed record ModuleRow(string PageId, string Summary);

public sealed record GlossaryCard(string PageId, List<GlossaryEntry> Entries, CardMeta Meta);

/// <param name="Manual">True when the entry comes from the hand-maintained glossary file and must never be overwritten.</param>
public sealed record GlossaryEntry(string Term, string CodeName, string Description, bool Manual);
