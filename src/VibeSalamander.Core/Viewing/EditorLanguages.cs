// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Viewing;

/// <summary>The editor's languages (Monaco's names for them): by file name, and the list to choose from.</summary>
public static class EditorLanguages
{
    private static readonly Dictionary<string, string> ByExtension = new(StringComparer.OrdinalIgnoreCase)
    {
        [".txt"] = "plaintext", [".log"] = "plaintext",
        [".cs"] = "csharp", [".csx"] = "csharp", [".vb"] = "vb", [".fs"] = "fsharp", [".fsx"] = "fsharp",
        [".js"] = "javascript", [".mjs"] = "javascript", [".cjs"] = "javascript", [".jsx"] = "javascript",
        [".ts"] = "typescript", [".mts"] = "typescript", [".tsx"] = "typescript",
        [".json"] = "json", [".jsonc"] = "json", [".geojson"] = "json", [".webmanifest"] = "json", [".ipynb"] = "json", [".har"] = "json",
        [".xml"] = "xml", [".xaml"] = "xml", [".xsd"] = "xml", [".xsl"] = "xml", [".xslt"] = "xml", [".svg"] = "xml", [".config"] = "xml",
        [".csproj"] = "xml", [".vbproj"] = "xml", [".fsproj"] = "xml", [".props"] = "xml", [".targets"] = "xml", [".resx"] = "xml",
        [".nuspec"] = "xml", [".plist"] = "xml", [".manifest"] = "xml", [".rss"] = "xml", [".kml"] = "xml", [".gpx"] = "xml",
        [".yaml"] = "yaml", [".yml"] = "yaml",
        [".md"] = "markdown", [".markdown"] = "markdown", [".mdx"] = "markdown",
        [".html"] = "html", [".htm"] = "html", [".xhtml"] = "html", [".vue"] = "html", [".razor"] = "razor", [".cshtml"] = "razor",
        [".css"] = "css", [".scss"] = "scss", [".sass"] = "scss", [".less"] = "less",
        [".py"] = "python", [".pyw"] = "python", [".pyi"] = "python",
        [".ps1"] = "powershell", [".psm1"] = "powershell", [".psd1"] = "powershell",
        [".bat"] = "bat", [".cmd"] = "bat", [".sh"] = "shell", [".bash"] = "shell", [".zsh"] = "shell",
        [".sql"] = "sql", [".java"] = "java", [".kt"] = "kotlin", [".kts"] = "kotlin", [".scala"] = "scala",
        [".go"] = "go", [".rs"] = "rust", [".swift"] = "swift", [".dart"] = "dart",
        [".c"] = "c", [".h"] = "cpp", [".cpp"] = "cpp", [".cc"] = "cpp", [".cxx"] = "cpp", [".hpp"] = "cpp", [".hxx"] = "cpp", [".ino"] = "cpp",
        [".m"] = "objective-c", [".php"] = "php", [".rb"] = "ruby", [".lua"] = "lua", [".pl"] = "perl", [".pm"] = "perl",
        [".r"] = "r", [".jl"] = "julia", [".ex"] = "elixir", [".exs"] = "elixir", [".clj"] = "clojure", [".coffee"] = "coffeescript",
        [".ini"] = "ini", [".cfg"] = "ini", [".inf"] = "ini", [".reg"] = "ini", [".toml"] = "ini", [".editorconfig"] = "ini", [".gitconfig"] = "ini",
        [".dockerfile"] = "dockerfile", [".graphql"] = "graphql", [".gql"] = "graphql", [".proto"] = "protobuf", [".tf"] = "hcl", [".hcl"] = "hcl",
        [".sol"] = "sol", [".pas"] = "pascal", [".dpr"] = "pascal", [".tcl"] = "tcl", [".twig"] = "twig", [".bicep"] = "bicep",
    };

    private static readonly Dictionary<string, string> ByName = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Dockerfile"] = "dockerfile", ["Makefile"] = "shell", [".gitignore"] = "ini", [".gitattributes"] = "ini",
        [".env"] = "ini", ["CMakeLists.txt"] = "plaintext",
    };

    /// <summary>The language of a file ("plaintext" when it isn't known).</summary>
    public static string For(string path)
    {
        string name = Path.GetFileName(path);
        if (ByName.TryGetValue(name, out string? byName))
            return byName;
        return ByExtension.TryGetValue(Path.GetExtension(path), out string? language) ? language : "plaintext";
    }

    /// <summary>The languages to choose from, plain text first.</summary>
    public static IReadOnlyList<string> All { get; } =
        new[] { "plaintext" }.Concat(ByExtension.Values.Concat(ByName.Values).Distinct().Where(l => l != "plaintext").OrderBy(l => l, StringComparer.Ordinal)).ToList();

    /// <summary>The structure the editor checks the language against (JSON, XML, YAML), or null.</summary>
    public static StructuredFormat? Checked(string language) => language switch
    {
        "json" => StructuredFormat.Json,
        "xml" => StructuredFormat.Xml,
        "yaml" => StructuredFormat.Yaml,
        _ => null,
    };

    /// <summary>The live preview for a language: Markdown, HTML or SVG (null: none).</summary>
    public static string? Preview(string language, string? path = null) =>
        language is "markdown" or "html" ? language
        : path != null && path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) ? "svg"
        : null;
}
