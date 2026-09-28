using DocGen.Contracts;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace DocGen.Search;

static class Http
{
    public static async Task<JsonElement> SendAsync(HttpClient client, HttpMethod method, string path, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, path) { Content = body is null ? null : JsonContent.Create(body) };
        using var response = await client.SendAsync(request, ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"{method} {client.BaseAddress}{path} -> {(int)response.StatusCode}: {text}", null, response.StatusCode);
        return text.Length == 0 ? default : JsonDocument.Parse(text).RootElement.Clone();
    }
}

public sealed class Embedder(IHttpClientFactory factory, IOptions<EmbeddingsOptions> options)
{
    readonly EmbeddingsOptions _o = options.Value;

    /// <summary>Stable id of the vector space; points embedded with a different one are re-indexed.</summary>
    public string Signature => _o.IsOffline ? $"offline-hash:{_o.Dimensions}" : $"{_o.Model}:{_o.Dimensions}";

    public async Task<List<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct)
    {
        if (_o.IsOffline)
            return texts.Select(t => Chunker.HashedEmbedding(t, _o.Dimensions)).ToList();

        var client = factory.CreateClient("Embeddings");
        var result = new List<float[]>();
        foreach (var batch in texts.Chunk(64))
        {
            var json = await Http.SendAsync(client, HttpMethod.Post, "embeddings",
                new { model = _o.Model, input = batch, dimensions = _o.Dimensions }, ct);
            var vectors = json.GetProperty("data").EnumerateArray()
                .OrderBy(d => d.TryGetProperty("index", out var i) ? i.GetInt32() : 0)
                .Select(d => d.GetProperty("embedding").EnumerateArray().Select(x => x.GetSingle()).ToArray())
                .ToList();
            if (vectors.Count != batch.Length || vectors.Any(v => v.Length != _o.Dimensions))
                throw new InvalidDataException($"Embeddings: expected {batch.Length} vectors of size {_o.Dimensions} (Embeddings:Dimensions), " +
                                               $"got {vectors.Count} of size {vectors.FirstOrDefault()?.Length}");
            result.AddRange(vectors);
        }
        return result;
    }
}

public sealed class Reranker(IHttpClientFactory factory, IOptions<RerankerOptions> options)
{
    readonly RerankerOptions _o = options.Value;

    /// <summary>Returns (index into documents, score) best first. Offline: retrieval order, score null.</summary>
    public async Task<List<(int Index, double? Score)>> RerankAsync(string query, IReadOnlyList<string> documents, int topN, CancellationToken ct)
    {
        if (_o.IsOffline || documents.Count == 0)
            return Enumerable.Range(0, Math.Min(topN, documents.Count)).Select(i => (i, (double?)null)).ToList();

        var json = await Http.SendAsync(factory.CreateClient("Reranker"), HttpMethod.Post, "rerank",
            new { model = _o.Model, query, documents, top_n = topN }, ct);
        return Parse(json).OrderByDescending(r => r.Score).Take(topN).Select(r => (r.Index, (double?)r.Score)).ToList();
    }

    /// <summary>Accepts {results:[{index, relevance_score}]} (Jina/Cohere/vLLM) and [{index, score}] (HF TEI).</summary>
    public static List<(int Index, double Score)> Parse(JsonElement json)
    {
        var items = json.ValueKind == JsonValueKind.Array ? json : json.GetProperty("results");
        return items.EnumerateArray()
            .Select(r => (r.GetProperty("index").GetInt32(),
                (r.TryGetProperty("relevance_score", out var s) ? s : r.GetProperty("score")).GetDouble()))
            .ToList();
    }
}

public sealed class Answerer(IHttpClientFactory factory, IOptions<SearchAnswerOptions> options)
{
    readonly SearchAnswerOptions _o = options.Value;

    public async Task<string> AnswerAsync(string question, IReadOnlyList<SearchHit> hits, CancellationToken ct)
    {
        if (hits.Count == 0)
            return "Dokumentacja nie zawiera fragmentów pasujących do pytania.";
        if (_o.IsOffline)
            return "Model LLM nie jest skonfigurowany (SearchAnswer:BaseAddress), poniżej najlepiej pasujące fragmenty dokumentacji:\n\n" +
                   string.Join('\n', hits.Select((h, i) => $"- [{i + 1}] {h.Title} › {h.Section}: {Snippet(h.Text)}"));

        var fragments = new StringBuilder($"Pytanie: {question}\n\nFragmenty dokumentacji:\n");
        for (var i = 0; i < hits.Count; i++)
            fragments.Append($"\n[{i + 1}] {hits[i].Title} › {hits[i].Section}\n{hits[i].Text}\n");

        var json = await Http.SendAsync(factory.CreateClient("SearchAnswer"), HttpMethod.Post, "chat/completions", new
        {
            model = _o.Model,
            messages = new object[]
            {
                new { role = "system", content = _o.SystemPrompt },
                new { role = "user", content = fragments.ToString() }
            }
        }, ct);
        return json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() ?? "";
    }

    static string Snippet(string text)
    {
        var flat = string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return flat.Length <= 240 ? flat : flat[..240] + "…";
    }
}

/// <summary>Minimal Qdrant REST client for one collection.</summary>
public sealed class Qdrant(IHttpClientFactory factory, IOptions<QdrantOptions> options)
{
    readonly string _c = Uri.EscapeDataString(options.Value.Collection);
    HttpClient Client => factory.CreateClient("Qdrant");

    public async Task EnsureCollectionAsync(int dimensions, CancellationToken ct)
    {
        try
        {
            await Http.SendAsync(Client, HttpMethod.Get, $"collections/{_c}", null, ct);
        }
        catch (HttpRequestException e) when (e.StatusCode == HttpStatusCode.NotFound)
        {
            await Http.SendAsync(Client, HttpMethod.Put, $"collections/{_c}", new
            {
                vectors = new { dense = new { size = dimensions, distance = "Cosine" } },
                sparse_vectors = new { sparse = new { modifier = "idf" } }
            }, ct);
        }
    }

    /// <summary>All points' ids and payloads (without vectors).</summary>
    public async Task<List<(string Id, JsonElement Payload)>> ScrollAllAsync(CancellationToken ct)
    {
        var points = new List<(string, JsonElement)>();
        object? offset = null;
        do
        {
            var json = await Http.SendAsync(Client, HttpMethod.Post, $"collections/{_c}/points/scroll",
                new { limit = 256, offset, with_payload = true, with_vector = false }, ct);
            var result = json.GetProperty("result");
            points.AddRange(result.GetProperty("points").EnumerateArray()
                .Select(p => (p.GetProperty("id").ToString(), p.GetProperty("payload"))));
            var next = result.GetProperty("next_page_offset");
            offset = next.ValueKind == JsonValueKind.Null ? null : next.ToString();
        } while (offset is not null);
        return points;
    }

    public Task UpsertAsync(IEnumerable<object> points, CancellationToken ct) =>
        Http.SendAsync(Client, HttpMethod.Put, $"collections/{_c}/points?wait=true", new { points }, ct);

    public async Task DeleteAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        foreach (var batch in ids.Chunk(256))
            await Http.SendAsync(Client, HttpMethod.Post, $"collections/{_c}/points/delete?wait=true", new { points = batch }, ct);
    }

    /// <summary>Hybrid query: dense + sparse prefetch fused with RRF.</summary>
    public async Task<List<(double Score, JsonElement Payload)>> QueryAsync(float[] dense, (uint[] Indices, float[] Values) sparse, int limit, CancellationToken ct)
    {
        var json = await Http.SendAsync(Client, HttpMethod.Post, $"collections/{_c}/points/query", new
        {
            prefetch = new object[]
            {
                new { query = dense, @using = "dense", limit },
                new { query = new { indices = sparse.Indices, values = sparse.Values }, @using = "sparse", limit }
            },
            query = new { fusion = "rrf" },
            limit,
            with_payload = true
        }, ct);
        return json.GetProperty("result").GetProperty("points").EnumerateArray()
            .Select(p => (p.GetProperty("score").GetDouble(), p.GetProperty("payload"))).ToList();
    }
}
