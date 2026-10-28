// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VibeSalamander.Core;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Operations;
using VibeSalamander.Plugins;
using VibeSalamander.Services;

namespace VibeSalamander.ViewModels;

public enum TransferState
{
    Queued,
    Preparing,
    Running,
    Paused,
    WaitingForAnswer,
    Completed,
    CompletedWithErrors,
    Cancelled,
    Failed,
}

/// <summary>One copy/move/delete in the transfer panel.</summary>
public sealed class TransferItemViewModel : BindableBase
{
    private TransferState _state = TransferState.Queued;
    private double _percent;
    private bool _indeterminate = true;
    private string _currentFile = string.Empty;
    private string _detailText = string.Empty;
    private string _speedText = string.Empty;
    private string _remainingText = string.Empty;
    private TransferState _stateBeforeQuestion;
    private bool _showGraph;
    private readonly System.Diagnostics.Stopwatch _sampleClock = System.Diagnostics.Stopwatch.StartNew();
    private long _sampleBytes;
    private double _currentSpeed;

    /// <summary>The graph's size in the row.</summary>
    public const double GraphWidth = 120, GraphHeight = 26;

    public TransferItemViewModel(OperationKind kind, IReadOnlyList<string> sources, string? target)
    {
        Kind = kind;
        Sources = sources;
        Target = target;
        Disks = TransferScheduler.Disks(sources, target);
        string what = sources.Count == 1 ? Path.GetFileName(sources[0].TrimEnd('\\')) : Loc.F("{0} items", sources.Count);
        string format = kind switch
        {
            OperationKind.Move => "Move {0}",
            OperationKind.Delete => "Delete {0}",
            OperationKind.Pack => "Pack {0}",
            OperationKind.Unpack => "Unpack {0}",
            _ => "Copy {0}",
        };
        Title = Loc.F(format, what);
        Route = target == null
            ? Path.GetDirectoryName(sources[0].TrimEnd('\\')) ?? string.Empty
            : $"{Path.GetDirectoryName(sources[0].TrimEnd('\\'))}  →  {target.TrimEnd('\\')}";
    }

    public OperationKind Kind { get; }

    /// <summary>The disks it reads and writes (transfers on the same disk run one after another).</summary>
    public IReadOnlyList<string> Disks { get; }

    public IReadOnlyList<string> Sources { get; }
    public string? Target { get; }
    public string Title { get; }
    public string Route { get; }

    internal CancellationTokenSource Cancellation { get; } = new();

    /// <summary>This transfer's own speed limit (the one for all transfers applies too).</summary>
    public SpeedLimiter Limiter { get; } = new();

    /// <summary>The limit for all transfers, to draw it in the graph.</summary>
    internal SpeedLimiter? SharedLimiter { get; set; }

    /// <summary>The speed every half second, for the graph.</summary>
    public SpeedHistory History { get; } = new(60);

    public long Limit
    {
        get => Limiter.BytesPerSecond;
        set
        {
            Limiter.BytesPerSecond = value;
            OnPropertyChanged(nameof(Limit));
            OnPropertyChanged(nameof(LimitText));
            OnPropertyChanged(nameof(LimitGlyphOpacity));
            UpdateGraph();
        }
    }

    /// <summary>The tooltip of the limit button.</summary>
    public string LimitText => Limit > 0 ? Loc.F("Speed limit: {0}", SpeedLimits.Describe(Limit)) : Loc.T("Speed limit");

    /// <summary>The limit button stands out while a limit is set.</summary>
    public double LimitGlyphOpacity => Limit > 0 ? 1 : 0.55;

    public bool ShowGraph
    {
        get => _showGraph;
        set
        {
            if (SetProperty(ref _showGraph, value))
                OnPropertyChanged(nameof(GraphVisibility));
        }
    }

    public Visibility GraphVisibility => _showGraph ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The speed line of the graph.</summary>
    public Microsoft.UI.Xaml.Media.PointCollection GraphLine { get; private set; } = new();

    /// <summary>The area under the speed line.</summary>
    public Microsoft.UI.Xaml.Media.PointCollection GraphArea { get; private set; } = new();

    /// <summary>Where the limit is in the graph (hidden without a limit).</summary>
    public double LimitLineY { get; private set; }

    public Visibility LimitLineVisibility { get; private set; } = Visibility.Collapsed;

    /// <summary>The tooltip of the graph: now and the fastest so far.</summary>
    public string GraphText => History.Count == 0 ? string.Empty
        : Loc.F("Now {0}, fastest {1}", Formats.ShortSize((long)_currentSpeed) + "/s", Formats.ShortSize((long)History.Max) + "/s");

    private long EffectiveLimit
    {
        get
        {
            long own = Limiter.BytesPerSecond, shared = SharedLimiter?.BytesPerSecond ?? 0;
            return own > 0 && shared > 0 ? Math.Min(own, shared) : Math.Max(own, shared);
        }
    }

    /// <summary>A new speed sample every half second, from the bytes done since the last one.</summary>
    private void Sample(long totalBytesDone)
    {
        double seconds = _sampleClock.Elapsed.TotalSeconds;
        if (seconds < 0.5)
            return;
        _currentSpeed = Math.Max(0, (totalBytesDone - _sampleBytes) / seconds);
        _sampleBytes = totalBytesDone;
        _sampleClock.Restart();
        History.Add(_state == TransferState.Paused ? 0 : _currentSpeed);
        UpdateGraph();
    }

    internal void UpdateGraph()
    {
        IReadOnlyList<double> samples = History.Samples;
        long limit = EffectiveLimit;
        double top = Math.Max(Math.Max(History.Max, limit) * 1.1, 1);
        var line = new Microsoft.UI.Xaml.Media.PointCollection();
        var area = new Microsoft.UI.Xaml.Media.PointCollection();
        double step = GraphWidth / (History.Capacity - 1);
        // the newest sample at the right edge
        double x0 = GraphWidth - (samples.Count - 1) * step;
        for (int i = 0; i < samples.Count; i++)
            line.Add(new Windows.Foundation.Point(x0 + i * step, GraphHeight - samples[i] / top * GraphHeight));
        if (samples.Count > 0)
        {
            area.Add(new Windows.Foundation.Point(x0, GraphHeight));
            foreach (Windows.Foundation.Point p in line)
                area.Add(p);
            area.Add(new Windows.Foundation.Point(GraphWidth, GraphHeight));
        }
        GraphLine = line;
        GraphArea = area;
        LimitLineVisibility = limit > 0 && samples.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        LimitLineY = GraphHeight - limit / top * GraphHeight;
        OnPropertyChanged(nameof(GraphLine));
        OnPropertyChanged(nameof(GraphArea));
        OnPropertyChanged(nameof(LimitLineVisibility));
        OnPropertyChanged(nameof(LimitLineY));
        OnPropertyChanged(nameof(GraphText));
    }

    /// <summary>What pauses: the copy engine or a plugin job (set when the operation starts).</summary>
    internal IPausable? Pausable { get; set; }
    public OperationResult? Result { get; internal set; }

    public string Glyph => Kind switch
    {
        OperationKind.Move => "\uE8DE",
        OperationKind.Delete => "\uE74D",
        OperationKind.Pack => "\uE7B8",
        OperationKind.Unpack => "\uE896",
        _ => "\uE8C8",
    };

    public TransferState State
    {
        get => _state;
        internal set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(PauseVisibility));
                OnPropertyChanged(nameof(ResumeVisibility));
                OnPropertyChanged(nameof(CancelVisibility));
                OnPropertyChanged(nameof(RemoveVisibility));
                OnPropertyChanged(nameof(QueuedVisibility));
                OnPropertyChanged(nameof(IsIndeterminate));
                OnPropertyChanged(nameof(IsPaused));
                OnPropertyChanged(nameof(HasError));
                if (_state == TransferState.Completed)
                    Percent = 100;
                if (!IsActive)
                {
                    SpeedText = string.Empty;
                    RemainingText = string.Empty;
                }
            }
        }
    }

    public bool IsActive => _state is TransferState.Queued or TransferState.Preparing or TransferState.Running
                                   or TransferState.Paused or TransferState.WaitingForAnswer;

    public string StatusText => _state switch
    {
        TransferState.Queued => Loc.T("Queued"),
        TransferState.Preparing => Loc.T("Preparing"),
        TransferState.Running => $"{_percent:0} %",
        TransferState.Paused => Loc.T("Paused"),
        TransferState.WaitingForAnswer => Loc.T("Needs your answer"),
        TransferState.Completed => Loc.T("Done"),
        TransferState.CompletedWithErrors => Loc.F("Done, {0} failed", Result?.Failed ?? 0),
        TransferState.Cancelled => Loc.T("Cancelled"),
        _ => string.IsNullOrEmpty(ErrorText) ? Loc.T("Failed") : Loc.F("Failed: {0}", Loc.T(ErrorText)),
    };

    /// <summary>Why the operation failed (shown in the row instead of a message box).</summary>
    public string ErrorText { get; internal set; } = string.Empty;

    /// <summary>Set by "Clear all": the item leaves the list as soon as it has stopped.</summary>
    internal bool RemoveWhenDone { get; set; }

    public double Percent
    {
        get => _percent;
        private set
        {
            if (SetProperty(ref _percent, value))
                OnPropertyChanged(nameof(StatusText));
        }
    }

    /// <summary>For the progress bar's paused look (also while a question is waiting).</summary>
    public bool IsPaused => _state is TransferState.Paused or TransferState.WaitingForAnswer;

    /// <summary>For the progress bar's error look.</summary>
    public bool HasError => _state is TransferState.Failed or TransferState.CompletedWithErrors or TransferState.Cancelled;

    public bool IsIndeterminate => _indeterminate && _state is TransferState.Preparing or TransferState.Running;

    public string CurrentFile
    {
        get => _currentFile;
        private set => SetProperty(ref _currentFile, value);
    }

    public string DetailText
    {
        get => _detailText;
        private set => SetProperty(ref _detailText, value);
    }

    public string SpeedText
    {
        get => _speedText;
        private set => SetProperty(ref _speedText, value);
    }

    public string RemainingText
    {
        get => _remainingText;
        private set => SetProperty(ref _remainingText, value);
    }

    public Visibility PauseVisibility => _state is TransferState.Running or TransferState.Preparing ? Visibility.Visible : Visibility.Collapsed;
    public Visibility ResumeVisibility => _state == TransferState.Paused ? Visibility.Visible : Visibility.Collapsed;
    public Visibility CancelVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility RemoveVisibility => IsActive ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>Waiting: it can be moved up and down the queue.</summary>
    public Visibility QueuedVisibility => _state == TransferState.Queued ? Visibility.Visible : Visibility.Collapsed;

    internal void Apply(OperationProgress p)
    {
        if (!IsActive)
            return;
        if (p.Analyzing)
        {
            if (_state == TransferState.Running)
                State = TransferState.Preparing;
            _indeterminate = true;
            OnPropertyChanged(nameof(IsIndeterminate));
            CurrentFile = p.CurrentSource;
            DetailText = Loc.T("Reading directory tree...");
            return;
        }
        if (_state == TransferState.Preparing)
            State = TransferState.Running;
        _indeterminate = false;
        OnPropertyChanged(nameof(IsIndeterminate));
        Percent = Math.Round(p.TotalFraction * 100, 1);
        CurrentFile = p.CurrentSource;
        DetailText = Kind == OperationKind.Delete || p.TotalBytes == 0
            ? Loc.F("{0} / {1} items", p.ItemsDone.ToString("N0"), p.ItemsTotal.ToString("N0"))
            : $"{Formats.ShortSize(p.TotalBytesDone)} / {Formats.ShortSize(p.TotalBytes)}  ·  {p.ItemsDone:N0} / {p.ItemsTotal:N0}";
        Sample(p.TotalBytesDone);
        if (_state == TransferState.Running)
        {
            SpeedText = p.BytesPerSecond > 0 ? Formats.ShortSize((long)p.BytesPerSecond) + "/s" : string.Empty;
            RemainingText = p.Remaining is TimeSpan r
                ? Loc.F("{0} left", r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes}:{r.Seconds:00}")
                : string.Empty;
        }
    }

    internal void BeginQuestion()
    {
        _stateBeforeQuestion = _state;
        State = TransferState.WaitingForAnswer;
    }

    internal void EndQuestion()
    {
        if (_state == TransferState.WaitingForAnswer)
            State = _stateBeforeQuestion;
    }

    public void Pause()
    {
        if (Pausable == null || _state is not (TransferState.Running or TransferState.Preparing))
            return;
        Pausable.Paused = true;
        State = TransferState.Paused;
    }

    public void Resume()
    {
        if (Pausable == null || _state != TransferState.Paused)
            return;
        Pausable.Paused = false;
        State = _indeterminate ? TransferState.Preparing : TransferState.Running;
    }

    public void Cancel()
    {
        if (!IsActive)
            return;
        Cancellation.Cancel();
        if (Pausable != null)
            Pausable.Paused = false; // let a paused engine notice the cancellation
        if (_state == TransferState.Queued)
            State = TransferState.Cancelled;
    }
}

/// <summary>
/// The transfer queue shown in the panel below the file panels (WinSCP-style): operations run in the
/// background, several at once, while the panels stay usable.
/// </summary>
public sealed class TransfersViewModel : BindableBase
{
    private readonly DispatcherQueue _dispatcher;
    private readonly Views.Dialogs _dialogs;
    private readonly Func<EngineOptions> _engineOptions;
    private readonly Func<int> _maxParallel;

    private readonly Func<bool> _onePerDisk;

    // waiting for a slot (in the order of the list), and running: how many run at once comes from the configuration
    // (General → parallel transfers), and by default only one at a time per disk
    private readonly List<(TransferItemViewModel Item, TaskCompletionSource<bool> Start)> _waiting = new();
    private readonly HashSet<TransferItemViewModel> _running = new();

    /// <param name="engineOptions">Confirmations the engine follows (read when an operation starts).</param>
    /// <param name="maxParallel">How many operations run at the same time; the rest wait as "Queued".</param>
    /// <param name="onePerDisk">Transfers on the same disk run one after another.</param>
    public TransfersViewModel(DispatcherQueue dispatcher, Views.Dialogs dialogs, Func<EngineOptions> engineOptions, Func<int> maxParallel,
        Func<bool> onePerDisk)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _engineOptions = engineOptions;
        _maxParallel = maxParallel;
        _onePerDisk = onePerDisk;
        Items.CollectionChanged += (_, _) => Changed();
    }

    /// <summary>Waits until the item may start (on the UI thread). Throws OperationCanceledException when cancelled while queued.</summary>
    private async Task AcquireSlotAsync(TransferItemViewModel item, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var start = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting.Add((item, start));
        StartWaiting();
        if (start.Task.IsCompleted)
            return;
        using (ct.Register(() => _dispatcher.TryEnqueue(() =>
               {
                   if (_waiting.RemoveAll(w => w.Start == start) > 0)
                       start.TrySetCanceled();
               })))
        {
            await start.Task;
        }
    }

    private void ReleaseSlot(TransferItemViewModel item)
    {
        _running.Remove(item);
        StartWaiting();
    }

    /// <summary>Starts queued operations, in the list's order, as far as the limits allow (also after the limit was raised).</summary>
    public void StartWaiting()
    {
        if (_waiting.Count == 0)
            return;
        var ordered = _waiting.OrderBy(w => Items.IndexOf(w.Item)).ToList();
        IReadOnlyList<int> startable = TransferScheduler.Startable(ordered.Select(w => w.Item.Disks).ToList(),
            _running.Select(i => i.Disks), Math.Max(1, _maxParallel()), _onePerDisk());
        foreach (int index in startable)
        {
            var (item, start) = ordered[index];
            _waiting.Remove(ordered[index]);
            _running.Add(item);
            start.TrySetResult(true);
        }
    }

    /// <summary>Moves a queued transfer up or down the list (the list's order is the order they start in).</summary>
    public void MoveQueued(TransferItemViewModel item, int delta)
    {
        int from = Items.IndexOf(item);
        if (from < 0 || item.State != TransferState.Queued)
            return;
        int to = delta == int.MinValue ? 0 : Math.Clamp(from + delta, 0, Items.Count - 1);
        if (to != from)
            Items.Move(from, to);
        StartWaiting();
    }

    public ObservableCollection<TransferItemViewModel> Items { get; } = new();

    /// <summary>The speed limit for all transfers together.</summary>
    public SpeedLimiter SharedLimiter { get; } = new();

    public long SharedLimit
    {
        get => SharedLimiter.BytesPerSecond;
        set
        {
            SharedLimiter.BytesPerSecond = value;
            OnPropertyChanged(nameof(SharedLimit));
            foreach (TransferItemViewModel item in Items)
                item.UpdateGraph();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private Core.Interop.WhenDone _whenDone;

    /// <summary>What happens when the last transfer has finished (this time; back to Nothing after it happened).</summary>
    public Core.Interop.WhenDone WhenDone
    {
        get => _whenDone;
        set
        {
            if (SetProperty(ref _whenDone, value))
                StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>For the taskbar button: how far all running transfers are together, and whether they wait or failed.</summary>
    public (Services.TaskbarProgress.State State, double Percent) Overall
    {
        get
        {
            var active = Items.Where(i => i.IsActive).ToList();
            if (active.Count == 0)
                return (Services.TaskbarProgress.State.None, 0);
            double percent = active.Average(i => i.State == TransferState.Queued ? 0 : i.Percent);
            if (active.Any(i => i.State == TransferState.WaitingForAnswer))
                return (Services.TaskbarProgress.State.Error, percent);
            if (active.All(i => i.State is TransferState.Paused or TransferState.Queued))
                return (Services.TaskbarProgress.State.Paused, percent);
            if (active.All(i => i.IsIndeterminate || i.State == TransferState.Queued))
                return (Services.TaskbarProgress.State.Indeterminate, 0);
            return (Services.TaskbarProgress.State.Normal, percent);
        }
    }

    private bool _showGraphs;

    /// <summary>Each row shows its speed graph.</summary>
    public bool ShowGraphs
    {
        get => _showGraphs;
        set
        {
            if (!SetProperty(ref _showGraphs, value))
                return;
            foreach (TransferItemViewModel item in Items)
                item.ShowGraph = value;
        }
    }

    /// <summary>Asks for a limit typed by hand ("5", "500 KB", "1.5 MB/s"); null when cancelled or unreadable.</summary>
    public async Task<long?> AskLimitAsync(long current)
    {
        string? answer = await _dialogs.InputAsync(Loc.T("Speed limit"),
            Loc.T("Bytes per second, like 5 MB, 500 KB or 1.5 GB (a number alone is MB). 0 = no limit."),
            current > 0 ? SpeedLimits.Describe(current).Replace("/s", string.Empty, StringComparison.Ordinal) : string.Empty);
        return answer == null ? null : SpeedLimits.Parse(answer);
    }

    public int ActiveCount => Items.Count(i => i.IsActive);

    public bool HasItems => Items.Count > 0;

    public string SummaryText
    {
        get
        {
            int running = Items.Count(i => i.State is TransferState.Running or TransferState.Preparing or TransferState.WaitingForAnswer);
            int queued = Items.Count(i => i.State == TransferState.Queued);
            int paused = Items.Count(i => i.State == TransferState.Paused);
            int done = Items.Count(i => !i.IsActive);
            var parts = new List<string>();
            if (running > 0) parts.Add(Loc.F("{0} running", running));
            if (paused > 0) parts.Add(Loc.F("{0} paused", paused));
            if (queued > 0) parts.Add(Loc.F("{0} queued", queued));
            if (done > 0) parts.Add(Loc.F("{0} finished", done));
            return parts.Count == 0 ? Loc.T("No transfers") : string.Join(", ", parts);
        }
    }

    /// <summary>Raised when an operation ends (on the UI thread).</summary>
    public event EventHandler<TransferItemViewModel>? Finished;

    /// <summary>Raised when the list or a state changes (for the panel's visibility and summary).</summary>
    public event EventHandler? StateChanged;

    private void Changed()
    {
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(HasItems));
        OnPropertyChanged(nameof(SummaryText));
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Adds an operation; it starts as soon as a slot is free. Returns immediately.</summary>
    public TransferItemViewModel Enqueue(OperationKind kind, IReadOnlyList<string> sources, string? target,
        Func<FileOperationEngine, CancellationToken, Task<OperationResult>> run) =>
        Add(kind, sources, target, item =>
        {
            var engine = new FileOperationEngine(new ItemCallbacks(item, _dispatcher, _dialogs), new ItemProgress(item, _dispatcher))
            {
                Options = _engineOptions(),
            };
            engine.Limiters = new[] { item.Limiter, SharedLimiter };
            item.Pausable = engine;
            return run(engine, item.Cancellation.Token);
        });

    /// <summary>
    /// Adds a plugin operation (pack, unpack): it runs like a copy - queued, with progress, pause,
    /// cancel and questions shown when they come up.
    /// </summary>
    public TransferItemViewModel EnqueueJob(OperationKind kind, IReadOnlyList<string> sources, string? target,
        Func<IArchiveJob, Task<OperationResult>> run) =>
        Add(kind, sources, target, item =>
        {
            var job = new PluginJob(item, new ItemCallbacks(item, _dispatcher, _dialogs), new ItemProgress(item, _dispatcher), SharedLimiter);
            item.Pausable = job;
            return run(job);
        });

    private TransferItemViewModel Add(OperationKind kind, IReadOnlyList<string> sources, string? target,
        Func<TransferItemViewModel, Task<OperationResult>> run)
    {
        var item = new TransferItemViewModel(kind, sources, target) { SharedLimiter = SharedLimiter, ShowGraph = _showGraphs };
        item.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TransferItemViewModel.State))
                Changed();
        };
        Items.Add(item);
        _ = RunAsync(item, run);
        return item;
    }

    private async Task RunAsync(TransferItemViewModel item, Func<TransferItemViewModel, Task<OperationResult>> run)
    {
        try
        {
            await AcquireSlotAsync(item, item.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            item.State = TransferState.Cancelled;
            Finished?.Invoke(this, item);
            if (item.RemoveWhenDone)
                Items.Remove(item);
            return;
        }

        try
        {
            // cancelled between getting the slot and this continuation (it already shows "Cancelled")
            item.Cancellation.Token.ThrowIfCancellationRequested();
            item.State = TransferState.Preparing;
            OperationResult result = await run(item);
            item.Result = result;
            item.State = result.Cancelled ? TransferState.Cancelled
                       : result.Failed > 0 ? TransferState.CompletedWithErrors
                       : TransferState.Completed;
        }
        catch (OperationCanceledException)
        {
            item.State = TransferState.Cancelled;
        }
        catch (Exception ex)
        {
            item.Result = new OperationResult { Failed = 1 };
            item.ErrorText = ex.Message; // before State, so StatusText includes it
            item.State = TransferState.Failed;
        }
        finally
        {
            ReleaseSlot(item);
        }
        Finished?.Invoke(this, item);
        if (item.RemoveWhenDone)
            Items.Remove(item);
    }

    public void Remove(TransferItemViewModel item)
    {
        if (!item.IsActive)
            Items.Remove(item);
    }

    /// <summary>Removes the transfers that finished without problems (the ones that failed stay).</summary>
    public void ClearSucceeded()
    {
        foreach (var item in Items.Where(i => i.State == TransferState.Completed).ToList())
            Items.Remove(item);
    }

    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => !i.IsActive).ToList())
            Items.Remove(item);
    }

    /// <summary>Empties the list: finished items go at once, running and queued ones are cancelled and go when stopped.</summary>
    public void ClearAll()
    {
        foreach (var item in Items.ToList())
        {
            if (item.IsActive)
            {
                item.RemoveWhenDone = true;
                item.Cancel();
            }
            else
            {
                Items.Remove(item);
            }
        }
    }

    public void CancelAll()
    {
        foreach (var item in Items.ToList())
            item.Cancel();
    }

    /// <summary>Prompts of one operation: marks the item as waiting while the question is shown.</summary>
    private sealed class ItemCallbacks : IOperationCallbacks
    {
        private readonly TransferItemViewModel _item;
        private readonly DispatcherQueue _dispatcher;
        private readonly Views.Dialogs _dialogs;

        public ItemCallbacks(TransferItemViewModel item, DispatcherQueue dispatcher, Views.Dialogs dialogs)
        {
            _item = item;
            _dispatcher = dispatcher;
            _dialogs = dialogs;
        }

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) =>
            AskAsync(() => _dialogs.ConflictAsync(conflict, _item.Title), ConflictChoice.Cancel);

        public Task<ErrorChoice> ResolveErrorAsync(OperationError error)
        {
            Core.Diagnostics.ErrorLog.Error(_item.Title, $"{error.Path}: {error.Message}");
            return AskAsync(() => _dialogs.ErrorAsync(error, _item.Title), ErrorChoice.Cancel);
        }

        public Task<string?> AskPasswordAsync(string archivePath, bool wrongPassword) =>
            AskAsync<string?>(() => _dialogs.PasswordAsync(archivePath, wrongPassword, _item.Title), null);

        public Task<bool> ConfirmAsync(string title, string message) =>
            AskAsync(() => _dialogs.YesNoAsync(title, _item.Title + "\n\n" + message, "Copy anyway"), false);

        private Task<T> AskAsync<T>(Func<Task<T>> ask, T fallback)
        {
            var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool queued = _dispatcher.TryEnqueue(async () =>
            {
                _item.BeginQuestion();
                try
                {
                    tcs.TrySetResult(_item.Cancellation.IsCancellationRequested ? fallback : await ask());
                }
                catch (Exception)
                {
                    tcs.TrySetResult(fallback);
                }
                finally
                {
                    _item.EndQuestion();
                }
            });
            if (!queued)
                tcs.SetResult(fallback);
            return tcs.Task;
        }
    }

    private sealed class ItemProgress : IProgress<OperationProgress>
    {
        private readonly TransferItemViewModel _item;
        private readonly DispatcherQueue _dispatcher;

        public ItemProgress(TransferItemViewModel item, DispatcherQueue dispatcher)
        {
            _item = item;
            _dispatcher = dispatcher;
        }

        public void Report(OperationProgress value) => _dispatcher.TryEnqueue(() => _item.Apply(value));
    }

    /// <summary>A plugin's view of its transfer row (IArchiveJob): progress, pause, cancel and questions.</summary>
    private sealed class PluginJob : IArchiveJob, IPausable
    {
        private readonly TransferItemViewModel _item;
        private readonly ItemCallbacks _callbacks;
        private readonly ItemProgress _progress;
        private readonly ManualResetEventSlim _running = new(true);
        private readonly SpeedLimiter _shared;
        private volatile bool _paused;
        private long _counted;

        public PluginJob(TransferItemViewModel item, ItemCallbacks callbacks, ItemProgress progress, SpeedLimiter shared)
        {
            _item = item;
            _callbacks = callbacks;
            _progress = progress;
            _shared = shared;
        }

        public CancellationToken Cancellation => _item.Cancellation.Token;

        public bool Paused
        {
            get => _paused;
            set
            {
                _paused = value;
                if (value)
                    _running.Reset();
                else
                    _running.Set();
            }
        }

        public void WaitWhilePaused()
        {
            if (_paused)
                _running.Wait(Cancellation); // throws OperationCanceledException when cancelled while paused
            Cancellation.ThrowIfCancellationRequested();
        }

        /// <summary>Progress from the plugin's thread; the speed limits make it wait there.</summary>
        public void Report(OperationProgress progress)
        {
            _progress.Report(progress);
            long done = progress.TotalBytesDone;
            if (done > _counted)
            {
                long bytes = done - _counted;
                _counted = done;
                _item.Limiter.Take(bytes, Cancellation);
                _shared.Take(bytes, Cancellation);
            }
            else if (done < _counted)
            {
                _counted = done; // a new stage started from zero
            }
        }

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => _callbacks.ResolveConflictAsync(conflict);

        public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => _callbacks.ResolveErrorAsync(error);

        public Task<string?> AskPasswordAsync(string archivePath, bool wrongPassword) => _callbacks.AskPasswordAsync(archivePath, wrongPassword);
    }
}
