using DocGen.Contracts;

namespace DocGen.Search;

/// <summary>Section "Qdrant": vector store (non-LLM infra, not an HttpClientOptions call site).</summary>
public class QdrantOptions
{
    public string Url { get; set; } = "http://localhost:6333";
    public string Collection { get; set; } = "docgen_pages";
    public string? ApiKey { get; set; }
    public int TimeoutSeconds { get; set; } = 60;
}

/// <summary>Section "Embeddings": OpenAI-compatible POST embeddings. Offline = hashed bag-of-words.</summary>
public class EmbeddingsOptions : HttpClientOptions
{
    public string Model { get; set; } = "text-embedding-3-small";
    public int Dimensions { get; set; } = 1536;
}

/// <summary>Section "Reranker": POST rerank {model, query, documents, top_n}. Offline = retrieval order.</summary>
public class RerankerOptions : HttpClientOptions
{
    public string Model { get; set; } = "bge-reranker-v2-m3";
}

/// <summary>Section "SearchAnswer": OpenAI-compatible chat/completions. Offline = list of top fragments.</summary>
public class SearchAnswerOptions : HttpClientOptions
{
    public string Model { get; set; } = "gpt-4.1";

    public string SystemPrompt { get; set; } =
        "Jesteś asystentem odpowiadającym na pytania o działanie systemu na podstawie jego dokumentacji biznesowej. " +
        "Odpowiadaj po polsku, zwięźle, wyłącznie na podstawie podanych fragmentów dokumentacji. " +
        "Każde stwierdzenie opatrz odwołaniem do fragmentu w formie [n], gdzie n to numer fragmentu. " +
        "Nie zgaduj i nie korzystaj z wiedzy spoza fragmentów. " +
        "Jeśli fragmenty nie zawierają odpowiedzi, napisz wprost, że dokumentacja nie zawiera odpowiedzi na to pytanie.";
}
