using DocGen.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocGen.Indexer;

public static class ServiceCollectionExtensions
{
    // Requires DocGenPaths (registered by DocGen.Cli) and MSBuildLocator registered before any Roslyn workspace type loads.
    public static IServiceCollection AddDocGenIndexer(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<IndexerOptions>(config.GetSection("Indexer"));
        services.AddSingleton<ICodeIndexer, RoslynCodeIndexer>();
        return services;
    }
}

/// <summary>Section "Indexer". Framework names default to MediatR, FluentValidation, FluentResults, FeatureManagement and ASP.NET minimal APIs.</summary>
public class IndexerOptions
{
    public string SolutionPath { get; set; } = "";
    /// <summary>Empty: `git rev-parse --short HEAD` in the solution directory, else "unknown".</summary>
    public string Commit { get; set; } = "";
    public int MaxDepth { get; set; } = 4;
    public string[] EntityNamespaceSuffixes { get; set; } = [".Domain"];
    /// <summary>snake_case | as_is — convention for tables/columns not configured via EF Fluent API.</summary>
    public string ColumnNaming { get; set; } = "snake_case";

    public string MediatRNamespace { get; set; } = "MediatR";
    public string RequestHandlerInterface { get; set; } = "IRequestHandler";
    public string NotificationHandlerInterface { get; set; } = "INotificationHandler";
    public string PipelineBehaviorInterface { get; set; } = "IPipelineBehavior";
    public string RequestInterface { get; set; } = "IBaseRequest";
    public string NotificationInterface { get; set; } = "INotification";
    public string SendMethod { get; set; } = "Send";

    public string ValidatorNamespace { get; set; } = "FluentValidation";
    public string ValidatorBaseType { get; set; } = "AbstractValidator";
    public string RuleMethod { get; set; } = "RuleFor";

    public string ResultNamespace { get; set; } = "FluentResults";
    public string ResultType { get; set; } = "Result";
    public string OkMethod { get; set; } = "Ok";
    public string FailMethod { get; set; } = "Fail";
    public string ErrorBaseType { get; set; } = "Error";

    public string FeatureManagementNamespace { get; set; } = "Microsoft.FeatureManagement";
    public string FeatureFlagMethod { get; set; } = "IsEnabledAsync";

    public string EndpointNamespace { get; set; } = "Microsoft.AspNetCore";
    public string EndpointMethodPrefix { get; set; } = "Map";

    /// <summary>Callee text prefixes of throwing guard helpers.</summary>
    public string[] GuardCallPrefixes { get; set; } =
        ["Guard.Against", "ArgumentNullException.ThrowIf", "ArgumentException.ThrowIf", "ArgumentOutOfRangeException.ThrowIf", "ObjectDisposedException.ThrowIf"];
}
