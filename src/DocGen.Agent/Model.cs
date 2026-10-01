using System.Text.Json;
using System.Text.Json.Serialization;
using DocGen.Contracts;

namespace DocGen.Agent;

/// <summary>
/// General-purpose agent answering questions about the system (how a process works, which rules apply,
/// where logic lives, what writes a field, why data is in a given state, ...) from the generated documentation,
/// the code index and — optionally — a snapshot of case data.
/// </summary>
public interface IDocAgent
{
    Task<AgentResult> AskAsync(string question, Snapshot? snapshot, CancellationToken ct);

    /// <summary>Markdown report (Polish) with code links.</summary>
    string ToMarkdown(AgentResult result);
}

/// <param name="Answer">LLM answer; null in offline mode (no Agent:BaseAddress).</param>
/// <param name="Sources">Documentation fragments found for the question.</param>
/// <param name="Analysis">Automatic data analysis; only when a snapshot was given.</param>
/// <param name="ToolCalls">Tools the agent used, in order.</param>
public sealed record AgentResult(
    string Question,
    string? Answer,
    List<SearchHit> Sources,
    DataAnalysis? Analysis,
    List<ToolCall> ToolCalls);

public sealed record ToolCall(string Name, string Arguments);

public enum RuleState { Ok, Violated, Unknown }

/// <summary>One business rule of a use case evaluated against the snapshot.</summary>
/// <param name="Silent">The rule stops the process without an error (Result.Ok / return).</param>
public sealed record RuleCheck(
    string PageId,
    string UseCase,
    int No,
    string Rule,
    string OnFail,
    RuleState State,
    bool Silent,
    List<string> Evidence,
    List<string> Locations);

public enum StepState { Done, NotDone, NoTrace, Unknown, External }

public sealed record StepCheck(int No, string? PageId, string Label, StepState State, string Evidence);

public sealed record FlowCheck(string FlowId, string Title, List<StepCheck> Steps, int? BreakStep, string? NegativeOutcome);

/// <summary>Which documented process the data shows as stopped, and which rules explain it.</summary>
/// <param name="Verdict">cause_found | candidates | outside_code | insufficient_data | no_problem_found</param>
public sealed record DataAnalysis(
    string Verdict,
    FlowCheck? Flow,
    List<RuleCheck> Causes,
    List<RuleCheck> RuledOut,
    List<RuleCheck> Unresolved,
    List<string> MissingData,
    List<string> Notes);

static class AgentJson
{
    /// <summary>DocGen JSON settings + enums as names (readable for the LLM).</summary>
    public static readonly JsonSerializerOptions Options = new(DocGenJson.Options) { Converters = { new JsonStringEnumConverter() } };
}
