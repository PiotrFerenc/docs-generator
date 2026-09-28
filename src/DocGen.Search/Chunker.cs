using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace DocGen.Search;

public sealed record Chunk(string Section, string Text);

public static partial class Chunker
{
    public const string IntroSection = "Wprowadzenie";

    /// <summary>One chunk per H2 section; H1 + intro is its own chunk. Frontmatter, the auto-generated
    /// blockquote and the empty notes placeholder are dropped; empty sections are skipped.</summary>
    // ponytail: oversized sections are not split; add splitting if embeddings hit the model's token limit.
    public static List<Chunk> Split(string markdown)
    {
        var lines = StripFrontmatter(markdown.Replace("\r\n", "\n")).Split('\n');
        var chunks = new List<Chunk>();
        var section = IntroSection;
        var body = new List<string>();

        void Flush()
        {
            var text = string.Join('\n', body).Trim();
            while (text.EndsWith("---")) // horizontal rule before the next section
                text = text[..^3].TrimEnd();
            if (text.Length == 0)
                return;
            var name = section;
            for (var n = 2; chunks.Any(c => c.Section == name); n++)
                name = $"{section} ({n})";
            chunks.Add(new Chunk(name, text));
        }

        foreach (var line in lines)
        {
            if (line.StartsWith("## "))
            {
                Flush();
                section = line[3..].Trim();
                body.Clear();
            }
            else if (section == IntroSection && (line.StartsWith("# ") || line.StartsWith('>')))
                continue; // H1 is the page title (payload); intro blockquote = "generated, do not edit" banner
            else if (!NotesPlaceholder().IsMatch(line))
                body.Add(line);
        }
        Flush();
        return chunks;
    }

    static string StripFrontmatter(string md)
    {
        if (!md.StartsWith("---\n"))
            return md;
        var end = md.IndexOf("\n---\n", 4, StringComparison.Ordinal);
        return end < 0 ? md : md[(end + 5)..];
    }

    [GeneratedRegex(@"^\(treść dołączana z .*\)\s*$")]
    private static partial Regex NotesPlaceholder();

    [GeneratedRegex(@"[\p{L}\p{N}_]+")]
    private static partial Regex Word();

    /// <summary>Lower-cased word tokens truncated to 6 chars — a crude stemmer for Polish inflection
    /// ("wypłata", "wypłaty" → "wypłat").</summary>
    // ponytail: prefix stemming; swap for a real Polish stemmer/lemmatizer if recall matters.
    public static IEnumerable<string> Tokens(string text) =>
        Word().Matches(text.ToLowerInvariant()).Select(m => m.Value.Length > 6 ? m.Value[..6] : m.Value);

    public static uint Hash(string token)
    {
        var h = 2166136261u; // FNV-1a
        foreach (var b in Encoding.UTF8.GetBytes(token))
            h = (h ^ b) * 16777619u;
        return h;
    }

    /// <summary>Client-side sparse vector: hashed token ids with raw TF (Qdrant applies IDF).</summary>
    public static (uint[] Indices, float[] Values) Sparse(string text)
    {
        var tf = new SortedDictionary<uint, float>();
        foreach (var t in Tokens(text))
            tf[Hash(t)] = tf.GetValueOrDefault(Hash(t)) + 1;
        return (tf.Keys.ToArray(), tf.Values.ToArray());
    }

    /// <summary>Offline dense embedding: hashed bag-of-words, L2-normalized.</summary>
    public static float[] HashedEmbedding(string text, int dimensions)
    {
        var v = new float[dimensions];
        foreach (var t in Tokens(text))
            v[Hash(t) % (uint)dimensions] += 1;
        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0)
            for (var i = 0; i < v.Length; i++)
                v[i] /= norm;
        return v;
    }

    static readonly Guid Namespace = new("6f1c2a9e-4b7d-4e38-9a51-0d3c8e2f7b64");

    /// <summary>Deterministic name-based (v5) UUID of a chunk.</summary>
    public static string PointId(string pageId, string section)
    {
        var name = Encoding.UTF8.GetBytes($"{pageId}\n{section}");
        var hash = SHA1.HashData([.. Namespace.ToByteArray(bigEndian: true), .. name]);
        hash[6] = (byte)((hash[6] & 0x0F) | 0x50);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true).ToString();
    }
}
