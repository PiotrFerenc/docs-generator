using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using System.Web;

namespace DocGen.Confluence.Tests;

/// <summary>In-memory Confluence REST API v1 subset used by ConfluencePublisher.</summary>
sealed class FakeConfluence : HttpMessageHandler, IHttpClientFactory
{
    public sealed class Page
    {
        public required string Id, Title, Space;
        public string? ParentId;
        public int Version = 1;
        public string Body = "";
        public HashSet<string> Labels = [];
        public JsonNode? Property;
        public int PropertyVersion;
    }

    public readonly Dictionary<string, Page> Pages = [];
    public readonly List<string> Requests = [];
    public int FailNextWith429;
    public int SearchPageSize = 3;
    int nextId = 1000;

    public HttpClient CreateClient(string name) => new(this, false) { BaseAddress = new Uri("https://fake.example/wiki/") };

    public int Writes => Requests.Count(r => !r.StartsWith("GET"));

    public Page Add(string title, string space = "DOCS", string? label = null)
    {
        var p = new Page { Id = (nextId++).ToString(), Title = title, Space = space };
        if (label is not null) p.Labels.Add(label);
        return Pages[p.Id] = p;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        var path = req.RequestUri!.AbsolutePath["/wiki/".Length..];
        Requests.Add($"{req.Method} {path}");
        if (FailNextWith429 > 0)
        {
            FailNextWith429--;
            var r = new HttpResponseMessage(HttpStatusCode.TooManyRequests);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.Zero);
            return r;
        }
        var q = HttpUtility.ParseQueryString(req.RequestUri.Query);
        var body = req.Content is StringContent ? JsonNode.Parse(await req.Content.ReadAsStringAsync(ct)) : null;
        var seg = path.Split('/'); // rest, api, content, {id}, ...
        Page P() => Pages[seg[3]];

        return (req.Method.Method, seg.Length) switch
        {
            ("GET", 3) => Ok(new JsonObject
            {
                ["results"] = new JsonArray(Pages.Values.Where(p => p.Space == q["spaceKey"] && p.Title == q["title"]).Select(Json).ToArray())
            }),
            ("POST", 3) when Pages.Values.Any(p => p.Space == (string)body!["space"]!["key"]! && p.Title == (string)body["title"]!) =>
                new HttpResponseMessage(HttpStatusCode.BadRequest),
            ("POST", 3) => Ok(Json(Create(body!))),
            ("GET", 4) when seg[3] == "search" => Search(q),
            ("PUT", 4) when (int)body!["version"]!["number"]! != P().Version + 1 => new HttpResponseMessage(HttpStatusCode.Conflict),
            ("PUT", 4) => Ok(Json(Update(P(), body!))),
            ("POST", 5) when seg[4] == "label" => Label(P(), body!),
            ("POST", 5) when seg[4] == "property" => SetProperty(P(), body!, 0),
            ("GET", 6) when seg[4] == "property" => P().Property is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : Ok(new JsonObject { ["key"] = "docgen", ["value"] = P().Property!.DeepClone(), ["version"] = new JsonObject { ["number"] = P().PropertyVersion } }),
            ("PUT", 6) when seg[4] == "property" => SetProperty(P(), body!, (int)body!["version"]!["number"]! - 1),
            _ => new HttpResponseMessage(HttpStatusCode.NotImplemented) { Content = new StringContent($"fake: {req.Method} {path}") }
        };
    }

    Page Create(JsonNode body)
    {
        var p = Add((string)body["title"]!, (string)body["space"]!["key"]!);
        p.ParentId = (string?)body["ancestors"]?[0]?["id"];
        p.Body = (string)body["body"]!["storage"]!["value"]!;
        return p;
    }

    Page Update(Page p, JsonNode body)
    {
        p.Version++;
        p.Title = (string)body["title"]!;
        p.ParentId = (string?)body["ancestors"]?[0]?["id"];
        p.Body = (string)body["body"]!["storage"]!["value"]!;
        return p;
    }

    HttpResponseMessage Label(Page p, JsonNode body)
    {
        foreach (var l in body.AsArray()) p.Labels.Add((string)l!["name"]!);
        return Ok(new JsonObject());
    }

    HttpResponseMessage SetProperty(Page p, JsonNode body, int expectedCurrent)
    {
        if (p.PropertyVersion != expectedCurrent) return new HttpResponseMessage(HttpStatusCode.Conflict);
        p.Property = body["value"]!.DeepClone();
        p.PropertyVersion++;
        return Ok(new JsonObject { ["key"] = "docgen" });
    }

    HttpResponseMessage Search(System.Collections.Specialized.NameValueCollection q)
    {
        var cql = q["cql"]!;
        var start = int.Parse(q["start"] ?? "0");
        var all = Pages.Values.Where(p => p.Labels.Any(l => cql.Contains($"label = \"{l}\"")) && cql.Contains($"space = \"{p.Space}\"")).ToList();
        var res = new JsonObject
        {
            ["results"] = new JsonArray(all.Skip(start).Take(SearchPageSize).Select(p => (JsonNode)new JsonObject { ["id"] = p.Id, ["title"] = p.Title }).ToArray()),
            ["_links"] = new JsonObject()
        };
        if (start + SearchPageSize < all.Count)
            res["_links"]!["next"] = $"/rest/api/content/search?cql={Uri.EscapeDataString(cql)}&start={start + SearchPageSize}";
        return Ok(res);
    }

    static JsonNode Json(Page p) => new JsonObject
    {
        ["id"] = p.Id,
        ["title"] = p.Title,
        ["version"] = new JsonObject { ["number"] = p.Version },
        ["ancestors"] = p.ParentId is null ? new JsonArray() : new JsonArray(new JsonObject { ["id"] = p.ParentId }),
        ["metadata"] = new JsonObject
        {
            ["labels"] = new JsonObject { ["results"] = new JsonArray(p.Labels.Select(l => (JsonNode)new JsonObject { ["name"] = l }).ToArray()) },
            ["properties"] = p.Property is null ? new JsonObject() : new JsonObject { ["docgen"] = new JsonObject { ["value"] = p.Property.DeepClone() } }
        }
    };

    static HttpResponseMessage Ok(JsonNode json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json.ToJsonString(), Encoding.UTF8, "application/json") };
}
