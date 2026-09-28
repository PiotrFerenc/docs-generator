using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using DocGen.Contracts;

namespace DocGen.Render;

public sealed record RenderedPage(ManifestPage Page, string Content);

public sealed record RenderResult(Manifest Manifest, List<RenderedPage> Pages, List<string> Warnings);

/// <summary>Pure CardSet → Markdown pages. No IO except reading optional notes files.</summary>
public sealed partial class PageBuilder
{
    record Target(string Path, string Label);

    readonly CardSet cards;
    readonly string repoUrl;
    readonly string? notesDir;
    readonly Dictionary<string, Target> targets = [];
    readonly List<string> warnings = [];
    string current = "";

    PageBuilder(CardSet cards, string repoUrl, string? notesDir) =>
        (this.cards, this.repoUrl, this.notesDir) = (cards, repoUrl, notesDir);

    public static RenderResult Build(CardSet cards, string repoUrlTemplate, string? notesDir) =>
        new PageBuilder(cards, repoUrlTemplate, notesDir).Run();

    RenderResult Run()
    {
        var sys = cards.System;
        string? ModulePage(string module) => cards.Modules.FirstOrDefault(m => m.Module == module)?.PageId ?? sys?.PageId;

        var specs = new List<(string Id, string Path, string Title, string Label, string Kind, string? Parent, Func<List<string>> Body, string Slug)>();
        if (sys is not null)
            specs.Add((sys.PageId, "index.md", "Przegląd systemu", sys.Title, "system", null, () => SystemBody(sys), "index"));
        if (cards.Glossary is { } g)
            specs.Add((g.PageId, "glossary.md", "Słownik pojęć", "Słownik pojęć", "glossary", sys?.PageId, () => GlossaryBody(g), "glossary"));
        foreach (var m in cards.Modules)
            specs.Add((m.PageId, $"modules/{m.Module}/index.md", $"{m.Module} / Przegląd modułu", m.Module, "module", sys?.PageId, () => ModuleBody(m), "index"));
        foreach (var u in cards.UseCases)
            specs.Add((u.PageId, $"modules/{u.Module}/use-cases/{u.Slug}.md", $"{u.Module} / {u.Title}", u.Title, "use_case", ModulePage(u.Module), () => UseCaseBody(u), u.Slug));
        foreach (var e in cards.Entities)
            specs.Add((e.PageId, $"modules/{e.Module}/entities/{e.Slug}.md", $"{e.Module} / Encja: {e.Name}", e.Name, "entity", ModulePage(e.Module), () => EntityBody(e), e.Slug));
        foreach (var f in cards.Flows)
            specs.Add((f.PageId, $"flows/{f.Slug}.md", $"Proces: {f.Title}", f.Title, "flow", sys?.PageId, () => FlowBody(f), f.Slug));
        foreach (var r in cards.GlobalRules)
            specs.Add((r.PageId, $"global-rules/{r.Slug}.md", $"Reguły globalne / {r.Title}", r.Title, "global_rule", sys?.PageId, () => GlobalRuleBody(r), r.Slug));

        foreach (var s in specs)
            if (!targets.TryAdd(s.Id, new(s.Path, s.Label)))
                warnings.Add($"Duplicate page id '{s.Id}' ({s.Path}); links go to the first one.");
        foreach (var dup in specs.GroupBy(s => s.Path).Where(x => x.Count() > 1))
            warnings.Add($"Duplicate page path '{dup.Key}' — pages overwrite each other.");
        foreach (var dup in specs.GroupBy(s => s.Title).Where(x => x.Count() > 1))
            warnings.Add($"Duplicate page title '{dup.Key}' — Confluence requires unique titles.");

        var pages = new List<RenderedPage>();
        foreach (var s in specs)
        {
            current = s.Path;
            var content = Page(s.Title, s.Id, s.Slug, s.Body());
            pages.Add(new(new(s.Id, s.Path, s.Title, s.Kind, s.Parent, MarkdownRenderer.Sha256(content)), content));
        }
        return new(new(cards.Commit, pages.Select(p => p.Page).ToList()), pages, warnings);
    }

    // ── Page skeleton ──────────────────────────────────────────────────────

    string Page(string title, string pageId, string slug, List<string> blocks)
    {
        var sb = new StringBuilder()
            .Append("---\n")
            .Append($"title: {Quote(title)}\n")
            .Append($"page_id: {Quote(pageId)}\n")
            .Append("generated: true\n")
            .Append($"source_commit: {cards.Commit}\n")
            .Append("---\n\n");
        blocks.Insert(1, $"> Strona generowana automatycznie z kodu. Nie edytuj jej. Uzupełnienia dodawaj w `{slug}.notes.md`.");
        blocks.Add("## Uwagi zespołu");
        blocks.Add(Notes() ?? $"(treść dołączana z `{slug}.notes.md`, jeśli istnieje)");
        return sb.Append(string.Join("\n\n", blocks.Where(b => b.Length > 0))).Append('\n').ToString();
    }

    string? Notes()
    {
        if (notesDir is null)
            return null;
        var file = Path.Combine(notesDir, current[..^3] + ".notes.md");
        if (!File.Exists(file))
            return null;
        var text = File.ReadAllText(file).Replace("\r\n", "\n").Trim();
        return text.Length > 0 ? text : null;
    }

    static string Quote(string s) => "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    // ── Page kinds ─────────────────────────────────────────────────────────

    List<string> UseCaseBody(UseCaseCard u)
    {
        var b = new List<string> { $"# {u.Title}", "## Cel", T(u.Summary) };
        if (u.Triggers.Count > 0 || u.TriggersNote is not null)
            b.AddRange(["## Kiedy się uruchamia", Bullets(u.Triggers), Opt(u.TriggersNote)]);

        b.Add("## Scenariusze");
        // A MainScenario that starts with bold carries its own lead(s), e.g. "**Scenariusz główny (sukces):** ...".
        b.Add(u.MainScenario.StartsWith("**") ? T(u.MainScenario) : $"**Scenariusz główny:** {T(u.MainScenario)}");
        if (u.AlternativeScenarios.Count > 0)
            b.AddRange(["**Scenariusze alternatywne:**", Bullets(u.AlternativeScenarios.Select(AltScenario))]);
        b.Add(Opt(u.ScenariosNote));

        if (u.Rules.Count > 0)
            b.AddRange(["## Reguły biznesowe", Opt(u.RulesIntro),
                Table(["#", "Reguła", "Skutek niespełnienia"], u.Rules.Select(r => new[] { r.No.ToString(), T(r.Rule), T(r.OnFail) }))]);
        if (u.Effects.Count > 0)
            b.AddRange(["## Efekty", Bullets(u.Effects)]);
        if (u.GlobalRules.Count > 0)
            b.AddRange(["## Obowiązujące reguły globalne", Bullets(u.GlobalRules.Select(id => Link(id)))]);
        if (u.Flows.Count > 0)
            b.AddRange(["## Proces", Bullets(u.Flows.Select(id => Link(id)))]);

        var t = u.Technical;
        b.AddRange(["---", "## Szczegóły techniczne", KeyValues(t.Summary)]);
        if (t.CodeRules.Count > 0)
            b.AddRange(["**Reguły w kodzie**", Table(["#", "Warunek", "Wyjście", "Miejsce"],
                t.CodeRules.Select(r => new[] { r.No.ToString(), T(r.Condition), T(r.Exit), Locations(r.Locations) }))]);
        b.Add(Opt(t.DismissedNote));
        if (t.ResultHandling.Count > 0)
            b.AddRange(["**Obsługa wyniku wywołania**", Table(["Wywołanie", "Gdy `IsFailed`", "Miejsce"],
                t.ResultHandling.Select(r => new[] { T(r.Call), T(r.WhenFailed), Location(r.Location) }))]);
        b.AddRange(Section("Dane", t.Data.Count == 0 ? null
            : Table(["Operacja", "Tabela", "Kolumny"], t.Data.Select(d => new[] { T(d.Operation), T(d.Table), T(d.Columns) })), t.DataNote));
        b.AddRange(Section("Konfiguracja", t.Config.Count == 0 ? null
            : Table(["Klucz", "Znaczenie", "Domyślnie"], t.Config.Select(c => new[] { T(c.Key), T(c.Meaning), T(c.Default) })), t.ConfigNote));
        return b;
    }

    string AltScenario(AltScenario a)
    {
        var s = $"**{T(a.Title)}**";
        if (a.RuleRef is not null)
            s += $" ({a.RuleRef})";
        // Text starting with punctuation continues the lead, e.g. "**X** (reguła 2), np. ...: błąd".
        return s + (a.Text.Length > 0 && a.Text[0] is ',' or ';' ? "" : ": ") + T(a.Text);
    }

    /// <summary>"**Name**" + table, or "**Name:** note" when there are no rows.</summary>
    IEnumerable<string> Section(string name, string? table, string? note) =>
        table is not null ? [$"**{name}**", table]
        : note is not null ? [$"**{name}:** {T(note)}"]
        : [];

    List<string> EntityBody(EntityCard e)
    {
        var b = new List<string> { $"# {e.Name}", "## Opis", T(e.Description) };
        if (e.Statuses.Count > 0)
            b.AddRange(["## Statusy", Table(["Status", "Znaczenie", "Status końcowy"],
                e.Statuses.Select(s => new[] { $"*{s.Label}*", T(s.Meaning), s.IsFinal ? "tak" : "nie" }))]);
        if (e.StateDiagram.Count > 0)
            b.Add(Mermaid("stateDiagram-v2", e.StateDiagram.Select(s =>
                $"{StateId(s.From)} --> {StateId(s.To)}" + (s.Label is null ? "" : $": {s.Label}"))));
        if (e.Rules.Count > 0)
            b.AddRange(["## Reguły", Bullets(e.Rules)]);
        if (e.Changes.Count > 0)
            b.AddRange([$"## Kto zmienia {Accusative(e.Name)}", Table(["Zmiana", "Proces"],
                e.Changes.Select(c => new[] { T(c.Change), Link(c.PageId) }))]);

        var t = e.Technical;
        b.AddRange(["---", "## Szczegóły techniczne", KeyValues(t.Summary)]);
        if (t.Fields.Count > 0)
            b.AddRange(["**Pola**", Table(["Pole", "Kolumna", "Typ", "Opis"],
                t.Fields.Select(f => new[] { T(f.Field), T(f.Column), T(f.Type), T(f.Description) }))]);
        if (t.Transitions.Count > 0)
            b.AddRange(["**Przejścia statusów w kodzie**", Table(["Do", "Metoda", "Warunek", "Miejsce"],
                t.Transitions.Select(x => new[] { T(x.To), T(x.Method), T(x.Condition), Location(x.Location) }))]);
        return b;
    }

    List<string> FlowBody(FlowCard f)
    {
        var b = new List<string> { $"# {f.Title}", "## Opis", T(f.Description), "## Przebieg" };
        if (f.Nodes.Count > 0)
            b.Add(Mermaid("flowchart TD", FlowLines(f)));
        if (f.Steps.Count > 0)
            b.Add(Table(["Krok", "Proces", "Moduł", "Tryb"], f.Steps.Select(s => new[]
                { s.No.ToString(), s.PageId is null ? T(s.Label) : Link(s.PageId, fallback: s.Label), s.Module, T(s.Mode) })));
        if (f.SilentStops.Count > 0)
            b.AddRange(["## Gdzie proces może się zatrzymać bez widocznego błędu", Opt(f.SilentStopsIntro),
                Table(["Miejsce", "Powód", "Gdzie widać ślad"], f.SilentStops.Select(s => new[] { T(s.Place), T(s.Reason), T(s.Trace) }))]);
        if (f.Technical.Count > 0)
            b.AddRange(["---", "## Szczegóły techniczne", Table(["Krok", "Wejście", "Połączenie z kolejnym krokiem"],
                f.Technical.Select(r => new[] { r.Step.ToString(), T(r.Input), T(r.Connection) }))]);
        return b;
    }

    IEnumerable<string> FlowLines(FlowCard f)
    {
        var nodes = f.Nodes.ToDictionary(n => n.Id);
        var declared = new HashSet<string>();
        string N(string id)
        {
            if (!declared.Add(id) || !nodes.TryGetValue(id, out var n))
                return id;
            var label = "\"" + n.Label.Replace("\"", "#quot;") + "\"";
            return n.Shape switch
            {
                "decision" => $"{id}{{{label}}}",
                "end" => $"{id}([{label}])",
                _ => $"{id}[{label}]"
            };
        }
        foreach (var e in f.Edges)
        {
            var from = N(e.From);
            var arrow = (e.Label, e.Dashed) switch
            {
                (null, false) => "-->",
                (null, true) => "-.->",
                (var l, false) => $"-- \"{l}\" -->",
                (var l, true) => $"-. \"{l}\" .->"
            };
            yield return $"{from} {arrow} {N(e.To)}";
        }
        foreach (var n in f.Nodes.Where(n => !declared.Contains(n.Id)))
            yield return N(n.Id);
    }

    List<string> GlobalRuleBody(GlobalRuleCard r)
    {
        var b = new List<string> { $"# {r.Title}", "## Cel", T(r.Summary) };
        if (r.Behaviour.Count > 0)
            b.AddRange(["## Działanie", Bullets(r.Behaviour)]);
        if (r.AppliesTo.Count > 0)
            // ponytail: column header fixed to validation wording; contract has no per-rule header field.
            b.AddRange(["## Gdzie obowiązuje", Table(["Proces", "Reguły walidacji"],
                r.AppliesTo.Select(a => new[] { Link(a.PageId), T(a.Rules) }))]);
        b.AddRange(["---", "## Szczegóły techniczne", KeyValues(r.TechnicalSummary)]);
        if (r.Logic.Count > 0)
            b.AddRange(["**Logika w kodzie**", Table(["Warunek", "Wyjście", "Miejsce"],
                r.Logic.Select(l => new[] { T(l.Condition), T(l.Exit), Location(l.Location) }))]);
        return b;
    }

    List<string> ModuleBody(ModuleCard m)
    {
        var b = new List<string> { $"# {m.Title}", "## Opis", T(m.Summary) };
        void List(string heading, List<string> ids)
        {
            if (ids.Count > 0)
                b.AddRange([heading, Bullets(ids.Select(id => Link(id)))]);
        }
        List("## Procesy w module", m.UseCases);
        List("## Encje", m.Entities);
        List("## Procesy obejmujące moduł", m.Flows);
        List("## Zależy od modułów", m.DependsOn);
        return b;
    }

    List<string> SystemBody(SystemCard s)
    {
        var b = new List<string> { $"# {s.Title}", "## Opis", T(s.Summary) };
        if (s.Modules.Count > 0)
            b.AddRange(["## Moduły", Table(["Moduł", "Opis"], s.Modules.Select(m => new[] { Link(m.PageId), T(m.Summary) }))]);
        if (cards.Flows.Count > 0)
            b.AddRange(["## Procesy", Bullets(cards.Flows.Select(f => Link(f.PageId)))]);
        if (cards.GlobalRules.Count > 0)
            b.AddRange(["## Reguły globalne", Bullets(cards.GlobalRules.Select(r => Link(r.PageId)))]);
        if (cards.Glossary is not null)
            b.AddRange(["## Słownik", Bullets([Link(cards.Glossary.PageId)])]);
        return b;
    }

    List<string> GlossaryBody(GlossaryCard g)
    {
        var b = new List<string> { "# Słownik pojęć" };
        b.Add(g.Entries.Count == 0 ? "Brak pojęć."
            : Table(["Pojęcie", "Nazwa w kodzie", "Opis"], g.Entries.Select(e => new[]
                { T(e.Term), e.CodeName.Length > 0 ? $"`{e.CodeName}`" : "", T(e.Description) })));
        return b;
    }

    // ── Markdown helpers ───────────────────────────────────────────────────

    string Opt(string? text) => text is null ? "" : T(text);

    string Bullets(IEnumerable<string> items) => string.Join("\n", items.Select(i => "- " + T(i)));

    string KeyValues(List<KeyValue> rows) =>
        rows.Count == 0 ? "" : Table(["", ""], rows.Select(r => new[] { T(r.Key), T(r.Value) }));

    static string Table(string[] header, IEnumerable<string[]> rows)
    {
        static string Row(IEnumerable<string> cells) => "| " + string.Join(" | ", cells.Select(Cell)) + " |";
        var sb = new StringBuilder(header.All(h => h == "") ? "|" + string.Concat(header.Select(_ => " |")) : Row(header));
        sb.Append('\n').Append('|').Append(string.Concat(header.Select(_ => "---|")));
        foreach (var r in rows)
            sb.Append('\n').Append(Row(r));
        return sb.ToString();
    }

    static string Cell(string s) => UnescapedPipe().Replace(s.Replace("\r", "").Replace("\n", " "), "\\|");

    static string Mermaid(string kind, IEnumerable<string> lines) =>
        $"```mermaid\n{kind}\n" + string.Concat(lines.Select(l => $"    {l}\n")) + "```";

    /// <summary>Mermaid state id: ASCII-folded label without spaces ("Oczekująca" → "Oczekujaca").</summary>
    static string StateId(string label)
    {
        if (label == "[*]")
            return label;
        var sb = new StringBuilder();
        foreach (var c in label.Replace('ł', 'l').Replace('Ł', 'L').Normalize(NormalizationForm.FormD))
            if (char.IsAsciiLetterOrDigit(c) || c == '_')
                sb.Append(c);
        return sb.ToString();
    }

    /// <summary>Polish accusative for "Kto zmienia …": feminine "-a" → "-ę" ("Wypłata" → "wypłatę"), else unchanged.</summary>
    // ponytail: first-word heuristic, wrong for animate masculine / adjective phrases; add an EntityCard field if it matters.
    static string Accusative(string name)
    {
        var words = name.Split(' ');
        words[0] = words[0].ToLower(CultureInfo.GetCultureInfo("pl-PL"));
        if (words[0].EndsWith('a'))
            words[0] = words[0][..^1] + "ę";
        return string.Join(' ', words);
    }

    // ── Tokens and links ───────────────────────────────────────────────────

    /// <summary>Resolves [[page:..]] / [[code:..]] tokens.</summary>
    string T(string text) => Token().Replace(text, m => m.Groups["page"].Success
        ? Link(m.Groups["page"].Value, m.Groups["label"].Success ? m.Groups["label"].Value : null)
        : Code(m.Groups["path"].Value, m.Groups["line"].Value, m.Groups["label"].Success ? m.Groups["label"].Value : null));

    string Link(string pageId, string? label = null, string? fallback = null)
    {
        if (targets.TryGetValue(pageId, out var t))
            return $"[{label ?? t.Label}]({Relative(t.Path)})";
        warnings.Add($"{current}: unknown page id '{pageId}', rendered as plain text.");
        return label ?? fallback ?? pageId;
    }

    string Code(string path, string line, string? label)
    {
        label ??= $"{Path.GetFileName(path)}:{line}";
        if (repoUrl.Length == 0)
            return label;
        var url = repoUrl.Replace("{commit}", cards.Commit).Replace("{path}", path.Replace(" ", "%20")).Replace("{line}", line);
        return $"[{label}]({url})";
    }

    /// <summary>"path:line" → code link "[File.cs:line](url)"; anything else is treated as Markdown.</summary>
    string Location(string location)
    {
        var m = PathLine().Match(location);
        return m.Success ? Code(m.Groups[1].Value, m.Groups[2].Value, null) : T(location);
    }

    string Locations(List<string> locations) => string.Join(", ", locations.Select(Location));

    string Relative(string target)
    {
        var from = current.Split('/')[..^1];
        var to = target.Split('/');
        var common = 0;
        while (common < from.Length && common < to.Length - 1 && from[common] == to[common])
            common++;
        return string.Concat(Enumerable.Repeat("../", from.Length - common)) + string.Join('/', to[common..]);
    }

    [GeneratedRegex(@"\[\[(?:page:(?<page>[^\]|]+)|code:(?<path>[^\]|]+):(?<line>\d+))(?:\|(?<label>[^\]]*))?\]\]")]
    private static partial Regex Token();

    [GeneratedRegex(@"^([^\s\[\]()]+):(\d+)$")]
    private static partial Regex PathLine();

    [GeneratedRegex(@"(?<!\\)\|")]
    private static partial Regex UnescapedPipe();
}
