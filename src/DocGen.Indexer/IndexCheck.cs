using System.Text.Json;
using DocGen.Contracts;

namespace DocGen.Indexer;

// Compares the index with expectations taken from the golden documentation pages (tests/expected-index.json).
// Missing items fail the check; extra items are only reported (the card step may dismiss them).
//   entries:  guards "<location> <exit>" (propagate guards ignored), edges Edge.ToString() (behavior edges never reported as extra)
//   entities: "table <name|null>", "column <Field> <column>" (only when "columns" given), "transition <Field> <From|*> -> <To> via <Method>"
public static class IndexCheck
{
    sealed record ExpectedEntry(string[] Guards, string[] Edges);
    sealed record ExpectedEntity(string? Table, string[]? Columns, string[] Transitions);
    sealed record Expected(Dictionary<string, ExpectedEntry> Entries, Dictionary<string, ExpectedEntity>? Entities);

    public static int Run(CodeIndex index, string expectedPath)
    {
        var expected = JsonSerializer.Deserialize<Expected>(
            File.ReadAllText(expectedPath), new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
        var failed = false;

        foreach (var (id, exp) in expected.Entries)
        {
            var entry = index.Entries.FirstOrDefault(e => e.Id == id);
            failed |= entry is null
                ? NotFound(id)
                : Report(id,
                    exp.Guards.Concat(exp.Edges),
                    entry.Guards.Where(g => g.Exit != "propagate").Select(g => $"{g.Location} {g.Exit}").Concat(entry.Edges.Select(e => e.ToString())));
        }

        foreach (var (id, exp) in expected.Entities ?? [])
        {
            var entity = index.Entities.FirstOrDefault(e => e.Id == id);
            failed |= entity is null
                ? NotFound(id)
                : Report(id,
                    exp.Transitions.Select(t => $"transition {t}").Append($"table {exp.Table ?? "null"}")
                        .Concat(exp.Columns?.Select(c => $"column {c}") ?? []),
                    entity.Transitions.Select(t => $"transition {t.Field} {t.From ?? "*"} -> {t.To} via {t.Method}").Append($"table {entity.Table ?? "null"}")
                        .Concat(exp.Columns is null ? [] : entity.Fields.Select(f => $"column {f.Name} {f.Column ?? "null"}")));
        }

        return failed ? 1 : 0;
    }

    static bool NotFound(string id)
    {
        Console.WriteLine($"FAIL {id}: not found in index");
        return true;
    }

    static bool Report(string id, IEnumerable<string> expected, IEnumerable<string> actual)
    {
        var exp = expected.ToHashSet();
        var act = actual.ToHashSet();
        var missing = exp.Except(act).ToList();
        var extra = act.Except(exp).Where(x => !x.StartsWith("behavior ")).ToList();

        Console.WriteLine($"{(missing.Count == 0 ? "OK  " : "FAIL")} {id}");
        missing.ForEach(m => Console.WriteLine($"     missing: {m}"));
        extra.ForEach(x => Console.WriteLine($"     extra:   {x}"));
        return missing.Count > 0;
    }
}
