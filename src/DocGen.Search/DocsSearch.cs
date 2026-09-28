using System.Text.Json;
using DocGen.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocGen.Search;

public sealed class DocsSearchIndexer(Qdrant qdrant, Embedder embedder, IOptions<EmbeddingsOptions> embeddings, ILogger<DocsSearchIndexer> log)
    : IDocsSearchIndexer
{
    /// <summary>Returns the number of chunks (re)indexed; 0 when nothing changed.</summary>
    public async Task<int> IndexAsync(string docsDir, Manifest manifest, CancellationToken ct)
    {
        await qdrant.EnsureCollectionAsync(embeddings.Value.Dimensions, ct);
        var existing = (await qdrant.ScrollAllAsync(ct))
            .Select(p => (p.Id, PageId: Str(p.Payload, "pageId"), Hash: Str(p.Payload, "pageHash"), Embedder: Str(p.Payload, "embedder")))
            .ToList();

        var inManifest = manifest.Pages.Select(p => p.PageId).ToHashSet();
        var orphans = existing.Where(e => !inManifest.Contains(e.PageId)).Select(e => e.Id).ToList();
        if (orphans.Count > 0)
        {
            await qdrant.DeleteAsync(orphans, ct);
            log.LogInformation("search-index: removed {Count} chunks of pages no longer in manifest", orphans.Count);
        }

        var indexed = 0;
        foreach (var page in manifest.Pages)
        {
            var old = existing.Where(e => e.PageId == page.PageId).ToList();
            if (old.Count > 0 && old.All(e => e.Hash == page.Hash && e.Embedder == embedder.Signature))
                continue;

            var chunks = Chunker.Split(await File.ReadAllTextAsync(Path.Combine(docsDir, page.Path), ct));
            var vectors = await embedder.EmbedAsync(chunks.Select(c => EmbeddingText(page.Title, c)).ToList(), ct);
            var points = chunks.Select((c, i) =>
            {
                var sparse = Chunker.Sparse(EmbeddingText(page.Title, c));
                return new
                {
                    id = Chunker.PointId(page.PageId, c.Section),
                    vector = new Dictionary<string, object>
                    {
                        ["dense"] = vectors[i],
                        ["sparse"] = new { indices = sparse.Indices, values = sparse.Values }
                    },
                    payload = new
                    {
                        pageId = page.PageId, title = page.Title, section = c.Section, text = c.Text,
                        path = page.Path, pageHash = page.Hash, commit = manifest.Commit, embedder = embedder.Signature
                    }
                };
            }).ToList();

            foreach (var batch in points.Chunk(64))
                await qdrant.UpsertAsync(batch, ct);
            var stale = old.Select(e => e.Id).Except(points.Select(p => p.id)).ToList();
            if (stale.Count > 0)
                await qdrant.DeleteAsync(stale, ct);
            indexed += points.Count;
            log.LogInformation("search-index: {Path} -> {Count} chunks", page.Path, points.Count);
        }
        return indexed;
    }

    internal static string EmbeddingText(string title, Chunk c) => $"{title}\n{c.Section}\n\n{c.Text}";

    internal static string Str(JsonElement payload, string name) =>
        payload.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
}

public sealed class DocsSearch(Qdrant qdrant, Embedder embedder, Reranker reranker, Answerer answerer) : IDocsSearch
{
    const int RetrieveLimit = 20, TopN = 5;

    public async Task<SearchAnswer> AskAsync(string question, CancellationToken ct)
    {
        var dense = (await embedder.EmbedAsync([question], ct))[0];
        var candidates = (await qdrant.QueryAsync(dense, Chunker.Sparse(question), RetrieveLimit, ct))
            .Select(p => new SearchHit(DocsSearchIndexer.Str(p.Payload, "pageId"), DocsSearchIndexer.Str(p.Payload, "title"),
                DocsSearchIndexer.Str(p.Payload, "section"), DocsSearchIndexer.Str(p.Payload, "text"), p.Score))
            .ToList();

        var ranked = await reranker.RerankAsync(question,
            candidates.Select(h => DocsSearchIndexer.EmbeddingText(h.Title, new Chunk(h.Section, h.Text))).ToList(), TopN, ct);
        var top = ranked.Select(r => candidates[r.Index] with { Score = r.Score ?? candidates[r.Index].Score }).ToList();

        return new SearchAnswer(await answerer.AnswerAsync(question, top, ct), top);
    }

}
