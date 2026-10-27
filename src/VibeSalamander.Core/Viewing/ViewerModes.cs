// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

namespace VibeSalamander.Core.Viewing;

/// <summary>How the viewer shows a file.</summary>
public enum ViewerMode
{
    /// <summary>Lines of text, any size.</summary>
    Text,

    /// <summary>Bytes in hex and as characters, any size.</summary>
    Hex,

    /// <summary>JSON, XML or YAML as a tree.</summary>
    Tree,

    /// <summary>Markdown, a Jupyter notebook or a web page, rendered.</summary>
    Preview,

    Image,
    Pdf,

    /// <summary>Audio or video: plays it, with its details.</summary>
    Media,
}

/// <summary>Which modes fit a file, and which one it opens in.</summary>
public static class ViewerModes
{
    private static readonly HashSet<string> Images = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".jpe", ".jfif", ".png", ".gif", ".bmp", ".dib", ".tif", ".tiff", ".ico", ".cur", ".webp",
        ".heic", ".heif", ".avif", ".jxl", ".svg", ".wdp", ".jxr",
        // camera RAW (through Windows' RAW Image Extension)
        ".dng", ".cr2", ".cr3", ".crw", ".nef", ".nrw", ".arw", ".srf", ".sr2", ".orf", ".rw2", ".raf", ".pef", ".srw", ".3fr", ".erf", ".kdc", ".mrw", ".x3f",
    };

    private static readonly HashSet<string> Media = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".mpg", ".mpeg", ".m2ts", ".mts", ".ts", ".3gp", ".flv", ".ogv",
        ".mp3", ".wav", ".flac", ".ogg", ".oga", ".opus", ".m4a", ".aac", ".wma", ".aiff", ".aif", ".mid", ".midi", ".amr",
    };

    public static bool IsImage(string path) => Images.Contains(Path.GetExtension(path));

    public static bool IsMedia(string path) => Media.Contains(Path.GetExtension(path));

    public static bool IsPdf(string path) => Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>The rendered mode for a file (image, PDF, media, preview, tree), or null when there is none.</summary>
    public static ViewerMode? Rendered(string path)
    {
        if (IsImage(path))
            return ViewerMode.Image;
        if (IsPdf(path))
            return ViewerMode.Pdf;
        if (IsMedia(path))
            return ViewerMode.Media;
        if (HtmlRendering.IsMarkdown(path) || HtmlRendering.IsNotebook(path) || HtmlRendering.IsHtml(path))
            return ViewerMode.Preview;
        if (Structured.FormatOf(path) != null)
            return ViewerMode.Tree;
        return null;
    }

    /// <summary>
    /// The modes offered for a file, in order: text and hex always, and its rendered mode (an SVG is an image and
    /// also XML, so it gets both).
    /// </summary>
    public static IReadOnlyList<ViewerMode> For(string path)
    {
        var modes = new List<ViewerMode>();
        if (Rendered(path) is { } rendered)
            modes.Add(rendered);
        if (IsXmlImage(path))
            modes.Add(ViewerMode.Tree);
        modes.Add(ViewerMode.Text);
        modes.Add(ViewerMode.Hex);
        return modes;
    }

    private static bool IsXmlImage(string path) => IsImage(path) && Structured.FormatOf(path) == StructuredFormat.Xml;

    /// <summary>
    /// The mode a file opens in: pictures, PDFs, media, Markdown, notebooks and web pages rendered; JSON, XML and YAML
    /// as text (the tree is one key away); other files as text, or in hex when they look binary.
    /// </summary>
    public static ViewerMode Initial(string path, bool looksBinary)
    {
        ViewerMode? rendered = Rendered(path);
        if (rendered is ViewerMode.Image or ViewerMode.Pdf or ViewerMode.Media or ViewerMode.Preview)
            return rendered.Value;
        return looksBinary ? ViewerMode.Hex : ViewerMode.Text;
    }
}
