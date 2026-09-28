using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DocGen.Cards;
using DocGen.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocGen.Cards.Tests;

public sealed class CardGeneratorTests : IDisposable
{
    const string RequestPayout = "T:Payments.Application.RequestPayoutHandler";
    readonly string workDir = Path.Combine(Path.GetTempPath(), "docgen-cards-" + Guid.NewGuid().ToString("N"));
    static readonly CodeIndex Index = DocGenJson.Read<CodeIndex>(Path.Combine(RepoRoot(), "tests/fixtures/index.sample.json"));
    static readonly string Canned = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "RequestPayout.handler.json"));

    public void Dispose()
    {
        if (Directory.Exists(workDir))
            Directory.Delete(workDir, true);
    }

    [Fact]
    public async Task Offline_run_produces_complete_valid_card_set()
    {
        var cards = await Generator(null).GenerateAsync(Index, CancellationToken.None);

        Assert.Equal(4, cards.UseCases.Count);
        var payout = Assert.Single(cards.Entities, e => e.PageId == "T:Payments.Domain.Payout");
        Assert.Equal(3, payout.Technical.Transitions.Count);
        Assert.Equal(["Pending", "Completed", "Failed"], payout.Statuses.Select(s => s.Value));
        Assert.Equal([false, true, true], payout.Statuses.Select(s => s.IsFinal));

        var flow = Assert.Single(cards.Flows);
        Assert.True(flow.Steps.Count >= 4, $"steps: {flow.Steps.Count}");
        var step = flow.Steps.Single(s => s.PageId == RequestPayout).No;
        var requestPayout = cards.UseCases.Single(u => u.PageId == RequestPayout);
        var flagRule = requestPayout.Rules.Single(r => r.GuardIds.Contains("G4")).No; // Payouts.Enabled feature flag
        Assert.Contains(flow.SilentStops, s => s.Place == $"krok {step}, reguła {flagRule}");
        Assert.Contains(flow.SilentStops, s => s.Place.StartsWith($"krok {step}, reguły") && s.Trace == "ostrzeżenie w logach");
        Assert.Contains(flow.Steps, s => s.PageId == "T:Payments.Application.ConfirmPayoutHandler"); // entity continuation

        Assert.Single(cards.GlobalRules);
        Assert.Equal(["Orders", "Payments"], cards.Modules.Select(m => m.Module));
        Assert.Contains("module:Orders", cards.Modules.Single(m => m.Module == "Payments").DependsOn);
        Assert.NotNull(cards.System);
        Assert.NotNull(cards.Glossary);

        foreach (var meta in AllMeta(cards))
        {
            Assert.Equal("offline", meta.Status);
            Assert.Empty(meta.Warnings);
        }
    }

    [Fact]
    public async Task Llm_card_matches_golden_rules()
    {
        var stub = new StubLlm(_ => Canned);
        var cards = await Generator(stub).GenerateAsync(Index, CancellationToken.None);

        var card = cards.UseCases.Single(u => u.PageId == RequestPayout);
        Assert.Equal("ok", card.Meta.Status);
        Assert.Equal("Zlecenie wypłaty dla klienta", card.Title);
        Assert.Equal(
            ["G3", "G4", "G5,G6", "G8,G7", "G9", "G10", "G11"],
            card.Rules.Select(r => string.Join(",", r.GuardIds)));
        Assert.Equal(Enumerable.Range(1, 7), card.Rules.Select(r => r.No));
        Assert.Equal(2, card.Technical.CodeRules[2].Locations.Count);
        Assert.StartsWith("Pominięte w części biznesowej (warunki techniczne): `RuleFor(x => x.OrderId).NotEmpty()`", card.Technical.DismissedNote);
        Assert.Equal("`20000`", card.Technical.Config.Single(c => c.Key == "`Payouts:MaxAmount`").Default);
        Assert.Equal("feature flag włączający wypłaty", card.Technical.Config.Single(c => c.Key == "`Payouts.Enabled`").Meaning);
        Assert.Equal(1, stub.CallsFor(RequestPayout));
    }

    [Fact]
    public async Task Missing_guard_retries_once_then_needs_review()
    {
        var broken = JsonNode.Parse(Canned)!;
        broken["rules"]!.AsArray().RemoveAt(6); // drops rule 7 → guard G11 uncovered
        var stub = new StubLlm(_ => broken.ToJsonString());

        var cards = await Generator(stub).GenerateAsync(Index, CancellationToken.None);

        var card = cards.UseCases.Single(u => u.PageId == RequestPayout);
        Assert.Equal(2, stub.CallsFor(RequestPayout));
        Assert.Equal("needs_review", card.Meta.Status);
        Assert.Contains(card.Meta.Warnings, w => w.Contains("G11"));
    }

    [Fact]
    public async Task Cache_hit_makes_no_http_calls()
    {
        await Generator(new StubLlm(_ => Canned)).GenerateAsync(Index, CancellationToken.None);

        var second = new StubLlm(_ => Canned);
        var cards = await Generator(second).GenerateAsync(Index, CancellationToken.None);

        Assert.Empty(second.Requests);
        Assert.Equal("ok", cards.UseCases.Single(u => u.PageId == RequestPayout).Meta.Status);
    }

    ICardGenerator Generator(StubLlm? handlerCardStub)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["HandlerCard:BaseAddress"] = handlerCardStub is null ? "" : "http://llm.test/v1",
            ["HandlerCard:Model"] = "test-model"
        }).Build();
        var services = new ServiceCollection()
            .AddLogging()
            .AddSingleton(new DocGenPaths(workDir))
            .Configure<GeneratorOptions>(o => o.WorkDir = ".");
        services.AddDocGenCards(config);
        if (handlerCardStub is not null)
            services.AddHttpClient("HandlerCard").ConfigurePrimaryHttpMessageHandler(() => handlerCardStub);
        return services.BuildServiceProvider().GetRequiredService<ICardGenerator>();
    }

    static IEnumerable<CardMeta> AllMeta(CardSet c) =>
        c.UseCases.Select(x => x.Meta).Concat(c.Entities.Select(x => x.Meta)).Concat(c.Flows.Select(x => x.Meta))
            .Concat(c.GlobalRules.Select(x => x.Meta)).Concat(c.Modules.Select(x => x.Meta)).Append(c.System!.Meta).Append(c.Glossary!.Meta);

    static string RepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(dir, "DocGen.sln")))
            dir = Path.GetDirectoryName(dir) ?? throw new InvalidOperationException("DocGen.sln not found");
        return dir;
    }
}

/// <summary>OpenAI-compatible chat/completions stub: returns the given content as choices[0].message.content.</summary>
sealed class StubLlm(Func<string, string> content) : HttpMessageHandler
{
    public List<string> Requests { get; } = [];

    public int CallsFor(string entryId)
    {
        lock (Requests)
            return Requests.Count(r => r.Contains($"{entryId} (request_handler"));
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        lock (Requests)
            Requests.Add(body);
        var response = new JsonObject
        {
            ["choices"] = new JsonArray(new JsonObject { ["message"] = new JsonObject { ["role"] = "assistant", ["content"] = content(body) } })
        };
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response.ToJsonString(), Encoding.UTF8, "application/json") };
    }
}
