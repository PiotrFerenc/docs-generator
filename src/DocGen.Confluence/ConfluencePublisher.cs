using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using DocGen.Contracts;
using Microsoft.Extensions.Options;

namespace DocGen.Confluence;

/// <summary>
/// Syncs rendered Markdown pages to Confluence via REST API v1 (Cloud and Data Center).
/// Pages are matched by title in the space and verified by the "docgen" content property {pageId, hash, format};
/// a page is written only when its hash (or conversion format) changed. Nothing is ever deleted.
/// </summary>
public sealed class ConfluencePublisher(
    IHttpClientFactory httpFactory,
    IOptions<ConfluenceOptions> options,
    DocGenPaths paths,
    IOptions<GeneratorOptions> generator) : IConfluencePublisher
{
    public const string ClientName = "Confluence";
    public const string Label = "docgen-generated";
    const string PropertyKey = "docgen";
    const int MaxAttempts = 4;

    readonly ConfluenceOptions o = options.Value;

    // Stamped next to the hash: switching e.g. MermaidMode must rewrite pages even though the Markdown did not change.
    string Format => $"{o.MermaidMode}/{o.MermaidMacroName}";

    public async Task<PublishReport> PublishAsync(string docsDir, Manifest manifest, bool dryRun, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(o.SpaceKey))
            throw new InvalidOperationException("Confluence.SpaceKey is not configured.");
        if (o.MermaidMode is not ("macro" or "code" or "image"))
            throw new InvalidOperationException($"Confluence.MermaidMode '{o.MermaidMode}' is invalid (macro | code | image).");

        var messages = new List<string>();
        var converter = new StorageConverter(o.SpaceKey, o.MermaidMode, o.MermaidMacroName);
        var titleByPath = manifest.Pages.ToDictionary(p => p.Path, p => p.Title);
        var pages = ParentsFirst(manifest.Pages, messages)
            .Select(p => (Page: p, Storage: converter.Convert(File.ReadAllText(Path.Combine(docsDir, p.Path)), p.Path, titleByPath)))
            .ToList();
        pages.ForEach(p => messages.AddRange(p.Storage.Warnings.Select(w => "warning: " + w)));

        if (dryRun)
            return DryRun(pages, messages);

        if (o.IsOffline)
            throw new InvalidOperationException("Confluence.BaseAddress is empty; use --dry-run or configure the Confluence section.");

        var http = httpFactory.CreateClient(ClientName);
        var rootId = await EnsureRoot(http, messages, ct);
        var idByPageId = new Dictionary<string, string>();
        int created = 0, updated = 0, unchanged = 0;

        foreach (var (page, storage) in pages)
        {
            var parentId = page.ParentPageId is { } pp && idByPageId.TryGetValue(pp, out var pid) ? pid : rootId;
            var existing = await FindByTitle(http, page.Title, ct);
            string id;
            if (existing is null)
            {
                id = await CreatePage(http, page.Title, parentId, storage.Xhtml, ct);
                created++;
                messages.Add($"created: {page.Title}");
            }
            else
            {
                id = existing["id"]!.GetValue<string>();
                var stamp = existing["metadata"]?["properties"]?[PropertyKey]?["value"];
                var labelled = existing["metadata"]?["labels"]?["results"]?.AsArray().Any(l => l?["name"]?.GetValue<string>() == Label) == true;
                var stampedPageId = stamp?["pageId"]?.GetValue<string>();
                if (stampedPageId is null ? !labelled : stampedPageId != page.PageId)
                {
                    messages.Add($"error: '{page.Title}' already exists in space {o.SpaceKey} (id {id}) but is not the docgen page {page.PageId}; skipped");
                    continue;
                }
                var currentParent = existing["ancestors"]?.AsArray().LastOrDefault()?["id"]?.GetValue<string>();
                if (stamp?["hash"]?.GetValue<string>() == page.Hash && stamp?["format"]?.GetValue<string>() == Format && currentParent == parentId)
                {
                    idByPageId[page.PageId] = id;
                    unchanged++;
                    continue;
                }
                var version = existing["version"]!["number"]!.GetValue<int>();
                await Send(http, () => Json(HttpMethod.Put, $"rest/api/content/{id}", PageBody(page.Title, parentId, storage.Xhtml, version + 1, manifest.Commit)), ct);
                updated++;
                messages.Add($"updated: {page.Title} (version {version + 1})");
            }
            idByPageId[page.PageId] = id;
            await AddLabel(http, id, ct);
            await SetProperty(http, id, new JsonObject { ["pageId"] = page.PageId, ["hash"] = page.Hash, ["format"] = Format }, ct);
            foreach (var att in storage.Attachments)
                await UploadAttachment(http, id, att.FileName, await RenderMermaid(att, ct), ct);
        }

        await ReportOrphans(http, manifest, messages, ct);
        return new PublishReport(created, updated, unchanged, messages);
    }

    PublishReport DryRun(List<(ManifestPage Page, StorageResult Storage)> pages, List<string> messages)
    {
        var outDir = Path.Combine(paths.Resolve(generator.Value.WorkDir), "confluence-dry-run");
        messages.Insert(0, $"dry-run: no Confluence calls made; {pages.Count} pages converted to {outDir}. " +
                           "Confluence state is unknown, so every page is counted as Created (planned create-or-update).");
        foreach (var (page, storage) in pages)
        {
            var file = Path.Combine(outDir, Path.ChangeExtension(page.Path, ".xhtml"));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, storage.Xhtml);
            messages.Add($"planned: {page.Title} (parent: {page.ParentPageId ?? o.RootPageTitle})" +
                         (storage.Attachments.Count > 0 ? $", {storage.Attachments.Count} mermaid image(s) to render" : ""));
        }
        return new PublishReport(pages.Count, 0, 0, messages);
    }

    static List<ManifestPage> ParentsFirst(List<ManifestPage> pages, List<string> messages)
    {
        var byId = pages.ToDictionary(p => p.PageId);
        foreach (var p in pages.Where(p => p.ParentPageId is not null && !byId.ContainsKey(p.ParentPageId)))
            messages.Add($"warning: parent '{p.ParentPageId}' of '{p.Title}' is not in the manifest; placed under the root page");
        int Depth(ManifestPage p, int guard) =>
            guard < pages.Count && p.ParentPageId is { } parent && byId.TryGetValue(parent, out var pp) ? 1 + Depth(pp, guard + 1) : 0;
        return pages.OrderBy(p => Depth(p, 0)).ToList();
    }

    async Task<string> EnsureRoot(HttpClient http, List<string> messages, CancellationToken ct)
    {
        if (await FindByTitle(http, o.RootPageTitle, ct) is { } root)
            return root["id"]!.GetValue<string>();
        var id = await CreatePage(http, o.RootPageTitle, null,
            "<p>Dokumentacja generowana automatycznie z kodu. Strony podrzędne są nadpisywane przy każdej synchronizacji.</p>", ct);
        await AddLabel(http, id, ct);
        messages.Add($"created root page: {o.RootPageTitle}");
        return id;
    }

    async Task<JsonNode?> FindByTitle(HttpClient http, string title, CancellationToken ct)
    {
        var url = $"rest/api/content?type=page&spaceKey={Uri.EscapeDataString(o.SpaceKey)}&title={Uri.EscapeDataString(title)}" +
                  $"&expand=version,ancestors,metadata.labels,metadata.properties.{PropertyKey}";
        var res = await Send(http, () => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return res?["results"]?.AsArray().FirstOrDefault();
    }

    async Task<string> CreatePage(HttpClient http, string title, string? parentId, string xhtml, CancellationToken ct)
    {
        var res = await Send(http, () => Json(HttpMethod.Post, "rest/api/content", PageBody(title, parentId, xhtml, null, null)), ct);
        return res!["id"]!.GetValue<string>();
    }

    JsonObject PageBody(string title, string? parentId, string xhtml, int? version, string? commit)
    {
        var body = new JsonObject
        {
            ["type"] = "page",
            ["title"] = title,
            ["space"] = new JsonObject { ["key"] = o.SpaceKey },
            ["body"] = new JsonObject { ["storage"] = new JsonObject { ["value"] = xhtml, ["representation"] = "storage" } }
        };
        if (parentId is not null)
            body["ancestors"] = new JsonArray(new JsonObject { ["id"] = parentId });
        if (version is not null)
            body["version"] = new JsonObject { ["number"] = version, ["message"] = $"docgen {commit}" };
        return body;
    }

    Task AddLabel(HttpClient http, string id, CancellationToken ct) =>
        Send(http, () => Json(HttpMethod.Post, $"rest/api/content/{id}/label", new JsonArray(new JsonObject { ["prefix"] = "global", ["name"] = Label })), ct);

    async Task SetProperty(HttpClient http, string id, JsonObject value, CancellationToken ct)
    {
        var url = $"rest/api/content/{id}/property/{PropertyKey}";
        var current = await Send(http, () => new HttpRequestMessage(HttpMethod.Get, url), ct, allowNotFound: true);
        if (current is null)
        {
            await Send(http, () => Json(HttpMethod.Post, $"rest/api/content/{id}/property", new JsonObject { ["key"] = PropertyKey, ["value"] = value.DeepClone() }), ct);
            return;
        }
        var version = current["version"]!["number"]!.GetValue<int>() + 1;
        await Send(http, () => Json(HttpMethod.Put, url, new JsonObject
        {
            ["key"] = PropertyKey, ["value"] = value.DeepClone(), ["version"] = new JsonObject { ["number"] = version }
        }), ct);
    }

    async Task ReportOrphans(HttpClient http, Manifest manifest, List<string> messages, CancellationToken ct)
    {
        var known = manifest.Pages.Select(p => p.Title).Append(o.RootPageTitle).ToHashSet();
        var cql = $"label = \"{Label}\" and space = \"{o.SpaceKey}\" and type = page";
        string? url = $"rest/api/content/search?limit=100&cql={Uri.EscapeDataString(cql)}";
        while (url is not null)
        {
            var next = url;
            var res = await Send(http, () => new HttpRequestMessage(HttpMethod.Get, next), ct);
            foreach (var r in res?["results"]?.AsArray() ?? [])
                if (r?["title"]?.GetValue<string>() is { } title && !known.Contains(title))
                    messages.Add($"orphan: '{title}' (id {r["id"]}) is labelled {Label} but not in the manifest; not deleted");
            // _links.next is relative to the context path (".../wiki"), same as our BaseAddress.
            url = res?["_links"]?["next"]?.GetValue<string>()?.TrimStart('/');
        }
    }

    async Task<byte[]> RenderMermaid(MermaidAttachment att, CancellationToken ct)
    {
        var dir = Path.Combine(paths.Resolve(generator.Value.WorkDir), "confluence-mermaid");
        var png = Path.Combine(dir, att.FileName);
        if (!File.Exists(png)) // file name is a hash of the source, so a cached PNG is always current
        {
            Directory.CreateDirectory(dir);
            var src = Path.ChangeExtension(png, ".mmd");
            await File.WriteAllTextAsync(src, att.Source, ct);
            Process process;
            try
            {
                process = Process.Start(new ProcessStartInfo(o.MmdcPath, ["-i", src, "-o", png, "-b", "white"])
                    { RedirectStandardError = true, RedirectStandardOutput = true })!;
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException(
                    $"MermaidMode=image needs mermaid-cli, but '{o.MmdcPath}' could not be started ({ex.Message}). " +
                    "Install it with `npm install -g @mermaid-js/mermaid-cli`, set Confluence.MmdcPath, or use MermaidMode macro/code.", ex);
            }
            using (process)
            {
                var stderr = process.StandardError.ReadToEndAsync(ct);
                await process.StandardOutput.ReadToEndAsync(ct);
                await process.WaitForExitAsync(ct);
                if (process.ExitCode != 0 || !File.Exists(png))
                    throw new InvalidOperationException($"mmdc failed for {att.FileName} (exit {process.ExitCode}): {await stderr}");
            }
        }
        return await File.ReadAllBytesAsync(png, ct);
    }

    async Task UploadAttachment(HttpClient http, string pageId, string fileName, byte[] data, CancellationToken ct)
    {
        var existing = await Send(http, () => new HttpRequestMessage(HttpMethod.Get,
            $"rest/api/content/{pageId}/child/attachment?filename={Uri.EscapeDataString(fileName)}"), ct);
        var attId = existing?["results"]?.AsArray().FirstOrDefault()?["id"]?.GetValue<string>();
        var url = attId is null ? $"rest/api/content/{pageId}/child/attachment" : $"rest/api/content/{pageId}/child/attachment/{attId}/data";
        await Send(http, () =>
        {
            var file = new ByteArrayContent(data);
            file.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new MultipartFormDataContent { { file, "file", fileName } } };
            req.Headers.Add("X-Atlassian-Token", "no-check");
            return req;
        }, ct);
    }

    static HttpRequestMessage Json(HttpMethod method, string url, JsonNode body) =>
        new(method, url) { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };

    /// <summary>Sends with retry on 429/5xx (honours Retry-After). Returns parsed JSON, or null for an empty body / allowed 404.</summary>
    static async Task<JsonNode?> Send(HttpClient http, Func<HttpRequestMessage> request, CancellationToken ct, bool allowNotFound = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            using var req = request();
            using var res = await http.SendAsync(req, ct);
            if ((res.StatusCode == HttpStatusCode.TooManyRequests || (int)res.StatusCode >= 500) && attempt < MaxAttempts)
            {
                var retryAfter = res.Headers.RetryAfter;
                var delay = retryAfter?.Delta ?? (retryAfter?.Date - DateTimeOffset.UtcNow) ?? TimeSpan.FromSeconds(Math.Pow(2, attempt));
                await Task.Delay(delay < TimeSpan.Zero ? TimeSpan.Zero : delay > TimeSpan.FromMinutes(1) ? TimeSpan.FromMinutes(1) : delay, ct);
                continue;
            }
            if (allowNotFound && res.StatusCode == HttpStatusCode.NotFound)
                return null;
            var body = await res.Content.ReadAsStringAsync(ct);
            if (!res.IsSuccessStatusCode)
                throw new HttpRequestException(
                    $"Confluence {req.Method} {req.RequestUri} failed: {(int)res.StatusCode} {body[..Math.Min(body.Length, 500)]}", null, res.StatusCode);
            return string.IsNullOrWhiteSpace(body) ? null : JsonNode.Parse(body);
        }
    }
}
