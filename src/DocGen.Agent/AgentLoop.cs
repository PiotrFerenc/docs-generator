using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DocGen.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocGen.Agent;

/// <summary>LLM tool-calling loop over OpenAI-compatible chat/completions (section/named client "Agent").</summary>
sealed class AgentLoop(IHttpClientFactory http, IOptions<AgentOptions> options, ILogger<AgentLoop> log)
{
    readonly AgentOptions _options = options.Value;

    public bool IsOffline => _options.IsOffline;

    public async Task<string?> RunAsync(string question, List<SearchHit> sources, DataAnalysis? analysis, Tools tools,
        List<ToolCall> calls, CancellationToken ct)
    {
        if (_options.IsOffline)
            return null;

        var client = http.CreateClient("Agent");
        var definitions = tools.Definitions();
        var messages = new JsonArray
        {
            new JsonObject { ["role"] = "system", ["content"] = _options.SystemPrompt },
            new JsonObject { ["role"] = "user", ["content"] = Prompt(question, sources, analysis) }
        };

        for (var step = 0; step < _options.MaxSteps; step++)
        {
            var last = step == _options.MaxSteps - 1;
            var request = new JsonObject
            {
                ["model"] = _options.Model,
                ["messages"] = messages.DeepClone(),
                ["tools"] = definitions.DeepClone(),
                ["tool_choice"] = last ? "none" : "auto"
            };
            using var response = await client.PostAsJsonAsync("chat/completions", request, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"Agent: HTTP {(int)response.StatusCode}: {body}");

            var message = JsonNode.Parse(body)?["choices"]?[0]?["message"]?.AsObject()
                          ?? throw new InvalidDataException("Agent: response without choices[0].message");
            if (message["tool_calls"] is not JsonArray toolCalls || toolCalls.Count == 0)
                return message["content"]?.GetValue<string>();

            messages.Add(message.DeepClone());
            foreach (var call in toolCalls.OfType<JsonObject>())
            {
                var name = call["function"]?["name"]?.GetValue<string>() ?? "";
                var arguments = call["function"]?["arguments"]?.GetValue<string>() ?? "{}";
                log.LogInformation("agent: {Tool}({Arguments})", name, arguments);
                calls.Add(new ToolCall(name, arguments));
                messages.Add(new JsonObject
                {
                    ["role"] = "tool",
                    ["tool_call_id"] = call["id"]?.GetValue<string>(),
                    ["content"] = await tools.ExecuteAsync(name, arguments, ct)
                });
            }
        }
        return null;
    }

    static string Prompt(string question, List<SearchHit> sources, DataAnalysis? analysis)
    {
        var prompt = new StringBuilder($"Pytanie: {question}\n\n");
        prompt.AppendLine("Fragmenty dokumentacji znalezione dla pytania:");
        prompt.AppendLine(sources.Count == 0 ? "(brak)" : string.Join("\n\n", sources.Select((h, i) =>
            $"[{i + 1}] {h.Title} › {h.Section} (page_id: {h.PageId})\n{h.Text}")));
        if (analysis is not null)
        {
            prompt.AppendLine("\nDołączono snapshot danych przypadku. Automatyczna analiza danych (może nie dotyczyć pytania):");
            prompt.AppendLine(JsonSerializer.Serialize(analysis, AgentJson.Options));
        }
        return prompt.ToString();
    }
}
