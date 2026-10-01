using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DocGen.Contracts;

namespace DocGen.Agent;

/// <summary>
/// Current data of one customer/case. JSON shape:
/// { "tables": { "payouts": [ { "status": "Failed", ... } ], ... }, "config": { "Payouts.Enabled": true } }
/// A plain { "payouts": [...] } object is accepted as "tables". An empty array means "no rows" (a fact);
/// a missing table means "not fetched" (unknown).
/// </summary>
public sealed class Snapshot
{
    public Dictionary<string, List<JsonObject>> Tables { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, JsonNode?> Config { get; } = new(StringComparer.OrdinalIgnoreCase);

    public static Snapshot Parse(string json)
    {
        var root = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("Snapshot must be a JSON object.");
        var snapshot = new Snapshot();
        var tables = root["tables"]?.AsObject() ?? root;
        foreach (var (name, rows) in tables)
            if (rows is JsonArray array)
                snapshot.Tables[name] = array.OfType<JsonObject>().ToList();
        if (root["config"] is JsonObject config)
            foreach (var (key, value) in config)
                snapshot.Config[key] = value?.DeepClone();
        return snapshot;
    }

    public static Snapshot Load(string path) => Parse(File.ReadAllText(path));
}

/// <summary>Snapshot seen through the index: entity → table rows, property → column value.</summary>
sealed class DataView(CodeIndex index, Snapshot snapshot)
{
    public Snapshot Snapshot => snapshot;

    public EntityInfo? Entity(string name) =>
        index.Entities.FirstOrDefault(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Entity that has all given properties; prefers entities whose table is in the snapshot.</summary>
    public EntityInfo? EntityWith(IReadOnlyCollection<string> properties)
    {
        var candidates = index.Entities
            .Where(e => properties.All(p => e.Fields.Any(f => f.Name.Equals(p, StringComparison.OrdinalIgnoreCase))))
            .OrderByDescending(e => Rows(e).Fetched)
            .ToList();
        return candidates.Count == 1 || candidates.Count > 1 && Rows(candidates[0]).Fetched && !Rows(candidates[1]).Fetched
            ? candidates[0]
            : null;
    }

    public (bool Fetched, string Table, List<JsonObject> Rows) Rows(EntityInfo entity)
    {
        foreach (var key in TableKeys(entity))
            if (snapshot.Tables.TryGetValue(key, out var rows))
                return (true, key, rows);
        return (false, entity.Table ?? Snake(entity.Name) + "s", []);
    }

    public string? Value(JsonObject row, EntityInfo entity, string property)
    {
        var field = entity.Fields.FirstOrDefault(f => f.Name.Equals(property, StringComparison.OrdinalIgnoreCase));
        foreach (var key in new[] { field?.Column, Snake(property), property }.OfType<string>())
            foreach (var (name, value) in row)
                if (name.Equals(key, StringComparison.OrdinalIgnoreCase))
                    return value is null ? null : value is JsonValue v && v.TryGetValue<string>(out var s) ? s : value.ToJsonString();
        return null;
    }

    public bool HasColumn(JsonObject row, EntityInfo entity, string property)
    {
        var field = entity.Fields.FirstOrDefault(f => f.Name.Equals(property, StringComparison.OrdinalIgnoreCase));
        return new[] { field?.Column, Snake(property), property }.OfType<string>()
            .Any(k => row.Any(p => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase)));
    }

    public string Column(EntityInfo entity, string property) =>
        entity.Fields.FirstOrDefault(f => f.Name.Equals(property, StringComparison.OrdinalIgnoreCase))?.Column ?? Snake(property);

    static IEnumerable<string> TableKeys(EntityInfo e) =>
        new[] { e.Table, e.Name, Snake(e.Name) + "s", Snake(e.Name), e.Name + "s" }.OfType<string>().Distinct();

    public static string Snake(string name) => Regex.Replace(name, "(?<=[a-z0-9])([A-Z])", "_$1").ToLowerInvariant();
}
