using DocGen.Contracts;
using Microsoft.Extensions.Options;
using static DocGen.Cards.IndexFacts;

namespace DocGen.Cards;

/// <summary>
/// CodeIndex → CardSet, bottom-up: use cases + entities → flows + global rules → modules + glossary → system.
/// Deterministic facts come from the index; each call site's LLM only writes the Polish prose.
/// </summary>
sealed class CardGenerator(
    LlmRunner llm,
    DocGenPaths paths,
    IOptions<GeneratorOptions> generator,
    IOptions<HandlerCardOptions> handlerCard,
    IOptions<EntityCardOptions> entityCard,
    IOptions<FlowCardOptions> flowCard,
    IOptions<GlobalRuleCardOptions> globalRuleCard,
    IOptions<ModuleSummaryOptions> moduleSummary,
    IOptions<SystemSummaryOptions> systemSummary,
    IOptions<GlossaryOptions> glossary) : ICardGenerator
{
    public async Task<CardSet> GenerateAsync(CodeIndex index, CancellationToken ct)
    {
        var f = new IndexFacts(index);
        var commit = index.Commit;
        var entityInfos = EntityCards.Select(f);
        var graphs = FlowCards.Discover(f);
        var modules = f.UseCases.Select(u => u.Module).Concat(entityInfos.Select(e => e.Module)).Distinct().Order(StringComparer.Ordinal).ToList();

        var pages = new Dictionary<string, PageRef>();
        f.UseCases.ForEach(u => pages[u.Id] = new(u.Id, u.Kind == "event_handler" ? "use_case (event handler)" : "use_case", Short(u.Id)));
        entityInfos.ForEach(e => pages[e.Id] = new(e.Id, "entity", e.Name));
        graphs.ForEach(g => pages[g.PageId] = new(g.PageId, "flow", g.Root.Id));
        f.Behaviors.ForEach(b => pages[b.Id] = new(b.Id, "global_rule", Short(b.Id)));
        modules.ForEach(m => pages[$"module:{m}"] = new($"module:{m}", "module", m));
        pages["system"] = new("system", "system", "Przegląd systemu");
        pages["glossary"] = new("glossary", "glossary", "Słownik pojęć");
        var flowsOf = graphs.SelectMany(g => g.Steps.Where(s => s.Entry is not null).Select(s => (s.Entry!.Id, g.PageId)))
            .GroupBy(x => x.Item1).ToDictionary(x => x.Key, x => x.Select(y => y.PageId).Distinct().ToList());
        var c = new Ctx(f, pages, flowsOf);

        var useCasesTask = Task.WhenAll(f.UseCases.Select(u =>
            llm.RunAsync(Call("HandlerCard", handlerCard.Value, handlerCard.Value.Model, handlerCard.Value.SystemPrompt, handlerCard.Value.PromptVersion, handlerCard.Value.MaxRetries), commit, UseCaseCards.Prepare(u, c), (x, m) => x with { Meta = m }, ct)));
        var entitiesTask = Task.WhenAll(entityInfos.Select(e =>
            llm.RunAsync(Call("EntityCard", entityCard.Value, entityCard.Value.Model, entityCard.Value.SystemPrompt, entityCard.Value.PromptVersion, entityCard.Value.MaxRetries), commit, EntityCards.Prepare(e, c), (x, m) => x with { Meta = m }, ct)));
        var useCases = (await useCasesTask).ToList();
        var entities = (await entitiesTask).ToList();
        var byId = useCases.ToDictionary(u => u.PageId);

        var flowsTask = Task.WhenAll(graphs.Select(g =>
            llm.RunAsync(Call("FlowCard", flowCard.Value, flowCard.Value.Model, flowCard.Value.SystemPrompt, flowCard.Value.PromptVersion, flowCard.Value.MaxRetries), commit, FlowCards.Prepare(g, c, byId), (x, m) => x with { Meta = m }, ct)));
        var rulesTask = Task.WhenAll(f.Behaviors.Select(b =>
            llm.RunAsync(Call("GlobalRuleCard", globalRuleCard.Value, globalRuleCard.Value.Model, globalRuleCard.Value.SystemPrompt, globalRuleCard.Value.PromptVersion, globalRuleCard.Value.MaxRetries), commit, GlobalRuleCards.Prepare(b, c, useCases), (x, m) => x with { Meta = m }, ct)));
        var flows = (await flowsTask).ToList();
        var globalRules = (await rulesTask).ToList();

        var moduleCards = (await Task.WhenAll(modules.Select(m =>
            llm.RunAsync(Call("ModuleSummary", moduleSummary.Value, moduleSummary.Value.Model, moduleSummary.Value.SystemPrompt, moduleSummary.Value.PromptVersion, moduleSummary.Value.MaxRetries), commit, ModuleCards.Prepare(m, c, useCases, entities, flows), (x, meta) => x with { Meta = meta }, ct)))).ToList();
        var glossaryCard = await llm.RunAsync(Call("Glossary", glossary.Value, glossary.Value.Model, glossary.Value.SystemPrompt, glossary.Value.PromptVersion, glossary.Value.MaxRetries), commit,
            GlossaryCards.Prepare(c, entities, useCases, ReadManualGlossary()), (x, m) => x with { Meta = m }, ct);
        var system = await llm.RunAsync(Call("SystemSummary", systemSummary.Value, systemSummary.Value.Model, systemSummary.Value.SystemPrompt, systemSummary.Value.PromptVersion, systemSummary.Value.MaxRetries), commit, SystemCards.Prepare(c, moduleCards), (x, m) => x with { Meta = m }, ct);

        return new CardSet(commit, useCases, entities, flows, globalRules, moduleCards, system, glossaryCard);
    }

    List<GlossaryEntry> ReadManualGlossary()
    {
        var file = paths.Resolve(generator.Value.GlossaryFile);
        return File.Exists(file) ? DocGenJson.Read<List<GlossaryEntry>>(file).Select(e => e with { Manual = true }).ToList() : [];
    }

    static LlmCall Call(string callSite, HttpClientOptions http, string model, string systemPrompt, string promptVersion, int maxRetries) =>
        new(callSite, http.IsOffline, model, systemPrompt, promptVersion, maxRetries);
}
