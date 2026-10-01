using DocGen.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocGen.Agent;

public static class ServiceCollectionExtensions
{
    /// <summary>Registers IDocAgent. IDocsSearch is used when registered (AddDocGenSearch), otherwise keyword search.</summary>
    public static IServiceCollection AddDocGenAgent(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<AgentOptions>(config.GetSection("Agent"));
        services.AddHttpClient("Agent", (sp, client) =>
            HttpClientHeaders.Apply(client, sp.GetRequiredService<IOptions<AgentOptions>>().Value));

        services.Configure<AgentReportOptions>(config.GetSection("AgentReport"));
        services.PostConfigure<AgentReportOptions>(o => o.RepoUrlTemplate ??= config["Render:RepoUrlTemplate"]);

        services.AddSingleton<AgentLoop>();
        services.AddSingleton<IDocAgent>(sp => new DocAgent(
            sp.GetRequiredService<AgentLoop>(),
            sp.GetRequiredService<DocGenPaths>(),
            sp.GetRequiredService<IOptions<GeneratorOptions>>(),
            sp.GetRequiredService<IOptions<AgentReportOptions>>(),
            sp.GetService<IDocsSearch>()));
        return services;
    }
}
