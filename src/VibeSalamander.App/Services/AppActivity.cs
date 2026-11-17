// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Dispatching;

namespace VibeSalamander.Services;

/// <summary>
/// What the program is busy with besides listing the panels: the editor starting (WebView2 and Monaco), the viewer
/// loading a file or a page, a search or comparison, thumbnails and icons, a plugin showing progress. The main window
/// shows it, together with the panels' own state, open dialogs and running transfers, in an element with nothing to
/// see (automation id "AppState": "ready" or "busy: ...", then "; dialog" and "; transfers" when there are), so the UI
/// tests wait for the program instead of guessing how long a slow machine takes.
/// </summary>
public static class AppActivity
{
    /// <summary>Nothing takes longer than this to start: a piece of work that never reports its end isn't "busy" for ever.</summary>
    private static readonly TimeSpan Longest = TimeSpan.FromSeconds(90);

    private static readonly List<Work> Running = new();
    private static int _dialogs;

    /// <summary>How many dialogs are open (counted from when one has finished opening).</summary>
    public static int OpenDialogs => Volatile.Read(ref _dialogs);

    /// <summary>A dialog has opened (on screen, its opening animation done).</summary>
    public static void DialogOpened()
    {
        Interlocked.Increment(ref _dialogs);
        RaiseChanged();
    }

    /// <summary>A dialog has closed.</summary>
    public static void DialogClosed()
    {
        Interlocked.Decrement(ref _dialogs);
        RaiseChanged();
    }

    /// <summary>Something started or ended (raised on the thread that did it).</summary>
    public static event Action? Changed;

    /// <summary>Tells the listeners; a listener's failure never reaches the code that reported (a plugin, a worker).</summary>
    private static void RaiseChanged()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            Core.Diagnostics.ErrorLog.Exception("App state", ex);
        }
    }

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
            work.Queue = queue;
            timer.Start();
        }
        RaiseChanged();
        return work;
    }

    private sealed class Work(string what) : IDisposable
    {
        public string What { get; } = what;
        public DispatcherQueueTimer? Timer { get; set; }
        public DispatcherQueue? Queue { get; set; }

        public void Dispose()
        {
            bool removed;
            lock (Running)
                removed = Running.Remove(this);
            if (!removed)
                return;
            if (Timer is { } timer)
            {
                // a timer is stopped on the thread that made it (the end may be reported from another one)
                if (Queue is { HasThreadAccess: false } queue)
                    queue.TryEnqueue(() => timer.Stop()); // a lambda: WinRT refuses a delegate made from the timer's own method
                else
                    timer.Stop();
            }
            RaiseChanged();
        }
    }
}

/// <summary>Busy while something says so (a queue with work, a progress bar shown), from any thread.</summary>
public sealed class BusyFlag(string what)
{
    private readonly object _lock = new();
    private IDisposable? _work;

    public void Set(bool busy)
    {
        IDisposable? end = null;
        lock (_lock)
        {
            if (busy && _work == null)
                _work = AppActivity.Begin(what);
            else if (!busy && _work != null)
                (end, _work) = (_work, null);
        }
        end?.Dispose();
    }
}
