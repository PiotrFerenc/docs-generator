using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocGen.Contracts;

namespace DocGen.Agent;

/// <summary>
/// Read-only tools of the agent. Knowledge comes from the rendered documentation, cards, code index and — when given —
/// the data snapshot. <paramref name="search"/> (Qdrant + reranker) is optional; without it a keyword search over the cards is used.
/// </summary>
sealed class Tools(CodeIndex index, CardSet cards, Snapshot? snapshot, string? docsDir, IDocsSearch? search)
{
    readonly Analyzer? analyzer = snapshot is null ? null : new Analyzer(index, cards, snapshot);

    public JsonArray Definitions()
    {
        var tools = new JsonArray
        {
            Tool("search_docs", "Wyszukiwanie w dokumentacji systemu (procesy, reguły, encje). Zwraca najtrafniejsze fragmenty ze stronami.",
                new JsonObject { ["query"] = Str("Zapytanie w języku naturalnym") }, "query"),
            Tool("list_pages", "Lista wszystkich stron dokumentacji (id, rodzaj, tytuł).", new JsonObject()),
            Tool("get_page", "Pełna treść strony dokumentacji (Markdown).",
                new JsonObject { ["page_id"] = Str("Id strony, np. T:Ns.SomeHandler albo flow:...") }, "page_id"),
            Tool("get_rules", "Reguły biznesowe przypadku użycia z warunkami z kodu (guardy) i miejscami w kodzie.",
                new JsonObject { ["page_id"] = Str("Id strony przypadku użycia") }, "page_id"),
            Tool("find_usages", "Które przypadki użycia czytają, zapisują albo tworzą encję / pole i które reguły od niego zależą.",
                new JsonObject { ["name"] = Str("Nazwa encji albo pola, np. Order, Order.Status, KycStatus") }, "name"),
            Tool("get_code", "Fragment kodu źródłowego wokół podanego miejsca.",
                new JsonObject { ["location"] = Str("Ścieżka i linia, np. src/Module.Application/Handler.cs:42") }, "location")
        };
        if (snapshot is not null)
        {
            tools.Add(Tool("get_rows", "Wiersze tabeli ze snapshotu danych.",
                new JsonObject { ["table"] = Str("Nazwa tabeli") }, "table"));
            tools.Add(Tool("check_flow", "Które kroki procesu mają ślad w danych i gdzie proces się zatrzymał.",
                new JsonObject { ["flow_id"] = Str("Id procesu (flow:...)") }, "flow_id"));
            tools.Add(Tool("evaluate_rules", "Ocena reguł przypadku użycia na danych ze snapshotu (❌ niespełniona, ✅ spełniona, ❓ nie do sprawdzenia).",
                new JsonObject { ["page_id"] = Str("Id strony przypadku użycia") }, "page_id"));
        }
        return tools;
    }

    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct)
    {
        JsonObject args;
        try
        {
            args = JsonNode.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson)?.AsObject() ?? [];
        }
        catch (JsonException ex)
        {
            return $"Niepoprawne argumenty narzędzia {name}: {ex.Message}";
        }
        string Arg(string key) => args[key]?.ToString() ?? "";

        return name switch
        {
            "search_docs" => Format(await SearchAsync(Arg("query"), ct)),
            "list_pages" => ListPages(),
            "get_page" => GetPage(Arg("page_id")),
            "get_rules" => GetRules(Arg("page_id")),
            "find_usages" => FindUsages(Arg("name")),
            "get_code" => GetCode(Arg("location")),
            "get_rows" => GetRows(Arg("table")),
            "check_flow" => analyzer?.CheckFlow(Arg("flow_id")) is { } flow ? Json(flow) : $"Brak procesu {Arg("flow_id")} albo snapshotu.",
            "evaluate_rules" => analyzer is null ? "Brak snapshotu danych." : Json(analyzer.EvaluateUseCase(Arg("page_id"))),
            _ => $"Nieznane narzędzie: {name}"
        };
    }

    /// <summary>Semantic search when configured and reachable, otherwise keyword search over the cards.</summary>
    public async Task<List<SearchHit>> SearchAsync(string query, CancellationToken ct)
    {
        if (search is not null)
        {
            try
            {
                var hits = await search.SearchAsync(query, ct);
                if (hits.Count > 0)
                    return hits;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
            {
                // Qdrant / embeddings unavailable — fall back to keyword search.
            }
        }
        return KeywordSearch(query);
    }

    List<SearchHit> KeywordSearch(string query)
    {
        var terms = Stems(query).ToHashSet();
        return Documents()
            .Select(d => (d, score: Stems(d.Text).Distinct().Count(terms.Contains)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Take(5)
            .Select(x => new SearchHit(x.d.PageId, x.d.Title, x.d.Section, x.d.Text.Length > 600 ? x.d.Text[..600] + "…" : x.d.Text,
                (double)x.score / Math.Max(1, terms.Count)))
            .ToList();
    }

    IEnumerable<(string PageId, string Title, string Section, string Text)> Documents()
    {
        foreach (var u in cards.UseCases)
        {
            yield return (u.PageId, u.Title, "Opis", $"{u.Title}. {u.Summary} {string.Join(' ', u.Triggers)} {u.MainScenario} {string.Join(' ', u.Effects)}");
            yield return (u.PageId, u.Title, "Reguły biznesowe", string.Join(' ', u.Rules.Select(r => $"{r.No}. {r.Rule} {r.OnFail}"))
                + " " + string.Join(' ', u.AlternativeScenarios.Select(a => $"{a.Title}: {a.Text}")));
        }
        foreach (var e in cards.Entities)
            yield return (e.PageId, e.Name, "Encja", $"{e.Name}. {e.Description} {string.Join(' ', e.Statuses.Select(s => $"{s.Label} {s.Value} {s.Meaning}"))} {string.Join(' ', e.Rules)}");
        foreach (var f in cards.Flows)
            yield return (f.PageId, f.Title, "Proces", $"{f.Title}. {f.Description} {string.Join(' ', f.Steps.Select(s => s.Label))} {string.Join(' ', f.SilentStops.Select(s => s.Reason))}");
        foreach (var g in cards.GlobalRules)
            yield return (g.PageId, g.Title, "Reguła globalna", $"{g.Title}. {g.Summary} {string.Join(' ', g.Behaviour)}");
        if (cards.Glossary is { } glossary)
            foreach (var entry in glossary.Entries)
                yield return (glossary.PageId, "Słownik pojęć", entry.Term, $"{entry.Term} {entry.CodeName} {entry.Description}");
    }

    // ponytail: 6-char prefix as crude Polish stemming; semantic search (search_docs with Qdrant) covers the rest.
    static IEnumerable<string> Stems(string text) =>
        Regex.Matches(text.ToLowerInvariant(), @"\p{L}[\p{L}\d]{2,}").Select(m => m.Value.Length > 6 ? m.Value[..6] : m.Value);

    string ListPages() => string.Join('\n',
        cards.UseCases.Select(u => $"{u.PageId} | przypadek użycia | {u.Title}")
            .Concat(cards.Entities.Select(e => $"{e.PageId} | encja | {e.Name}"))
            .Concat(cards.Flows.Select(f => $"{f.PageId} | proces | {f.Title}"))
            .Concat(cards.GlobalRules.Select(g => $"{g.PageId} | reguła globalna | {g.Title}"))
            .Concat(cards.Modules.Select(m => $"{m.PageId} | moduł | {m.Title}")));

    string GetPage(string pageId)
    {
        if (docsDir is not null && File.Exists(Path.Combine(docsDir, ".manifest.json")))
        {
            var page = DocGenJson.Read<Manifest>(Path.Combine(docsDir, ".manifest.json")).Pages.FirstOrDefault(p => p.PageId == pageId);
            var path = page is null ? null : Path.Combine(docsDir, page.Path);
            if (path is not null && File.Exists(path))
                return File.ReadAllText(path);
        }
        object? card = cards.UseCases.FirstOrDefault(c => c.PageId == pageId) as object
                       ?? cards.Entities.FirstOrDefault(c => c.PageId == pageId) as object
                       ?? cards.Flows.FirstOrDefault(c => c.PageId == pageId) as object
                       ?? cards.GlobalRules.FirstOrDefault(c => c.PageId == pageId) as object
                       ?? cards.Modules.FirstOrDefault(c => c.PageId == pageId);
        return card is null ? $"Brak strony {pageId}. Użyj list_pages." : Json(card);
    }

    string GetRules(string pageId)
    {
        var card = cards.UseCases.FirstOrDefault(c => c.PageId == pageId);
        var entry = index.Entries.FirstOrDefault(e => e.Id == pageId);
        if (card is null || entry is null)
            return $"Brak przypadku użycia {pageId}. Użyj list_pages.";
        var guards = entry.Guards.ToDictionary(g => g.Id);
        return string.Join('\n', card.Rules.Select(r =>
            $"{r.No}. {r.Rule} → {r.OnFail}\n" + string.Join('\n', r.GuardIds.Where(guards.ContainsKey).Select(id =>
                $"   {id}: `{guards[id].Condition}` → {guards[id].Exit} @ {guards[id].Location}"))));
    }

    string FindUsages(string name)
    {
        var dot = name.IndexOf('.');
        var entity = dot < 0 ? name : name[..dot];
        var field = dot < 0 ? null : name[(dot + 1)..];
        bool Matches(string target) =>
            field is null
                ? target.Equals(entity, StringComparison.OrdinalIgnoreCase)
                  || target.StartsWith(entity + ".", StringComparison.OrdinalIgnoreCase)
                  || target.EndsWith("." + entity, StringComparison.OrdinalIgnoreCase)
                : target.Equals($"{entity}.{field}", StringComparison.OrdinalIgnoreCase);

        var titles = cards.UseCases.ToDictionary(u => u.PageId, u => u.Title);
        var lines = new List<string>();
        foreach (var e in index.Entries)
        {
            var edges = e.Edges.Where(x => x.Type is "reads" or "writes" or "insert" or "update" or "delete" && Matches(x.Target)).ToList();
            var guards = e.Guards.Where(g => g.Structured?.Left is { } left && Matches(left)
                                             || Regex.IsMatch(g.Condition, $@"\b{Regex.Escape(field ?? entity)}\b")).ToList();
            if (edges.Count == 0 && guards.Count == 0)
                continue;
            lines.Add($"{e.Id} ({titles.GetValueOrDefault(e.Id, e.Kind)})");
            lines.AddRange(edges.Select(x => $"   {x}"));
            lines.AddRange(guards.Select(g => $"   warunek {g.Id}: `{g.Condition}` → {g.Exit} @ {g.Location}"));
        }
        if (index.Entities.FirstOrDefault(x => x.Name.Equals(entity, StringComparison.OrdinalIgnoreCase)) is { } info)
            lines.Add($"Encja {info.Name}: tabela {info.Table ?? "?"}, przejścia statusów: " +
                      string.Join("; ", info.Transitions.Select(t => $"{t.From ?? "∅"} → {t.To} ({t.Method} @ {t.Location})")));
        return lines.Count == 0 ? $"Nie znaleziono użyć {name}." : string.Join('\n', lines);
    }

    string GetCode(string location)
    {
        var colon = location.LastIndexOf(':');
        if (colon < 0 || !int.TryParse(location[(colon + 1)..], out var line))
            return "Podaj miejsce w formacie ścieżka:linia.";
        var path = location[..colon];
        var chunk = index.Entries.SelectMany(e => e.Chunks)
            .FirstOrDefault(c => c.Location.StartsWith(path + ":") && Start(c) <= line && line <= c.EndLine);
        if (chunk is null)
            return $"Brak kodu dla {location} w indeksie.";
        var start = Start(chunk);
        return string.Join('\n', chunk.Text.Split('\n').Select((text, i) => $"{start + i,5}  {text.TrimEnd('\r')}"));
    }

    string GetRows(string table) =>
        snapshot is null ? "Brak snapshotu danych."
        : snapshot.Tables.TryGetValue(table, out var rows)
            ? new JsonArray(rows.Select(r => (JsonNode)r.DeepClone()).ToArray()).ToJsonString(DocGenJson.Options)
            : $"Tabeli {table} nie ma w snapshocie. Dostępne: {string.Join(", ", snapshot.Tables.Keys)}.";

    static string Format(List<SearchHit> hits) =>
        hits.Count == 0 ? "Brak wyników."
        : string.Join("\n\n", hits.Select((h, i) => $"[{i + 1}] {h.Title} › {h.Section} (page_id: {h.PageId})\n{h.Text}"));

    static string Json<T>(T value) => JsonSerializer.Serialize(value, AgentJson.Options);

    static int Start(CodeChunk c) => int.Parse(c.Location[(c.Location.LastIndexOf(':') + 1)..]);

    static JsonObject Str(string description) => new() { ["type"] = "string", ["description"] = description };

    static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["type"] = "function",
        ["function"] = new JsonObject
        {
            ["name"] = name,
            ["description"] = description,
            ["parameters"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray())
            }
        }
    };
}
