// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.Globalization;
using FlaUI.Core.WindowsAPI;

namespace VibeSalamander.UiTests;

/// <summary>
/// How long the program takes to start and how much memory it uses, on the dummy files and on a folder of 20,000
/// files (roadmap T4). The numbers go to measurements.md beside the screenshots (the UI test run's page shows
/// them), to compare from release to release; the limits here only catch a change that makes things many times worse.
/// </summary>
public sealed class MeasurementScenarios : IDisposable
{
    private const int ManyCount = 20_000;
    private readonly DummyFiles _files = new();
    private readonly string _many = Directory.CreateTempSubdirectory("vibe-ui-many").FullName;

    public void Dispose()
    {
        _files.Dispose();
        try
        {
            Directory.Delete(_many, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temp folder left behind on a build machine is fine
        }
    }

    private static string Megabytes(long bytes) => (bytes / (1024.0 * 1024)).ToString("N0", CultureInfo.InvariantCulture) + " MB";

    private static string Milliseconds(TimeSpan time) => time.TotalMilliseconds.ToString("N0", CultureInfo.InvariantCulture) + " ms";

    /// <summary>The memory only this program uses (private bytes), a moment after it settled.</summary>
    private static long Memory(AppSession app)
    {
        Timing.Pause(2000);
        using Process process = Process.GetProcessById(app.App.ProcessId);
        process.Refresh();
        return process.PrivateMemorySize64;
    }

    [UiFact]
    public void Startup_time_and_memory_are_measured()
    {
        var lines = new List<string> { "| Measured | Value |", "|---|---:|" };
        Parallel.For(0, ManyCount, i => File.WriteAllBytes(Path.Combine(_many, $"file {i:00000}.txt"), Array.Empty<byte>()));

        TimeSpan start, startMany, toEnd;
        long memory, memoryMany;
        using (var app = new AppSession(s =>
               {
                   s.Left.Path = _files.Documents;
                   s.Right.Path = _files.Photos;
               }))
        {
            start = app.ReadyAfter;
            memory = Memory(app);
        }
        using (var app = new AppSession(s =>
               {
                   s.Left.Path = _many;
                   s.Right.Path = _files.Photos;
               }))
        {
            startMany = app.ReadyAfter;
            AppSession.WaitUntil(() => app.Items().Contains("file 00000.txt"), "the folder of 20,000 files");
            var clock = Stopwatch.StartNew();
            app.Press(VirtualKeyShort.END, settleMs: 0);
            AppSession.WaitUntil(() => app.Caret() == $"file {ManyCount - 1:00000}.txt", "the caret on the last of 20,000 files", 30);
            toEnd = clock.Elapsed;
            memoryMany = Memory(app);
            AppSession.Screenshot("90 a folder of 20,000 files");
            string log = app.ErrorLogText();
            Assert.True(log.Length == 0, "The error log has entries:\n" + log);
        }

        lines.Add($"| Start until both panels are listed (to about 0.2 s) | {Milliseconds(start)} |");
        lines.Add($"| Memory after the start | {Megabytes(memory)} |");
        lines.Add($"| Start with a folder of 20,000 files in the left panel | {Milliseconds(startMany)} |");
        lines.Add($"| End key: the caret on the last of the 20,000 files (to about 0.2 s) | {Milliseconds(toEnd)} |");
        lines.Add($"| Memory with the folder of 20,000 files | {Megabytes(memoryMany)} |");
        File.WriteAllLines(Path.Combine(AppSession.ScreenshotFolder, "measurements.md"), lines);

        Assert.True(start.TotalSeconds < 45, $"the start took {start.TotalSeconds:0.0} s");
        Assert.True(startMany.TotalSeconds < 60, $"the start with 20,000 files took {startMany.TotalSeconds:0.0} s");
        Assert.True(memoryMany < 2L * 1024 * 1024 * 1024, $"{Megabytes(memoryMany)} with a folder of 20,000 files");
    }
}
