using DocGen.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocGen.Cards;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers ICardGenerator and its LLM call sites. Expects DocGenPaths and GeneratorOptions from the CLI.</summary>
    public static IServiceCollection AddDocGenCards(this IServiceCollection services, IConfiguration config)
    {
        // One section, one Options class and one named HttpClient per call site.
        services.Configure<HandlerCardOptions>(config.GetSection("HandlerCard"));
        services.AddHttpClient("HandlerCard", (sp, c) => HttpClientHeaders.Apply(c, sp.GetRequiredService<IOptions<HandlerCardOptions>>().Value));
        services.Configure<EntityCardOptions>(config.GetSection("EntityCard"));
        services.AddHttpClient("EntityCard", (sp, c) => HttpClientHeaders.Apply(c, sp.GetRequiredService<IOptions<EntityCardOptions>>().Value));
        services.Configure<FlowCardOptions>(config.GetSection("FlowCard"));
        services.AddHttpClient("FlowCard", (sp, c) => HttpClientHeaders.Apply(c, sp.GetRequiredService<IOptions<FlowCardOptions>>().Value));
        services.Configure<GlobalRuleCardOptions>(config.GetSection("GlobalRuleCard"));
        services.AddHttpClient("GlobalRuleCard", (sp, c) => HttpClientHeaders.Apply(c, sp.GetRequiredService<IOptions<GlobalRuleCardOptions>>().Value));
        services.Configure<ModuleSummaryOptions>(config.GetSection("ModuleSummary"));
        services.AddHttpClient("ModuleSummary", (sp, c) => HttpClientHeaders.Apply(c, sp.GetRequiredService<IOptions<ModuleSummaryOptions>>().Value));
        services.Configure<SystemSummaryOptions>(config.GetSection("SystemSummary"));
        services.AddHttpClient("SystemSummary", (sp, c) => HttpClientHeaders.Apply(c, sp.GetRequiredService<IOptions<SystemSummaryOptions>>().Value));
        services.Configure<GlossaryOptions>(config.GetSection("Glossary"));
        services.AddHttpClient("Glossary", (sp, c) => HttpClientHeaders.Apply(c, sp.GetRequiredService<IOptions<GlossaryOptions>>().Value));

        services.AddSingleton<LlmRunner>();
        services.AddSingleton<ICardGenerator, CardGenerator>();
        return services;
    }
}
