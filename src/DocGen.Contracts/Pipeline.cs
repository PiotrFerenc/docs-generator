using System.Text.Json;
using System.Text.Json.Serialization;

namespace DocGen.Contracts;

// ─────────────────────────────────────────────────────────────────────────────
// Stage interfaces. Each stage project registers its implementation via an
// IServiceCollection extension (AddDocGenIndexer, AddDocGenCards, ...) and reads
// its own configuration sections from appsettings.json. DocGen.Cli wires them together.
// ─────────────────────────────────────────────────────────────────────────────

public interface ICodeIndexer
{
    Task<CodeIndex> IndexAsync(CancellationToken ct);
}

public interface ICardGenerator
{
    /// <summary>Builds all cards; reuses cached cards whose CardHash did not change.</summary>
    Task<CardSet> GenerateAsync(CodeIndex index, CancellationToken ct);
}

public interface IPageRenderer
{
    /// <summary>Writes Markdown pages to the output directory and returns the manifest (also written as .manifest.json).</summary>
    Task<Manifest> RenderAsync(CardSet cards, CancellationToken ct);
}

public interface IConfluencePublisher
{
    Task<PublishReport> PublishAsync(string docsDir, Manifest manifest, bool dryRun, CancellationToken ct);
}

public interface IDocsSearchIndexer
{
    Task<int> IndexAsync(string docsDir, Manifest manifest, CancellationToken ct);
}

public interface IDocsSearch
{
    Task<SearchAnswer> AskAsync(string question, CancellationToken ct);
}

// ── Manifest (.manifest.json in the output dir) ─────────────────────────────

/// <param name="Path">Relative to output dir, forward slashes, e.g. "modules/Payments/use-cases/RequestPayout.md".</param>
/// <param name="Kind">use_case | entity | flow | global_rule | module | system | glossary</param>
/// <param name="ParentPageId">Page under which it is placed in Confluence (module page, system page), null for root.</param>
/// <param name="Hash">SHA-256 of the rendered page content.</param>
public sealed record ManifestPage(string PageId, string Path, string Title, string Kind, string? ParentPageId, string Hash);

public sealed record Manifest(string Commit, List<ManifestPage> Pages);

public sealed record PublishReport(int Created, int Updated, int Unchanged, List<string> Messages);

public sealed record SearchHit(string PageId, string Title, string Section, string Text, double Score);

public sealed record SearchAnswer(string Answer, List<SearchHit> Sources);

// ── Shared JSON settings ────────────────────────────────────────────────────

public static class DocGenJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static T Read<T>(string path) =>
        JsonSerializer.Deserialize<T>(File.ReadAllText(path), Options)
        ?? throw new InvalidDataException($"Empty JSON in {path}");

    public static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Options));
    }
}
