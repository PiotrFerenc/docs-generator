using System.Xml.Linq;
using static DocGen.Confluence.Tests.Golden;

namespace DocGen.Confluence.Tests;

public class StorageConverterTests
{
    static StorageResult ConvertGolden(string path, string mode = "macro", string macro = "mermaid-cloud") =>
        new StorageConverter("DOCS", mode, macro).Convert(File.ReadAllText(Path.Combine(Dir, path)), path, Titles(Manifest()));

    static IEnumerable<XElement> Macros(XElement x, string name) =>
        x.Descendants(Ac + "structured-macro").Where(m => (string?)m.Attribute(Ac + "name") == name);

    [Fact]
    public void AllGoldenPages_AreWellFormed_WithNoticeAndNoFrontmatter()
    {
        var manifest = Manifest();
        Assert.Equal(7, manifest.Pages.Count);
        foreach (var page in manifest.Pages)
        {
            var result = ConvertGolden(page.Path);
            var x = Parse(result.Xhtml); // throws if not well-formed
            Assert.DoesNotContain("page_id", result.Xhtml);
            Assert.DoesNotContain("&#", result.Xhtml); // Polish letters stay UTF-8
            var info = Assert.Single(Macros(x, "info"));
            Assert.Contains("Strona generowana automatycznie", info.Value);
            Assert.NotEmpty(x.Elements("h1"));
            Assert.NotEmpty(x.Descendants("table"));
        }
    }

    [Fact]
    public void RequestPayout_LinksTablesAndCode()
    {
        var result = ConvertGolden("modules/Payments/use-cases/RequestPayout.md");
        Assert.Empty(result.Warnings);
        var x = Parse(result.Xhtml);

        var links = x.Descendants(Ac + "link").Select(l => (
            Title: (string?)l.Element(Ri + "page")?.Attribute(Ri + "content-title"),
            Space: (string?)l.Element(Ri + "page")?.Attribute(Ri + "space-key"),
            Label: l.Element(Ac + "plain-text-link-body")?.Value)).ToList();
        Assert.Contains(("Payments / Encja: Wypłata", "DOCS", "wypłatę"), links);
        Assert.Contains(links, l => l.Title == "Reguły globalne / Walidacja komend");
        Assert.Contains(links, l => l.Title == "Proces: Wypłata po rozliczeniu zlecenia skupu");
        Assert.Contains(links, l => l.Title == "Payments / Start wypłaty po rozliczeniu zlecenia");

        var hrefs = x.Descendants("a").Select(a => (string?)a.Attribute("href")).ToList();
        Assert.Contains("https://git.example/skup/-/blob/a1b2c3d/src/Payments.Application/RequestPayout.cs#L22", hrefs);

        var rules = x.Descendants("table").First();
        Assert.Equal(["#", "Reguła", "Skutek niespełnienia"], rules.Element("thead")!.Descendants("th").Select(t => t.Value));
        Assert.Equal(7, rules.Element("tbody")!.Elements("tr").Count());
        Assert.Contains(x.Descendants("code"), c => c.Value == "Payouts.Enabled");
        Assert.Contains(x.Descendants("strong"), s => s.Value == "Scenariusz główny:");
        Assert.Contains(x.Descendants("em"), s => s.Value == "Oczekująca");
        Assert.Single(x.Elements("hr"));
        Assert.NotEmpty(x.Descendants("ul").Elements("li"));
    }

    [Fact]
    public void UnknownRelativeLink_IsPlainTextWithWarning()
    {
        var result = new StorageConverter("DOCS").Convert("See [missing](../x/Nope.md) & <b>.", "a/b.md", new Dictionary<string, string>());
        Assert.Equal("<p>See missing &amp; &lt;b&gt;.</p>", result.Xhtml);
        Assert.Contains(result.Warnings, w => w.Contains("../x/Nope.md"));
    }

    [Theory]
    [InlineData("modules/Payments/use-cases/RequestPayout.md", "../entities/Payout.md", "modules/Payments/entities/Payout.md")]
    [InlineData("modules/Payments/use-cases/RequestPayout.md", "../../../flows/Wyplata-po-rozliczeniu.md#przebieg", "flows/Wyplata-po-rozliczeniu.md")]
    [InlineData("flows/F.md", "G.md", "flows/G.md")]
    [InlineData("flows/F.md", "image.png", null)]
    public void ResolveRelative(string page, string url, string? expected) =>
        Assert.Equal(expected, StorageConverter.ResolveRelative(page, url));

    const string Flow = "flows/Wyplata-po-rozliczeniu.md";

    [Fact]
    public void Mermaid_MacroMode_UsesConfiguredMacroName()
    {
        var x = Parse(ConvertGolden(Flow, "macro", "mermaid-macro").Xhtml);
        var m = Assert.Single(Macros(x, "mermaid-macro"));
        Assert.StartsWith("flowchart TD", m.Element(Ac + "plain-text-body")!.Value);
        Assert.Contains("Klient wybrał przelew?", m.Value);
    }

    [Fact]
    public void Mermaid_CodeMode_UsesCodeMacro()
    {
        var x = Parse(ConvertGolden(Flow, "code").Xhtml);
        var m = Assert.Single(Macros(x, "code"));
        Assert.Equal("mermaid", m.Element(Ac + "parameter")!.Value);
        Assert.StartsWith("flowchart TD", m.Element(Ac + "plain-text-body")!.Value);
        Assert.Empty(Macros(x, "mermaid-cloud"));
    }

    [Fact]
    public void Mermaid_ImageMode_EmbedsAttachment()
    {
        var result = ConvertGolden(Flow, "image");
        var att = Assert.Single(result.Attachments);
        Assert.StartsWith("flowchart TD", att.Source);
        var img = Assert.Single(Parse(result.Xhtml).Descendants(Ac + "image"));
        Assert.Equal(att.FileName, (string?)img.Element(Ri + "attachment")!.Attribute(Ri + "filename"));
    }

    [Fact]
    public void CodeBlock_CDataTerminatorIsSplit()
    {
        var result = new StorageConverter("DOCS").Convert("```csharp\nvar s = \"]]>\";\n```", "a.md", new Dictionary<string, string>());
        var m = Assert.Single(Macros(Parse(result.Xhtml), "code"));
        Assert.Equal("csharp", m.Element(Ac + "parameter")!.Value);
        Assert.Equal("var s = \"]]>\";", m.Element(Ac + "plain-text-body")!.Value);
    }
}
