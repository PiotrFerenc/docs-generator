using System.Globalization;
using System.Text.RegularExpressions;
using DocGen.Contracts;

namespace DocGen.Agent;

/// <summary>
/// Evaluates a business rule (card rule + its index guards) against the snapshot.
/// A guard condition describes when the code EXITS, so "condition true" = rule violated.
/// Only facts visible in the data are evaluated; anything else is Unknown with a note on what is missing.
/// </summary>
sealed class RuleEvaluator(DataView data)
{
    static readonly Regex FlagCall = new("""IsEnabledAsync\("([^"]+)"\)""");
    static readonly Regex Lambda = new(@"^\w+\(\s*(\w+)\s*=>\s*(.+)\)$", RegexOptions.Singleline);

    public (RuleState State, List<string> Evidence) Evaluate(IReadOnlyList<Guard> guards)
    {
        var evidence = new List<string>();
        var results = new List<bool?>();
        var filter = guards.FirstOrDefault(g => g.Kind == "filter") is { } f ? Filter(f, evidence) : null;

        foreach (var guard in guards.Where(g => g.Kind != "filter"))
            results.Add(Guard(guard, filter, evidence));
        if (guards.All(g => g.Kind == "filter") && filter is { } onlyFilter)
            results.Add(onlyFilter.Matched is { } m ? m == 0 : null);

        var state = results.Any(r => r == true) ? RuleState.Violated
            : results.Count > 0 && results.All(r => r == false) ? RuleState.Ok
            : RuleState.Unknown;
        return (state, evidence);
    }

    bool? Guard(Guard guard, FilterResult? filter, List<string> evidence)
    {
        if (guard.Exit == "validation")
        {
            evidence.Add($"`{guard.Condition}` sprawdza dane wejściowe komendy — nie ma ich w bazie.");
            return null;
        }

        if (FlagCall.Match(guard.Condition) is { Success: true } flag)
            return Flag(flag.Groups[1].Value, guard.Condition.TrimStart().StartsWith('!'), evidence);

        if (filter is not null)
        {
            if (filter.Matched is not { } matched)
                return null;
            var condition = guard.Condition.Replace(" ", "");
            var fires = condition.Contains("isnull") || condition.Contains("==null") ? matched == 0 : matched > 0;
            return condition.StartsWith('!') ? !fires : fires;
        }

        if (guard.Structured is { } s)
            return Structured(s, guard.Condition, evidence);

        evidence.Add($"`{guard.Condition}` — warunku nie da się sprawdzić na danych ze snapshotu.");
        return null;
    }

    bool? Flag(string name, bool negated, List<string> evidence)
    {
        if (!data.Snapshot.Config.TryGetValue(name, out var node) || node is null)
        {
            evidence.Add($"Brak wartości flagi `{name}` (podaj ją w `config` snapshotu).");
            return null;
        }
        var enabled = node.ToJsonString().Trim('"').Equals("true", StringComparison.OrdinalIgnoreCase);
        evidence.Add($"flaga `{name}` = {(enabled ? "włączona" : "wyłączona")}");
        return negated ? !enabled : enabled;
    }

    bool? Structured(GuardExpr expr, string condition, List<string> evidence)
    {
        var dot = expr.Left.LastIndexOf('.');
        var property = dot < 0 ? expr.Left : expr.Left[(dot + 1)..];
        var entity = dot > 0 ? data.Entity(expr.Left[..dot]) : null;
        var byName = false;
        if (entity is null)
        {
            entity = data.EntityWith([property]);
            byName = entity is not null;
        }
        if (entity is null)
        {
            evidence.Add($"`{condition}` — nie wiadomo, której tabeli dotyczy `{expr.Left}`.");
            return null;
        }

        var (fetched, table, rows) = data.Rows(entity);
        if (!fetched || rows.Count == 0)
        {
            evidence.Add(fetched ? $"`{condition}` — brak rekordu w `{table}`." : $"`{condition}` — tabela `{table}` nie została pobrana.");
            return null;
        }
        if (!rows.All(r => data.HasColumn(r, entity, property)))
        {
            evidence.Add($"`{condition}` — w `{table}` brak kolumny `{data.Column(entity, property)}`.");
            return null;
        }

        var outcomes = rows.Select(r => Compare(data.Value(r, entity, property), expr.Op, expr.Right)).Distinct().ToList();
        var values = string.Join(", ", rows.Select(r => data.Value(r, entity, property) ?? "null").Distinct());
        evidence.Add($"`{table}.{data.Column(entity, property)}` = {values}{(byName ? " (dopasowane po nazwie pola)" : "")}");
        return outcomes.Count == 1 ? outcomes[0] : null;
    }

    sealed record FilterResult(int? Matched);

    FilterResult Filter(Guard guard, List<string> evidence)
    {
        var lambda = Lambda.Match(guard.Condition.Trim());
        if (!lambda.Success || lambda.Groups[2].Value.Contains("||"))
        {
            evidence.Add($"`{guard.Condition}` — filtra nie da się sprawdzić automatycznie.");
            return new(null);
        }

        var p = Regex.Escape(lambda.Groups[1].Value);
        var atoms = new List<(string Property, string Op, string? Right)>();
        foreach (var raw in lambda.Groups[2].Value.Split("&&", StringSplitOptions.TrimEntries))
        {
            if (Regex.Match(raw, $@"^!\s*{p}\.(\w+)$") is { Success: true } not)
                atoms.Add((not.Groups[1].Value, "is_false", null));
            else if (Regex.Match(raw, $@"^{p}\.(\w+)$") is { Success: true } flag)
                atoms.Add((flag.Groups[1].Value, "is_true", null));
            else if (Regex.Match(raw, $@"^{p}\.(\w+)\s*(==|!=|<=|>=|<|>)\s*(.+)$") is { Success: true } cmp)
                atoms.Add((cmp.Groups[1].Value, cmp.Groups[2].Value, cmp.Groups[3].Value.Trim()));
            else
            {
                evidence.Add($"`{guard.Condition}` — nieobsługiwany fragment filtra `{raw}`.");
                return new(null);
            }
        }

        var entity = data.EntityWith(atoms.Select(a => a.Property).Distinct().ToList());
        if (entity is null)
        {
            evidence.Add($"`{guard.Condition}` — nie wiadomo, której tabeli dotyczy filtr.");
            return new(null);
        }
        var (fetched, table, rows) = data.Rows(entity);
        if (!fetched)
        {
            evidence.Add($"Tabela `{table}` nie została pobrana (filtr `{guard.Condition}`).");
            return new(null);
        }

        // Comparisons with a variable (e.g. customerId) are bound to the case — the snapshot holds only its rows.
        var constant = atoms.Where(a => a.Right is null || IsConstant(a.Right)).ToList();
        var matched = rows.Count(r => constant.All(a => Compare(data.Value(r, entity, a.Property), a.Op, a.Right) == true));
        evidence.Add($"`{table}`: {matched} z {rows.Count} rekordów spełnia `{lambda.Groups[2].Value}`");
        return new(matched);
    }

    static bool IsConstant(string text) =>
        text is "true" or "false" or "null"
        || text.StartsWith('"')
        || decimal.TryParse(text.TrimEnd('m', 'M'), NumberStyles.Number, CultureInfo.InvariantCulture, out _)
        || Regex.IsMatch(text, @"^[A-Z]\w*\.[A-Z]\w*$");

    /// <summary>true/false when decidable, null otherwise.</summary>
    internal static bool? Compare(string? actual, string op, string? right)
    {
        switch (op)
        {
            case "is_null": return actual is null;
            case "is_not_null": return actual is not null;
            case "is_empty": return string.IsNullOrWhiteSpace(actual);
            case "is_not_empty": return !string.IsNullOrWhiteSpace(actual);
            case "is_true": return actual is null ? null : actual.Equals("true", StringComparison.OrdinalIgnoreCase);
            case "is_false": return actual is null ? null : actual.Equals("false", StringComparison.OrdinalIgnoreCase);
        }

        var expected = Normalize(right);
        if (expected is null || actual is null)
            return op switch { "==" => actual == expected, "!=" => actual != expected, _ => null };

        if (decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var a)
            && decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var b))
            return op switch { "==" => a == b, "!=" => a != b, "<" => a < b, "<=" => a <= b, ">" => a > b, ">=" => a >= b, _ => null };

        var equal = actual.Equals(expected, StringComparison.OrdinalIgnoreCase);
        return op switch { "==" => equal, "!=" => !equal, _ => null };
    }

    static string? Normalize(string? right)
    {
        if (right is null or "null")
            return null;
        if (right.StartsWith('"'))
            return right.Trim('"');
        var number = right.TrimEnd('m', 'M').Replace("_", "");
        if (decimal.TryParse(number, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
            return number;
        var dot = right.LastIndexOf('.');
        return dot < 0 ? right : right[(dot + 1)..];
    }
}
