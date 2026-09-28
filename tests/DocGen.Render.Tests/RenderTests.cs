using System.Text;
using System.Text.RegularExpressions;
using DocGen.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace DocGen.Render.Tests;

public class RenderTests
{
    const string RepoUrl = "https://git.example/skup/-/blob/{commit}/{path}#L{line}";

    static readonly string Root = FindRoot();
    static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "DocGen.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("DocGen.sln not found");
    }

    static CardSet Fixture() => DocGenJson.Read<CardSet>(Path.Combine(Root, "tests/fixtures/cards.golden.json"));
    static RenderResult Build(string? notesDir = null) => PageBuilder.Build(Fixture(), RepoUrl, notesDir);

    public static TheoryData<string> GoldenPages() =>
        new(Directory.GetFiles(Path.Combine(Root, "tests/golden"), "*.md", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Path.Combine(Root, "tests/golden"), f).Replace('\\', '/')).Order());

    [Theory]
    [MemberData(nameof(GoldenPages))]
    public void Golden_page_matches_byte_for_byte(string path)
    {
        var page = Build().Pages.SingleOrDefault(p => p.Page.Path == path);
        Assert.True(page is not null, $"page {path} not rendered");
        var expected = File.ReadAllText(Path.Combine(Root, "tests/golden", path)).Replace("\r\n", "\n");
        if (expected != page.Content)
            Assert.Fail($"{path} differs:\n{Diff(expected, page.Content)}");
    }

    static string Diff(string expected, string actual)
    {
        var e = expected.Split('\n');
        var a = actual.Split('\n');
        var i = 0;
        while (i < e.Length && i < a.Length && e[i] == a[i])
            i++;
        var sb = new StringBuilder($"@@ line {i + 1} @@\n");
        for (var k = Math.Max(0, i - 2); k < i; k++)
            sb.Append("  ").AppendLine(e[k]);
        for (var k = i; k < Math.Min(e.Length, i + 5); k++)
            sb.Append("- ").AppendLine(e[k]);
        for (var k = i; k < Math.Min(a.Length, i + 5); k++)
            sb.Append("+ ").AppendLine(a[k]);
        return sb.ToString();
    }

    [Fact]
    public void Fixture_renders_without_warnings() => Assert.Empty(Build().Warnings);

    [Fact]
    public void Every_relative_link_resolves_to_a_rendered_page()
    {
        var result = Build();
        var paths = result.Pages.Select(p => p.Page.Path).ToHashSet();
        var broken = new List<string>();
        foreach (var page in result.Pages)
            foreach (Match m in Regex.Matches(page.Content, @"\]\(([^)]+)\)"))
            {
                var link = m.Groups[1].Value;
                if (link.StartsWith("http"))
                    continue;
                // Resolve against a fake root so "../" never escapes into the real filesystem.
                const string fake = "/r/";
                var target = Path.GetFullPath(Path.Combine(fake + Path.GetDirectoryName(page.Page.Path), link));
                if (!target.StartsWith(fake) || !paths.Contains(target[fake.Length..]))
                    broken.Add($"{page.Page.Path} -> {link}");
            }
        Assert.Empty(broken);
    }

    [Fact]
    public void Unknown_page_id_renders_label_and_warns()
    {
        var cards = Fixture();
        cards.Entities[0].Rules.Add("Zobacz [[page:T:Missing|brakująca strona]].");
        var result = PageBuilder.Build(cards, RepoUrl, null);
        Assert.Contains("- Zobacz brakująca strona.", result.Pages.Single(p => p.Page.Kind == "entity").Content);
        Assert.Contains(result.Warnings, w => w.Contains("T:Missing"));
    }

    [Fact]
    public void Manifest_kinds_and_parents()
    {
        var pages = Build().Manifest.Pages.ToDictionary(p => p.Path);
        Assert.Equal(11, pages.Count);
        Assert.Equal(("system", null), (pages["index.md"].Kind, pages["index.md"].ParentPageId));
        Assert.Equal(("glossary", "system"), (pages["glossary.md"].Kind, pages["glossary.md"].ParentPageId));
        Assert.Equal(("module", "system"), (pages["modules/Payments/index.md"].Kind, pages["modules/Payments/index.md"].ParentPageId));
        Assert.Equal(("use_case", "module:Orders"), (pages["modules/Orders/use-cases/SettleOrder.md"].Kind, pages["modules/Orders/use-cases/SettleOrder.md"].ParentPageId));
        Assert.Equal(("entity", "module:Payments"), (pages["modules/Payments/entities/Payout.md"].Kind, pages["modules/Payments/entities/Payout.md"].ParentPageId));
        Assert.Equal(("flow", "system"), (pages["flows/Wyplata-po-rozliczeniu.md"].Kind, pages["flows/Wyplata-po-rozliczeniu.md"].ParentPageId));
        Assert.Equal(("global_rule", "system"), (pages["global-rules/ValidationBehavior.md"].Kind, pages["global-rules/ValidationBehavior.md"].ParentPageId));
        Assert.Equal("Payments / Encja: Wypłata", pages["modules/Payments/entities/Payout.md"].Title);
        Assert.All(pages.Values, p => Assert.Matches("^[0-9a-f]{64}$", p.Hash));
        Assert.Equal(pages.Count, pages.Values.Select(p => p.Title).Distinct().Count());
    }

    [Fact]
    public void Without_system_card_parents_are_null_for_top_level()
    {
        var cards = Fixture() with { System = null, Modules = [] };
        var pages = PageBuilder.Build(cards, RepoUrl, null).Manifest.Pages;
        Assert.All(pages, p => Assert.Null(p.ParentPageId));
    }

    // ── Filesystem behaviour via the DI-facing renderer ─────────────────────

    sealed class TempRoot : IDisposable
    {
        public string Dir { get; } = Directory.CreateTempSubdirectory("docgen-render-").FullName;
        public string Out => Path.Combine(Dir, "docs");
        public string Notes => Path.Combine(Dir, "notes");
        public void Dispose() => Directory.Delete(Dir, true);

        public Manifest Render(CardSet cards) =>
            new MarkdownRenderer(
                Options.Create(new RenderOptions { RepoUrlTemplate = RepoUrl }),
                Options.Create(new GeneratorOptions { OutputDir = "docs", NotesDir = "notes" }),
                new DocGenPaths(Dir),
                NullLogger<MarkdownRenderer>.Instance).RenderAsync(cards, CancellationToken.None).Result;
    }

    [Fact]
    public void Notes_file_replaces_placeholder()
    {
        using var root = new TempRoot();
        var notes = Path.Combine(root.Notes, "modules/Payments/use-cases/RequestPayout.notes.md");
        Directory.CreateDirectory(Path.GetDirectoryName(notes)!);
        File.WriteAllText(notes, "Limit uzgodniony z działem finansów.\r\n");
        root.Render(Fixture());

        var page = File.ReadAllText(Path.Combine(root.Out, "modules/Payments/use-cases/RequestPayout.md"));
        Assert.EndsWith("## Uwagi zespołu\n\nLimit uzgodniony z działem finansów.\n", page);
        Assert.DoesNotContain("(treść dołączana z", page);
        Assert.Contains("(treść dołączana z `ConfirmPayout.notes.md`", File.ReadAllText(Path.Combine(root.Out, "modules/Payments/use-cases/ConfirmPayout.md")));
    }

    [Fact]
    public void Stale_files_deleted_only_when_listed_in_old_manifest_and_unchanged_files_untouched()
    {
        using var root = new TempRoot();
        Directory.CreateDirectory(Path.Combine(root.Out, "old"));
        File.WriteAllText(Path.Combine(root.Out, "old/Gone.md"), "stale");
        File.WriteAllText(Path.Combine(root.Out, "hand-written.md"), "keep");
        DocGenJson.Write(Path.Combine(root.Out, ".manifest.json"),
            new Manifest("old", [new("x", "old/Gone.md", "Gone", "use_case", null, "h"), new("y", "../outside.md", "Out", "use_case", null, "h")]));
        File.WriteAllText(Path.Combine(root.Dir, "outside.md"), "outside");

        var manifest = root.Render(Fixture());

        Assert.False(File.Exists(Path.Combine(root.Out, "old/Gone.md")));
        Assert.True(File.Exists(Path.Combine(root.Out, "hand-written.md")));
        Assert.True(File.Exists(Path.Combine(root.Dir, "outside.md")));
        Assert.Equal(manifest, DocGenJson.Read<Manifest>(Path.Combine(root.Out, ".manifest.json")), ManifestComparer.Instance);

        var file = Path.Combine(root.Out, "index.md");
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(file, past);
        root.Render(Fixture());
        Assert.Equal(past, File.GetLastWriteTimeUtc(file));
    }

    sealed class ManifestComparer : IEqualityComparer<Manifest>
    {
        public static readonly ManifestComparer Instance = new();
        public bool Equals(Manifest? a, Manifest? b) => a!.Commit == b!.Commit && a.Pages.SequenceEqual(b.Pages);
        public int GetHashCode(Manifest m) => m.Commit.GetHashCode();
    }
}
