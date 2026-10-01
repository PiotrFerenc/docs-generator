using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using DocGen.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace DocGen.Agent.Tests;

public class AgentTests
{
    static readonly string Root = FindRoot();
    static readonly CodeIndex Index = DocGenJson.Read<CodeIndex>(Path.Combine(Root, "tests/fixtures/index.sample.json"));
    static readonly CardSet Cards = DocGenJson.Read<CardSet>(Path.Combine(Root, "tests/fixtures/cards.sample.json"));

    static Snapshot Example(string name) => Snapshot.Load(Path.Combine(Root, "examples/snapshots", name + ".json"));

    static DataAnalysis Analyze(string snapshot) => new Analyzer(Index, Cards, Example(snapshot)).Analyze();

    static Tools ToolsFor(Snapshot? snapshot) => new(Index, Cards, snapshot, null, null);

    // ── general questions (no snapshot) ─────────────────────────────────────

    [Fact]
    public async Task Keyword_search_finds_the_use_case_for_a_general_question()
    {
        var hits = await ToolsFor(null).SearchAsync("jakie reguły ma request payout", CancellationToken.None);
        Assert.Contains(hits, h => h.PageId.EndsWith("RequestPayoutHandler"));
    }

    [Fact]
    public async Task Find_usages_lists_writers_and_rules_of_a_field()
    {
        var text = await ToolsFor(null).ExecuteAsync("find_usages", """{"name":"Payout.Status"}""", CancellationToken.None);
        Assert.Contains("RequestPayoutHandler", text);
        Assert.Contains("ConfirmPayoutHandler", text);
        Assert.Contains("przejścia statusów", text);
    }

    [Fact]
    public async Task Get_code_returns_numbered_source_lines()
    {
        var text = await ToolsFor(null).ExecuteAsync("get_code", """{"location":"src/Payments.Application/RequestPayout.cs:45"}""", CancellationToken.None);
        Assert.Contains("   45  ", text);
        Assert.Contains("IsNullOrWhiteSpace(account.Iban)", text);
    }

    [Fact]
    public void Data_tools_are_offered_only_with_a_snapshot()
    {
        static List<string> Names(Tools t) => t.Definitions().Select(d => d!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.DoesNotContain("evaluate_rules", Names(ToolsFor(null)));
        Assert.Contains("evaluate_rules", Names(ToolsFor(Example("brak-iban"))));
    }

    // ── case questions (with snapshot) ──────────────────────────────────────

    [Fact]
    public void Missing_iban_stops_the_process_at_request_payout()
    {
        var a = Analyze("brak-iban");
        Assert.Equal("cause_found", a.Verdict);
        Assert.Equal(3, a.Flow!.BreakStep);
        Assert.Contains("src/Payments.Application/RequestPayout.cs:45", a.Causes[0].Locations);
        Assert.Contains(a.RuledOut, c => c.Locations.Contains("src/Payments.Application/RequestPayout.cs:42"));
    }

    [Fact]
    public void Blocked_account_fails_the_repository_filter_rule() =>
        Assert.Contains("src/Payments.Infrastructure/PaymentsInfrastructure.cs:27", Analyze("konto-zablokowane").Causes[0].Locations);

    [Fact]
    public void Voucher_is_a_silent_stop_in_the_event_handler()
    {
        var cause = Analyze("wyplata-bonem").Causes[0];
        Assert.EndsWith("OrderSettledEventHandler", cause.PageId);
        Assert.True(cause.Silent);
    }

    [Fact]
    public void Disabled_feature_flag_is_a_silent_stop()
    {
        var cause = Analyze("wyplaty-wylaczone").Causes[0];
        Assert.Contains("src/Payments.Application/RequestPayout.cs:32", cause.Locations);
        Assert.True(cause.Silent);
    }

    [Fact]
    public void Negative_status_points_outside_the_code()
    {
        var a = Analyze("przelew-odrzucony");
        Assert.Equal("outside_code", a.Verdict);
        Assert.Contains("Rachunek odbiorcy zamknięty", a.Flow!.NegativeOutcome);
    }

    [Fact]
    public void Missing_flag_value_gives_candidates_and_asks_for_it()
    {
        var a = Analyze("bez-konfiguracji");
        Assert.Equal("candidates", a.Verdict);
        Assert.Contains(a.MissingData, m => m.Contains("Payouts.Enabled"));
    }

    [Fact]
    public void Missing_table_is_unknown_not_a_cause()
    {
        var snapshot = Snapshot.Parse("""{ "orders": [ { "status": "Settled", "payout_method": "BankTransfer" } ], "payouts": [] }""");
        var a = new Analyzer(Index, Cards, snapshot).Analyze();
        Assert.DoesNotContain(a.Causes, c => c.Locations.Contains("src/Payments.Application/RequestPayout.cs:45"));
        Assert.Contains(a.Unresolved, c => c.Locations.Contains("src/Payments.Application/RequestPayout.cs:45"));
    }

    [Fact]
    public async Task Evaluate_rules_tool_works_for_any_use_case()
    {
        var text = await ToolsFor(Example("brak-iban")).ExecuteAsync("evaluate_rules",
            """{"page_id":"T:Payments.Application.RequestPayoutHandler"}""", CancellationToken.None);
        Assert.Contains("\"state\": \"Violated\"", text);
    }

    [Theory]
    [InlineData("Verified", "!=", "KycStatus.Verified", false)]
    [InlineData("Pending", "!=", "KycStatus.Verified", true)]
    [InlineData(null, "is_empty", null, true)]
    [InlineData("25000", ">", "20_000m", true)]
    [InlineData("abc", ">", "1", null)]
    public void Compare(string? actual, string op, string? right, bool? expected) =>
        Assert.Equal(expected, RuleEvaluator.Compare(actual, op, right));

    // ── LLM loop ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Agent_executes_tool_calls_and_returns_the_final_answer()
    {
        var responses = new Queue<string>([
            """{"choices":[{"message":{"role":"assistant","content":null,"tool_calls":[{"id":"c1","type":"function","function":{"name":"find_usages","arguments":"{\"name\":\"Payout.Status\"}"}}]}}]}""",
            """{"choices":[{"message":{"role":"assistant","content":"### Odpowiedź\nStatus wypłaty zmieniają dwa procesy."}}]}"""
        ]);
        var requests = new List<JsonObject>();
        var http = new StubFactory(new StubHandler(async req =>
        {
            requests.Add(JsonNode.Parse(await req.Content!.ReadAsStringAsync())!.AsObject());
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(responses.Dequeue(), Encoding.UTF8, "application/json") };
        }));
        var loop = new AgentLoop(http, Options.Create(new AgentOptions { BaseAddress = "http://llm.test/v1/" }), NullLogger<AgentLoop>.Instance);
        var tools = ToolsFor(null);
        var calls = new List<ToolCall>();

        var answer = await loop.RunAsync("Co zmienia status wypłaty?", await tools.SearchAsync("status wypłaty", CancellationToken.None),
            null, tools, calls, CancellationToken.None);

        Assert.Equal("### Odpowiedź\nStatus wypłaty zmieniają dwa procesy.", answer);
        Assert.Equal("find_usages", Assert.Single(calls).Name);
        var toolMessage = requests[1]["messages"]!.AsArray().Last()!;
        Assert.Equal("tool", toolMessage["role"]!.GetValue<string>());
        Assert.Contains("ConfirmPayoutHandler", toolMessage["content"]!.GetValue<string>());
        Assert.DoesNotContain(requests[0]["tools"]!.AsArray(), t => t!["function"]!["name"]!.GetValue<string>() == "get_rows");
    }

    sealed class StubHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => respond(request);
    }

    sealed class StubFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler) { BaseAddress = new Uri("http://llm.test/v1/") };
    }

    static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "DocGen.sln")))
                return dir.FullName;
        throw new InvalidOperationException("DocGen.sln not found");
    }
}
