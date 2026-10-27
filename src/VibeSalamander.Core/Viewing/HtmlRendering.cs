// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Markdig;

namespace VibeSalamander.Core.Viewing;

/// <summary>Markdown and Jupyter notebooks as HTML pages for the viewer's preview (and the editor's live preview).</summary>
public static partial class HtmlRendering
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions() // tables, task lists, footnotes, auto links, emphasis extras, ...
        .UseEmojiAndSmiley()
        .Build();

    public static bool IsMarkdown(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".md" or ".markdown" or ".mdown" or ".mkd" or ".mdx";

    public static bool IsNotebook(string path) => Path.GetExtension(path).Equals(".ipynb", StringComparison.OrdinalIgnoreCase);

    public static bool IsHtml(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".html" or ".htm" or ".xhtml" or ".shtml";

    /// <summary>Markdown as HTML (the body only).</summary>
    public static string MarkdownBody(string markdown) => Markdown.ToHtml(markdown, Pipeline);

    /// <summary>
    /// A whole page: the body with styles for the light or dark theme. Relative links and pictures are resolved from
    /// <paramref name="baseDirectory"/>. A content security policy blocks scripts, and remote pictures and styles unless
    /// <paramref name="allowRemote"/>.
    /// </summary>
    public static string Page(string title, string body, bool dark, string? baseDirectory, bool allowRemote)
    {
        string remote = allowRemote ? " https: http:" : string.Empty;
        string baseTag = baseDirectory == null ? string.Empty
            : $"<base href=\"{WebUtility.HtmlEncode(new Uri(baseDirectory.TrimEnd('\\') + "\\").AbsoluteUri)}\">";
        string colors = dark
            ? "--bg:#1e1e1e;--fg:#e4e4e4;--muted:#a0a0a0;--code:#2b2b2b;--border:#3c3c3c;--link:#6cb6ff;--out:#252526"
            : "--bg:#ffffff;--fg:#1f1f1f;--muted:#6b6b6b;--code:#f4f4f4;--border:#dddddd;--link:#0b62c4;--out:#fafafa";
        return $$"""
            <!DOCTYPE html>
            <html><head><meta charset="utf-8">
            <meta http-equiv="Content-Security-Policy" content="default-src 'none'; img-src file: data:{{remote}}; style-src 'unsafe-inline' file:{{remote}}; font-src file: data:{{remote}}; media-src file: data:{{remote}}">
            {{baseTag}}
            <title>{{WebUtility.HtmlEncode(title)}}</title>
            <style>
            :root{ {{colors}} }
            html{background:var(--bg);color:var(--fg)}
            body{font:15px/1.6 "Segoe UI Variable Text","Segoe UI",sans-serif;max-width:920px;margin:0 auto;padding:24px 32px 64px}
            a{color:var(--link)} img{max-width:100%}
            h1,h2{border-bottom:1px solid var(--border);padding-bottom:.3em}
            code,pre{font-family:"Cascadia Mono",Consolas,monospace;font-size:13px;background:var(--code);border-radius:4px}
            code{padding:.15em .35em} pre{padding:12px;overflow:auto} pre code{padding:0;background:none}
            blockquote{margin:0;padding:0 1em;color:var(--muted);border-left:4px solid var(--border)}
            table{border-collapse:collapse} th,td{border:1px solid var(--border);padding:6px 12px}
            hr{border:0;border-top:1px solid var(--border)}
            .cell{margin:18px 0} .prompt{color:var(--muted);font:12px "Cascadia Mono",Consolas,monospace;margin-bottom:4px}
            .output{background:var(--out);border-left:3px solid var(--border);padding:8px 12px;margin-top:6px;overflow:auto}
            .output pre{background:none;padding:0;margin:0} .error{color:#e05050}
            </style></head><body>
            {{body}}
            </body></html>
            """;
    }

    /// <summary>A Markdown file as a page.</summary>
    public static string MarkdownPage(string path, string markdown, bool dark, bool allowRemote) =>
        Page(Path.GetFileName(path), MarkdownBody(markdown), dark, Path.GetDirectoryName(path), allowRemote);

    /// <summary>A Jupyter notebook (.ipynb) as a page: Markdown cells, code cells with their outputs (text, pictures, tables).</summary>
    public static string NotebookPage(string path, string json, bool dark, bool allowRemote)
    {
        var body = new StringBuilder();
        using JsonDocument document = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        JsonElement root = document.RootElement;
        if (!root.TryGetProperty("cells", out JsonElement cells) || cells.ValueKind != JsonValueKind.Array)
            throw new JsonException("This isn't a Jupyter notebook: it has no cells.");
        foreach (JsonElement cell in cells.EnumerateArray())
        {
            string type = cell.TryGetProperty("cell_type", out JsonElement t) ? t.GetString() ?? "code" : "code";
            string source = Joined(cell, "source");
            body.Append("<div class=\"cell\">");
            if (type == "markdown")
            {
                body.Append(MarkdownBody(source));
            }
            else if (type == "code")
            {
                string count = cell.TryGetProperty("execution_count", out JsonElement n) && n.ValueKind == JsonValueKind.Number ? n.GetRawText() : " ";
                body.Append($"<div class=\"prompt\">In [{count}]:</div><pre><code>{WebUtility.HtmlEncode(source)}</code></pre>");
                if (cell.TryGetProperty("outputs", out JsonElement outputs) && outputs.ValueKind == JsonValueKind.Array)
                    foreach (JsonElement output in outputs.EnumerateArray())
                        AppendOutput(body, output);
            }
            else
            {
                body.Append($"<pre><code>{WebUtility.HtmlEncode(source)}</code></pre>");
            }
            body.Append("</div>");
        }
        return Page(Path.GetFileName(path), body.ToString(), dark, Path.GetDirectoryName(path), allowRemote);
    }

    private static void AppendOutput(StringBuilder body, JsonElement output)
    {
        string kind = output.TryGetProperty("output_type", out JsonElement k) ? k.GetString() ?? string.Empty : string.Empty;
        body.Append("<div class=\"output\">");
        switch (kind)
        {
            case "stream":
                body.Append($"<pre>{WebUtility.HtmlEncode(StripAnsi(Joined(output, "text")))}</pre>");
                break;
            case "error":
                string traceback = output.TryGetProperty("traceback", out JsonElement tb) && tb.ValueKind == JsonValueKind.Array
                    ? string.Join("\n", tb.EnumerateArray().Select(e => e.GetString()))
                    : $"{Text(output, "ename")}: {Text(output, "evalue")}";
                body.Append($"<pre class=\"error\">{WebUtility.HtmlEncode(StripAnsi(traceback))}</pre>");
                break;
            default: // execute_result, display_data
                if (!output.TryGetProperty("data", out JsonElement data))
                    break;
                if (data.TryGetProperty("image/png", out JsonElement png))
                    body.Append($"<img src=\"data:image/png;base64,{Base64(png)}\">");
                else if (data.TryGetProperty("image/jpeg", out JsonElement jpeg))
                    body.Append($"<img src=\"data:image/jpeg;base64,{Base64(jpeg)}\">");
                else if (data.TryGetProperty("image/svg+xml", out JsonElement svg))
                    body.Append($"<img src=\"data:image/svg+xml;base64,{Convert.ToBase64String(Encoding.UTF8.GetBytes(JoinedValue(svg)))}\">");
                else if (data.TryGetProperty("text/html", out JsonElement html))
                    body.Append(JoinedValue(html)); // tables of data frames (scripts don't run: the page doesn't allow them)
                else if (data.TryGetProperty("text/markdown", out JsonElement md))
                    body.Append(MarkdownBody(JoinedValue(md)));
                else if (data.TryGetProperty("text/plain", out JsonElement plain))
                    body.Append($"<pre>{WebUtility.HtmlEncode(StripAnsi(JoinedValue(plain)))}</pre>");
                break;
        }
        body.Append("</div>");
    }

    private static string Text(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) ? v.ToString() : string.Empty;

    /// <summary>Notebook text is a string or an array of lines.</summary>
    private static string Joined(JsonElement e, string name) => e.TryGetProperty(name, out JsonElement v) ? JoinedValue(v) : string.Empty;

    private static string JoinedValue(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Array => string.Concat(v.EnumerateArray().Select(x => x.GetString())),
        JsonValueKind.String => v.GetString() ?? string.Empty,
        _ => v.GetRawText(),
    };

    private static string Base64(JsonElement v) => JoinedValue(v).Replace("\n", string.Empty);

    /// <summary>Terminal colors in outputs ("\x1b[31m") are left out.</summary>
    public static string StripAnsi(string text) => AnsiCodes().Replace(text, string.Empty);

    [GeneratedRegex(@"\x1B\[[0-9;]*[A-Za-z]")]
    private static partial Regex AnsiCodes();
}
