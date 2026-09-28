using System.Runtime.CompilerServices;
using DocGen.Contracts;
using Microsoft.Build.Locator;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocGen.Indexer.Tests;

public class SampleIndexTests
{
    // Must run before any MSBuild/Roslyn workspace type is loaded.
    [ModuleInitializer]
    internal static void RegisterMsBuild()
    {
        if (!MSBuildLocator.IsRegistered)
            MSBuildLocator.RegisterDefaults();
    }

    internal static readonly string RepoRoot = FindRepoRoot();

    static readonly Lazy<Task<CodeIndex>> Index = new(() =>
        new RoslynCodeIndexer(
                Options.Create(new IndexerOptions { SolutionPath = "sample/Sample.sln", Commit = "test" }),
                new DocGenPaths(RepoRoot),
                NullLogger<RoslynCodeIndexer>.Instance)
            .IndexAsync(CancellationToken.None));

    [Fact]
    public async Task Index_passes_expected_checks()
    {
        var index = await Index.Value;
        Assert.Equal(0, IndexCheck.Run(index, Path.Combine(RepoRoot, "tests/expected-index.json")));
    }

    [Fact]
    public async Task RequestPayoutHandler_has_structured_guards_dependencies_and_chunks()
    {
        var handler = (await Index.Value).Entries.Single(e => e.Id == "T:Payments.Application.RequestPayoutHandler");

        Assert.Equal(("Payments", "Application"), (handler.Module, handler.Layer));
        Assert.Equal(["ICustomerAccountRepository", "IPayoutRepository", "IFeatureManager", "IOptions<PayoutOptions>", "TimeProvider"],
            handler.Dependencies);
        Assert.Contains(handler.Guards, g => g.Structured == new GuardExpr("CustomerAccount.KycStatus", "!=", "KycStatus.Verified"));
        Assert.Contains(handler.Guards, g => g.Structured == new GuardExpr("CustomerAccount.Iban", "is_empty", null));
        Assert.Equal(handler.Guards.Select((_, i) => $"G{i + 1}"), handler.Guards.Select(g => g.Id));
        Assert.Equal("rule", handler.Guards[0].Kind); // validator rules first
        Assert.Contains(handler.Chunks, c => c.Symbol == "RequestPayoutHandler.Handle" && c.Text.Contains("Payout.Create"));
        Assert.Contains(handler.Chunks, c => c.Symbol == "Payout.Create");
        Assert.Matches("^[0-9a-f]{64}$", handler.Hash);
    }

    [Fact]
    public async Task Payout_entity_has_table_columns_enum_values_and_transitions()
    {
        var payout = (await Index.Value).Entities.Single(e => e.Id == "T:Payments.Domain.Payout");

        Assert.Equal("payouts", payout.Table);
        var status = payout.Fields.Single(f => f.Name == "Status");
        Assert.Equal(("PayoutStatus", "status"), (status.Type, status.Column));
        Assert.Equal(["Pending", "Completed", "Failed"], status.EnumValues!);
        Assert.Equal("string?", payout.Fields.Single(f => f.Name == "PspReference").Type);
        Assert.DoesNotContain(payout.Fields, f => f.Name == "DomainEvents"); // inherited, not mapped
        Assert.Equal(
            [
                new StatusTransition("Status", null, "Pending", "Payout.Create", "src/Payments.Domain/Payments.cs:57", "amount > maxAmount"),
                new StatusTransition("Status", "Pending", "Completed", "Payout.MarkCompleted", "src/Payments.Domain/Payments.cs:69", "Status != PayoutStatus.Pending"),
                new StatusTransition("Status", "Pending", "Failed", "Payout.MarkFailed", "src/Payments.Domain/Payments.cs:79", "Status != PayoutStatus.Pending")
            ],
            payout.Transitions);
        Assert.Equal(["T:Payments.Domain.PayoutRequestedEvent"], payout.RaisedEvents);
    }

    [Fact]
    public async Task Config_keys_and_entity_filtering()
    {
        var index = await Index.Value;

        Assert.Contains(new ConfigKey("Payouts.Enabled", "feature_flag", null, "src/Payments.Application/RequestPayout.cs:32"), index.Config);
        Assert.Contains(new ConfigKey("Payouts:MaxAmount", "option", "20_000m", "src/Payments.Application/Abstractions.cs:22"), index.Config);
        // Events, errors, enums and interfaces in *.Domain are not entities.
        Assert.Equal(["T:Orders.Domain.Order", "T:Payments.Domain.CustomerAccount", "T:Payments.Domain.Payout"], index.Entities.Select(e => e.Id));
        Assert.Null(index.Entities.Single(e => e.Name == "Order").Table); // no DbSet / ToTable in the sample
    }

    static string FindRepoRoot()
    {
        for (var dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "DocGen.sln")))
                return dir;
        throw new InvalidOperationException("DocGen.sln not found above " + AppContext.BaseDirectory);
    }
}
