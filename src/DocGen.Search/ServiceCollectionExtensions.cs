using DocGen.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocGen.Search;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDocGenSearch(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<QdrantOptions>(config.GetSection("Qdrant"));
        services.Configure<EmbeddingsOptions>(config.GetSection("Embeddings"));
        services.Configure<RerankerOptions>(config.GetSection("Reranker"));
        services.Configure<SearchAnswerOptions>(config.GetSection("SearchAnswer"));

        services.AddHttpClient("Qdrant", (sp, client) =>
        {
            var o = sp.GetRequiredService<IOptions<QdrantOptions>>().Value;
            client.BaseAddress = new Uri(o.Url.TrimEnd('/') + "/");
            client.Timeout = TimeSpan.FromSeconds(o.TimeoutSeconds);
            if (!string.IsNullOrEmpty(o.ApiKey))
                client.DefaultRequestHeaders.Add("api-key", o.ApiKey);
        });
        services.AddHttpClient("Embeddings", (sp, client) =>
            HttpClientHeaders.Apply(client, sp.GetRequiredService<IOptions<EmbeddingsOptions>>().Value));
        services.AddHttpClient("Reranker", (sp, client) =>
            HttpClientHeaders.Apply(client, sp.GetRequiredService<IOptions<RerankerOptions>>().Value));
        services.AddHttpClient("SearchAnswer", (sp, client) =>
            HttpClientHeaders.Apply(client, sp.GetRequiredService<IOptions<SearchAnswerOptions>>().Value));

        services.AddLogging();
        services.AddSingleton<Qdrant>();
        services.AddSingleton<Embedder>();
        services.AddSingleton<Reranker>();
        services.AddSingleton<Answerer>();
        services.AddSingleton<IDocsSearchIndexer, DocsSearchIndexer>();
        services.AddSingleton<IDocsSearch, DocsSearch>();
        return services;
    }
}
