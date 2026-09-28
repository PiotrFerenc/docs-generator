using System.Text.RegularExpressions;
using DocGen.Contracts;

namespace DocGen.Cards;

/// <summary>Deterministic lookups and formatting over the code index, shared by all card builders.</summary>
sealed class IndexFacts
{
    public CodeIndex Index { get; }
    public List<IndexEntry> UseCases { get; }
    public List<IndexEntry> Behaviors { get; }
    readonly HashSet<string> locations = [];
    readonly List<(string Path, int From, int To)> spans = [];

    public IndexFacts(CodeIndex index)
    {
        Index = index;
        UseCases = index.Entries.Where(e => e.Kind is "request_handler" or "event_handler").OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        Behaviors = index.Entries.Where(e => e.Kind == "behavior").OrderBy(e => e.Id, StringComparer.Ordinal).ToList();
        foreach (var e in index.Entries)
        {
            locations.Add(e.Location);
            e.Guards.ForEach(g => locations.Add(g.Location));
            foreach (var c in e.Chunks)
                if (Split(c.Location) is (var path, var line))
                    spans.Add((path, line, Math.Max(line, c.EndLine)));
        }
        foreach (var en in index.Entities)
        {
            locations.Add(en.Location);
            en.Transitions.ForEach(t => locations.Add(t.Location));
        }
        index.Config.ForEach(c => locations.Add(c.Location));
    }

    public bool CodeExists(string loc) =>
        locations.Contains(loc) || Split(loc) is (var path, var line) && spans.Any(s => s.Path == path && line >= s.From && line <= s.To);

    // ── graph ────────────────────────────────────────────────────────────────

    public static string? Target(IndexEntry e, string type) => e.Edges.FirstOrDefault(x => x.Type == type)?.Target;

    /// <summary>Request / event type handled by a use case.</summary>
    public static string? MessageType(IndexEntry e) => Target(e, "handles");

    public IEnumerable<IndexEntry> HandlersOf(string type) => UseCases.Where(u => MessageType(u) == type);

    public IEnumerable<IndexEntry> Validators(string? request) =>
        Index.Entries.Where(v => v.Kind == "validator" && v.Edges.Any(x => x.Type == "validates" && x.Target == request));

    /// <summary>Entries that trigger the use case: senders of its request, publishers of its event.</summary>
    public IEnumerable<(IndexEntry Source, Edge Edge)> Incoming(IndexEntry uc) =>
        MessageType(uc) is { } t
            ? Index.Entries.SelectMany(s => s.Edges.Where(x => x.Type is "sends" or "publishes" && x.Target == t).Select(x => (s, x)))
            : [];

    /// <summary>Where the entry leads: handlers of sent commands / published events; Target null when nothing in the index handles it.</summary>
    public IEnumerable<(Edge Edge, IndexEntry? Target)> Outgoing(IndexEntry e) =>
        e.Edges.Where(x => x.Type is "sends" or "publishes").SelectMany(x =>
        {
            var handlers = HandlersOf(x.Target).ToList();
            return handlers.Count == 0 ? [(x, (IndexEntry?)null)] : handlers.Select(h => (x, (IndexEntry?)h));
        });

    public EntityInfo? EntityOf(string target)
    {
        var name = target.StartsWith("T:") ? Short(target) : target.Split('.')[0];
        return Index.Entities.FirstOrDefault(en => en.Id == target || en.Name == name);
    }

    public static string Column(EntityInfo en, string property) =>
        en.Fields.FirstOrDefault(f => f.Name == property)?.Column ?? property;

    /// <summary>"Type.Method" that raises the event (searched in the entry's call closure), else the entry itself.</summary>
    public static string RaiseSite(IndexEntry e, string eventType) =>
        e.Chunks.FirstOrDefault(c => c.Text.Contains($"new {Short(eventType)}("))?.Symbol ?? Short(e.Id);

    /// <summary>First source line in the entry's chunks matching the predicate, as "path:line".</summary>
    public static string? FindLine(IndexEntry e, Func<string, bool> predicate)
    {
        foreach (var c in e.Chunks)
        {
            if (Split(c.Location) is not (var path, var start))
                continue;
            var lines = c.Text.Split('\n');
            for (var i = 0; i < lines.Length; i++)
                if (predicate(lines[i]))
                    return $"{path}:{start + i}";
        }
        return null;
    }

    // ── formatting ───────────────────────────────────────────────────────────

    public static (string Path, int Line)? Split(string loc)
    {
        var i = loc.LastIndexOf(':');
        return i > 0 && int.TryParse(loc[(i + 1)..], out var line) ? (loc[..i], line) : null;
    }

    public static string FileLine(string loc) => Split(loc) is (var path, var line) ? $"{Path.GetFileName(path)}:{line}" : loc;

    public static string Code(string loc, string? label = null) => $"[[code:{loc}|{label ?? FileLine(loc)}]]";

    static string StripT(string id) => id.StartsWith("T:") ? id[2..] : id;

    /// <summary>"T:Ns.Type`2" → "Type".</summary>
    public static string Short(string id)
    {
        var s = StripT(id);
        s = s[(s.LastIndexOf('.') + 1)..];
        var tick = s.IndexOf('`');
        return tick < 0 ? s : s[..tick];
    }

    /// <summary>"T:Ns.Type`2" → "Ns.Type&lt;,&gt;".</summary>
    public static string Full(string id)
    {
        var s = StripT(id);
        var tick = s.IndexOf('`');
        return tick < 0 ? s : s[..tick] + "<" + new string(',', int.TryParse(s[(tick + 1)..], out var n) ? n - 1 : 0) + ">";
    }

    /// <summary>"PayoutStatus.Pending" → "Pending".</summary>
    public static string Tail(string value) => value[(value.LastIndexOf('.') + 1)..];

    /// <summary>"RequestPayout" → "Request payout".</summary>
    public static string Humanize(string pascal)
    {
        var s = Regex.Replace(pascal, "(?<=[a-z0-9])(?=[A-Z])", " ");
        return s.Length == 0 ? s : s[..1].ToUpperInvariant() + s[1..].ToLowerInvariant();
    }

    public static string Strip(string s, params string[] suffixes) =>
        suffixes.FirstOrDefault(x => s.EndsWith(x) && s.Length > x.Length) is { } x ? s[..^x.Length] : s;

    /// <summary>[1] → "reguła 1"; [1,2] → "reguły 1–2"; [1,4,5,6,7] → "reguły 1 i 4–7".</summary>
    public static string RuleRef(IEnumerable<int> numbers)
    {
        var n = numbers.Distinct().Order().ToList();
        if (n.Count == 0)
            return "";
        var parts = new List<string>();
        for (var i = 0; i < n.Count;)
        {
            var j = i;
            while (j + 1 < n.Count && n[j + 1] == n[j] + 1)
                j++;
            parts.Add(i == j ? $"{n[i]}" : $"{n[i]}–{n[j]}");
            i = j + 1;
        }
        var list = parts.Count == 1 ? parts[0] : string.Join(", ", parts[..^1]) + " i " + parts[^1];
        return (n.Count == 1 ? "reguła " : "reguły ") + list;
    }

    public static string ExitLabel(Guard g, string? behavior) => g.Exit switch
    {
        "validation" => $"`Result.Fail` ({behavior ?? "walidacja"})",
        "silent_success" => "`Result.Ok()` (cichy sukces)",
        "return" => "`return`",
        "return_value" => "`return` z wartością",
        "propagate" => "propagacja błędu",
        "filter" => "brak wyniku zapytania",
        var e when e.StartsWith("fail:") => $"`{e[5..]}`",
        var e when e.StartsWith("throw:") => $"wyjątek `{e[6..]}`",
        var e => $"`{e}`"
    };

    public static string OnFail(Guard g) => g.Exit switch
    {
        "validation" => "Błąd walidacji.",
        "silent_success" or "return" => "Brak akcji, bez błędu.",
        "return_value" => "Zwraca wynik bez dalszego przetwarzania.",
        "filter" => "Brak wyniku zapytania.",
        "propagate" => "Błąd przekazany dalej.",
        var e when e.StartsWith("fail:") => $"Błąd `{e[5..]}`.",
        var e when e.StartsWith("throw:") => $"Wyjątek `{e[6..]}`.",
        var e => $"Wyjście `{e}`."
    };

    public static bool IsFailure(Guard g) => g.Exit is "validation" || g.Exit.StartsWith("fail:") || g.Exit.StartsWith("throw:");
}
