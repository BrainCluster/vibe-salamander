// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace VibeSalamander.Core.Tests;

/// <summary>
/// The translations stay complete: every language file has every entry, placeholders survive translation, and every
/// text the program translates (Loc.T / Loc.F, L.T / L.F, a plugin's Ui.T, XAML texts) has an entry. A text added
/// without its translations fails here instead of showing up in English for the user.
/// </summary>
public sealed class TranslationTests
{
    private static readonly string Root = FindRoot();
    private static readonly string AppFolder = Path.Combine(Root, "src", "VibeSalamander.App");
    private static readonly string StringsFolder = Path.Combine(AppFolder, "Strings");

    /// <summary>
    /// Texts the program passes for translation that read the same in every language: names and examples.
    /// (Texts without words, such as "{0}: {1}" or "{0} %", are skipped without being listed here.)
    /// </summary>
    private static readonly HashSet<string> SameInEveryLanguage = new(StringComparer.Ordinal)
    {
        "Vibe Salamander",
        "GNU General Public License v3",
        "node_modules;.git",
        "node_modules;.git;bin;obj",
        "*.txt;*.doc|*.bak",
        "10 KB",
        "2.5 GB",
    };

    /// <summary>XAML properties that Loc.Translate translates when it walks a window or dialog.</summary>
    private static readonly HashSet<string> TranslatedAttributes = new(StringComparer.Ordinal)
    {
        "Text", "Content", "Header", "PlaceholderText", "ToolTipService.ToolTip", "Title", "Message",
        "PrimaryButtonText", "SecondaryButtonText", "CloseButtonText", "OnContent", "OffContent",
    };

    /// <summary>{0}, {1:N0}, {2,-10}: the arguments a text is formatted with.</summary>
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:[,:][^}]*)?\}", RegexOptions.CultureInvariant);

    /// <summary>
    /// A literal passed to a translating call: Loc.T("..."), Loc.F("...", x), L.T, _host.Ui.T, or a plugin's own T / F.
    /// A literal followed by "+" is only the start of a text and is not checked.
    /// </summary>
    private static readonly Regex TranslatingCall = new(
        @"(?:(?:\bLoc|\bL|\bUi|\bui|_host!?\??\.Ui)\.|(?<![\w.]))(?:T|F)\(\s*(@?)""((?:[^""\\]|\\.)*)""(?!\s*\+)",
        RegexOptions.CultureInvariant);

    [Fact]
    public void Every_language_of_the_program_has_a_file_and_every_file_a_language()
    {
        string loc = File.ReadAllText(Path.Combine(AppFolder, "Services", "Loc.cs"));
        int start = loc.IndexOf("Languages =", StringComparison.Ordinal);
        Assert.True(start >= 0, "Loc.Languages wasn't found in Loc.cs");
        int end = loc.IndexOf("};", start, StringComparison.Ordinal);
        string[] codes = Regex.Matches(loc[start..end], @"\(""([A-Za-z-]+)"",")
            .Select(m => m.Groups[1].Value)
            .Where(code => code != "en")
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        string[] files = LoadTables().Keys.OrderBy(code => code, StringComparer.Ordinal).ToArray();

        Assert.NotEmpty(codes);
        Assert.Equal(codes, files);
    }

    [Fact]
    public void Every_language_has_every_entry()
    {
        Dictionary<string, Dictionary<string, string>> tables = LoadTables();
        var all = new SortedSet<string>(tables.Values.SelectMany(t => t.Keys), StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var (code, table) in tables.OrderBy(t => t.Key, StringComparer.Ordinal))
        {
            foreach (string key in all)
                if (!table.ContainsKey(key))
                    problems.Add($"{code}.json has no \"{key}\"");
            foreach (var (key, value) in table)
                if (string.IsNullOrWhiteSpace(value))
                    problems.Add($"{code}.json: \"{key}\" has an empty translation");
        }

        Assert.True(problems.Count == 0, Report("Every language file needs every entry", problems));
    }

    [Fact]
    public void Placeholders_survive_translation()
    {
        var problems = new List<string>();
        foreach (var (code, table) in LoadTables().OrderBy(t => t.Key, StringComparer.Ordinal))
            foreach (var (key, value) in table)
                if (Placeholders(key) != Placeholders(value))
                    problems.Add($"{code}.json: \"{key}\" has {Placeholders(key)}, its translation \"{value}\" has {Placeholders(value)}");

        Assert.True(problems.Count == 0, Report("A translation must keep the placeholders of its text", problems));
    }

    [Fact]
    public void Every_text_the_code_translates_has_an_entry()
    {
        Dictionary<string, Dictionary<string, string>> tables = LoadTables();
        var problems = new List<string>();
        foreach (string file in SourceFiles("*.cs"))
        {
            string source = File.ReadAllText(file);
            foreach (Match m in TranslatingCall.Matches(source))
            {
                string text = m.Groups[1].Value == "@" ? m.Groups[2].Value.Replace("\"\"", "\"") : Unescape(m.Groups[2].Value);
                Check(tables, text, $"{Relative(file)}:{LineOf(source, m.Index)}", problems);
            }
        }

        Assert.True(problems.Count == 0, Report("Add these texts to every Strings\\*.json", problems));
    }

    [Fact]
    public void Every_text_in_XAML_has_an_entry()
    {
        Dictionary<string, Dictionary<string, string>> tables = LoadTables();
        var problems = new List<string>();
        foreach (string file in SourceFiles("*.xaml"))
        {
            XDocument doc = XDocument.Load(file, LoadOptions.SetLineInfo);
            if (doc.Root != null)
                Visit(doc.Root, file, tables, problems);
        }

        Assert.True(problems.Count == 0, Report("Add these texts to every Strings\\*.json (or tag the element NoLoc)", problems));
    }

    [Fact]
    public void The_check_finds_a_text_without_translations()
    {
        // the regular expression must keep recognizing the ways the code asks for a translation
        const string source = """
            a = Loc.T("Copy");
            b = Loc.F("Copy {0} to:", name);
            c = L.T("Delete");
            d = _host.Ui.T("Upload");
            e = T(@"C:\Temp");
            f = Loc.T("Start of " + name);
            g = Loc.T($"Not {checked}");
            """;
        string[] found = TranslatingCall.Matches(source).Select(m => m.Groups[2].Value).ToArray();

        Assert.Equal(new[] { "Copy", "Copy {0} to:", "Delete", "Upload", @"C:\Temp" }, found);
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static void Visit(XElement element, string file, Dictionary<string, Dictionary<string, string>> tables, List<string> problems)
    {
        if ((string?)element.Attribute("Tag") == "NoLoc" || element.Name.LocalName == "DataTemplate")
            return; // names and paths; templates show data
        foreach (XAttribute attribute in element.Attributes())
        {
            if (!TranslatedAttributes.Contains(attribute.Name.LocalName) || attribute.Value.StartsWith('{'))
                continue; // a binding or another markup extension
            int line = ((System.Xml.IXmlLineInfo)attribute).LineNumber;
            Check(tables, attribute.Value, $"{Relative(file)}:{line}", problems);
        }
        foreach (XElement child in element.Elements())
            Visit(child, file, tables, problems);
    }

    private static void Check(Dictionary<string, Dictionary<string, string>> tables, string text, string where, List<string> problems)
    {
        if (SameInEveryLanguage.Contains(text) || !Regex.IsMatch(Placeholder.Replace(text, string.Empty), @"\p{L}{2}"))
            return;
        string[] missing = tables
            .Where(t => !Has(t.Value, text))
            .Select(t => t.Key)
            .OrderBy(code => code, StringComparer.Ordinal)
            .ToArray();
        if (missing.Length > 0)
            problems.Add($"\"{text}\" ({where}) is missing in {string.Join(", ", missing)}");
    }

    /// <summary>As Loc.T finds it: the text itself, or "Configuration..." through "Configuration".</summary>
    private static bool Has(Dictionary<string, string> table, string text) =>
        table.ContainsKey(text) ||
        (text.EndsWith("...", StringComparison.Ordinal) && text.Length > 3 && table.ContainsKey(text[..^3]));

    private static string Placeholders(string text) =>
        string.Join(",", Placeholder.Matches(text).Select(m => int.Parse(m.Groups[1].Value)).Distinct().Order());

    private static string Unescape(string literal) => Regex.Replace(literal, @"\\(u[0-9a-fA-F]{4}|.)", m =>
    {
        string escape = m.Groups[1].Value;
        return escape[0] switch
        {
            'n' => "\n",
            'r' => "\r",
            't' => "\t",
            '0' => "\0",
            'u' when escape.Length == 5 => ((char)Convert.ToInt32(escape[1..], 16)).ToString(),
            _ => escape,
        };
    });

    private static Dictionary<string, Dictionary<string, string>> LoadTables()
    {
        var tables = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        foreach (string file in Directory.GetFiles(StringsFolder, "*.json"))
        {
            var table = new Dictionary<string, string>(StringComparer.Ordinal);
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            foreach (JsonProperty property in doc.RootElement.EnumerateObject())
            {
                Assert.False(table.ContainsKey(property.Name), $"{Path.GetFileName(file)}: \"{property.Name}\" is there twice");
                table[property.Name] = property.Value.GetString() ?? string.Empty;
            }
            tables[Path.GetFileNameWithoutExtension(file)] = table;
        }
        Assert.NotEmpty(tables);
        return tables;
    }

    /// <summary>The program's sources (not build output).</summary>
    private static IEnumerable<string> SourceFiles(string pattern)
    {
        char sep = Path.DirectorySeparatorChar;
        return Directory.EnumerateFiles(Path.Combine(Root, "src"), pattern, SearchOption.AllDirectories)
            .Where(f => !f.Contains($"{sep}obj{sep}", StringComparison.OrdinalIgnoreCase) &&
                        !f.Contains($"{sep}bin{sep}", StringComparison.OrdinalIgnoreCase));
    }

    private static string Relative(string file) => Path.GetRelativePath(Root, file);

    private static int LineOf(string text, int index) => text.AsSpan(0, index).Count('\n') + 1;

    private static string Report(string title, List<string> problems) =>
        $"{title} ({problems.Count}):{Environment.NewLine}" + string.Join(Environment.NewLine, problems.Take(60));

    private static string FindRoot()
    {
        string? dir = AppContext.BaseDirectory;
        while (dir != null && !File.Exists(Path.Combine(dir, "VibeSalamander.sln")))
            dir = Path.GetDirectoryName(dir);
        return dir ?? throw new InvalidOperationException("No VibeSalamander.sln above " + AppContext.BaseDirectory);
    }
}
