using System.Text;
using System.Text.RegularExpressions;
using DocGen.Contracts;
using static DocGen.Cards.IndexFacts;

namespace DocGen.Cards;

sealed record PageRef(string Id, string Kind, string Name);

/// <summary>Everything a card builder may reference: index facts and the ids of all pages that will exist.</summary>
sealed record Ctx(IndexFacts F, IReadOnlyDictionary<string, PageRef> Pages, IReadOnlyDictionary<string, List<string>> FlowsOf)
{
    public IReadOnlySet<string> PageIds { get; } = Pages.Keys.ToHashSet();

    public string Catalog => "## Strony, do których możesz linkować ([[page:<id>]] albo [[page:<id>|etykieta]])\n" +
                             string.Join("\n", Pages.Values.Select(p => $"- {p.Id} | {p.Kind} | {p.Name}"));

    public string EntityRef(EntityInfo en) => Pages.ContainsKey(en.Id) ? $"[[page:{en.Id}|{en.Name}]]" : $"`{en.Name}`";

    public string? BehaviorName => F.Behaviors.Select(b => Short(b.Id)).FirstOrDefault();

    /// <summary>Behavior applies to the use case: explicit behavior edge (or, if the index has none, every request handler),
    /// and for validation behaviors only when the request has validators.</summary>
    public bool Applies(IndexEntry behavior, IndexEntry uc)
    {
        var anyEdges = F.Index.Entries.Any(e => e.Edges.Any(x => x.Type == "behavior"));
        var linked = anyEdges ? uc.Edges.Any(x => x.Type == "behavior" && x.Target == behavior.Id) : uc.Kind == "request_handler";
        return linked && (!IsValidation(behavior) || F.Validators(MessageType(uc)).Any());
    }

    public static bool IsValidation(IndexEntry behavior) => Short(behavior.Id).Contains("Validation");
}

sealed record DismissedGuard(string GuardId, string Reason);

sealed record ConfigMeaning(string Key, string Meaning);

/// <summary>LLM-written part of a use case card (HandlerCard call site).</summary>
sealed record HandlerProse(
    string Slug, string Title, string Summary, List<string> Triggers, string? TriggersNote, string MainScenario,
    List<AltScenario> AlternativeScenarios, string? ScenariosNote, string? RulesIntro, List<BusinessRule> Rules,
    List<DismissedGuard> Dismissed, List<string> Effects, List<ConfigMeaning> ConfigMeanings);

static partial class UseCaseCards
{
    [GeneratedRegex(@"^[A-Z][A-Za-z0-9]*$")] private static partial Regex PascalSlug();
    [GeneratedRegex(@"^RuleFor\(\w+ => \w+\.\w*Id\)\.NotEmpty\(\)$")] private static partial Regex TechnicalIdRule();

    public static CardJob<HandlerProse, UseCaseCard> Prepare(IndexEntry e, Ctx c)
    {
        var f = c.F;
        var msg = MessageType(e);
        var isEvent = e.Kind == "event_handler";
        var incoming = f.Incoming(e).ToList();
        var guards = e.Guards.ToDictionary(g => g.Id);
        var checkedGuards = e.Guards.Where(g => g.Exit != "propagate").ToList();

        // ── technical summary ──
        var summary = new List<KeyValue> { new("Handler", $"`{Full(e.Id)}` ({Code(e.Location, "kod")})") };
        if (msg is not null)
            summary.Add(isEvent ? new("Typ", $"`INotificationHandler<{Short(msg)}>`") : new("Komenda", $"`{Short(msg)}`"));
        var validators = f.Validators(msg).ToList();
        if (validators.Count > 0)
            summary.Add(new("Walidator", string.Join("; ", validators.Select(v => $"`{Short(v.Id)}` ({Code(v.Location, "kod")})"))));
        var triggers = incoming.Where(i => i.Source.Kind == "endpoint" || i.Edge.Type == "publishes").Select(i => TriggerRow(i.Source, i.Edge)).Distinct().ToList();
        if (triggers.Count > 0)
            summary.Add(new("Wyzwalacz", string.Join("; ", triggers)));
        var callers = incoming.Where(i => i.Source.Kind != "endpoint" && i.Edge.Type == "sends").Select(i => $"`{Short(i.Source.Id)}` (`ISender.Send`)").Distinct().ToList();
        if (callers.Count > 0)
            summary.Add(new("Wywoływany przez", string.Join(", ", callers)));
        // ponytail: published events are assumed to be domain events dispatched after commit (BuildingBlocks convention).
        var published = e.Edges.Where(x => x.Type == "publishes").Select(x => $"`{Short(x.Target)}` (z `{RaiseSite(e, x.Target)}`, po commicie)").ToList();
        if (published.Count > 0)
            summary.Add(new("Publikuje", string.Join(", ", published)));
        var sent = e.Edges.Where(x => x.Type == "sends").Select(x => $"`{Short(x.Target)}` (`ISender.Send`)").ToList();
        if (sent.Count > 0)
            summary.Add(new("Wysyła", string.Join(", ", sent)));
        var external = e.Edges.Where(x => x.Type == "calls_external").Select(x => $"`{x.Target}{(x.Detail is null ? "" : "." + x.Detail)}`").Distinct().ToList();
        if (external.Count > 0)
            summary.Add(new("Wywołania zewnętrzne", string.Join(", ", external)));
        if (e.Dependencies.Count > 0)
            summary.Add(new("Zależności", string.Join(", ", e.Dependencies.Select(d => $"`{d}`"))));

        // ── result handling, data, config ──
        var resultHandling = e.Edges.Where(x => x.Type == "sends" && x.Detail is "logged" or "ignored").Select(x => new ResultHandlingRow(
            $"`sender.Send({Short(x.Target)})`",
            x.Detail == "logged" ? "`logger.Log…`, brak ponowienia, brak propagacji błędu" : "wynik ignorowany, brak propagacji błędu",
            FindLine(e, l => l.Contains("IsFailed")) ?? FindLine(e, l => l.Contains("Send(")) ?? e.Location)).ToList();
        var data = DataRows(e, f);
        var dataNote = data.Count > 0 ? null : "brak bezpośrednich odczytów i zapisów." + (isEvent ? " Dane pochodzą z eventu." : "");
        var configKeys = e.Edges.Where(x => x.Type == "reads_config").Select(x => x.Target).Distinct().ToList();
        var configRows = configKeys.Select(k =>
        {
            var info = f.Index.Config.FirstOrDefault(x => x.Key == k);
            var def = Literal(info?.Default ?? e.Edges.First(x => x.Type == "reads_config" && x.Target == k).Detail?.Replace("default ", ""));
            return (Key: k, Row: new ConfigRow($"`{k}`", info?.Kind == "feature_flag" ? "feature flag" : "opcja konfiguracji",
                def is null ? "brak (zależy od konfiguracji środowiska)" : $"`{def}`"));
        }).ToList();

        var globalRules = f.Behaviors.Where(b => c.Applies(b, e)).Select(b => b.Id).ToList();
        var flows = c.FlowsOf.GetValueOrDefault(e.Id) ?? [];

        // ── offline prose: guard conditions as rules ──
        var rules = new List<BusinessRule>();
        var dismissed = new List<DismissedGuard>();
        var filters = new List<Guard>();
        foreach (var g in checkedGuards)
        {
            if (g.Exit == "validation" && TechnicalIdRule().IsMatch(g.Condition))
                dismissed.Add(new(g.Id, "Warunek techniczny: identyfikator nie może być pusty."));
            else if (g.Exit == "filter")
                filters.Add(g);
            else if (rules.Count > 0 && guards[rules[^1].GuardIds[^1]] is var prev && prev.Condition == g.Condition && prev.Exit == g.Exit)
                rules[^1] = rules[^1] with { GuardIds = [.. rules[^1].GuardIds, g.Id] }; // same check in sibling methods
            else
                rules.Add(new(rules.Count + 1, g.Exit == "validation" ? $"Walidacja: `{g.Condition}`." : $"Nie może zachodzić: `{g.Condition}`.", OnFail(g), [g.Id]));
        }
        foreach (var g in filters)
        {
            // A query filter belongs to the first check after the line that calls its repository method.
            var method = Tail(g.Via);
            var call = e.Chunks.Where(ch => ch.Symbol != g.Via).Select(ch => FindLine(e with { Chunks = [ch] }, l => l.Contains(method + "("))).FirstOrDefault(l => l is not null);
            var owner = call is null || Split(call) is not (var path, var line) ? null
                : rules.Select(r => (Rule: r, Loc: Split(guards[r.GuardIds[0]].Location))).Where(x => x.Loc?.Path == path && x.Loc?.Line >= line)
                    .OrderBy(x => x.Loc!.Value.Line).Select(x => x.Rule).FirstOrDefault();
            if (owner is not null)
                rules[owner.No - 1] = owner with { GuardIds = [.. owner.GuardIds, g.Id] };
            else
                rules.Add(new(rules.Count + 1, $"Zapytanie musi zwrócić wynik: `{g.Condition}`.", OnFail(g), [g.Id]));
        }
        var effects = Effects(e, c);
        var offline = new HandlerProse(
            Slug: Strip(Short(e.Id), "Handler"),
            Title: Humanize(Strip(Short(msg ?? e.Id), "Command", "Query", "Event", "Handler")),
            Summary: isEvent ? $"Reaguje na zdarzenie `{Short(msg ?? e.Id)}`." : $"Obsługuje komendę `{Short(msg ?? e.Id)}`.",
            Triggers: OfflineTriggers(incoming),
            TriggersNote: null,
            MainScenario: "wszystkie reguły są spełnione." + (effects.Count > 0 ? " " + string.Join(" ", effects) : ""),
            AlternativeScenarios: rules.Select(r => new AltScenario(
                (guards[r.GuardIds[0]].Exit == "validation" ? "Niespełniona walidacja " : "Zachodzi ") + $"`{guards[r.GuardIds[0]].Condition}`", RuleRef([r.No]), r.OnFail.ToLowerInvariant()[..1] + r.OnFail[1..])).ToList(),
            ScenariosNote: null,
            RulesIntro: rules.Count > 0 ? "Kolejność odpowiada kolejności sprawdzania w kodzie." : null,
            Rules: rules,
            Dismissed: dismissed,
            Effects: effects.Count > 0 ? effects : ["Brak zmian w danych."],
            ConfigMeanings: []);

        UseCaseCard Apply(HandlerProse p) => new(
            e.Id, e.Module, p.Slug, p.Title, p.Summary, p.Triggers, p.TriggersNote, p.MainScenario, p.AlternativeScenarios,
            p.ScenariosNote, p.RulesIntro, p.Rules, p.Effects, globalRules, flows,
            new UseCaseTechnical(
                summary,
                p.Rules.Select(r => CodeRule(r, e, guards, c.BehaviorName)).ToList(),
                DismissedNote(p.Dismissed.Where(d => guards.ContainsKey(d.GuardId)).Select(d => guards[d.GuardId]).ToList()),
                resultHandling,
                data,
                dataNote,
                configRows.Select(r => r.Row with { Meaning = p.ConfigMeanings.FirstOrDefault(m => m.Key == r.Key)?.Meaning ?? r.Row.Meaning }).ToList(),
                null),
            LlmRunner.BlankMeta);

        List<string> Validate(HandlerProse p, UseCaseCard card)
        {
            var errors = new List<string>();
            CardChecks.Required(errors, p.Title, "title");
            CardChecks.Required(errors, p.Summary, "summary");
            CardChecks.Required(errors, p.MainScenario, "mainScenario");
            if (!PascalSlug().IsMatch(p.Slug ?? ""))
                errors.Add($"slug \"{p.Slug}\" musi być w PascalCase (litery i cyfry).");
            if (p.Triggers.Count == 0)
                errors.Add("Lista triggers jest pusta.");
            for (var i = 0; i < p.Rules.Count; i++)
            {
                var r = p.Rules[i];
                if (r.No != i + 1)
                    errors.Add($"Reguły muszą być numerowane 1..n; reguła na pozycji {i + 1} ma numer {r.No}.");
                CardChecks.Required(errors, r.Rule, $"rules[{i}].rule");
                if (r.GuardIds.Count == 0)
                    errors.Add($"Reguła {r.No} nie wskazuje żadnego guarda.");
                errors.AddRange(r.GuardIds.Where(id => !guards.ContainsKey(id)).Select(id => $"Reguła {r.No} wskazuje nieistniejący guard {id}."));
            }
            errors.AddRange(p.Dismissed.Where(d => !guards.ContainsKey(d.GuardId)).Select(d => $"Pominięty guard {d.GuardId} nie istnieje."));
            foreach (var g in checkedGuards)
            {
                var uses = p.Rules.Count(r => r.GuardIds.Contains(g.Id)) + p.Dismissed.Count(d => d.GuardId == g.Id);
                if (uses == 0)
                    errors.Add($"Guard {g.Id} (`{g.Condition}`, {g.Exit}) nie trafił ani do rules, ani do dismissed.");
                else if (uses > 1)
                    errors.Add($"Guard {g.Id} występuje {uses} razy w rules/dismissed (dozwolony dokładnie raz).");
            }
            errors.AddRange(CardChecks.Links(card, f, c.PageIds,
                card.Technical.CodeRules.SelectMany(r => r.Locations).Concat(card.Technical.ResultHandling.Select(r => r.Location))));
            return errors;
        }

        return new(Context(e, c, summary, data, configRows.Select(r => r.Row).ToList()), offline, Apply, Validate);
    }

    /// <summary>C# numeric literal to plain number: "20_000m" → "20000".</summary>
    static string? Literal(string? value) =>
        value is not null && NumericLiteral().IsMatch(value) ? value.Replace("_", "").TrimEnd('m', 'M', 'd', 'D', 'f', 'F', 'l', 'L') : value;

    [GeneratedRegex(@"^-?[\d_]+(\.[\d_]+)?[mMdDfFlL]?$")] private static partial Regex NumericLiteral();

    static string TriggerRow(IndexEntry source, Edge edge)
    {
        if (edge.Type == "publishes")
            return $"`{Short(edge.Target)}` (domain event z `{RaiseSite(source, edge.Target)}`, publikowany po commicie)";
        var maps = source.Edges.Where(x => x.Type == "maps" && x.Detail is { } d && d.IndexOfAny(['=', '<', '>', '!', '?', '(']) >= 0)
            .Select(x => $"; `{x.Target} = {x.Detail}`");
        return $"`{source.Id}` ({Code(source.Location, "kod")})" + string.Concat(maps) + (edge.Detail == "http" ? "; wynik zwracany jako odpowiedź HTTP" : "");
    }

    static List<string> OfflineTriggers(List<(IndexEntry Source, Edge Edge)> incoming)
    {
        var list = incoming.Select(i => i.Source.Kind == "endpoint"
                ? $"Wywołanie `{i.Source.Id}`."
                : i.Edge.Type == "publishes"
                    ? $"Automatycznie, po zdarzeniu `{Short(i.Edge.Target)}` z [[page:{i.Source.Id}]] (po zapisie)."
                    : $"Wywołanie z [[page:{i.Source.Id}]].")
            .Distinct().ToList();
        return list.Count > 0 ? list : ["Brak wyzwalacza w analizowanym kodzie."];
    }

    static List<string> Effects(IndexEntry e, Ctx c)
    {
        var f = c.F;
        var effects = new List<string>();
        var inserted = e.Edges.Where(x => x.Type == "insert").Select(x => f.EntityOf(x.Target)).OfType<EntityInfo>().Distinct().ToList();
        foreach (var en in inserted)
            effects.Add($"Tworzy {c.EntityRef(en)}.");
        foreach (var grp in e.Edges.Where(x => x.Type == "writes").GroupBy(x => f.EntityOf(x.Target)).Where(g => g.Key is not null && !inserted.Contains(g.Key)))
            effects.Add($"Zmienia {c.EntityRef(grp.Key!)}: " + string.Join(", ", grp.GroupBy(x => Tail(x.Target)).Select(p =>
                $"`{p.Key}`" + (p.Any(x => x.Detail is not null) ? " → " + string.Join(" / ", p.Where(x => x.Detail is not null).Select(x => $"`{Tail(x.Detail!)}`")) : ""))) + ".");
        foreach (var x in e.Edges.Where(x => x.Type == "delete").Select(x => f.EntityOf(x.Target)).OfType<EntityInfo>())
            effects.Add($"Usuwa {c.EntityRef(x)}.");
        foreach (var x in e.Edges.Where(x => x.Type == "publishes"))
            effects.Add($"Publikuje zdarzenie `{Short(x.Target)}`.");
        foreach (var x in e.Edges.Where(x => x.Type == "sends"))
            effects.Add(f.HandlersOf(x.Target).FirstOrDefault() is { } h ? $"Uruchamia [[page:{h.Id}]]." : $"Wysyła komendę `{Short(x.Target)}`.");
        return effects;
    }

    static List<DataRow> DataRows(IndexEntry e, IndexFacts f)
    {
        var rows = new List<DataRow>();
        // Reads of an entity the use case itself inserts are in-memory (event payloads etc.), not database reads.
        var inserted = e.Edges.Where(x => x.Type == "insert").Select(x => f.EntityOf(x.Target)).ToHashSet();
        foreach (var grp in e.Edges.Where(x => x.Type == "reads").GroupBy(x => f.EntityOf(x.Target)).Where(g => g.Key is not null && !inserted.Contains(g.Key)))
            rows.Add(new("odczyt", $"`{grp.Key!.Table ?? grp.Key.Name}`", string.Join(", ", grp.Select(x => $"`{Column(grp.Key, Tail(x.Target))}`").Distinct())));

        var writeEntities = e.Edges.Where(x => x.Type is "writes" or "insert" or "update" or "delete").Select(x => f.EntityOf(x.Target)).OfType<EntityInfo>().Distinct();
        foreach (var en in writeEntities)
        {
            bool Has(string type) => e.Edges.Any(x => x.Type == type && f.EntityOf(x.Target) == en);
            var op = Has("insert") ? "zapis (insert)" : Has("delete") ? "zapis (delete)" : "zapis (update)";
            var columns = e.Edges.Where(x => x.Type == "writes" && f.EntityOf(x.Target) == en).GroupBy(x => Tail(x.Target)).Select(p =>
                $"`{Column(en, p.Key)}`" + (op == "zapis (update)" && p.Any(x => x.Detail is not null)
                    ? " → " + string.Join(" / ", p.Where(x => x.Detail is not null).Select(x => $"`{Tail(x.Detail!)}`"))
                    : "")).ToList();
            rows.Add(new(op, $"`{en.Table ?? en.Name}`", columns.Count > 0 ? string.Join(", ", columns) : "—"));
        }
        return rows;
    }

    static CodeRule CodeRule(BusinessRule r, IndexEntry e, Dictionary<string, Guard> guards, string? behavior)
    {
        var gs = r.GuardIds.Where(guards.ContainsKey).Select(id => guards[id]).ToList();
        var entryClass = Short(e.Id);
        var condition = string.Join(", ", gs.GroupBy(g => g.Condition).Select(grp =>
        {
            var foreign = grp.Where(g => g.Exit != "validation" && g.Via[..Math.Max(0, g.Via.LastIndexOf('.'))] != entryClass).Select(g => $"`{g.Via}`").Distinct().ToList();
            return $"`{grp.Key}`" + (foreign.Count > 0 ? " w " + string.Join(" / ", foreign) : "");
        }));
        var exitGuards = gs.Any(g => g.Exit != "filter") ? gs.Where(g => g.Exit != "filter") : gs;
        var exits = exitGuards.Select(g => ExitLabel(g, behavior));
        return new(r.No, condition, string.Join(" / ", exits.Distinct()), gs.Select(g => g.Location).Distinct().ToList());
    }

    static string? DismissedNote(List<Guard> dismissed) => dismissed.Count == 0
        ? null
        : "Pominięte w części biznesowej (warunki techniczne): " +
          string.Join(", ", dismissed.Select(g => $"`{g.Condition}` ({Code(g.Location)})")) + ".";

    static string Context(IndexEntry e, Ctx c, List<KeyValue> summary, List<DataRow> data, List<ConfigRow> config)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Punkt wejścia: {e.Id} ({e.Kind}, moduł {e.Module})");
        sb.AppendLine("\n## Fakty techniczne (deterministyczne, strona je pokaże)");
        summary.ForEach(kv => sb.AppendLine($"- {kv.Key}: {kv.Value}"));
        sb.AppendLine("\n## Guardy (w kolejności wykonania; warunek → wyjście)");
        foreach (var g in e.Guards)
            sb.AppendLine($"[{g.Id}] {g.Via}  {g.Location}  ({g.Kind})\n     {g.Condition}  → {g.Exit}");
        sb.AppendLine("\n## Krawędzie");
        e.Edges.ForEach(x => sb.AppendLine($"- {x}"));
        foreach (var v in c.F.Validators(MessageType(e)))
            sb.AppendLine($"- walidator {v.Id}");
        sb.AppendLine("\n## Dane");
        data.ForEach(d => sb.AppendLine($"- {d.Operation} {d.Table}: {d.Columns}"));
        if (config.Count > 0)
        {
            sb.AppendLine("\n## Konfiguracja (podaj configMeanings dla tych kluczy, key bez backticków)");
            config.ForEach(r => sb.AppendLine($"- {r.Key.Trim('`')} (domyślnie {r.Default})"));
        }
        sb.AppendLine().AppendLine(c.Catalog);
        sb.AppendLine("\n## Kod");
        foreach (var ch in e.Chunks)
            sb.AppendLine($"### {ch.Symbol} ({ch.Location})\n{ch.Text}");
        return sb.ToString();
    }
}
