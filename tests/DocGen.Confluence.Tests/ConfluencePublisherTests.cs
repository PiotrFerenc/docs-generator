using DocGen.Contracts;
using Microsoft.Extensions.Options;

namespace DocGen.Confluence.Tests;

public class ConfluencePublisherTests : IDisposable
{
    const string Root = "Dokumentacja systemu (generowana)";
    readonly string work = Directory.CreateTempSubdirectory("docgen-confluence-").FullName;
    readonly FakeConfluence fake = new();

    public void Dispose() => Directory.Delete(work, true);

    ConfluencePublisher Publisher(IHttpClientFactory? http = null, string mermaid = "macro") => new(
        http ?? fake,
        Options.Create(new ConfluenceOptions { BaseAddress = "https://fake.example/wiki", SpaceKey = "DOCS", RootPageTitle = Root, MermaidMode = mermaid }),
        new DocGenPaths(work),
        Options.Create(new GeneratorOptions { WorkDir = ".docgen" }));

    /// <summary>Golden manifest with one nested page listed before its parent (ConfirmPayout under RequestPayout).</summary>
    static Manifest NestedManifest()
    {
        var m = Golden.Manifest();
        var request = m.Pages.Single(p => p.Path.EndsWith("RequestPayout.md"));
        var confirm = m.Pages.Single(p => p.Path.EndsWith("ConfirmPayout.md"));
        m.Pages.Remove(confirm);
        m.Pages.Insert(0, confirm with { ParentPageId = request.PageId });
        return m;
    }

    FakeConfluence.Page ByTitle(string title) => fake.Pages.Values.Single(p => p.Title == title);

    [Fact]
    public async Task FirstPublishCreates_SecondIsUnchanged_HashChangeUpdates()
    {
        var manifest = NestedManifest();

        var first = await Publisher().PublishAsync(Golden.Dir, manifest, false, default);
        Assert.Equal((7, 0, 0), (first.Created, first.Updated, first.Unchanged));
        Assert.Equal(8, fake.Pages.Count);
        var root = ByTitle(Root);
        Assert.All(fake.Pages.Values, p => Assert.Contains(ConfluencePublisher.Label, p.Labels));
        var request = ByTitle("Payments / Zlecenie wypłaty dla klienta");
        Assert.Equal(root.Id, request.ParentId);
        Assert.Equal(request.Id, ByTitle("Payments / Potwierdzenie wypłaty przez operatora płatności").ParentId);
        Assert.Equal(manifest.Pages.Single(p => p.Title == request.Title).Hash, (string?)request.Property!["hash"]);
        Assert.Contains("ri:content-title=\"Payments / Encja: Wypłata\"", request.Body);
        Assert.DoesNotContain(first.Messages, m => m.StartsWith("orphan") || m.StartsWith("error"));

        fake.Requests.Clear();
        var second = await Publisher().PublishAsync(Golden.Dir, manifest, false, default);
        Assert.Equal((0, 0, 7), (second.Created, second.Updated, second.Unchanged));
        Assert.Equal(0, fake.Writes);

        var changed = manifest with { Pages = manifest.Pages.Select(p => p.Title == request.Title ? p with { Hash = "new" } : p).ToList() };
        var third = await Publisher().PublishAsync(Golden.Dir, changed, false, default);
        Assert.Equal((0, 1, 6), (third.Created, third.Updated, third.Unchanged));
        Assert.Equal(2, request.Version);
        Assert.Equal("new", (string?)request.Property!["hash"]);
        Assert.Equal(2, request.PropertyVersion);

        // Switching the Mermaid mode rewrites pages even though Markdown hashes did not change.
        var fourth = await Publisher(mermaid: "code").PublishAsync(Golden.Dir, changed, false, default);
        Assert.Equal(7, fourth.Updated);
        Assert.Contains("ac:name=\"code\"", ByTitle("Proces: Wypłata po rozliczeniu zlecenia skupu").Body);
    }

    [Fact]
    public async Task Orphans_AreReportedAcrossSearchPages_NeverDeleted()
    {
        fake.Add("Stara strona", label: ConfluencePublisher.Label);
        fake.Add("Inna przestrzeń", space: "OTHER", label: ConfluencePublisher.Label);
        fake.Add("Ręczna strona");
        var report = await Publisher().PublishAsync(Golden.Dir, Golden.Manifest(), false, default);
        Assert.Equal(["orphan: 'Stara strona' (id 1000) is labelled docgen-generated but not in the manifest; not deleted"],
            report.Messages.Where(m => m.StartsWith("orphan")));
        Assert.Equal(11, fake.Pages.Count);
        Assert.DoesNotContain(fake.Requests, r => r.StartsWith("DELETE"));
    }

    [Fact]
    public async Task ExistingForeignPageWithSameTitle_IsNotOverwritten()
    {
        var foreign = fake.Add("Payments / Encja: Wypłata");
        var report = await Publisher().PublishAsync(Golden.Dir, Golden.Manifest(), false, default);
        Assert.Equal(6, report.Created);
        Assert.Contains(report.Messages, m => m.StartsWith("error: 'Payments / Encja: Wypłata' already exists"));
        Assert.Equal(1, foreign.Version);
        Assert.Empty(foreign.Labels);
    }

    [Fact]
    public async Task RetriesOn429()
    {
        fake.FailNextWith429 = 2;
        var report = await Publisher().PublishAsync(Golden.Dir, Golden.Manifest(), false, default);
        Assert.Equal(7, report.Created);
    }

    [Fact]
    public async Task DryRun_WritesXhtml_WithoutHttp()
    {
        var report = await Publisher(new ThrowingFactory()).PublishAsync(Golden.Dir, Golden.Manifest(), true, default);
        Assert.Equal(7, report.Created);
        Assert.StartsWith("dry-run:", report.Messages[0]);
        var file = Path.Combine(work, ".docgen", "confluence-dry-run", "modules", "Payments", "use-cases", "RequestPayout.xhtml");
        Golden.Parse(File.ReadAllText(file));
        Assert.Equal(7, Directory.GetFiles(Path.Combine(work, ".docgen", "confluence-dry-run"), "*.xhtml", SearchOption.AllDirectories).Length);
    }

    sealed class ThrowingFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("dry-run must not create an HttpClient");
    }
}
