using System.Text;
using DocGen.Contracts;
using Microsoft.Extensions.Options;

namespace DocGen.Agent;

sealed class DocAgent(
    AgentLoop loop,
    DocGenPaths paths,
    IOptions<GeneratorOptions> generator,
    IOptions<AgentReportOptions> report,
    IDocsSearch? search) : IDocAgent
{
    string commit = "";

    public async Task<AgentResult> AskAsync(string question, Snapshot? snapshot, CancellationToken ct)
    {
        var workDir = paths.Resolve(generator.Value.WorkDir);
        var index = DocGenJson.Read<CodeIndex>(Path.Combine(workDir, "index.json"));
        var cards = DocGenJson.Read<CardSet>(Path.Combine(workDir, "cards.json"));
        commit = index.Commit;

        var docsDir = paths.Resolve(generator.Value.OutputDir);
        var tools = new Tools(index, cards, snapshot, Directory.Exists(docsDir) ? docsDir : null, search);
        var sources = await tools.SearchAsync(question, ct);
        var analysis = snapshot is null ? null : new Analyzer(index, cards, snapshot).Analyze();

        var calls = new List<ToolCall>();
        var answer = await loop.RunAsync(question, sources, analysis, tools, calls, ct);
        return new AgentResult(question, answer, sources, analysis, calls);
    }

    public string ToMarkdown(AgentResult r)
    {
        var md = new StringBuilder();
        md.AppendLine("# Odpowiedź agenta").AppendLine();
        md.AppendLine($"**Pytanie:** {r.Question}").AppendLine();

        if (r.Answer is not null)
            md.AppendLine(r.Answer.Trim()).AppendLine();
        else
            md.AppendLine("> Model LLM nie jest skonfigurowany (sekcja `Agent` w `appsettings.json`) — poniżej tylko wyniki wyszukiwania"
                          + (r.Analysis is null ? "." : " i automatycznej analizy danych.")).AppendLine();

        if (r.Analysis is { } a)
            Analysis(md, a);

        if (r.Sources.Count > 0)
        {
            md.AppendLine("## Źródła w dokumentacji").AppendLine();
            foreach (var (h, i) in r.Sources.Select((h, i) => (h, i)))
                md.AppendLine($"{i + 1}. **{h.Title}** › {h.Section} (`{h.PageId}`)");
            md.AppendLine();
        }

        if (r.ToolCalls.Count > 0)
        {
            md.AppendLine("## Użyte narzędzia").AppendLine();
            r.ToolCalls.ForEach(c => md.AppendLine($"- `{c.Name}({c.Arguments})`"));
            md.AppendLine();
        }
        return md.ToString().TrimEnd() + "\n";
    }

    void Analysis(StringBuilder md, DataAnalysis a)
    {
        md.AppendLine("## Analiza danych").AppendLine();
        md.AppendLine($"**Wynik:** {Verdict(a.Verdict)}").AppendLine();
        if (a.Flow is { } flow)
        {
            md.AppendLine($"**Proces:** {flow.Title}").AppendLine();
            md.AppendLine("| Krok | Proces | Stan | Dowód |").AppendLine("|---|---|---|---|");
            foreach (var s in flow.Steps)
                md.AppendLine($"| {s.No} | {Cell(s.Label)} | {State(s, flow.BreakStep)} | {Cell(s.Evidence)} |");
            md.AppendLine();
        }
        Rules(md, "Niespełnione reguły", a.Causes, "❌");
        Rules(md, "Nierozstrzygnięte", a.Unresolved, "❓");
        Rules(md, "Spełnione", a.RuledOut, "✅");
        List(md, "Uwagi", a.Notes);
        List(md, "Brakujące dane", a.MissingData);
    }

    void Rules(StringBuilder md, string title, List<RuleCheck> rules, string icon)
    {
        if (rules.Count == 0)
            return;
        md.AppendLine($"### {title}").AppendLine();
        foreach (var c in rules)
        {
            var silent = c.Silent ? " *(ciche zatrzymanie — bez błędu)*" : "";
            md.AppendLine($"- {icon} **{c.UseCase}, reguła {c.No}:** {c.Rule} Skutek: {c.OnFail}{silent}");
            c.Evidence.ForEach(e => md.AppendLine($"  - {e}"));
            if (c.Locations.Count > 0)
                md.AppendLine($"  - Kod: {string.Join(", ", c.Locations.Select(Link))}");
        }
        md.AppendLine();
    }

    static void List(StringBuilder md, string title, List<string> items)
    {
        if (items.Count == 0)
            return;
        md.AppendLine($"### {title}").AppendLine();
        items.ForEach(i => md.AppendLine($"- {i}"));
        md.AppendLine();
    }

    static string Verdict(string v) => v switch
    {
        "cause_found" => "proces zatrzymał się na niespełnionej regule",
        "outside_code" => "proces zakończył się wynikiem negatywnym albo zatrzymał poza kodem (integracja zewnętrzna)",
        "candidates" => "proces się zatrzymał, ale przyczyny nie da się rozstrzygnąć na tych danych",
        "insufficient_data" => "za mało danych, by ocenić przebieg procesu",
        _ => "wszystkie kroki opisanych procesów mają ślad w danych"
    };

    static string State(StepCheck s, int? breakStep) => s.State switch
    {
        StepState.Done => "✅ wykonany",
        StepState.NotDone when s.No == breakStep => "❌ **tu się zatrzymało**",
        StepState.NotDone => "nie doszło",
        StepState.NoTrace => "➖ brak śladu w danych",
        StepState.External => "↗ poza kodem",
        _ => "❓ nieznany"
    };

    string Link(string location)
    {
        var template = report.Value.RepoUrlTemplate;
        var colon = location.LastIndexOf(':');
        if (string.IsNullOrWhiteSpace(template) || colon < 0)
            return $"`{location}`";
        var path = location[..colon];
        var line = location[(colon + 1)..];
        return $"[{Path.GetFileName(path)}:{line}]({template.Replace("{commit}", commit).Replace("{path}", path).Replace("{line}", line)})";
    }

    static string Cell(string text) => text.Replace("|", "\\|").Replace("\n", " ");
}
