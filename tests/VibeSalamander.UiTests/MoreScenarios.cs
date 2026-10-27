// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using FlaUI.Core.WindowsAPI;
using VibeSalamander.Core.Settings;

namespace VibeSalamander.UiTests;

/// <summary>More of what a user does: moving, conflicts, masks, sorting, archives, the mouse, plugins, looks.</summary>
public sealed class MoreScenarios : IDisposable
{
    private readonly DummyFiles _files = new();

    public void Dispose() => _files.Dispose();

    /// <summary>Documents on the left, the empty Target folder on the right.</summary>
    private AppSession Start(Action<AppSettings>? configure = null)
    {
        var app = new AppSession(s =>
        {
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Target;
            configure?.Invoke(s);
        });
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");
        return app;
    }

    private string Doc(string name) => Path.Combine(_files.Documents, name);
    private string Target(string name) => Path.Combine(_files.Target, name);

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    // ---- moving and conflicts -------------------------------------------------------------------------------

    [UiFact]
    public void Move_with_F6()
    {
        using AppSession app = Start();
        app.GoTo("notes.md");
        app.Press(VirtualKeyShort.F6, settleMs: 1000);
        AppSession.Screenshot("60 move dialog");
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => File.Exists(Target("notes.md")) && !File.Exists(Doc("notes.md")), "notes.md moved");
        AppSession.WaitUntil(() => !app.Items().Contains("notes.md") && app.Items(left: false).Contains("notes.md"), "both panels showing the move");
        NoErrors(app);
    }

    [UiFact]
    public void Copy_onto_an_existing_file_asks_and_overwrites()
    {
        File.WriteAllText(Target("report.txt"), "old version");
        using AppSession app = Start();
        app.GoTo("report.txt");
        app.Press(VirtualKeyShort.F5, settleMs: 800);
        app.Press(VirtualKeyShort.ENTER);
        app.Button("Overwrite", seconds: 15);
        AppSession.Screenshot("61 overwrite question");
        app.Click("Overwrite");
        AppSession.WaitUntil(() => File.ReadAllText(Target("report.txt")).StartsWith("Quarterly report", StringComparison.Ordinal), "report.txt overwritten");
        NoErrors(app);
    }

    [UiFact]
    public void Mark_by_mask_and_copy_the_marked_files()
    {
        using AppSession app = Start();
        app.Press(VirtualKeyShort.ADD, settleMs: 1000); // Num +
        AppSession.Screenshot("62 select by mask");
        app.Type("*.txt");
        app.Press(VirtualKeyShort.ENTER, settleMs: 800);
        AppSession.WaitUntil(() => app.InfoLine().StartsWith("Selected:", StringComparison.Ordinal), "the .txt files marked");
        AppSession.Screenshot("63 marked by mask");
        app.Press(VirtualKeyShort.F5, settleMs: 800);
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => File.Exists(Target("report.txt")) && File.Exists(Target("Žuta kuća – čšćđž.txt")) && File.Exists(Target("日本語のファイル.txt")),
            "the marked .txt files copied");
        Assert.False(File.Exists(Target("notes.md")));
        Assert.True((File.GetAttributes(Target("read-only.txt")) & FileAttributes.ReadOnly) != 0); // attributes kept
        NoErrors(app);
    }

    // ---- what the panel shows ------------------------------------------------------------------------------

    [UiFact]
    public void Sort_by_size_and_back_by_name()
    {
        using AppSession app = Start();
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.F6, settleMs: 1000);
        AppSession.Screenshot("64 sorted by size");
        List<string> bySize = app.Items();
        Assert.True(bySize.IndexOf("data.bin") < bySize.IndexOf("report.txt") && bySize.IndexOf("report.txt") < bySize.IndexOf("notes.md"),
            "largest first: " + string.Join(", ", bySize));
        Assert.True(bySize.IndexOf("Projects") < bySize.IndexOf("notes.md"), "folders stay on top: " + string.Join(", ", bySize));
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.F3, settleMs: 1000);
        List<string> byName = app.Items();
        Assert.True(byName.IndexOf("budget 2026.csv") < byName.IndexOf("report.txt"), "by name: " + string.Join(", ", byName));
        NoErrors(app);
    }

    [UiFact]
    public void Hidden_files_on_and_off()
    {
        using AppSession app = Start();
        Assert.Contains("hidden.txt", app.Items());
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_H, settleMs: 1000);
        AppSession.WaitUntil(() => !app.Items().Contains("hidden.txt"), "hidden.txt hidden");
        AppSession.Screenshot("65 hidden files off");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_H, settleMs: 1000);
        AppSession.WaitUntil(() => app.Items().Contains("hidden.txt"), "hidden.txt shown again");
        NoErrors(app);
    }

    [UiFact]
    public void Filter_shows_only_matching_files()
    {
        using AppSession app = Start();
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.F12, settleMs: 1000);
        app.Type("*.md");
        app.Press(VirtualKeyShort.ENTER, settleMs: 1000);
        AppSession.WaitUntil(() => app.Items().Contains("notes.md") && !app.Items().Contains("report.txt"), "only .md files (and folders)");
        Assert.Contains("Projects", app.Items());
        AppSession.Screenshot("66 filter");
        NoErrors(app);
    }

    [UiFact]
    public void Swap_panels()
    {
        using AppSession app = Start(s => s.Right.Path = _files.Photos);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_U, settleMs: 1200);
        AppSession.WaitUntil(() => app.Items().Contains("holiday 01.png") && app.Items(left: false).Contains("report.txt"), "the panels swapped");
        NoErrors(app);
    }

    [UiFact]
    public void Directory_sizes()
    {
        using AppSession app = Start();
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.F10);
        Thread.Sleep(2000);
        AppSession.Screenshot("67 directory sizes");
        NoErrors(app);
    }

    [UiFact]
    public void Hot_path_there_and_back()
    {
        using AppSession app = Start();
        FlaUI.Core.Input.Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.KEY_1); // remember Documents
        Thread.Sleep(800);
        app.GoTo("Projects");
        app.Press(VirtualKeyShort.ENTER, settleMs: 1000);
        AppSession.WaitUntil(() => app.Items().Contains("Vibe"), "inside Projects");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_1, settleMs: 1000);
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "back in Documents through hot path 1");
        NoErrors(app);
    }

    [UiFact]
    public void Compare_directories_marks_the_differences()
    {
        File.Copy(Doc("report.txt"), Target("report.txt"));
        using AppSession app = Start();
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.F10, settleMs: 1200);
        AppSession.Screenshot("68 compare directories");
        app.Press(VirtualKeyShort.ENTER, settleMs: 2000);
        AppSession.WaitUntil(() => app.InfoLine().StartsWith("Selected:", StringComparison.Ordinal), "the files only on the left marked");
        AppSession.Screenshot("69 compared");
        NoErrors(app);
    }

    // ---- archives -------------------------------------------------------------------------------------------

    [UiFact]
    public void Into_a_zip_and_copy_a_file_out()
    {
        using AppSession app = Start();
        app.GoTo("archive.zip");
        app.Press(VirtualKeyShort.ENTER, settleMs: 1500);
        AppSession.WaitUntil(() => app.Items().Contains("inside"), "the archive's content");
        app.GoTo("inside");
        app.Press(VirtualKeyShort.ENTER, settleMs: 1200);
        AppSession.WaitUntil(() => app.Items().Contains("two.txt"), "the folder inside the archive");
        AppSession.Screenshot("70 inside a zip");
        app.GoTo("two.txt");
        app.Press(VirtualKeyShort.F5, settleMs: 1000);
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => File.Exists(Target("two.txt")), "two.txt unpacked into the target");
        Assert.Equal("two", File.ReadAllText(Target("two.txt")));
        NoErrors(app);
    }

    [UiFact]
    public void Pack_then_unpack()
    {
        using AppSession app = Start();
        app.GoTo("notes.md");
        app.Press(VirtualKeyShort.SPACE);
        app.GoTo("budget 2026.csv");
        app.Press(VirtualKeyShort.SPACE);
        app.Press(VirtualKeyShort.ALT, VirtualKeyShort.F5, settleMs: 1200);
        AppSession.Screenshot("71 pack dialog");
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => Directory.EnumerateFiles(_files.Target, "*.zip").Any(), "an archive in the target", seconds: 20);
        string zip = Path.GetFileName(Directory.EnumerateFiles(_files.Target, "*.zip").First());

        app.Press(VirtualKeyShort.TAB, settleMs: 800);
        AppSession.WaitUntil(() => app.Items(left: false).Contains(zip), "the archive in the right panel");
        app.GoTo(zip, left: false);
        app.Press(VirtualKeyShort.ALT, VirtualKeyShort.F6, settleMs: 1200);
        AppSession.Screenshot("72 unpack dialog");
        string output = Path.Combine(_files.Target, "out");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A); // the whole path in the dialog, replaced by typing
        app.Type(output);
        app.Press(VirtualKeyShort.ENTER, settleMs: 1000);
        app.Click("Create"); // "The target directory doesn't exist. Do you want to create it?" (Confirmations)
        AppSession.WaitUntil(() => Directory.Exists(output) &&
            Directory.EnumerateFiles(output, "notes.md", SearchOption.AllDirectories).Any() &&
            Directory.EnumerateFiles(output, "budget 2026.csv", SearchOption.AllDirectories).Any(), "both files unpacked", seconds: 20);
        string notes = Directory.EnumerateFiles(output, "notes.md", SearchOption.AllDirectories).First();
        Assert.Equal(File.ReadAllText(Doc("notes.md")), File.ReadAllText(notes));
        NoErrors(app);
    }

    // ---- the mouse ------------------------------------------------------------------------------------------

    // WinUI doesn't start a drag from simulated mouse input on the test machine (the press reaches the row, no drag
    // begins, with stepped or smooth movement): drag and drop stays on the manual checklist
    [UiFact(Skip = "WinUI doesn't start drags from simulated mouse input on the test machine")]
    public void Drag_a_file_to_the_other_panel()
    {
        using AppSession app = Start(s => s.Confirm.DragAndDrop = false);
        AppSession.Drag(app.OnName("report.txt"), app.EmptyArea(left: false), () => AppSession.Screenshot("73 dragging"));
        AppSession.WaitUntil(() => File.Exists(Target("report.txt")), "report.txt dropped into the target");
        AppSession.Screenshot("73 after drag and drop (2)");
        NoErrors(app);
    }

    [UiFact]
    public void Double_click_a_folder_and_right_click_a_file()
    {
        using AppSession app = Start();
        AppSession.DoubleClick(app.OnName("Projects"));
        AppSession.WaitUntil(() => app.Items().Contains("Vibe"), "Projects opened by double-click");
        AppSession.RightClick(app.OnName("Old"));
        AppSession.Screenshot("74 right-click menu");
        app.Press(VirtualKeyShort.ESCAPE);
        NoErrors(app);
    }

    // ---- dialogs and plugins --------------------------------------------------------------------------------

    [UiFact]
    public void Change_attributes_and_case_dialogs()
    {
        using AppSession app = Start();
        app.GoTo("report.txt");
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.F2, settleMs: 1200);
        AppSession.Screenshot("75 change attributes");
        app.Press(VirtualKeyShort.ESCAPE, settleMs: 800);
        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.F7, settleMs: 1200);
        AppSession.Screenshot("76 change case");
        app.Press(VirtualKeyShort.ESCAPE);
        NoErrors(app);
    }

    [UiFact]
    public void Disk_map_plugin()
    {
        using AppSession app = Start();
        app.Menu("Plugins", "Disk Map", "Show disk map");
        Thread.Sleep(4000);
        AppSession.Screenshot("77 disk map");
        app.CloseOtherWindows();
        NoErrors(app);
    }

    [UiFact]
    public void Checksum_plugin()
    {
        using AppSession app = Start();
        app.GoTo("report.txt");
        app.Menu("Plugins", "Checksum", "Calculate Checksums...");
        Thread.Sleep(2000);
        AppSession.Screenshot("78 checksums");
        app.Press(VirtualKeyShort.ESCAPE);
        app.CloseOtherWindows();
        NoErrors(app);
    }

    [UiFact]
    public void Error_log_says_there_are_none()
    {
        using AppSession app = Start();
        app.Menu("Help", "Error Log");
        AppSession.WaitUntil(() => app.Text("No errors have been logged.") != null, "the notification that nothing was logged");
        AppSession.Screenshot("79 no errors logged");
        NoErrors(app);
    }

    // ---- looks ----------------------------------------------------------------------------------------------

    [UiFact]
    public void Color_schemes_density_and_a_small_window()
    {
        foreach (var (scheme, name) in new[] { ("Norton", "80 norton commander"), ("Dracula", "81 dracula"), ("Explorer", "82 explorer") })
        {
            using AppSession app = Start(s =>
            {
                s.Colors.Scheme = scheme;
                s.Right.Path = _files.Photos;
            });
            Thread.Sleep(1000);
            AppSession.Screenshot(name);
            NoErrors(app);
        }
        using (AppSession app = Start(s =>
        {
            s.Density = Density.Comfortable;
            s.WindowMaximized = false;
            s.WindowX = 40;
            s.WindowY = 40;
            s.WindowWidth = 900;
            s.WindowHeight = 600;
        }))
        {
            Thread.Sleep(1000);
            AppSession.Screenshot("83 comfortable small window");
            // in a narrow panel the names still show (other columns step aside)
            var name = app.Row("report.txt").FindFirstDescendant(cf => cf.ByName("report").And(cf.ByControlType(FlaUI.Core.Definitions.ControlType.Text)));
            Assert.NotNull(name);
            Assert.True(name!.BoundingRectangle.Width >= 30, $"the name column is {name.BoundingRectangle.Width} px wide");
            NoErrors(app);
        }
    }
}
