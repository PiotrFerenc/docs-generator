namespace DocGen.Contracts;

// Per-call-site HTTP configuration (see ~/.claude/skills/dotnet-llm-http-config):
// every LLM/API call site has its own Options class deriving from HttpClientOptions,
// its own config section and its own named HttpClient. Never share them between call sites.
// Empty BaseAddress = offline mode for that call site (deterministic output, Meta.Status = "offline").
public class HttpClientOptions
{
    public string BaseAddress { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 120;
    public string? ApiKey { get; set; }
    public Dictionary<string, string> Headers { get; set; } = new();

    public bool IsOffline => string.IsNullOrWhiteSpace(BaseAddress);
}

public static class HttpClientHeaders
{
    public static void Apply(HttpClient client, HttpClientOptions options)
    {
        if (options.IsOffline)
            return;
        client.BaseAddress = new Uri(options.BaseAddress.EndsWith('/') ? options.BaseAddress : options.BaseAddress + "/");
        client.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        foreach (var (name, rawValue) in options.Headers)
        {
            var value = options.ApiKey is null ? rawValue : rawValue.Replace("{ApiKey}", options.ApiKey);
            client.DefaultRequestHeaders.Remove(name);
            client.DefaultRequestHeaders.Add(name, value);
        }
    }
}

/// <summary>Section "Generator": paths shared by all stages. Relative paths resolve against the appsettings.json directory.</summary>
public class GeneratorOptions
{
    public string WorkDir { get; set; } = ".docgen";          // index.json, cards.json, card cache
    public string OutputDir { get; set; } = "docs";           // rendered Markdown
    public string NotesDir { get; set; } = "docs-notes";      // hand-written <Slug>.notes.md files
    public string GlossaryFile { get; set; } = "glossary.json"; // hand-maintained glossary entries (Manual = true)
    public string Language { get; set; } = "pl";
}

/// <summary>Registered as a singleton by DocGen.Cli: directory of appsettings.json; resolve every relative path against it.</summary>
public sealed record DocGenPaths(string RootDir)
{
    public string Resolve(string path) => Path.GetFullPath(Path.Combine(RootDir, path));
}
