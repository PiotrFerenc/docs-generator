using System.Text;
using System.Text.RegularExpressions;
using DocGen.Contracts;
using static DocGen.Cards.IndexFacts;

namespace DocGen.Cards;

/// <param name="Entry">Use case of the step; null for a step outside the index (unhandled event / command).</param>
/// <param name="Parent">Step that leads here; null for the first step.</param>
/// <param name="Via">Edge from the parent (or endpoint) to this step.</param>
/// <param name="Endpoint">Endpoint that starts this step, if any.</param>
sealed record FlowStepInfo(int No, IndexEntry? Entry, string? External, int? Parent, Edge? Via, bool Dashed, IndexEntry? Endpoint, string Mode);

sealed record FlowGraph(string PageId, IndexEntry Root, List<FlowStepInfo> Steps);

sealed record NodeLabel(string Id, string Label);

sealed record StopText(string Place, string Reason, string Trace);

/// <summary>LLM-written part of a flow card (FlowCard call site).</summary>
sealed record FlowProse(string Slug, string Title, string Description, string? SilentStopsIntro, List<NodeLabel> Nodes, List<StopText> SilentStops);

static partial class FlowCards
{
    [GeneratedRegex("^[A-Za-z0-9-]+$")] private static partial Regex FlowSlug();
    [GeneratedRegex("(?i)fail|reject|cancel|error")] private static partial Regex FailedStatus();

    /// <summary>
    /// Chains from roots (endpoints, use cases nobody triggers) along sends → handles → publishes → event handler → ...
    /// Kept when ≥ 2 use cases. A chain that inserts an entity is continued (dashed) by another endpoint whose use case updates it.
    /// </summary>
    public static List<FlowGraph> Discover(IndexFacts f)
    {
        var endpoints = f.Index.Entries.Where(e => e.Kind == "endpoint").OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        var roots = endpoints.Concat(f.UseCases.Where(u => !f.Incoming(u).Any())).ToList();
        var flows = new List<FlowGraph>();
        foreach (var root in roots)
        {
            var steps = Chain(f, root, 1, null, false, []);
            if (steps.Count(s => s.Entry is not null) < 2)
                continue;
            var inserted = steps.SelectMany(s => s.Entry?.Edges.Where(x => x.Type == "insert") ?? []).Select(x => f.EntityOf(x.Target)).OfType<EntityInfo>().ToHashSet();
            foreach (var other in endpoints.Where(ep => ep != root))
            {
                var first = f.Outgoing(other).Select(o => o.Target).OfType<IndexEntry>().FirstOrDefault();
                if (first is null || steps.Any(s => s.Entry == first) ||
                    !first.Edges.Any(x => x.Type is "writes" or "update" && f.EntityOf(x.Target) is { } en && inserted.Contains(en)))
                    continue;
                steps.AddRange(Chain(f, other, steps.Count + 1, steps[^1].No, true, steps));
            }
            flows.Add(new($"flow:{root.Id}", root, steps));
        }
        return flows;
    }

    static List<FlowStepInfo> Chain(IndexFacts f, IndexEntry root, int startNo, int? parent, bool dashed, List<FlowStepInfo> existing)
    {
        var steps = new List<FlowStepInfo>();
        bool Seen(IndexEntry t) => steps.Concat(existing).Any(s => s.Entry == t);

        void Visit(IndexEntry from, int? fromNo)
        {
            foreach (var (edge, target) in f.Outgoing(from))
            {
                if (target is not null && Seen(target))
                    continue;
                var no = startNo + steps.Count;
                var isEndpoint = from.Kind == "endpoint";
                var mode = isEndpoint ? (dashed ? $"na zewnętrzne wywołanie (`{from.Id}`)" : $"na żądanie (`{from.Id}`)")
                    : target is null ? "poza zakresem indeksu"
                    : edge.Type == "publishes" ? $"w tle, po zapisie kroku {fromNo}"
                    : $"wywołane przez krok {fromNo}";
                steps.Add(new(no, target, target is null ? edge.Target : null, fromNo ?? parent, edge, isEndpoint && dashed, isEndpoint ? from : null, mode));
                if (target is not null)
                    Visit(target, no);
            }
        }

        if (root.Kind == "endpoint")
            Visit(root, null);
        else
        {
            steps.Add(new(startNo, root, null, parent, null, dashed, null, "automatycznie (brak wyzwalacza w indeksie)"));
            Visit(root, startNo);
        }
        return steps;
    }

    public static CardJob<FlowProse, FlowCard> Prepare(FlowGraph g, Ctx c, IReadOnlyDictionary<string, UseCaseCard> useCases)
    {
        var f = c.F;
        var steps = g.Steps;
        string Label(FlowStepInfo s) => s.Entry is { } e ? useCases[e.Id].Title : $"Obsługa `{Short(s.External!)}` (poza indeksem)";
        string Module(FlowStepInfo s) => s.Entry?.Module ?? steps.FirstOrDefault(p => p.No == s.Parent)?.Entry?.Module ?? g.Root.Module;
        var flowSteps = steps.Select(s => new FlowStep(s.No, s.Entry?.Id, Label(s), Module(s), s.Mode)).ToList();

        // ── diagram ──
        var nodes = new List<FlowNode>();
        var edges = new List<FlowEdge>();
        string Node(string label, string shape)
        {
            var id = nodes.Count < 26 ? ((char)('A' + nodes.Count)).ToString() : $"N{nodes.Count}";
            nodes.Add(new(id, label, shape));
            return id;
        }
        var start = g.Root.Kind == "endpoint" ? Node(g.Root.Id, "step") : null;
        var exitNode = new Dictionary<int, (string Id, bool Decision)>();
        foreach (var s in steps)
        {
            var id = Node(Label(s).Replace("`", ""), "step");
            var from = s.Parent is int p ? exitNode[p] : (start, false);
            var link = s.Dashed ? "zewnętrzne wywołanie" : s.Via?.Type == "publishes" ? "po zapisie, w tle" : null;
            if (from.Item1 is { } fromId)
                edges.Add(new(fromId, id, from.Item2 ? "nie" : link, s.Dashed));
            exitNode[s.No] = (id, false);
            var returns = s.Entry?.Guards.Where(x => x.Exit == "return").ToList() ?? [];
            if (returns.Count > 0 && steps.Any(x => x.Parent == s.No))
            {
                var decision = Node(string.Join(" lub ", returns.Select(r => r.Condition)) + "?", "decision");
                edges.Add(new(id, decision, null, false));
                edges.Add(new(decision, Node("Koniec bez dalszych kroków", "end"), "tak", false));
                exitNode[s.No] = (decision, true);
            }
        }
        var leaves = steps.Where(s => !steps.Any(x => x.Parent == s.No)).ToList();
        if (leaves.Count > 0)
        {
            var end = Node("Koniec procesu", "end");
            leaves.ForEach(s => edges.Add(new(exitNode[s.No].Id, end, null, false)));
        }

        // ── silent stops ──
        var stops = new List<StopText>();
        foreach (var s in steps.Where(s => s.Entry is not null))
        {
            var card = useCases[s.Entry!.Id];
            var guards = s.Entry.Guards.ToDictionary(x => x.Id);
            foreach (var grp in s.Entry.Guards.Where(x => x.Exit is "silent_success" or "return")
                         .GroupBy(x => card.Rules.FirstOrDefault(r => r.GuardIds.Contains(x.Id))))
                stops.Add(grp.Key is { } rule
                    ? new($"krok {s.No}, {RuleRef([rule.No])}", rule.Rule, "brak śladu w danych ani w logach")
                    : new($"krok {s.No}", string.Join(", ", grp.Select(x => $"`{x.Condition}`")), "brak śladu w danych ani w logach"));
            if (s.Via is { Type: "sends", Detail: "logged" or "ignored" } via)
            {
                var failRules = card.Rules.Where(r => r.GuardIds.Any(id => guards.TryGetValue(id, out var x) && IsFailure(x))).Select(r => r.No).ToList();
                stops.Add(new(failRules.Count > 0 ? $"krok {s.No}, {RuleRef(failRules)}" : $"krok {s.No}",
                    $"Błąd kroku {s.No} jest {(via.Detail == "logged" ? "tylko zapisywany w logach" : "ignorowany")} przez krok {s.Parent}, bez ponowienia.",
                    via.Detail == "logged" ? "ostrzeżenie w logach" : "brak"));
            }
            foreach (var w in s.Entry.Edges.Where(x => x.Type == "writes" && x.Detail is not null))
                if (f.EntityOf(w.Target) is { } en && en.Transitions.Any(t => t.To == Tail(w.Detail!)) &&
                    !en.Transitions.Any(t => t.From == Tail(w.Detail!)) && FailedStatus().IsMatch(Tail(w.Detail!)))
                    stops.Add(new($"krok {s.No}", $"Status `{Tail(w.Detail!)}` jest końcowy; nic nie wznawia procesu.", $"`{w.Target} = {Tail(w.Detail!)}`"));
        }

        // ── technical rows ──
        var technical = steps.Select(s =>
        {
            var input = s.Endpoint is { } ep ? $"`{ep.Id}` → `{Short(s.Via!.Target)}`"
                : s.Entry is { } en ? $"`{Short(en.Id)}`"
                : $"handler `{Short(s.External!)}` (poza indeksem)";
            var next = steps.Where(x => x.Parent == s.No).Select(x =>
                x.Dashed ? "zewnętrzne wywołanie (poza indeksem)"
                : x.Via!.Type == "publishes" ? $"`{(s.Entry is null ? Short(x.Via.Target) : RaiseSite(s.Entry, x.Via.Target))}` → `{Short(x.Via.Target)}` (domain event, po commicie)"
                : $"`ISender.Send({Short(x.Via.Target)})`" + (x.Via.Detail == "logged" ? ", wynik tylko logowany" : x.Via.Detail == "ignored" ? ", wynik ignorowany" : "")).Distinct().ToList();
            return new FlowTechnicalRow(s.No, input, next.Count > 0 ? string.Join("; ", next) : "koniec procesu");
        }).ToList();

        var firstUseCase = steps.First(s => s.Entry is not null).Entry!;
        var offline = new FlowProse(
            Regex.Replace(useCases[firstUseCase.Id].Slug, "[^A-Za-z0-9-]", "") + "Flow",
            $"Przebieg od: {useCases[firstUseCase.Id].Title}",
            "Kroki procesu: " + string.Join(" → ", flowSteps.Select(s => s.Label)) + ".",
            stops.Count > 0 ? "W tych miejscach proces może zatrzymać się bez widocznego błędu:" : null,
            nodes.Select(n => new NodeLabel(n.Id, n.Label)).ToList(),
            stops);

        FlowCard Apply(FlowProse p) => new(g.PageId, p.Slug, p.Title, p.Description,
            nodes.Select(n => n with { Label = p.Nodes.FirstOrDefault(x => x.Id == n.Id)?.Label ?? n.Label }).ToList(),
            edges, flowSteps, p.SilentStopsIntro,
            stops.Select((st, i) => p.SilentStops.ElementAtOrDefault(i) is { } t && t.Place == st.Place ? new SilentStop(st.Place, t.Reason, t.Trace) : new SilentStop(st.Place, st.Reason, st.Trace)).ToList(),
            technical, LlmRunner.BlankMeta);

        List<string> Validate(FlowProse p, FlowCard card)
        {
            var errors = new List<string>();
            CardChecks.Required(errors, p.Title, "title");
            CardChecks.Required(errors, p.Description, "description");
            if (!FlowSlug().IsMatch(p.Slug ?? ""))
                errors.Add($"slug \"{p.Slug}\" może zawierać tylko litery ASCII, cyfry i myślniki.");
            foreach (var n in nodes)
                if (string.IsNullOrWhiteSpace(p.Nodes.FirstOrDefault(x => x.Id == n.Id)?.Label))
                    errors.Add($"Brak etykiety węzła {n.Id}.");
            errors.AddRange(p.Nodes.Where(x => nodes.All(n => n.Id != x.Id)).Select(x => $"Węzeł {x.Id} nie istnieje."));
            if (p.SilentStops.Count != stops.Count)
                errors.Add($"silentStops musi mieć {stops.Count} pozycji (jest {p.SilentStops.Count}).");
            for (var i = 0; i < Math.Min(stops.Count, p.SilentStops.Count); i++)
            {
                if (p.SilentStops[i].Place != stops[i].Place)
                    errors.Add($"silentStops[{i}].place musi być \"{stops[i].Place}\".");
                CardChecks.Required(errors, p.SilentStops[i].Reason, $"silentStops[{i}].reason");
                CardChecks.Required(errors, p.SilentStops[i].Trace, $"silentStops[{i}].trace");
            }
            errors.AddRange(CardChecks.Links(card, f, c.PageIds));
            return errors;
        }

        var sb = new StringBuilder();
        sb.AppendLine($"Proces: {g.PageId}");
        sb.AppendLine("\n## Kroki");
        foreach (var s in steps)
        {
            sb.AppendLine($"{s.No}. {Label(s)} [{s.Entry?.Id ?? "poza indeksem"}], moduł {Module(s)}, tryb: {s.Mode}");
            if (s.Entry is { } e)
            {
                var card = useCases[e.Id];
                sb.AppendLine($"   cel: {card.Summary}");
                card.Rules.ForEach(r => sb.AppendLine($"   reguła {r.No}: {r.Rule} → {r.OnFail}"));
            }
        }
        sb.AppendLine("\n## Węzły diagramu (id | kształt | etykieta robocza)");
        nodes.ForEach(n => sb.AppendLine($"- {n.Id} | {n.Shape} | {n.Label}"));
        sb.AppendLine("## Krawędzie");
        edges.ForEach(x => sb.AppendLine($"- {x.From} -> {x.To}{(x.Label is null ? "" : $" [{x.Label}]")}{(x.Dashed ? " (przerywana)" : "")}"));
        sb.AppendLine("\n## Miejsca cichych zatrzymań (place | fakt)");
        stops.ForEach(st => sb.AppendLine($"- {st.Place} | {st.Reason} | {st.Trace}"));
        sb.AppendLine().AppendLine(c.Catalog);
        return new(sb.ToString(), offline, Apply, Validate);
    }
}
