using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocGen.Contracts;
using DocGen.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DocGen.Search.Tests;

sealed class FakeHandler(string responseJson) : HttpMessageHandler
{
    public List<(string Url, JsonElement Body)> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = request.Content is null ? default : JsonDocument.Parse(await request.Content.ReadAsStringAsync(ct)).RootElement.Clone();
        Requests.Add((request.RequestUri!.ToString(), body));
        return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responseJson, Encoding.UTF8, "application/json") };
    }
}

static class TestHost
{
    public static ServiceProvider Build(Dictionary<string, string?> settings, string? fakeClient = null, FakeHandler? fake = null)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection().AddDocGenSearch(config);
        if (fakeClient is not null)
            services.AddHttpClient(fakeClient).ConfigurePrimaryHttpMessageHandler(() => fake!);
        return services.BuildServiceProvider();
    }

    public static string GoldenDir
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, "tests", "golden")))
                    return Path.Combine(dir.FullName, "tests", "golden");
            throw new DirectoryNotFoundException("tests/golden");
        }
    }
}

public class ChunkerTests
{
    [Fact]
    public void Splits_golden_page_into_h2_sections_without_banner_and_notes_placeholder()
    {
        var md = File.ReadAllText(Path.Combine(TestHost.GoldenDir, "modules/Payments/use-cases/RequestPayout.md"));
        var chunks = Chunker.Split(md);

        Assert.Equal(["Cel", "Kiedy się uruchamia", "Scenariusze", "Reguły biznesowe", "Efekty",
            "Obowiązujące reguły globalne", "Proces", "Szczegóły techniczne"], chunks.Select(c => c.Section));
        Assert.DoesNotContain(chunks, c => c.Text.Contains("generowana automatycznie") || c.Text.Contains("treść dołączana"));
        Assert.DoesNotContain(chunks, c => c.Text.Contains("page_id") || c.Text.EndsWith("---"));
    }

    [Fact]
    public void Intro_is_own_chunk_and_duplicate_sections_get_suffix()
    {
        var chunks = Chunker.Split("---\ntitle: x\n---\n\n# Tytuł\n\n> Strona generowana\n\nWstęp.\n\n## A\n\nraz\n\n## A\n\ndwa\n\n## Pusta\n\n");
        Assert.Equal([new Chunk(Chunker.IntroSection, "Wstęp."), new Chunk("A", "raz"), new Chunk("A (2)", "dwa")], chunks);
    }

    [Fact]
    public void Sparse_vector_and_point_id_are_deterministic()
    {
        var a = Chunker.Sparse("Wypłata klienta, wypłaty klienta i wypłata.");
        var b = Chunker.Sparse("Wypłata klienta, wypłaty klienta i wypłata.");
        Assert.Equal(a.Indices, b.Indices);
        Assert.Equal(a.Values, b.Values);
        Assert.Equal(a.Indices.Order(), a.Indices);
        Assert.Equal(3f, a.Values[Array.IndexOf(a.Indices, Chunker.Hash("wypłat"))]);

        var id = Chunker.PointId("T:X", "Cel");
        Assert.Equal(id, Chunker.PointId("T:X", "Cel"));
        Assert.NotEqual(id, Chunker.PointId("T:X", "Efekty"));
        Assert.Equal('5', id[14]);

        var v = Chunker.HashedEmbedding("wypłata klienta", 64);
        Assert.Equal(1.0, Math.Sqrt(v.Sum(x => x * x)), 5);
    }
}

public class HttpCallSiteTests
{
    [Fact]
    public void Reranker_parses_both_response_shapes()
    {
        var jina = JsonDocument.Parse("""{"results":[{"index":2,"relevance_score":0.9},{"index":0,"relevance_score":0.1}]}""").RootElement;
        var tei = JsonDocument.Parse("""[{"index":1,"score":0.7},{"index":0,"score":0.2}]""").RootElement;
        Assert.Equal([(2, 0.9), (0, 0.1)], Reranker.Parse(jina));
        Assert.Equal([(1, 0.7), (0, 0.2)], Reranker.Parse(tei));
    }

    [Fact]
    public async Task Reranker_sends_request_and_orders_by_score()
    {
        var fake = new FakeHandler("""[{"index":0,"score":0.1},{"index":1,"score":0.8}]""");
        using var sp = TestHost.Build(new() { ["Reranker:BaseAddress"] = "http://rr/v1", ["Reranker:Model"] = "rr-model" }, "Reranker", fake);

        var ranked = await sp.GetRequiredService<Reranker>().RerankAsync("q", ["a", "b"], 5, default);

        Assert.Equal([1, 0], ranked.Select(r => r.Index));
        var (url, body) = Assert.Single(fake.Requests);
        Assert.Equal("http://rr/v1/rerank", url);
        Assert.Equal("rr-model", body.GetProperty("model").GetString());
        Assert.Equal(2, body.GetProperty("documents").GetArrayLength());
        Assert.Equal(5, body.GetProperty("top_n").GetInt32());
    }

    [Fact]
    public async Task Embedder_sends_model_and_dimensions_and_orders_by_index()
    {
        var fake = new FakeHandler("""{"data":[{"index":1,"embedding":[0,1]},{"index":0,"embedding":[1,0]}]}""");
        using var sp = TestHost.Build(new()
        {
            ["Embeddings:BaseAddress"] = "http://emb/v1/", ["Embeddings:Model"] = "emb-model",
            ["Embeddings:Dimensions"] = "2", ["Embeddings:ApiKey"] = "k", ["Embeddings:Headers:Authorization"] = "Bearer {ApiKey}"
        }, "Embeddings", fake);

        var vectors = await sp.GetRequiredService<Embedder>().EmbedAsync(["a", "b"], default);

        Assert.Equal([1f, 0f], vectors[0]);
        var (url, body) = Assert.Single(fake.Requests);
        Assert.Equal("http://emb/v1/embeddings", url);
        Assert.Equal("emb-model", body.GetProperty("model").GetString());
        Assert.Equal(2, body.GetProperty("dimensions").GetInt32());
        Assert.Equal(["a", "b"], body.GetProperty("input").EnumerateArray().Select(x => x.GetString()));
    }

    static readonly List<SearchHit> Hits = [new("T:X", "Zlecenie wypłaty", "Reguły biznesowe", "Kwota musi być większa od zera.", 0.5)];

    [Fact]
    public async Task Answerer_sends_system_prompt_and_numbered_fragments()
    {
        var fake = new FakeHandler("""{"choices":[{"message":{"role":"assistant","content":"Bo kwota [1]."}}]}""");
        using var sp = TestHost.Build(new() { ["SearchAnswer:BaseAddress"] = "http://llm/v1", ["SearchAnswer:Model"] = "chat-model" }, "SearchAnswer", fake);

        var answer = await sp.GetRequiredService<Answerer>().AnswerAsync("Dlaczego?", Hits, default);

        Assert.Equal("Bo kwota [1].", answer);
        var (url, body) = Assert.Single(fake.Requests);
        Assert.Equal("http://llm/v1/chat/completions", url);
        Assert.Equal("chat-model", body.GetProperty("model").GetString());
        var messages = body.GetProperty("messages");
        Assert.Contains("[n]", messages[0].GetProperty("content").GetString());
        Assert.Contains("[1] Zlecenie wypłaty › Reguły biznesowe", messages[1].GetProperty("content").GetString());
    }

    [Fact]
    public async Task Offline_answer_lists_fragments_with_citations()
    {
        using var sp = TestHost.Build(new());
        var answer = await sp.GetRequiredService<Answerer>().AnswerAsync("Dlaczego?", Hits, default);
        Assert.Contains("nie jest skonfigurowany", answer);
        Assert.Contains("- [1] Zlecenie wypłaty › Reguły biznesowe: Kwota musi", answer);
    }
}

/// <summary>Runs only when the local Qdrant answers; otherwise the test is reported as skipped.</summary>
public sealed class QdrantFactAttribute : FactAttribute
{
    public const string Url = "http://localhost:6333";

    public QdrantFactAttribute()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            http.GetAsync(Url + "/collections").GetAwaiter().GetResult().EnsureSuccessStatusCode();
        }
        catch
        {
            Skip = $"Qdrant not reachable at {Url}";
        }
    }
}

public class QdrantEndToEndTests
{
    [QdrantFact]
    public async Task Indexes_golden_pages_is_idempotent_and_answers_offline()
    {
        // The local Qdrant is shared with another project: only ever touch docgen_test_* collections.
        var collection = $"docgen_test_{Guid.NewGuid():N}";
        using var sp = TestHost.Build(new()
        {
            ["Qdrant:Url"] = QdrantFactAttribute.Url, ["Qdrant:Collection"] = collection, ["Embeddings:Dimensions"] = "256"
        });
        var golden = TestHost.GoldenDir;
        var pages = Directory.GetFiles(golden, "*.md", SearchOption.AllDirectories).Select(f =>
        {
            var text = File.ReadAllText(f);
            string Front(string key) => text.Split('\n').First(l => l.StartsWith(key + ":"))[(key.Length + 1)..].Trim().Trim('"');
            return new ManifestPage(Front("page_id"), Path.GetRelativePath(golden, f).Replace('\\', '/'), Front("title"), "use_case", null,
                Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))));
        }).ToList();
        Assert.Equal(7, pages.Count);

        try
        {
            var indexer = sp.GetRequiredService<IDocsSearchIndexer>();
            var first = await indexer.IndexAsync(golden, new Manifest("a1b2c3d", pages), default);
            Assert.True(first > 30, $"indexed {first} chunks");
            Assert.Equal(0, await indexer.IndexAsync(golden, new Manifest("a1b2c3d", pages), default));

            // A page dropped from the manifest and a changed page hash are re-synced.
            var changed = pages.Skip(1).Select((p, i) => i == 0 ? p with { Hash = "changed" } : p).ToList();
            var second = await indexer.IndexAsync(golden, new Manifest("a1b2c3d", changed), default);
            Assert.InRange(second, 1, 12);

            var answer = await sp.GetRequiredService<IDocsSearch>().AskAsync("Dlaczego klient nie dostał wypłaty?", default);
            Assert.Equal(5, answer.Sources.Count);
            Assert.Contains(answer.Sources, s => s.PageId is "T:Payments.Application.RequestPayoutHandler" or "flow:Orders.Api.OrderEndpoints.settle");
            Assert.Contains("[1]", answer.Answer);
        }
        finally
        {
            Assert.StartsWith("docgen_test_", collection);
            using var http = new HttpClient();
            await http.DeleteAsync($"{QdrantFactAttribute.Url}/collections/{collection}");
        }
    }
}
