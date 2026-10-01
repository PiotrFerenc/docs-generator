using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocGen.Contracts;

namespace DocGen.Agent;

/// <summary>
/// Deterministic data analysis (no LLM): which documented process the snapshot shows as stopped (first step whose
/// effect is missing) and how the business rules evaluate against the data. Also used by the agent's tools.
/// </summary>
sealed class Analyzer(CodeIndex index, CardSet cards, Snapshot snapshot)
{
    // ponytail: negative outcome detected by status name; replace with an explicit "expected outcome" from question analysis if names differ.
    static readonly Regex Negative = new("fail|reject|cancel|declin|error|refus", RegexOptions.IgnoreCase);

    readonly DataView data = new(index, snapshot);
    readonly Dictionary<string, IndexEntry> entries = index.Entries.ToDictionary(e => e.Id);
    readonly Dictionary<string, UseCaseCard> useCases = cards.UseCases.ToDictionary(u => u.PageId);

    public DataAnalysis Analyze()
    {
        var flows = cards.Flows.Select(Check).Where(f => f.Steps.Any(s => s.State == StepState.Done)).ToList();
        var flow = flows.Where(f => f.BreakStep is not null || f.NegativeOutcome is not null)
                       .OrderByDescending(f => f.Steps.Count(s => s.State == StepState.Done))
                       .FirstOrDefault()
                   ?? flows.FirstOrDefault();

        var notes = new List<string>();
        var missing = new List<string>();
        if (flow is null)
        {
            missing.Add(cards.Flows.Count == 0
                ? "Brak opisanych procesów w dokumentacji (cards.json)."
                : "Żaden krok opisanych procesów nie ma śladu w snapshocie — dołącz tabele, które zapisuje pierwszy krok procesu.");
            return new("insufficient_data", null, [], [], [], missing, notes);
        }

        missing.AddRange(flow.Steps.Where(s => s.State == StepState.Unknown).Select(s => $"Krok {s.No} „{s.Label}”: {s.Evidence}"));
        var checks = RulesToCheck(flow).ToList();
        foreach (var check in checks.Where(c => c.State == RuleState.Unknown))
            missing.AddRange(check.Evidence.Where(e => e.StartsWith("Brak wartości") || e.Contains("nie została pobrana")));

        var causes = checks.Where(c => c.State == RuleState.Violated).ToList();
        var external = flow.BreakStep is { } b && flow.Steps.Any(s => s.No < b && s.State == StepState.External);
        if (external)
            notes.Add("Przed przerwanym krokiem jest krok poza indeksowanym kodem (integracja zewnętrzna) — przyczyna może leżeć po jego stronie.");
        if (flow.NegativeOutcome is not null)
            notes.Add($"Proces zakończył się wynikiem negatywnym: {flow.NegativeOutcome}");

        var verdict = causes.Count > 0 ? "cause_found"
            : flow.NegativeOutcome is not null || external ? "outside_code"
            : flow.BreakStep is not null && checks.Any(c => c.State == RuleState.Unknown) ? "candidates"
            : flow.BreakStep is not null ? "insufficient_data"
            : "no_problem_found";

        return new(verdict, flow, causes,
            checks.Where(c => c.State == RuleState.Ok).ToList(),
            checks.Where(c => c.State == RuleState.Unknown).ToList(),
            missing.Distinct().ToList(), notes);
    }

    /// <summary>Step states of one documented process against the snapshot (null when the flow id is unknown).</summary>
    public FlowCheck? CheckFlow(string flowId) =>
        cards.Flows.FirstOrDefault(f => f.PageId == flowId) is { } flow ? Check(flow) : null;

    /// <summary>All business rules of one use case evaluated against the snapshot.</summary>
    public List<RuleCheck> EvaluateUseCase(string pageId) => Rules(pageId, new RuleEvaluator(data)).ToList();

    /// <summary>Rules of the steps after the last completed step up to (and including) the break, in execution order.</summary>
    IEnumerable<RuleCheck> RulesToCheck(FlowCheck flow)
    {
        if (flow.BreakStep is not { } breakNo)
            yield break;
        var lastDone = flow.Steps.Where(s => s.No < breakNo && s.State == StepState.Done).Select(s => s.No).DefaultIfEmpty(0).Max();
        var evaluator = new RuleEvaluator(data);

        foreach (var step in flow.Steps.Where(s => s.No > lastDone && s.No <= breakNo && s.PageId is not null))
            foreach (var check in Rules(step.PageId!, evaluator))
                yield return check;
    }

    IEnumerable<RuleCheck> Rules(string pageId, RuleEvaluator evaluator)
    {
        if (!useCases.TryGetValue(pageId, out var card) || !entries.TryGetValue(pageId, out var entry))
            yield break;
        var guards = entry.Guards.ToDictionary(g => g.Id);
        foreach (var rule in card.Rules.OrderBy(r => r.No))
        {
            var ruleGuards = rule.GuardIds.Where(guards.ContainsKey).Select(id => guards[id]).ToList();
            var (state, evidence) = evaluator.Evaluate(ruleGuards);
            yield return new(card.PageId, card.Title, rule.No, rule.Rule, rule.OnFail, state,
                ruleGuards.Any(g => g.Exit is "silent_success" or "return"),
                evidence, ruleGuards.Select(g => g.Location).ToList());
        }
    }

    FlowCheck Check(FlowCard flow)
    {
        var steps = flow.Steps.Select(Step).ToList();
        var breakStep = steps.FirstOrDefault(s => s.State == StepState.NotDone && steps.Any(d => d.No < s.No && d.State == StepState.Done));
        var negative = steps.Where(s => s.State == StepState.Done).Select(s => NegativeOutcomeOf(s.PageId)).LastOrDefault(n => n is not null);
        return new(flow.PageId, flow.Title, steps, breakStep?.No, negative);
    }

    StepCheck Step(FlowStep step)
    {
        if (step.PageId is null)
            return new(step.No, null, step.Label, StepState.External, "krok poza indeksowanym kodem");
        if (!entries.TryGetValue(step.PageId, out var entry))
            return new(step.No, step.PageId, step.Label, StepState.Unknown, "brak w indeksie");

        var effects = Effects(entry).ToList();
        if (effects.Count == 0)
            return new(step.No, step.PageId, step.Label, StepState.NoTrace, "krok nie zapisuje danych — brak śladu w bazie");

        var done = effects.FirstOrDefault(e => e.State == true);
        if (done != default)
            return new(step.No, step.PageId, step.Label, StepState.Done, done.Evidence);
        return effects.Any(e => e.State is null)
            ? new(step.No, step.PageId, step.Label, StepState.Unknown, string.Join("; ", effects.Where(e => e.State is null).Select(e => e.Evidence)))
            : new(step.No, step.PageId, step.Label, StepState.NotDone, string.Join("; ", effects.Select(e => e.Evidence).Distinct()));
    }

    /// <summary>Visible effects of a step: inserted rows and constant status writes.</summary>
    IEnumerable<(bool? State, string Evidence)> Effects(IndexEntry entry)
    {
        foreach (var insert in entry.Edges.Where(e => e.Type == "insert"))
        {
            if (data.Entity(insert.Target) is not { } entity)
                continue;
            var (fetched, table, rows) = data.Rows(entity);
            yield return fetched
                ? (rows.Count > 0, $"`{table}`: {rows.Count} rekord(ów)")
                : (null, $"tabela `{table}` nie została pobrana");
        }

        var inserted = entry.Edges.Where(e => e.Type == "insert").Select(e => e.Target).ToHashSet();
        foreach (var write in StatusWrites(entry).Where(w => !inserted.Contains(w.Entity.Name)))
        {
            var (fetched, table, rows) = data.Rows(write.Entity);
            if (!fetched)
            {
                yield return (null, $"tabela `{table}` nie została pobrana");
                continue;
            }
            var column = data.Column(write.Entity, write.Property);
            var values = rows.Select(r => data.Value(r, write.Entity, write.Property)).ToList();
            var reached = Reachable(write.Entity, write.Property, write.Value);
            yield return values.Count == 0
                ? (false, $"`{table}`: brak rekordu")
                : (values.Any(v => v is not null && reached.Contains(v)), $"`{table}.{column}` = {string.Join(", ", values.Select(v => v ?? "null"))}");
        }
    }

    IEnumerable<(EntityInfo Entity, string Property, string Value)> StatusWrites(IndexEntry entry) =>
        entry.Edges.Where(e => e.Type == "writes" && e.Detail is { } d && Regex.IsMatch(d, @"^\w+\.\w+$"))
            .Select(e => (Entity: data.Entity(e.Target.Split('.')[0]), Property: e.Target.Split('.')[1], Value: e.Detail!.Split('.')[1]))
            .Where(w => w.Entity is not null && w.Entity.Transitions.Any(t => t.Field == w.Property))
            .Select(w => (w.Entity!, w.Property, w.Value));

    /// <summary>The written status plus every status reachable from it — a later status also proves the step ran.</summary>
    static HashSet<string> Reachable(EntityInfo entity, string field, string start)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { start };
        for (var grew = true; grew;)
        {
            grew = false;
            foreach (var t in entity.Transitions.Where(t => t.Field == field && t.From is not null && set.Contains(t.From)))
                grew |= set.Add(t.To);
        }
        return set;
    }

    string? NegativeOutcomeOf(string? pageId)
    {
        if (pageId is null || !entries.TryGetValue(pageId, out var entry))
            return null;
        foreach (var write in StatusWrites(entry).Where(w => Negative.IsMatch(w.Value)))
        {
            var (_, table, rows) = data.Rows(write.Entity);
            var row = rows.FirstOrDefault(r => string.Equals(data.Value(r, write.Entity, write.Property), write.Value, StringComparison.OrdinalIgnoreCase));
            if (row is not null)
                return $"`{table}.{data.Column(write.Entity, write.Property)}` = {write.Value} ({Describe(row)})";
        }
        return null;
    }

    static string Describe(JsonObject row) =>
        string.Join(", ", row.Where(p => p.Value is not null).Select(p => $"{p.Key} = {p.Value!.ToJsonString(DocGenJson.Options).Trim('"')}"));
}
