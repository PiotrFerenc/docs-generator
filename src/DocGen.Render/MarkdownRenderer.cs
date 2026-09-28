using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DocGen.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocGen.Render;

public sealed class MarkdownRenderer(
    IOptions<RenderOptions> render,
    IOptions<GeneratorOptions> generator,
    DocGenPaths paths,
    ILogger<MarkdownRenderer> logger) : IPageRenderer
{
    public const string ManifestFile = ".manifest.json";

    public Task<Manifest> RenderAsync(CardSet cards, CancellationToken ct)
    {
        var outDir = paths.Resolve(generator.Value.OutputDir);
        var notesDir = paths.Resolve(generator.Value.NotesDir);
        var result = PageBuilder.Build(cards, render.Value.RepoUrlTemplate, notesDir);
        foreach (var w in result.Warnings)
            logger.LogWarning("{Warning}", w);

        Directory.CreateDirectory(outDir);
        var manifestPath = Path.Combine(outDir, ManifestFile);
        var old = File.Exists(manifestPath) ? DocGenJson.Read<Manifest>(manifestPath) : null;

        int written = 0, deleted = 0;
        foreach (var (page, content) in result.Pages)
        {
            ct.ThrowIfCancellationRequested();
            if (WriteIfChanged(Path.Combine(outDir, page.Path), content))
                written++;
        }

        // Only files we generated earlier (old manifest) are ever deleted.
        var current = result.Pages.Select(p => p.Page.Path).ToHashSet();
        var root = Path.GetFullPath(outDir) + Path.DirectorySeparatorChar;
        foreach (var stale in old?.Pages.Select(p => p.Path).Where(p => !current.Contains(p)) ?? [])
        {
            var full = Path.GetFullPath(Path.Combine(outDir, stale));
            if (!full.StartsWith(root, StringComparison.Ordinal) || !File.Exists(full))
                continue;
            File.Delete(full);
            deleted++;
        }

        var manifest = result.Manifest;
        WriteIfChanged(manifestPath, JsonSerializer.Serialize(manifest, DocGenJson.Options));
        logger.LogInformation("render: {Pages} pages, {Written} written, {Deleted} stale deleted, {Warnings} warnings",
            result.Pages.Count, written, deleted, result.Warnings.Count);
        return Task.FromResult(manifest);
    }

    static bool WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content)
            return false;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return true;
    }

    public static string Sha256(string content) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(content)));
}
