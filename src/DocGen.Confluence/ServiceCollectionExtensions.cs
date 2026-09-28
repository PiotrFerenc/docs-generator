using DocGen.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DocGen.Confluence;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDocGenConfluence(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<ConfluenceOptions>(config.GetSection("Confluence"));
        services.AddHttpClient(ConfluencePublisher.ClientName,
            (sp, client) => HttpClientHeaders.Apply(client, sp.GetRequiredService<IOptions<ConfluenceOptions>>().Value));
        services.AddSingleton<IConfluencePublisher, ConfluencePublisher>();
        return services;
    }
}
