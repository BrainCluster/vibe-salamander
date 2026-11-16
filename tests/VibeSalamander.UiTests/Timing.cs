// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;

namespace VibeSalamander.UiTests;

/// <summary>
/// Where the time of a UI test run goes: starting and closing the app, making the dummy files, fixed pauses, waiting
/// for a state, screenshots. The totals are in timing.md beside the screenshots (the run's page shows them), so the
/// tests are made faster where the time really is.
/// </summary>
public static class Timing
{
    private static readonly ConcurrentDictionary<string, (long Ticks, int Count)> Totals = new();
    private static readonly Stopwatch Run = Stopwatch.StartNew();

    /// <summary>A pause of a fixed length (counted: these are what a faster run has less of).</summary>
    public static void Pause(int milliseconds)
    {
        if (milliseconds <= 0)
            return;
        Thread.Sleep(milliseconds);
        Add("Fixed pauses", TimeSpan.FromMilliseconds(milliseconds));
    }

    public static void Measure(string what, Action action)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            action();
        }
        finally
        {
            Add(what, clock.Elapsed);
        }
    }

    public static T Measure<T>(string what, Func<T> get)
    {
        var clock = Stopwatch.StartNew();
        try
        {
            return get();
        }
        finally
        {
            Add(what, clock.Elapsed);
        }
    }

    public static void Add(string what, TimeSpan time) =>
        Totals.AddOrUpdate(what, (time.Ticks, 1), (_, old) => (old.Ticks + time.Ticks, old.Count + 1));

    /// <summary>Writes the totals so far (after every app session: the last one written is the run's).</summary>
    public static void Write()
    {
        try
        {
            var lines = new List<string> { "| Where the time went | Seconds | Times |", "|---|---:|---:|" };
            foreach (var (what, total) in Totals.OrderByDescending(t => t.Value.Ticks))
                lines.Add(string.Create(CultureInfo.InvariantCulture, $"| {what} | {TimeSpan.FromTicks(total.Ticks).TotalSeconds:N1} | {total.Count:N0} |"));
            lines.Add(string.Create(CultureInfo.InvariantCulture, $"| **The whole run so far** | **{Run.Elapsed.TotalSeconds:N1}** | |"));
            File.WriteAllLines(Path.Combine(AppSession.ScreenshotFolder, "timing.md"), lines);
        }
        catch (IOException)
        {
            // the numbers are a help, not a result
        }
    }
}
