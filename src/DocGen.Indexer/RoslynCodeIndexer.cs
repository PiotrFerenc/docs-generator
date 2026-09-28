using System.ComponentModel;
using System.Diagnostics;
using DocGen.Contracts;
using Microsoft.CodeAnalysis.MSBuild;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DocGen.Indexer;

public sealed class RoslynCodeIndexer(IOptions<IndexerOptions> options, DocGenPaths paths, ILogger<RoslynCodeIndexer> logger) : ICodeIndexer
{
    public async Task<CodeIndex> IndexAsync(CancellationToken ct)
    {
        var o = options.Value;
        var solutionPath = paths.Resolve(o.SolutionPath);
        var root = Path.GetDirectoryName(solutionPath)!;

        using var workspace = MSBuildWorkspace.Create();
        using var _ = workspace.RegisterWorkspaceFailedHandler(e => logger.LogWarning("workspace: {Message}", e.Diagnostic.Message));
        var solution = await workspace.OpenSolutionAsync(solutionPath, cancellationToken: ct);
        return await new IndexBuilder(solution, root, o).BuildAsync(string.IsNullOrWhiteSpace(o.Commit) ? GitCommit(root) : o.Commit, ct);
    }

    static string GitCommit(string dir)
    {
        try
        {
            using var git = Process.Start(new ProcessStartInfo("git", "rev-parse --short HEAD")
            {
                WorkingDirectory = dir,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            })!;
            var sha = git.StandardOutput.ReadToEnd().Trim();
            git.WaitForExit();
            return git.ExitCode == 0 && sha.Length > 0 ? sha : "unknown";
        }
        catch (Win32Exception)
        {
            return "unknown"; // git not installed
        }
    }
}
