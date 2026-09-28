using System.Security.Cryptography;
using System.Text;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace DocGen.Confluence;

/// <summary>Mermaid diagram to render with mmdc and upload as a page attachment (MermaidMode = "image").</summary>
public sealed record MermaidAttachment(string FileName, string Source);

public sealed record StorageResult(string Xhtml, List<MermaidAttachment> Attachments, List<string> Warnings);

/// <summary>Markdown (as produced by DocGen.Render) → Confluence storage format (XHTML).</summary>
public sealed class StorageConverter(string spaceKey, string mermaidMode = "macro", string mermaidMacroName = "mermaid-cloud")
{
    const string GeneratedNotice = "Strona generowana automatycznie";

    static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseYamlFrontMatter().UsePipeTables().Build();

    /// <param name="pagePath">Path of the converted page relative to the docs dir (forward slashes).</param>
    /// <param name="titleByPath">Manifest page path → Confluence title, for resolving relative .md links.</param>
    public StorageResult Convert(string markdown, string pagePath, IReadOnlyDictionary<string, string> titleByPath)
    {
        var ctx = new Ctx(new StringBuilder(), [], [], pagePath, titleByPath);
        var noticeDone = false;
        foreach (var block in Markdown.Parse(markdown, Pipeline))
        {
            if (block is QuoteBlock q && !noticeDone && PlainText(q).Contains(GeneratedNotice))
            {
                noticeDone = true;
                ctx.Out.Append("<ac:structured-macro ac:name=\"info\"><ac:rich-text-body>");
                Blocks(q, ctx, tight: false);
                ctx.Out.Append("</ac:rich-text-body></ac:structured-macro>");
                continue;
            }
            Block(block, ctx, tight: false);
        }
        return new StorageResult(ctx.Out.ToString(), ctx.Attachments, ctx.Warnings);
    }

    sealed record Ctx(StringBuilder Out, List<MermaidAttachment> Attachments, List<string> Warnings,
        string PagePath, IReadOnlyDictionary<string, string> TitleByPath);

    void Blocks(ContainerBlock container, Ctx ctx, bool tight)
    {
        foreach (var b in container)
            Block(b, ctx, tight);
    }

    void Block(Block block, Ctx ctx, bool tight)
    {
        var o = ctx.Out;
        switch (block)
        {
            case YamlFrontMatterBlock:
                break;
            case HeadingBlock h:
                o.Append($"<h{h.Level}>");
                Inlines(h.Inline, ctx);
                o.Append($"</h{h.Level}>");
                break;
            case ParagraphBlock p when tight:
                Inlines(p.Inline, ctx);
                break;
            case ParagraphBlock p:
                o.Append("<p>");
                Inlines(p.Inline, ctx);
                o.Append("</p>");
                break;
            case ListBlock list:
                var tag = list.IsOrdered ? "ol" : "ul";
                o.Append('<').Append(tag).Append('>');
                foreach (var item in list.Cast<ListItemBlock>())
                {
                    o.Append("<li>");
                    Blocks(item, ctx, tight: !list.IsLoose);
                    o.Append("</li>");
                }
                o.Append("</").Append(tag).Append('>');
                break;
            case Table table:
                Table(table, ctx);
                break;
            case FencedCodeBlock code:
                Code(code.Info ?? "", code.Lines.ToString(), ctx);
                break;
            case CodeBlock code:
                Code("", code.Lines.ToString(), ctx);
                break;
            case QuoteBlock q:
                o.Append("<blockquote>");
                Blocks(q, ctx, tight: false);
                o.Append("</blockquote>");
                break;
            case ThematicBreakBlock:
                o.Append("<hr/>");
                break;
            case HtmlBlock html: // raw HTML is not part of the generated format; keep it visible, never inject it
                o.Append("<p>").Append(Esc(html.Lines.ToString())).Append("</p>");
                break;
            case ContainerBlock c:
                Blocks(c, ctx, tight);
                break;
            case LeafBlock leaf when leaf.Inline is not null:
                Inlines(leaf.Inline, ctx);
                break;
        }
    }

    void Table(Table table, Ctx ctx)
    {
        var o = ctx.Out;
        var rows = table.Cast<TableRow>().ToList();
        var header = rows.TakeWhile(r => r.IsHeader).ToList();
        o.Append("<table>");
        if (header.Count > 0)
        {
            o.Append("<thead>");
            header.ForEach(r => Row(r, "th", ctx));
            o.Append("</thead>");
        }
        o.Append("<tbody>");
        rows.Skip(header.Count).ToList().ForEach(r => Row(r, "td", ctx));
        o.Append("</tbody></table>");
    }

    void Row(TableRow row, string cellTag, Ctx ctx)
    {
        ctx.Out.Append("<tr>");
        foreach (var cell in row.Cast<TableCell>())
        {
            ctx.Out.Append('<').Append(cellTag).Append('>');
            Blocks(cell, ctx, tight: true);
            ctx.Out.Append("</").Append(cellTag).Append('>');
        }
        ctx.Out.Append("</tr>");
    }

    void Code(string language, string source, Ctx ctx)
    {
        var o = ctx.Out;
        if (language == "mermaid" && mermaidMode == "macro")
        {
            o.Append($"<ac:structured-macro ac:name=\"{Esc(mermaidMacroName)}\"><ac:plain-text-body>{CData(source)}</ac:plain-text-body></ac:structured-macro>");
            return;
        }
        if (language == "mermaid" && mermaidMode == "image")
        {
            var fileName = $"mermaid-{System.Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source)))[..12]}.png";
            if (ctx.Attachments.All(a => a.FileName != fileName))
                ctx.Attachments.Add(new MermaidAttachment(fileName, source));
            o.Append($"<ac:image><ri:attachment ri:filename=\"{fileName}\"/></ac:image>");
            return;
        }
        o.Append("<ac:structured-macro ac:name=\"code\">");
        if (language.Length > 0)
            o.Append($"<ac:parameter ac:name=\"language\">{Esc(language)}</ac:parameter>");
        o.Append($"<ac:plain-text-body>{CData(source)}</ac:plain-text-body></ac:structured-macro>");
    }

    void Inlines(ContainerInline? container, Ctx ctx)
    {
        if (container is null)
            return;
        var o = ctx.Out;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit:
                    o.Append(Esc(lit.Content.ToString()));
                    break;
                case CodeInline code:
                    o.Append("<code>").Append(Esc(code.Content)).Append("</code>");
                    break;
                case EmphasisInline em:
                    var tag = em.DelimiterCount >= 2 ? "strong" : "em";
                    o.Append('<').Append(tag).Append('>');
                    Inlines(em, ctx);
                    o.Append("</").Append(tag).Append('>');
                    break;
                case LineBreakInline br:
                    o.Append(br.IsHard ? "<br/>" : "\n");
                    break;
                case AutolinkInline auto:
                    var href = auto.IsEmail ? "mailto:" + auto.Url : auto.Url;
                    o.Append($"<a href=\"{Esc(href)}\">{Esc(auto.Url)}</a>");
                    break;
                case LinkInline link:
                    Link(link, ctx);
                    break;
                case HtmlEntityInline entity:
                    o.Append(Esc(entity.Transcoded.ToString()));
                    break;
                case HtmlInline html:
                    o.Append(Esc(html.Tag));
                    break;
                case ContainerInline c:
                    Inlines(c, ctx);
                    break;
            }
        }
    }

    void Link(LinkInline link, Ctx ctx)
    {
        var o = ctx.Out;
        var url = link.Url ?? "";
        if (Uri.TryCreate(url, UriKind.Absolute, out var abs) && abs.Scheme is "http" or "https" or "mailto")
        {
            o.Append($"<a href=\"{Esc(url)}\">");
            Inlines(link, ctx);
            o.Append("</a>");
            return;
        }

        var target = ResolveRelative(ctx.PagePath, url);
        if (target is not null && ctx.TitleByPath.TryGetValue(target, out var title))
        {
            o.Append($"<ac:link><ri:page ri:content-title=\"{Esc(title)}\" ri:space-key=\"{Esc(spaceKey)}\"/>")
             .Append($"<ac:plain-text-link-body>{CData(PlainText(link))}</ac:plain-text-link-body></ac:link>");
            return;
        }

        ctx.Warnings.Add($"{ctx.PagePath}: link target '{url}' is not a published page; rendered as plain text");
        Inlines(link, ctx);
    }

    /// <summary>"../entities/Payout.md#x" relative to "modules/Payments/use-cases/RequestPayout.md" → "modules/Payments/entities/Payout.md".</summary>
    public static string? ResolveRelative(string pagePath, string url)
    {
        var path = url.Split('#', '?')[0];
        if (path.Length == 0 || !path.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
            return null;
        var resolved = new Uri(new Uri("docgen:///" + pagePath), path).AbsolutePath.TrimStart('/');
        return Uri.UnescapeDataString(resolved);
    }

    static string PlainText(MarkdownObject node) => string.Concat(node.Descendants().Select(d => d switch
    {
        LiteralInline l => l.Content.ToString(),
        CodeInline c => c.Content,
        LineBreakInline => " ",
        _ => ""
    }));

    static string Esc(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    static string CData(string s) => "<![CDATA[" + s.Replace("]]>", "]]]]><![CDATA[>") + "]]>";
}
