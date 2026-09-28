using System.Text.RegularExpressions;
using System.Xml.Linq;
using DocGen.Contracts;

namespace DocGen.Confluence.Tests;

static class Golden
{
    public static readonly XNamespace Ac = "http://atlassian.com/content";
    public static readonly XNamespace Ri = "http://atlassian.com/resource/identifier";

    public static string Dir { get; } = FindDir();

    static string FindDir()
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            if (Directory.Exists(Path.Combine(d.FullName, "tests", "golden")))
                return Path.Combine(d.FullName, "tests", "golden");
        throw new DirectoryNotFoundException("tests/golden");
    }

    /// <summary>tests/golden has no manifest: build one from frontmatter, hash = file content hash.</summary>
    public static Manifest Manifest(string? docsDir = null)
    {
        docsDir ??= Dir;
        var pages = Directory.GetFiles(docsDir, "*.md", SearchOption.AllDirectories).Order().Select(f =>
        {
            var text = File.ReadAllText(f);
            string Front(string key) => Regex.Match(text, $"^{key}: \"?(.*?)\"?$", RegexOptions.Multiline).Groups[1].Value;
            return new ManifestPage(Front("page_id"), Path.GetRelativePath(docsDir, f).Replace('\\', '/'), Front("title"), "page", null,
                Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
        }).ToList();
        return new Manifest("a1b2c3d", pages);
    }

    public static IReadOnlyDictionary<string, string> Titles(Manifest m) => m.Pages.ToDictionary(p => p.Path, p => p.Title);

    public static XElement Parse(string xhtml) =>
        XElement.Parse($"<root xmlns:ac=\"{Ac}\" xmlns:ri=\"{Ri}\">{xhtml}</root>");
}
