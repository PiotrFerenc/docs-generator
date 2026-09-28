using DocGen.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocGen.Render;

/// <summary>Section "Render".</summary>
public sealed class RenderOptions
{
    /// <summary>Source link template with {commit}, {path}, {line}. Empty → code references render as plain text.</summary>
    public string RepoUrlTemplate { get; set; } = "";
}

public static class ServiceCollectionExtensions
{
    /// <summary>Requires DocGenPaths and IOptions&lt;GeneratorOptions&gt; (registered by DocGen.Cli).</summary>
    public static IServiceCollection AddDocGenRender(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<RenderOptions>(config.GetSection("Render"));
        services.AddSingleton<IPageRenderer, MarkdownRenderer>();
        return services;
    }
}
