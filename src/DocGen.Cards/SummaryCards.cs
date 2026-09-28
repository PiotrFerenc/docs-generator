using System.Text;
using DocGen.Contracts;
using static DocGen.Cards.IndexFacts;

namespace DocGen.Cards;

/// <summary>LLM-written part of a global rule card (GlobalRuleCard call site).</summary>
sealed record GlobalRuleProse(string Title, string Summary, List<string> Behaviour);

/// <summary>LLM-written part of a module card (ModuleSummary call site) and the system card (SystemSummary call site).</summary>
sealed record SummaryProse(string Title, string Summary);

sealed record GlossaryText(string Term, string CodeName, string Description);

/// <summary>LLM-written glossary proposals (Glossary call site).</summary>
sealed record GlossaryProse(List<GlossaryText> Entries);

static class GlobalRuleCards
{
    public static CardJob<GlobalRuleProse, GlobalRuleCard> Prepare(IndexEntry b, Ctx c, IReadOnlyList<UseCaseCard> useCases)
    {
        var f = c.F;
        var validation = Ctx.IsValidation(b);
        var applies = f.UseCases.Where(u => c.Applies(b, u)).Select(u =>
        {
            var card = useCases.First(x => x.PageId == u.Id);
            if (!validation)
                return new AppliesToRow(u.Id, "wszystkie");
            var guards = u.Guards.Where(g => g.Exit == "validation").Select(g => g.Id).ToHashSet();
            var nos = card.Rules.Where(r => r.GuardIds.Any(guards.Contains)).Select(r => r.No).ToList();
            return new AppliesToRow(u.Id, nos.Count > 0 ? RuleRef(nos) : "tylko techniczne");
        }).ToList();
        var summary = new List<KeyValue>
        {
            new("Klasa", $"`{Full(b.Id)}` ({Code(b.Location, "kod")})"),
            new("Typ", "`IPipelineBehavior<TRequest, TResponse>`")
        };
        if (validation)
            summary.Add(new("Źródło reguł", "wszystkie `IValidator<TRequest>` (FluentValidation) z kontenera DI"));
        if (b.Dependencies.Count > 0)
            summary.Add(new("Zależności", string.Join(", ", b.Dependencies.Select(d => $"`{d}`"))));
        var logic = b.Guards.Select(g => new LogicRow($"`{g.Condition}`", ExitLabel(g, null), g.Location)).ToList();

        var offline = new GlobalRuleProse(
            Humanize(Short(b.Id)),
            $"Zachowanie potoku `{Short(b.Id)}` uruchamiane wokół logiki komend.",
            b.Guards.Count > 0 ? b.Guards.Select(g => $"Gdy `{g.Condition}`: {ExitLabel(g, null)}.").ToList() : ["Brak warunków w kodzie."]);

        GlobalRuleCard Apply(GlobalRuleProse p) => new(b.Id, Short(b.Id), p.Title, p.Summary, p.Behaviour, applies, summary, logic, LlmRunner.BlankMeta);

        List<string> Validate(GlobalRuleProse p, GlobalRuleCard card)
        {
            var errors = new List<string>();
            CardChecks.Required(errors, p.Title, "title");
            CardChecks.Required(errors, p.Summary, "summary");
            if (p.Behaviour.Count == 0)
                errors.Add("Lista behaviour jest pusta.");
            errors.AddRange(CardChecks.Links(card, f, c.PageIds, logic.Select(l => l.Location)));
            return errors;
        }

        var sb = new StringBuilder($"Reguła globalna (pipeline behavior): {b.Id}\n");
        b.Guards.ForEach(g => sb.AppendLine($"[{g.Id}] {g.Location}: {g.Condition} → {g.Exit}"));
        sb.AppendLine("\n## Obowiązuje dla");
        applies.ForEach(a => sb.AppendLine($"- {a.PageId}: {useCases.First(x => x.PageId == a.PageId).Title} ({a.Rules})"));
        sb.AppendLine().AppendLine(c.Catalog);
        sb.AppendLine("\n## Kod");
        b.Chunks.ForEach(ch => sb.AppendLine($"### {ch.Symbol} ({ch.Location})\n{ch.Text}"));
        return new(sb.ToString(), offline, Apply, Validate);
    }
}

static class ModuleCards
{
    public static CardJob<SummaryProse, ModuleCard> Prepare(string module, Ctx c, IReadOnlyList<UseCaseCard> useCases, IReadOnlyList<EntityCard> entities, IReadOnlyList<FlowCard> flows)
    {
        var f = c.F;
        var ucs = useCases.Where(u => u.Module == module).ToList();
        var ens = entities.Where(e => e.Module == module).ToList();
        var fls = flows.Where(fl => fl.Steps.Any(s => s.Module == module)).ToList();
        var ownEntries = f.UseCases.Where(u => u.Module == module).ToList();
        var dependsOn = ownEntries.SelectMany(u => f.Incoming(u).Select(i => i.Source.Module)
                .Concat(u.Edges.Where(x => x.Type is "reads" or "writes" or "insert" or "update" or "delete").Select(x => f.EntityOf(x.Target)?.Module ?? module)))
            .Where(m => m != module && c.Pages.ContainsKey($"module:{m}")).Distinct().Order().Select(m => $"module:{m}").ToList();

        var offline = new SummaryProse($"Moduł {module}",
            ucs.Count > 0 ? "Przypadki użycia: " + string.Join(", ", ucs.Select(u => u.Title)) + "." : "Encje: " + string.Join(", ", ens.Select(e => e.Name)) + ".");
        ModuleCard Apply(SummaryProse p) => new($"module:{module}", module, p.Title, p.Summary,
            ucs.Select(u => u.PageId).ToList(), ens.Select(e => e.PageId).ToList(), fls.Select(x => x.PageId).ToList(), dependsOn, LlmRunner.BlankMeta);
        List<string> Validate(SummaryProse p, ModuleCard card) => Basic(p, card, c);

        var sb = new StringBuilder($"Moduł: {module}\n\n## Przypadki użycia\n");
        ucs.ForEach(u => sb.AppendLine($"- {u.Title}: {u.Summary}"));
        sb.AppendLine("\n## Encje");
        ens.ForEach(e => sb.AppendLine($"- {e.Name}: {e.Description}"));
        sb.AppendLine("\n## Procesy");
        fls.ForEach(x => sb.AppendLine($"- {x.Title}: {x.Description}"));
        sb.AppendLine("\n## Zależy od: " + string.Join(", ", dependsOn));
        return new(sb.ToString(), offline, Apply, Validate);
    }

    public static List<string> Basic(SummaryProse p, object card, Ctx c)
    {
        var errors = new List<string>();
        CardChecks.Required(errors, p.Title, "title");
        CardChecks.Required(errors, p.Summary, "summary");
        errors.AddRange(CardChecks.Links(card, c.F, c.PageIds));
        return errors;
    }
}

static class SystemCards
{
    public static CardJob<SummaryProse, SystemCard> Prepare(Ctx c, IReadOnlyList<ModuleCard> modules)
    {
        var offline = new SummaryProse("Przegląd systemu", "System składa się z modułów: " + string.Join(", ", modules.Select(m => m.Title)) + ".");
        SystemCard Apply(SummaryProse p) => new("system", p.Title, p.Summary, modules.Select(m => new ModuleRow(m.PageId, m.Summary)).ToList(), LlmRunner.BlankMeta);
        var sb = new StringBuilder("## Moduły\n");
        modules.ToList().ForEach(m => sb.AppendLine($"- {m.Module} ({m.Title}): {m.Summary}"));
        return new(sb.ToString(), offline, Apply, (p, card) => ModuleCards.Basic(p, card, c));
    }
}

static class GlossaryCards
{
    /// <summary>Candidates: entity names, enum types and enum values ("PayoutStatus.Pending").</summary>
    public static CardJob<GlossaryProse, GlossaryCard> Prepare(Ctx c, IReadOnlyList<EntityCard> entities, IReadOnlyList<UseCaseCard> useCases, List<GlossaryEntry> manual)
    {
        var candidates = new List<(string CodeName, string Hint)>();
        foreach (var en in c.F.Index.Entities.OrderBy(e => e.Name, StringComparer.Ordinal))
        {
            candidates.Add((en.Name, $"encja modułu {en.Module}"));
            foreach (var fld in en.Fields.Where(x => x.EnumValues is not null))
            {
                candidates.Add((fld.Type, $"enum pola {en.Name}.{fld.Name}"));
                candidates.AddRange(fld.EnumValues!.Select(v => ($"{fld.Type}.{v}", $"wartość {fld.Type}")));
            }
        }
        candidates = candidates.DistinctBy(x => x.CodeName).ToList();
        var known = candidates.Select(x => x.CodeName).ToHashSet();
        var manualNames = manual.Select(m => m.CodeName).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var offline = new GlossaryProse(candidates.Select(x => new GlossaryText(Humanize(Tail(x.CodeName)), x.CodeName, $"Nazwa w kodzie: {x.Hint}.")).ToList());

        // Manual entries always win and are never overwritten.
        GlossaryCard Apply(GlossaryProse p) => new("glossary",
            manual.Select(m => m with { Manual = true })
                .Concat(p.Entries.Where(x => known.Contains(x.CodeName) && !manualNames.Contains(x.CodeName)).DistinctBy(x => x.CodeName)
                    .Select(x => new GlossaryEntry(x.Term, x.CodeName, x.Description, false)))
                .OrderBy(x => x.Term, StringComparer.CurrentCultureIgnoreCase).ToList(),
            LlmRunner.BlankMeta);

        List<string> Validate(GlossaryProse p, GlossaryCard card)
        {
            var errors = p.Entries.Where(x => !known.Contains(x.CodeName)).Select(x => $"codeName {x.CodeName} nie jest na liście kandydatów.").ToList();
            errors.AddRange(p.Entries.Where(x => string.IsNullOrWhiteSpace(x.Term)).Select(x => $"Puste pojęcie dla {x.CodeName}."));
            errors.AddRange(CardChecks.Links(card, c.F, c.PageIds));
            return errors;
        }

        var sb = new StringBuilder("## Kandydaci (codeName | podpowiedź)\n");
        candidates.ForEach(x => sb.AppendLine($"- {x.CodeName} | {x.Hint}"));
        sb.AppendLine("\n## Tytuły stron (kontekst pojęć)");
        entities.ToList().ForEach(e => sb.AppendLine($"- encja {e.Slug}: {e.Name}"));
        useCases.ToList().ForEach(u => sb.AppendLine($"- {u.Title}: {u.Summary}"));
        sb.AppendLine("\n## Wpisy ręczne (pomiń je)");
        manual.ForEach(m => sb.AppendLine($"- {m.CodeName}: {m.Term}"));
        sb.AppendLine().AppendLine(c.Catalog);
        return new(sb.ToString(), offline, Apply, Validate);
    }
}
