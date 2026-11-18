// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.IO.Compression;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace VibeSalamander.UiTests;

/// <summary>The last Salamander features before 1.0 (roadmap S2): Convert, Edit with, the directory line, archives.</summary>
public sealed class ParityScenarios : IDisposable
{
    private readonly DummyFiles _files = new();

    public void Dispose() => _files.Dispose();

    private AppSession Start()
    {
        var app = new AppSession(s =>
        {
            s.Left.Path = _files.Documents;
            s.Right.Path = _files.Target;
        });
        AppSession.WaitUntil(() => app.Items().Contains("report.txt"), "the documents");
        return app;
    }

    private string Doc(string name) => Path.Combine(_files.Documents, name);

    private static void NoErrors(AppSession app)
    {
        string log = app.ErrorLogText();
        Assert.True(log.Length == 0, "The error log has entries:\n" + log);
    }

    [UiFact]
    public void Convert_gives_a_file_Windows_line_endings()
    {
        File.WriteAllText(Doc("unix.txt"), "one\ntwo\nthree\n");
        using AppSession app = Start();
        app.GoTo("unix.txt");

        app.Press(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_K);
        app.WaitDialog();
        AppSession.Screenshot("80 convert");
        app.Press(VirtualKeyShort.ENTER); // Windows (CR LF), the encoding as it is

        AppSession.WaitUntil(() => File.ReadAllText(Doc("unix.txt")) == "one\r\ntwo\r\nthree\r\n", "CR LF line endings");
        NoErrors(app);
    }

    [UiFact]
    public void Edit_with_lists_the_editors()
    {
        using AppSession app = Start();
        app.GoTo("notes.md");

        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.SHIFT, VirtualKeyShort.F4);
        AppSession.Retry(() => app.Window.FindFirstDescendant(cf => cf.ByName("Built-in editor")), "the Built-in editor choice");
        AppSession.Retry(() => app.Window.FindFirstDescendant(cf => cf.ByName("Windows' editor")), "the Windows' editor choice");
        AppSession.Screenshot("81 edit with");
        app.Press(VirtualKeyShort.ESCAPE);
        NoErrors(app);
    }

    [UiFact]
    public void The_directory_line_can_be_hidden()
    {
        using AppSession app = Start();
        int Lines() => app.Window.FindAllDescendants(cf => cf.ByAutomationId("DriveMenuButton")).Length;
        Assert.Equal(2, Lines());

        app.Menu("Options", "Show", "Directory Line");
        AppSession.WaitUntil(() => Lines() == 0, "both directory lines hidden");
        AppSession.Screenshot("82 no directory line");
        app.Menu("Options", "Show", "Directory Line");
        AppSession.WaitUntil(() => Lines() == 2, "the directory lines back");
        NoErrors(app);
    }

    [UiFact]
    public void Rename_and_make_a_folder_inside_a_zip()
    {
        using AppSession app = Start();
        app.GoTo("archive.zip");
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => app.Items().Contains("inside"), "the archive's content");

        app.GoTo("inside");
        app.Press(VirtualKeyShort.F2);
        app.WaitNameEditor();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("renamed");
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => app.Items().Contains("renamed") && !app.Items().Contains("inside"), "the folder renamed inside the zip");

        app.Press(VirtualKeyShort.F7);
        app.WaitDialog();
        Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
        Keyboard.Type("made here");
        app.Press(VirtualKeyShort.ENTER);
        AppSession.WaitUntil(() => app.Items().Contains("made here"), "the new folder inside the zip");
        app.WaitTransfers();

        using (ZipArchive zip = ZipFile.OpenRead(Doc("archive.zip")))
        {
            var names = zip.Entries.Select(e => e.FullName).ToList();
            Assert.Contains("renamed/two.txt", names);
            Assert.Contains("made here/", names);
            Assert.DoesNotContain(names, n => n.StartsWith("inside/", StringComparison.Ordinal));
        }
        NoErrors(app);
    }
}
