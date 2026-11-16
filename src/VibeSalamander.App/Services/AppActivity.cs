// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;

namespace VibeSalamander.Services;

/// <summary>
/// What the program is busy with besides listing the panels: the editor starting (WebView2 and Monaco), the viewer
/// loading a file. The main window shows it, together with the panels' own state, in an element that isn't
/// displayed (automation id "AppState": "ready" or "busy: ..."), so the UI tests wait for the program to be ready
/// instead of guessing how long a slow machine takes.
/// </summary>
public static class AppActivity
{
    /// <summary>Nothing takes longer than this to start: a piece of work that never reports its end isn't "busy" for ever.</summary>
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(90);

    private static readonly List<Work> Running = new();

    /// <summary>Something started or ended (raised on the thread that did it).</summary>
    public static event Action? Changed;

    /// <summary>"ready", or "busy: editor, viewer".</summary>
    public static string State
    {
        get
        {
            lock (Running)
                return Running.Count == 0 ? "ready" : "busy: " + string.Join(", ", Running.Select(w => w.What).Distinct());
        }
    }

    /// <summary>Something starts; dispose the result when it's done (more than once does no harm).</summary>
    public static IDisposable Begin(string what)
    {
        var work = new Work(what);
        lock (Running)
            Running.Add(work);
        if (DispatcherQueue.GetForCurrentThread() is { } queue)
        {
            DispatcherQueueTimer timer = queue.CreateTimer();
            timer.Interval = Longest;
            timer.IsRepeating = false;
            timer.Tick += (_, _) => work.Dispose();
            work.Timer = timer;
            timer.Start();
        }
        Changed?.Invoke();
        return work;
    }

    private sealed class Work(string what) : IDisposable
    {
        public string What { get; } = what;
        public DispatcherQueueTimer? Timer { get; set; }

        public void Dispose()
        {
            bool removed;
            lock (Running)
                removed = Running.Remove(this);
            if (!removed)
                return;
            Timer?.Stop();
            Changed?.Invoke();
        }
    }
}
