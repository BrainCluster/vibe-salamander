// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using YamlDotNet.Core;
using YamlDotNet.RepresentationModel;

namespace VibeSalamander.Core.Viewing;

/// <summary>Formats with a tree structure.</summary>
public enum StructuredFormat
{
    Json,
    Xml,
    Yaml,
}

public enum TreeNodeKind
{
    Document,
    Object,
    Array,
    String,
    Number,
    Boolean,
    Null,
    Element,
    Attribute,
    Text,
    Comment,
}

/// <summary>One node of a JSON, XML or YAML tree: its name, its value (for leaves) and where it is in the text.</summary>
public sealed class TreeNode
{
    public TreeNode(string label, TreeNodeKind kind, string? value = null, int line = 0)
    {
        Label = label;
        Kind = kind;
        Value = value;
        Line = line;
    }

    /// <summary>The key, index ("[3]"), element name ("&lt;item&gt;") or attribute ("@id").</summary>
    public string Label { get; }

    public TreeNodeKind Kind { get; }

    /// <summary>A leaf's value ("true", "42", "text"); a container's summary is <see cref="Summary"/>.</summary>
    public string? Value { get; }

    /// <summary>The line in the text, from 1 (0 = unknown).</summary>
    public int Line { get; }

    public List<TreeNode> Children { get; } = new();

    /// <summary>What a collapsed container shows: "{3}", "[12]", or an element's text.</summary>
    public string Summary => Kind switch
    {
        TreeNodeKind.Object => $"{{{Children.Count}}}",
        TreeNodeKind.Array => $"[{Children.Count}]",
        _ => Value ?? string.Empty,
    };

    /// <summary>All nodes below (depth first), for finding.</summary>
    public IEnumerable<TreeNode> Descendants()
    {
        foreach (TreeNode child in Children)
        {
            yield return child;
            foreach (TreeNode below in child.Descendants())
                yield return below;
        }
    }
}

/// <summary>A problem in a document: the parser's message and where (from 1; 0 = unknown).</summary>
public sealed record ParseProblem(string Message, int Line, int Column);

/// <summary>JSON, XML and YAML: recognizing them, their trees, checking them and formatting them.</summary>
public static class Structured
{
    private static readonly string[] JsonExtensions = { ".json", ".jsonc", ".geojson", ".webmanifest", ".har", ".jsonl" };
    private static readonly string[] XmlExtensions =
    {
        ".xml", ".xaml", ".xsd", ".xsl", ".xslt", ".svg", ".config", ".csproj", ".vbproj", ".fsproj", ".props", ".targets",
        ".resx", ".nuspec", ".plist", ".rss", ".atom", ".kml", ".gpx", ".manifest", ".wsdl", ".xlf", ".dtsx", ".storyboard",
    };
    private static readonly string[] YamlExtensions = { ".yaml", ".yml" };

    public static StructuredFormat? FormatOf(string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (JsonExtensions.Contains(ext))
            return StructuredFormat.Json;
        if (XmlExtensions.Contains(ext))
            return StructuredFormat.Xml;
        if (YamlExtensions.Contains(ext))
            return StructuredFormat.Yaml;
        return null;
    }

    private static readonly JsonDocumentOptions JsonOptions = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        MaxDepth = 512,
    };

    /// <summary>The document's tree, or the first problem found.</summary>
    public static (TreeNode? Root, ParseProblem? Problem) Parse(string text, StructuredFormat format)
    {
        try
        {
            return format switch
            {
                StructuredFormat.Json => (ParseJson(text), null),
                StructuredFormat.Xml => (ParseXml(text), null),
                _ => (ParseYaml(text), null),
            };
        }
        catch (JsonException ex)
        {
            return (null, new ParseProblem(CleanJsonMessage(ex.Message), (int)(ex.LineNumber ?? -1) + 1, (int)(ex.BytePositionInLine ?? -1) + 1));
        }
        catch (XmlException ex)
        {
            return (null, new ParseProblem(ex.Message, ex.LineNumber, ex.LinePosition));
        }
        catch (YamlException ex)
        {
            return (null, new ParseProblem(ex.Message, (int)ex.Start.Line, (int)ex.Start.Column));
        }
    }

    /// <summary>The problems of a document (empty when it's fine).</summary>
    public static IReadOnlyList<ParseProblem> Validate(string text, StructuredFormat format) =>
        string.IsNullOrWhiteSpace(text) && format != StructuredFormat.Xml ? Array.Empty<ParseProblem>()
        : Parse(text, format).Problem is { } problem ? new[] { problem } : Array.Empty<ParseProblem>();

    /// <summary>
    /// The document indented (JSON with 2 spaces, XML with 2 spaces), or null with a reason when it can't be: it has
    /// problems, it's YAML (formatting it would lose comments), or a JSON with comments (they would be lost).
    /// </summary>
    public static string? Format(string text, StructuredFormat format, out string? reason)
    {
        reason = null;
        if (Parse(text, format).Problem is { } problem)
        {
            reason = $"Line {problem.Line}: {problem.Message}";
            return null;
        }
        switch (format)
        {
            case StructuredFormat.Json:
                if (HasJsonComments(text))
                {
                    reason = "The JSON has comments, which formatting would remove.";
                    return null;
                }
                using (JsonDocument document = JsonDocument.Parse(text, JsonOptions))
                {
                    using var stream = new MemoryStream();
                    using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
                        document.WriteTo(writer);
                    return Encoding.UTF8.GetString(stream.ToArray());
                }
            case StructuredFormat.Xml:
            {
                XDocument document = XDocument.Parse(text, LoadOptions.None);
                var settings = new XmlWriterSettings
                {
                    Indent = true,
                    IndentChars = "  ",
                    OmitXmlDeclaration = document.Declaration == null,
                    NewLineChars = "\n",
                };
                var builder = new StringBuilder();
                using (var writer = new Utf8StringWriter(builder, document.Declaration?.Encoding))
                using (var xml = XmlWriter.Create(writer, settings))
                    document.Save(xml);
                return builder.ToString();
            }
            default:
                reason = "YAML isn't formatted: its comments and layout would be lost.";
                return null;
        }
    }

    /// <summary>A StringWriter that reports the document's own encoding (so the XML declaration keeps it).</summary>
    private sealed class Utf8StringWriter : StringWriter
    {
        private readonly Encoding _encoding;

        public Utf8StringWriter(StringBuilder builder, string? encodingName) : base(builder, CultureInfo.InvariantCulture)
        {
            _ = TextEncodings.Utf8; // the code pages (windows-1250, ...) are registered
            Encoding? named = null;
            try
            {
                if (!string.IsNullOrEmpty(encodingName))
                    named = Encoding.GetEncoding(encodingName);
            }
            catch (ArgumentException)
            {
                // an unknown name: UTF-8
            }
            _encoding = named ?? new UTF8Encoding(false);
        }

        public override Encoding Encoding => _encoding;
    }

    private static bool HasJsonComments(string text)
    {
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(text), new JsonReaderOptions { CommentHandling = JsonCommentHandling.Allow, AllowTrailingCommas = true });
        while (reader.Read())
            if (reader.TokenType == JsonTokenType.Comment)
                return true;
        return false;
    }

    private static string CleanJsonMessage(string message)
    {
        // "'x' is an invalid start of a value. Path: $ | LineNumber: 0 | BytePositionInLine: 0." → the first part
        int cut = message.IndexOf(" Path:", StringComparison.Ordinal);
        if (cut < 0)
            cut = message.IndexOf(" LineNumber:", StringComparison.Ordinal);
        return cut > 0 ? message[..cut].TrimEnd(' ', '|', '.') + "." : message;
    }

    // ---- JSON --------------------------------------------------------------------------------------------------

    private static TreeNode ParseJson(string text)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        // line starts (in bytes) to turn token positions into line numbers
        var lineStarts = new List<int> { 0 };
        for (int i = 0; i < bytes.Length; i++)
            if (bytes[i] == '\n')
                lineStarts.Add(i + 1);
        int LineOf(long position)
        {
            int index = lineStarts.BinarySearch((int)position);
            return (index >= 0 ? index : ~index - 1) + 1;
        }

        var reader = new Utf8JsonReader(bytes, new JsonReaderOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true, MaxDepth = 512 });
        var root = new TreeNode("JSON", TreeNodeKind.Document);
        var stack = new Stack<TreeNode>();
        stack.Push(root);
        string? propertyName = null;
        while (reader.Read())
        {
            TreeNode parent = stack.Peek();
            string Label()
            {
                if (propertyName != null)
                {
                    string name = propertyName;
                    propertyName = null;
                    return name;
                }
                return parent.Kind == TreeNodeKind.Array ? $"[{parent.Children.Count}]" : "(root)";
            }
            int line = LineOf(reader.TokenStartIndex);
            switch (reader.TokenType)
            {
                case JsonTokenType.PropertyName:
                    propertyName = reader.GetString();
                    break;
                case JsonTokenType.StartObject:
                case JsonTokenType.StartArray:
                    var container = new TreeNode(Label(), reader.TokenType == JsonTokenType.StartObject ? TreeNodeKind.Object : TreeNodeKind.Array, null, line);
                    parent.Children.Add(container);
                    stack.Push(container);
                    break;
                case JsonTokenType.EndObject:
                case JsonTokenType.EndArray:
                    stack.Pop();
                    break;
                case JsonTokenType.String:
                    parent.Children.Add(new TreeNode(Label(), TreeNodeKind.String, reader.GetString(), line));
                    break;
                case JsonTokenType.Number:
                    parent.Children.Add(new TreeNode(Label(), TreeNodeKind.Number, Encoding.UTF8.GetString(reader.ValueSpan), line));
                    break;
                case JsonTokenType.True:
                case JsonTokenType.False:
                    parent.Children.Add(new TreeNode(Label(), TreeNodeKind.Boolean, reader.TokenType == JsonTokenType.True ? "true" : "false", line));
                    break;
                case JsonTokenType.Null:
                    parent.Children.Add(new TreeNode(Label(), TreeNodeKind.Null, "null", line));
                    break;
            }
        }
        return root.Children.Count == 1 ? root.Children[0] : root;
    }

    // ---- XML ---------------------------------------------------------------------------------------------------

    private static TreeNode ParseXml(string text)
    {
        XDocument document = XDocument.Parse(text, LoadOptions.SetLineInfo | LoadOptions.PreserveWhitespace);
        var root = new TreeNode("XML", TreeNodeKind.Document);
        foreach (XNode node in document.Nodes())
            AddXml(root, node);
        return root.Children.Count == 1 ? root.Children[0] : root;
    }

    private static void AddXml(TreeNode parent, XNode node)
    {
        int line = node is IXmlLineInfo info && info.HasLineInfo() ? info.LineNumber : 0;
        switch (node)
        {
            case XElement element:
            {
                // an element with only text shows it as its value
                bool onlyText = !element.HasAttributes && element.Nodes().All(n => n is XText);
                string? value = onlyText ? element.Value.Trim() : null;
                var item = new TreeNode($"<{element.Name.LocalName}>", TreeNodeKind.Element, string.IsNullOrEmpty(value) ? null : value, line);
                parent.Children.Add(item);
                foreach (XAttribute attribute in element.Attributes())
                {
                    int attributeLine = ((IXmlLineInfo)attribute).HasLineInfo() ? ((IXmlLineInfo)attribute).LineNumber : line;
                    item.Children.Add(new TreeNode("@" + attribute.Name.LocalName, TreeNodeKind.Attribute, attribute.Value, attributeLine));
                }
                if (!onlyText)
                    foreach (XNode child in element.Nodes())
                        AddXml(item, child);
                break;
            }
            case XText text when !string.IsNullOrWhiteSpace(text.Value):
                parent.Children.Add(new TreeNode("#text", TreeNodeKind.Text, text.Value.Trim(), line));
                break;
            case XComment comment:
                parent.Children.Add(new TreeNode("#comment", TreeNodeKind.Comment, comment.Value.Trim(), line));
                break;
        }
    }

    // ---- YAML --------------------------------------------------------------------------------------------------

    private static TreeNode ParseYaml(string text)
    {
        var stream = new YamlStream();
        stream.Load(new StringReader(text));
        var root = new TreeNode("YAML", TreeNodeKind.Document);
        for (int i = 0; i < stream.Documents.Count; i++)
        {
            YamlNode node = stream.Documents[i].RootNode;
            string label = stream.Documents.Count == 1 ? "(root)" : $"document {i + 1}";
            root.Children.Add(YamlTree(label, node));
        }
        return root.Children.Count == 1 ? root.Children[0] : root;
    }

    private static TreeNode YamlTree(string label, YamlNode node)
    {
        int line = (int)node.Start.Line;
        switch (node)
        {
            case YamlMappingNode mapping:
            {
                var item = new TreeNode(label, TreeNodeKind.Object, null, line);
                foreach (var (key, value) in mapping.Children)
                    item.Children.Add(YamlTree(key is YamlScalarNode k ? k.Value ?? string.Empty : key.ToString(), value));
                return item;
            }
            case YamlSequenceNode sequence:
            {
                var item = new TreeNode(label, TreeNodeKind.Array, null, line);
                int index = 0;
                foreach (YamlNode child in sequence.Children)
                    item.Children.Add(YamlTree($"[{index++}]", child));
                return item;
            }
            case YamlScalarNode scalar:
            {
                string? value = scalar.Value;
                TreeNodeKind kind = scalar.Style is ScalarStyle.SingleQuoted or ScalarStyle.DoubleQuoted or ScalarStyle.Literal or ScalarStyle.Folded
                    ? TreeNodeKind.String
                    : value is null or "~" or "null" or "Null" or "NULL" or "" ? TreeNodeKind.Null
                    : value is "true" or "false" or "True" or "False" or "TRUE" or "FALSE" ? TreeNodeKind.Boolean
                    : double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out _) ? TreeNodeKind.Number
                    : TreeNodeKind.String;
                return new TreeNode(label, kind, kind == TreeNodeKind.Null ? "null" : value, line);
            }
            default:
                return new TreeNode(label, TreeNodeKind.Null, node.ToString(), line); // an alias
        }
    }
}
