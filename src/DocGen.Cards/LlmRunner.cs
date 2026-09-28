using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using DocGen.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocGen.Cards;

/// <summary>Deterministic card inputs: the prompt context, the offline prose, how prose becomes a card and how it is checked.</summary>
sealed record CardJob<TProse, TCard>(string Context, TProse Offline, Func<TProse, TCard> Apply, Func<TProse, TCard, List<string>> Validate);

sealed record CachedCard<TCard>(string Status, List<string> Warnings, TCard Card);

/// <summary>
/// Runs one card through its LLM call site: cache lookup by card hash, OpenAI-compatible chat/completions with a strict
/// json_schema for the prose part, deterministic validation, retries with the error list, needs_review fallback.
/// </summary>
/// <summary>Values one call site passes to the shared runner, taken from that call site's own Options class.</summary>
sealed record LlmCall(string CallSite, bool IsOffline, string Model, string SystemPrompt, string PromptVersion, int MaxRetries);

sealed class LlmRunner(IHttpClientFactory http, DocGenPaths paths, IOptions<GeneratorOptions> generator, ILogger<LlmRunner> log)
{
    internal static readonly JsonSerializerOptions Json = new(DocGenJson.Options)
    {
        WriteIndented = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        TypeInfoResolver = new DefaultJsonTypeInfoResolver()
    };

    internal static readonly CardMeta BlankMeta = new("", "", "", "", "", []);

    // ponytail: one global gate for all call sites; per-call-site limits if providers differ in rate limits.
    readonly SemaphoreSlim gate = new(4);

    public async Task<TCard> RunAsync<TProse, TCard>(LlmCall o, string commit, CardJob<TProse, TCard> job,
        Func<TCard, CardMeta, TCard> withMeta, CancellationToken ct)
    {
        var callSite = o.CallSite;
        var baseCard = job.Apply(job.Offline);
        var hash = Sha(string.Join("\n\u0001", callSite, o.Model, o.PromptVersion, o.SystemPrompt, job.Context, JsonSerializer.Serialize(baseCard, Json)));
        CardMeta Meta(string status, List<string> warnings) => new(commit, hash, status, o.PromptVersion, o.Model, warnings);

        if (o.IsOffline)
            return withMeta(baseCard, Meta("offline", job.Validate(job.Offline, baseCard)));

        var file = Path.Combine(paths.Resolve(generator.Value.WorkDir), "cache", callSite, hash + ".json");
        if (File.Exists(file))
        {
            var cached = DocGenJson.Read<CachedCard<TCard>>(file);
            return withMeta(cached.Card, Meta(cached.Status, cached.Warnings));
        }

        var schema = Schema<TProse>();
        var messages = new JsonArray { Message("system", o.SystemPrompt), Message("user", job.Context) };
        TCard? card = default;
        List<string> errors = [];
        for (var attempt = 0; attempt <= o.MaxRetries; attempt++)
        {
            if (attempt > 0)
            {
                log.LogInformation("{CallSite}: retry {Attempt} after {Count} validation errors", callSite, attempt, errors.Count);
                messages.Add(Message("user", "Odpowiedź nie przeszła walidacji. Popraw ją i zwróć cały JSON ponownie. Błędy:\n- " + string.Join("\n- ", errors)));
            }
            try
            {
                var content = await CompleteAsync(callSite, o, messages, schema, ct);
                messages.Add(Message("assistant", content));
                var prose = JsonSerializer.Deserialize<TProse>(content, Json) ?? throw new JsonException("pusta odpowiedź");
                var candidate = job.Apply(prose);
                errors = job.Validate(prose, candidate);
                card = candidate;
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                errors = [$"Niepoprawna odpowiedź LLM: {ex.Message}"];
            }
            if (errors.Count == 0)
                break;
        }

        var status = errors.Count == 0 ? "ok" : "needs_review";
        if (status == "needs_review")
            log.LogWarning("{CallSite}: card needs review: {Errors}", callSite, string.Join("; ", errors));
        if (card is null)
            return withMeta(baseCard, Meta(status, errors)); // no usable LLM answer (e.g. HTTP down): not cached, retried next run
        DocGenJson.Write(file, new CachedCard<TCard>(status, errors, card));
        return withMeta(card, Meta(status, errors));
    }

    async Task<string> CompleteAsync(string callSite, LlmCall o, JsonArray messages, JsonNode schema, CancellationToken ct)
    {
        await gate.WaitAsync(ct);
        try
        {
            var client = http.CreateClient(callSite);
            var body = new JsonObject
            {
                ["model"] = o.Model,
                ["temperature"] = 0,
                ["messages"] = messages.DeepClone(),
                ["response_format"] = new JsonObject
                {
                    ["type"] = "json_schema",
                    ["json_schema"] = new JsonObject { ["name"] = callSite, ["strict"] = true, ["schema"] = schema.DeepClone() }
                }
            };
            using var response = await client.PostAsJsonAsync("chat/completions", body, ct);
            response.EnsureSuccessStatusCode();
            var json = await response.Content.ReadFromJsonAsync<JsonNode>(ct);
            return json?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()
                   ?? throw new InvalidDataException("brak choices[0].message.content");
        }
        finally
        {
            gate.Release();
        }
    }

    static JsonObject Message(string role, string content) => new() { ["role"] = role, ["content"] = content };

    /// <summary>Strict OpenAI json_schema: every object closed and every property required (nullable ones typed [x, "null"]).</summary>
    internal static JsonNode Schema<T>() => JsonSchemaExporter.GetJsonSchemaAsNode(Json, typeof(T), new JsonSchemaExporterOptions
    {
        TreatNullObliviousAsNonNullable = true,
        TransformSchemaNode = (_, node) =>
        {
            if (node is JsonObject obj && obj["properties"] is JsonObject props)
            {
                obj["additionalProperties"] = false;
                obj["required"] = new JsonArray(props.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
            }
            return node;
        }
    });

    static string Sha(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s))).ToLowerInvariant();
}

/// <summary>Checks shared by every card kind.</summary>
static partial class CardChecks
{
    [GeneratedRegex(@"\[\[page:([^|\]]+)(?:\|[^\]]*)?\]\]")] private static partial Regex PageToken();
    [GeneratedRegex(@"\[\[code:([^|\]]+)(?:\|[^\]]*)?\]\]")] private static partial Regex CodeToken();

    /// <summary>All [[page:..]] ids exist, all [[code:..]] and raw location fields point into the index.</summary>
    public static IEnumerable<string> Links(object card, IndexFacts f, IReadOnlySet<string> pages, IEnumerable<string>? rawLocations = null)
    {
        var json = JsonSerializer.Serialize(card, DocGenJson.Options);
        foreach (var id in PageToken().Matches(json).Select(m => m.Groups[1].Value).Distinct())
            if (!pages.Contains(id))
                yield return $"Link [[page:{id}]] wskazuje nieistniejącą stronę.";
        foreach (var loc in CodeToken().Matches(json).Select(m => m.Groups[1].Value).Concat(rawLocations ?? []).Distinct())
            if (!f.CodeExists(loc))
                yield return $"Lokalizacja kodu {loc} nie istnieje w indeksie.";
    }

    public static void Required(List<string> errors, string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"Pole {field} jest puste.");
    }
}
