// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Viewing;

namespace VibeSalamander.Core.Tests;

/// <summary>JSON, XML and YAML trees, checks and formatting; Markdown and notebook pages.</summary>
public class StructuredTests
{
    [Theory]
    [InlineData("a.json", StructuredFormat.Json)]
    [InlineData("tsconfig.JSONC", StructuredFormat.Json)]
    [InlineData("App.xaml", StructuredFormat.Xml)]
    [InlineData("x.csproj", StructuredFormat.Xml)]
    [InlineData("ci.yml", StructuredFormat.Yaml)]
    public void Formats_by_extension(string name, StructuredFormat format) => Assert.Equal(format, Structured.FormatOf(name));

    [Fact]
    public void Other_files_have_no_structure() => Assert.Null(Structured.FormatOf("readme.txt"));

    [Fact]
    public void Json_tree_with_lines()
    {
        const string json = "{\n  \"name\": \"Vibe\",\n  // a comment\n  \"tags\": [\"a\", 2, true, null],\n  \"nested\": { \"x\": 1.5 },\n}";
        var (root, problem) = Structured.Parse(json, StructuredFormat.Json);
        Assert.Null(problem);
        Assert.NotNull(root);
        Assert.Equal(TreeNodeKind.Object, root!.Kind);
        Assert.Equal(new[] { "name", "tags", "nested" }, root.Children.Select(c => c.Label));
        TreeNode tags = root.Children[1];
        Assert.Equal("[4]", tags.Summary);
        Assert.Equal(new[] { "[0]", "[1]", "[2]", "[3]" }, tags.Children.Select(c => c.Label));
        Assert.Equal(new[] { TreeNodeKind.String, TreeNodeKind.Number, TreeNodeKind.Boolean, TreeNodeKind.Null }, tags.Children.Select(c => c.Kind));
        Assert.Equal(4, tags.Line);
        Assert.Equal(2, root.Children[0].Line);
        Assert.Equal("1.5", root.Children[2].Children[0].Value);
        Assert.Equal(8, root.Descendants().Count());
    }

    [Fact]
    public void Json_problems_say_where()
    {
        var (root, problem) = Structured.Parse("{\n  \"a\": 1,\n  \"b\" 2\n}", StructuredFormat.Json);
        Assert.Null(root);
        Assert.Equal(3, problem!.Line);
        Assert.DoesNotContain("LineNumber", problem.Message);
        Assert.Single(Structured.Validate("[1, 2", StructuredFormat.Json));
        Assert.Empty(Structured.Validate("[1, 2]", StructuredFormat.Json));
    }

    [Fact]
    public void Xml_tree()
    {
        const string xml = "<?xml version=\"1.0\"?>\n<root id=\"7\">\n  <item>first</item>\n  <!-- note -->\n  <group><item a=\"b\"/></group>\n</root>";
        var (root, problem) = Structured.Parse(xml, StructuredFormat.Xml);
        Assert.Null(problem);
        Assert.Equal("<root>", root!.Label);
        Assert.Equal(new[] { "@id", "<item>", "#comment", "<group>" }, root.Children.Select(c => c.Label));
        Assert.Equal("first", root.Children[1].Value);
        Assert.Equal(3, root.Children[1].Line);
        Assert.Equal("@a", root.Children[3].Children[0].Children[0].Label);
    }

    [Fact]
    public void Xml_problems()
    {
        var (_, problem) = Structured.Parse("<a>\n  <b>\n</a>", StructuredFormat.Xml);
        Assert.Equal(3, problem!.Line);
        Assert.Single(Structured.Validate("", StructuredFormat.Xml)); // an XML document needs an element
    }

    [Fact]
    public void Yaml_tree_and_problems()
    {
        const string yaml = "name: build\non:\n  push:\n    branches: [main]\njobs:\n  count: 3\n  enabled: true\n  nothing: ~\n";
        var (root, problem) = Structured.Parse(yaml, StructuredFormat.Yaml);
        Assert.Null(problem);
        Assert.Equal(new[] { "name", "on", "jobs" }, root!.Children.Select(c => c.Label));
        TreeNode jobs = root.Children[2];
        Assert.Equal(new[] { TreeNodeKind.Number, TreeNodeKind.Boolean, TreeNodeKind.Null }, jobs.Children.Select(c => c.Kind));
        Assert.Equal("main", root.Children[1].Children[0].Children[0].Children[0].Value);

        var (_, bad) = Structured.Parse("a: 1\n b: 2\nc: [", StructuredFormat.Yaml);
        Assert.NotNull(bad);
        Assert.True(bad!.Line >= 2);
        Assert.Empty(Structured.Validate("", StructuredFormat.Yaml));
    }

    [Fact]
    public void Several_yaml_documents()
    {
        var (root, _) = Structured.Parse("a: 1\n---\nb: 2\n", StructuredFormat.Yaml);
        Assert.Equal(new[] { "document 1", "document 2" }, root!.Children.Select(c => c.Label));
    }

    [Fact]
    public void Formatting_json_and_xml()
    {
        Assert.Equal("{\n  \"a\": [\n    1,\n    2\n  ],\n  \"č\": \"ž\"\n}", Structured.Format("{\"a\":[1,2],\"č\":\"ž\"}", StructuredFormat.Json, out _)!.ReplaceLineEndings("\n"));
        Assert.Null(Structured.Format("{\"a\": 1 // why\n}", StructuredFormat.Json, out string? comments));
        Assert.Contains("comments", comments);
        Assert.Null(Structured.Format("{\"a\": ", StructuredFormat.Json, out string? broken));
        Assert.StartsWith("Line 1", broken);

        string xml = Structured.Format("<a><b x=\"1\"><c/></b><!--n--></a>", StructuredFormat.Xml, out _)!;
        Assert.Equal("<a>\n  <b x=\"1\">\n    <c />\n  </b>\n  <!--n-->\n</a>", xml);
        Assert.StartsWith("<?xml version=\"1.0\" encoding=\"windows-1250\"?>",
            Structured.Format("<?xml version=\"1.0\" encoding=\"windows-1250\"?><a/>", StructuredFormat.Xml, out _));
        Assert.Null(Structured.Format("a: 1", StructuredFormat.Yaml, out _));
    }

    [Fact]
    public void Markdown_page()
    {
        string page = HtmlRendering.MarkdownPage(@"C:\docs\README.md", "# Title\n\n| a | b |\n|---|---|\n| 1 | 2 |\n\n- [x] done", dark: true, allowRemote: false);
        Assert.Contains("<h1", page);
        Assert.Contains("<table>", page);
        Assert.Contains("checkbox", page);
        Assert.Contains("<base href=\"file:///C:/docs/\">", page);
        Assert.Contains("default-src 'none'", page);
        Assert.DoesNotContain("https:", page[..page.IndexOf("</head>", StringComparison.Ordinal)]);
        Assert.Contains("https:", HtmlRendering.MarkdownPage(@"C:\d\a.md", "x", dark: false, allowRemote: true));
    }

    [Fact]
    public void Notebook_page()
    {
        const string notebook = """
            {"cells": [
              {"cell_type": "markdown", "source": ["# Analysis\n", "Some *text*"]},
              {"cell_type": "code", "execution_count": 3, "source": "print(1 < 2)",
               "outputs": [
                 {"output_type": "stream", "text": ["True\n"]},
                 {"output_type": "display_data", "data": {"image/png": "iVBORw0KGgo=", "text/plain": ["<Figure>"]}},
                 {"output_type": "error", "ename": "ValueError", "evalue": "bad", "traceback": ["\u001b[31mValueError\u001b[0m: bad"]}
               ]}
            ], "metadata": {}, "nbformat": 4}
            """;
        string page = HtmlRendering.NotebookPage(@"C:\n\a.ipynb", notebook, dark: false, allowRemote: false);
        Assert.Contains("<h1", page);
        Assert.Contains("<em>text</em>", page);
        Assert.Contains("In [3]:", page);
        Assert.Contains("print(1 &lt; 2)", page);
        Assert.Contains("data:image/png;base64,iVBORw0KGgo=", page);
        Assert.Contains("ValueError: bad", page); // terminal colors left out
        Assert.DoesNotContain("&lt;Figure&gt;", page); // the picture is shown instead of its text
        Assert.Throws<System.Text.Json.JsonException>(() => HtmlRendering.NotebookPage("x.ipynb", "{}", false, false));
    }
}
