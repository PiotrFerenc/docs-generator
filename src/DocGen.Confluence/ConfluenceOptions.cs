using DocGen.Contracts;

namespace DocGen.Confluence;

/// <summary>Section "Confluence". BaseAddress: Cloud "https://acme.atlassian.net/wiki" or Data Center base URL.</summary>
public sealed class ConfluenceOptions : HttpClientOptions
{
    public string SpaceKey { get; set; } = "";
    public string RootPageTitle { get; set; } = "Dokumentacja systemu (generowana)";
    /// <summary>macro (Marketplace Mermaid app) | code (plain code block) | image (PNG rendered by mmdc, uploaded as attachment)</summary>
    public string MermaidMode { get; set; } = "macro";
    public string MermaidMacroName { get; set; } = "mermaid-cloud";
    /// <summary>mermaid-cli executable, used only when MermaidMode = image.</summary>
    public string MmdcPath { get; set; } = "mmdc";
}
