using DocGen.Contracts;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace DocGen.Indexer.Tests;

// Guard kinds the sample does not contain, checked on an in-memory project (MediatR stubbed by name).
public class GuardKindTests
{
    const string Source = """
        namespace MediatR
        {
            public interface IBaseRequest { }
            public interface IRequest<T> : IBaseRequest { }
            public interface IRequestHandler<TRequest, TResponse>
            {
                System.Threading.Tasks.Task<TResponse> Handle(TRequest request, System.Threading.CancellationToken ct);
            }
        }

        namespace Shop.Application
        {
            public enum Kind { A, B, C }

            public record Cmd(Kind Kind, string? Name, object? Payload) : MediatR.IRequest<int>;

            public class CmdHandler : MediatR.IRequestHandler<Cmd, int>
            {
                public System.Threading.Tasks.Task<int> Handle(Cmd cmd, System.Threading.CancellationToken ct)
                {
                    System.ArgumentNullException.ThrowIfNull(cmd.Payload);
                    var name = cmd.Name ?? throw new System.InvalidOperationException("name");
                    switch (cmd.Kind)
                    {
                        case Kind.A:
                            throw new System.NotSupportedException();
                        case Kind.B:
                            break;
                    }
                    var n = cmd.Kind switch { Kind.C => throw new System.ArgumentException(), _ => 1 };
                    return System.Threading.Tasks.Task.FromResult(n);
                }
            }
        }
        """;

    [Fact]
    public async Task Switch_throw_expression_and_guard_call_are_guards()
    {
        using var workspace = new AdhocWorkspace();
        var references = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator)
            .Select(p => MetadataReference.CreateFromFile(p));
        var project = workspace.AddProject(ProjectInfo.Create(ProjectId.CreateNewId(), VersionStamp.Default, "Shop.Application", "Shop.Application",
            LanguageNames.CSharp, compilationOptions: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary), metadataReferences: references));
        workspace.AddDocument(DocumentInfo.Create(DocumentId.CreateNewId(project.Id), "Handler.cs",
            loader: TextLoader.From(TextAndVersion.Create(SourceText.From(Source), VersionStamp.Default)), filePath: "/repo/src/Handler.cs"));

        var index = await new IndexBuilder(workspace.CurrentSolution, "/repo", new IndexerOptions()).BuildAsync("x", CancellationToken.None);
        var guards = index.Entries.Single(e => e.Id == "T:Shop.Application.CmdHandler").Guards;

        Assert.Equal(
            [
                new Guard("G1", "guard_call", "System.ArgumentNullException.ThrowIfNull(cmd.Payload)", "throw:ArgumentNullException",
                    "src/Handler.cs:21", "CmdHandler.Handle", new GuardExpr("cmd.Payload", "is_null", null)),
                new Guard("G2", "throw_expr", "cmd.Name is null", "throw:InvalidOperationException",
                    "src/Handler.cs:22", "CmdHandler.Handle", new GuardExpr("cmd.Name", "is_null", null)),
                new Guard("G3", "switch", "cmd.Kind == Kind.A", "throw:NotSupportedException",
                    "src/Handler.cs:25", "CmdHandler.Handle", new GuardExpr("cmd.Kind", "==", "Kind.A")),
                new Guard("G4", "switch", "cmd.Kind is Kind.C", "throw:ArgumentException",
                    "src/Handler.cs:30", "CmdHandler.Handle", new GuardExpr("cmd.Kind", "==", "Kind.C"))
            ],
            guards);
    }
}
