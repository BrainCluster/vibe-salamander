// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Commands;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Tools;
using VibeSalamander.Core.Viewing;

namespace VibeSalamander.Core.Tests;

public sealed class MultiRenameTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "vibe-rename-" + Guid.NewGuid().ToString("N")[..8]);

    public MultiRenameTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    private RenameSource File(string name, DateTime? when = null)
    {
        string path = Path.Combine(_root, name);
        System.IO.File.WriteAllText(path, name);
        return new RenameSource(path, false, when ?? new DateTime(2026, 10, 28, 9, 5, 7));
    }

    [Fact]
    public void Masks_counter_and_dates()
    {
        var items = new[] { File("Holiday photo.JPG"), File("other.jpg") };
        var options = new MultiRenameOptions { NameMask = "[YMD] [C] [N1-3]", ExtensionMask = "[E]", Case = RenameCase.Lower, CounterDigits = 3, CounterStart = 9 };
        IReadOnlyList<RenameLine> plan = MultiRename.Plan(items, options);
        Assert.Equal("2026-10-28 009 hol.jpg", plan[0].NewName);
        Assert.Equal("2026-10-28 010 oth.jpg", plan[1].NewName);
        Assert.All(plan, l => Assert.Null(l.Problem));
    }

    [Fact]
    public void Search_and_replace_with_regular_expressions()
    {
        var items = new[] { File("IMG_0001.jpg"), File("IMG_0002.jpg") };
        var options = new MultiRenameOptions { Search = @"IMG_(\d+)", Replace = "Trip $1", RegularExpression = true };
        IReadOnlyList<RenameLine> plan = MultiRename.Plan(items, options);
        Assert.Equal("Trip 0001.jpg", plan[0].NewName);
        Assert.Equal("Trip 0002.jpg", plan[1].NewName);
    }

    [Fact]
    public void Two_items_with_one_name_and_names_taken_are_problems()
    {
        File("taken.txt");
        var items = new[] { File("a.txt"), File("b.txt"), File("c.txt") };
        IReadOnlyList<RenameLine> same = MultiRename.Plan(items, new MultiRenameOptions { NameMask = "same" });
        Assert.All(same, l => Assert.NotNull(l.Problem));
        IReadOnlyList<RenameLine> taken = MultiRename.Plan(new[] { items[0] }, new MultiRenameOptions { NameMask = "taken" });
        Assert.NotNull(taken[0].Problem);
        IReadOnlyList<RenameLine> bad = MultiRename.Plan(new[] { items[0] }, new MultiRenameOptions { NameMask = "a:b" });
        Assert.NotNull(bad[0].Problem);
    }

    [Fact]
    public void Shifting_names_by_one_works()
    {
        // 1 → 2, 2 → 3: each takes the name the other gives up
        var items = new[] { File("1.txt"), File("2.txt") };
        IReadOnlyList<RenameLine> plan = MultiRename.Plan(items, new MultiRenameOptions { NameMask = "[C]", CounterStart = 2 });
        Assert.All(plan, l => Assert.Null(l.Problem));
        Assert.Empty(MultiRename.Apply(plan));
        Assert.Equal("1.txt", System.IO.File.ReadAllText(Path.Combine(_root, "2.txt")));
        Assert.Equal("2.txt", System.IO.File.ReadAllText(Path.Combine(_root, "3.txt")));
        Assert.False(System.IO.File.Exists(Path.Combine(_root, "1.txt")));
    }

    [Fact]
    public void Each_word_capital()
    {
        IReadOnlyList<RenameLine> plan = MultiRename.Plan(new[] { File("the long way home.MP3") }, new MultiRenameOptions { Case = RenameCase.EachWord });
        Assert.Equal("The Long Way Home.mp3", plan[0].NewName);
    }
}

public sealed class StartupArgumentsTests
{
    [Fact]
    public void Salamander_switches()
    {
        StartupArguments a = StartupArguments.Parse(new[] { "-L", @"C:\left", "-R", "D:", "-P", "2" });
        Assert.Equal(@"C:\left", a.Left);
        Assert.Equal(@"D:\", a.Right);
        Assert.Equal(2, a.Panel);
        Assert.Null(a.Active);
    }

    [Fact]
    public void A_folder_alone_goes_to_the_active_panel_and_the_apps_own_switches_are_skipped()
    {
        StartupArguments a = StartupArguments.Parse(new[] { "--restart", @"C:\Work" });
        Assert.Equal(@"C:\Work", a.Active);
        Assert.True(StartupArguments.Parse(new[] { "--restart" }).IsEmpty);
    }

    [Fact]
    public void Command_lines_split_like_windows()
    {
        Assert.Equal(new[] { @"C:\Program Files\x.exe", "-L", @"C:\My Files" },
            StartupArguments.Split("\"C:\\Program Files\\x.exe\" -L \"C:\\My Files\""));
    }
}

public sealed class VsCodeThemeTests
{
    [Fact]
    public void A_theme_becomes_the_custom_colors()
    {
        const string json = """
            {
              // comments and trailing commas are allowed in VS Code themes
              "name": "Test",
              "type": "dark",
              "colors": {
                "editor.background": "#1e1e1e",
                "sideBar.background": "#252526",
                "foreground": "#ccc",
                "list.activeSelectionBackground": "#094771ff",
                "terminal.ansiBlue": "#2472C8",
              },
            }
            """;
        var colors = new ColorOptions();
        Assert.True(VsCodeThemes.TryImport(json, colors, out string? problem), problem);
        Assert.Equal("Custom", colors.Scheme);
        Assert.Equal("#252526", colors.PanelBackground);
        Assert.Equal("#CCCCCC", colors.Text);
        Assert.Equal("#094771", colors.FocusBackground);
        Assert.Equal("#2472C8", colors.DirectoryText);
    }

    [Fact]
    public void Other_json_is_refused()
    {
        var colors = new ColorOptions();
        Assert.False(VsCodeThemes.TryImport("{\"a\": 1}", colors, out string? problem));
        Assert.NotNull(problem);
        Assert.Equal("Vibe", colors.Scheme);
    }
}

public sealed class StructuredRewriteTests
{
    [Fact]
    public void Minify_and_sort_keys()
    {
        string json = "{\n  \"b\": 1,\n  \"a\": { \"z\": [3, 1], \"y\": 1.50 }\n}";
        Assert.Equal("{\"b\":1,\"a\":{\"z\":[3,1],\"y\":1.50}}", Structured.Minify(json, StructuredFormat.Json, out _));
        string? sorted = Structured.SortKeys(json, out _);
        Assert.NotNull(sorted);
        Assert.True(sorted!.IndexOf("\"a\"", StringComparison.Ordinal) < sorted.IndexOf("\"b\"", StringComparison.Ordinal));
        Assert.True(sorted.IndexOf("\"y\"", StringComparison.Ordinal) < sorted.IndexOf("\"z\"", StringComparison.Ordinal));
        Assert.Contains("[\n", sorted.Replace("\r\n", "\n"));
        Assert.Equal("<a><b x=\"1\">t</b></a>", Structured.Minify("<a>\n  <b x=\"1\">t</b>\n</a>", StructuredFormat.Xml, out _));
    }

    [Fact]
    public void Json_with_comments_or_problems_is_not_rewritten()
    {
        Assert.Null(Structured.Minify("{ // c\n \"a\": 1 }", StructuredFormat.Json, out string? reason));
        Assert.NotNull(reason);
        Assert.Null(Structured.SortKeys("{ \"a\": ", out reason));
        Assert.NotNull(reason);
        Assert.Null(Structured.Minify("a: 1", StructuredFormat.Yaml, out reason));
    }
}

/// <summary>Formats.DateStyle is shared by all tests: these run on their own.</summary>
[CollectionDefinition("Formats", DisableParallelization = true)]
public sealed class FormatsCollection
{
}

[Collection("Formats")]
public sealed class GapCommandTests
{
    [Fact]
    public void Total_commander_keys_for_the_new_commands()
    {
        KeyMap tc = KeyMap.CreateTotalCommanderDefaults();
        Assert.Equal(CommandId.Duplicate, tc.Resolve(VK.F5, KeyModifiers.Shift));
        Assert.Equal(CommandId.MultiRename, tc.Resolve(VK.M, KeyModifiers.Ctrl));
        Assert.Equal(CommandId.FlatView, tc.Resolve(VK.B, KeyModifiers.Ctrl));
        Assert.Equal(CommandId.QuickView, tc.Resolve(VK.Q, KeyModifiers.Ctrl));
        Assert.Equal(CommandId.HotPathList, tc.Resolve(VK.D, KeyModifiers.Ctrl));
        Assert.Equal(CommandId.NameToCommandLine, tc.Resolve(VK.Enter, KeyModifiers.Ctrl));
        KeyMap salamander = KeyMap.CreateSalamanderDefaults();
        Assert.Equal(CommandId.Duplicate, salamander.Resolve(VK.D, KeyModifiers.Alt | KeyModifiers.Shift));
        Assert.Equal(CommandId.MakeFileList, salamander.Resolve(VK.M, KeyModifiers.Ctrl));
    }

    [Fact]
    public void The_cheat_sheet_lists_the_shortcuts()
    {
        string html = CommandCatalog.CheatSheet(KeyMap.CreateSalamanderDefaults(), "Shortcuts <test>");
        Assert.Contains("Shortcuts &lt;test&gt;", html);
        Assert.Contains("Alt+Shift+D", html);
        Assert.Contains("F5", html);
    }

    [Fact]
    public void Iso_dates_and_24_hour_times()
    {
        var when = new DateTime(2026, 10, 28, 21, 26, 53);
        try
        {
            Formats.DateStyle = DateStyle.Iso;
            Formats.TimeStyle = TimeStyle.Hours24;
            Assert.Equal("2026-10-28", Formats.Date(when));
            Assert.Equal("21:26:53", Formats.Time(when));
            Formats.TimeStyle = TimeStyle.Hours12;
            Assert.Equal("9:26:53 PM", Formats.Time(when));
            Assert.True(Formats.TwelveHours);
        }
        finally
        {
            Formats.DateStyle = DateStyle.System;
            Formats.TimeStyle = TimeStyle.System;
        }
    }
}
