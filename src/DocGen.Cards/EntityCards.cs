using System.Text;
using DocGen.Contracts;
using static DocGen.Cards.IndexFacts;

namespace DocGen.Cards;

sealed record StatusText(string Value, string Label, string Meaning);

sealed record FieldText(string Field, string Description);

/// <summary>LLM-written part of an entity card (EntityCard call site).</summary>
sealed record EntityProse(string Name, string Description, List<StatusText> Statuses, List<FieldText> Fields, List<string> Rules);

static class EntityCards
{
    /// <summary>Entities worth a page: with status transitions or written by a use case.</summary>
    public static List<EntityInfo> Select(IndexFacts f) => f.Index.Entities.Where(en =>
            en.Transitions.Count > 0 ||
            f.UseCases.Any(u => u.Edges.Any(x => x.Type is "writes" or "insert" or "update" or "delete" && f.EntityOf(x.Target) == en)))
        .OrderBy(en => en.Id, StringComparer.Ordinal).ToList();

    static readonly Dictionary<string, string> Opposite = new() { ["=="] = "!=", ["!="] = "==", ["<"] = ">=", [">="] = "<", [">"] = "<=", ["<="] = ">" };

    /// <summary>Transition conditions are fail-guard texts; show the passing form: "a != b" → "a == b", else "!(cond)".</summary>
    static string Negate(string condition)
    {
        var m = System.Text.RegularExpressions.Regex.Match(condition, @"^(\S+) (==|!=|<=|>=|<|>) (\S+)$");
        return m.Success ? $"{m.Groups[1].Value} {Opposite[m.Groups[2].Value]} {m.Groups[3].Value}" : $"!({condition})";
    }

    public static CardJob<EntityProse, EntityCard> Prepare(EntityInfo en, Ctx c)
    {
        var f = c.F;
        var statusField = en.Fields.FirstOrDefault(x => x.EnumValues is not null && en.Transitions.Any(t => t.Field == x.Name));
        var values = statusField?.EnumValues ?? [];
        bool IsFinal(string v) => en.Transitions.Any(t => t.To == v) && !en.Transitions.Any(t => t.From == v);

        // Use cases performing each transition: writes "<Entity>.<Field>" with the assigned value.
        List<IndexEntry> Performers(StatusTransition t) => f.UseCases.Where(u => u.Edges.Any(x =>
            x.Type == "writes" && x.Target == $"{en.Name}.{t.Field}" && x.Detail is { } d && Tail(d) == t.To)).ToList();

        var summary = new List<KeyValue> { new("Klasa", $"`{Full(en.Id)}` ({Code(en.Location, "kod")})") };
        if (en.Table is not null)
            summary.Add(new("Tabela", $"`{en.Table}`"));
        if (en.RaisedEvents.Count > 0)
            summary.Add(new("Eventy", string.Join(", ", en.RaisedEvents.Select(ev => $"`{Short(ev)}`"))));
        var transitions = en.Transitions.Select(t => new TransitionRow($"`{t.To}`", $"`{t.Method}`", t.Condition is null ? "—" : $"`{Negate(t.Condition)}`", t.Location)).ToList();

        var offline = new EntityProse(
            en.Name,
            $"Encja `{en.Name}` modułu {en.Module}" + (en.Table is null ? "." : $", tabela `{en.Table}`."),
            values.Select(v => new StatusText(v, v, $"Status `{v}`.")).ToList(),
            en.Fields.Select(x => new FieldText(x.Name, x.EnumValues is { } ev ? string.Join(", ", ev.Select(v => $"`{v}`")) + "." : Humanize(x.Name) + ".")).ToList(),
            en.Transitions.Where(t => t.Condition is not null).Select(t => $"Przejście do statusu `{t.To}` (`{t.Method}`) wymaga: `{Negate(t.Condition!)}`.").ToList());

        EntityCard Apply(EntityProse p)
        {
            string Label(string v) => p.Statuses.FirstOrDefault(s => s.Value == v)?.Label ?? v;
            var diagram = en.Transitions.Select(t => new StateEdge(t.From is null ? "[*]" : Label(t.From), Label(t.To), Tail(t.Method)))
                .Concat(values.Where(IsFinal).Select(v => new StateEdge(Label(v), "[*]", null))).ToList();
            var changes = en.Transitions.SelectMany(t => Performers(t).Select(u => new ChangeRow(
                t.From is null ? $"utworzenie (*{Label(t.To)}*)" : $"*{Label(t.From)}* → *{Label(t.To)}*", u.Id))).ToList();
            return new EntityCard(en.Id, en.Module, en.Name, p.Name, p.Description,
                values.Select(v => new StatusRow(v, Label(v), p.Statuses.FirstOrDefault(s => s.Value == v)?.Meaning ?? "", IsFinal(v))).ToList(),
                diagram, p.Rules, changes,
                new EntityTechnical(summary,
                    en.Fields.Select(x => new FieldRow($"`{x.Name}`", x.Column is null ? "—" : $"`{x.Column}`", $"`{x.Type}`",
                        p.Fields.FirstOrDefault(d => d.Field == x.Name)?.Description ?? "")).ToList(),
                    transitions),
                LlmRunner.BlankMeta);
        }

        List<string> Validate(EntityProse p, EntityCard card)
        {
            var errors = new List<string>();
            CardChecks.Required(errors, p.Name, "name");
            CardChecks.Required(errors, p.Description, "description");
            foreach (var v in values)
                if (p.Statuses.FirstOrDefault(s => s.Value == v) is not { } s || string.IsNullOrWhiteSpace(s.Label) || string.IsNullOrWhiteSpace(s.Meaning))
                    errors.Add($"Brak etykiety lub znaczenia dla statusu {v}.");
            errors.AddRange(p.Statuses.Where(s => !values.Contains(s.Value)).Select(s => $"Status {s.Value} nie istnieje w enumie."));
            errors.AddRange(en.Fields.Where(x => string.IsNullOrWhiteSpace(p.Fields.FirstOrDefault(d => d.Field == x.Name)?.Description)).Select(x => $"Brak opisu pola {x.Name}."));
            errors.AddRange(CardChecks.Links(card, f, c.PageIds, card.Technical.Transitions.Select(t => t.Location)));
            return errors;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Encja: {en.Id} (moduł {en.Module}, tabela {en.Table ?? "nieznana"})");
        sb.AppendLine("\n## Pola");
        en.Fields.ForEach(x => sb.AppendLine($"- {x.Name}: {x.Type}, kolumna {x.Column ?? "-"}" + (x.EnumValues is { } ev ? $", wartości: {string.Join(", ", ev)}" : "")));
        sb.AppendLine("\n## Przejścia statusów");
        foreach (var t in en.Transitions)
            sb.AppendLine($"- {t.Field}: {t.From ?? "(utworzenie)"} → {t.To} w {t.Method}, warunek: {t.Condition ?? "brak"}; wykonuje: {string.Join(", ", Performers(t).Select(u => u.Id))}");
        sb.AppendLine("\n## Eventy: " + string.Join(", ", en.RaisedEvents));
        sb.AppendLine("\n## Przypadki użycia korzystające z encji");
        foreach (var u in f.UseCases.Where(u => u.Edges.Any(x => x.Type is "reads" or "writes" or "insert" or "update" or "delete" && f.EntityOf(x.Target) == en)))
        {
            sb.AppendLine($"- {u.Id}: " + string.Join("; ", u.Edges.Where(x => f.EntityOf(x.Target) == en).Select(x => x.ToString())));
            foreach (var g in u.Guards.Where(g => g.Condition.Contains(en.Name) || g.Via.StartsWith(en.Name + ".")))
                sb.AppendLine($"    guard {g.Condition} → {g.Exit}");
        }
        sb.AppendLine().AppendLine(c.Catalog);
        return new(sb.ToString(), offline, Apply, Validate);
    }
}
