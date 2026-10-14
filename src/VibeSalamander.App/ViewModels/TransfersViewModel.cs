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

    public TransferItemViewModel(OperationKind kind, IReadOnlyList<string> sources, string? target)
    {
        Kind = kind;
        Sources = sources;
        Target = target;
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
    public IReadOnlyList<string> Sources { get; }
    public string? Target { get; }
    public string Title { get; }
    public string Route { get; }

    internal CancellationTokenSource Cancellation { get; } = new();

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

    // slots: how many run at once comes from the configuration (General → parallel transfers) and may change
    private int _running;
    private readonly LinkedList<TaskCompletionSource<bool>> _waiting = new();

    /// <param name="engineOptions">Confirmations the engine follows (read when an operation starts).</param>
    /// <param name="maxParallel">How many operations run at the same time; the rest wait as "Queued".</param>
    public TransfersViewModel(DispatcherQueue dispatcher, Views.Dialogs dialogs, Func<EngineOptions> engineOptions, Func<int> maxParallel)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        _engineOptions = engineOptions;
        _maxParallel = maxParallel;
        Items.CollectionChanged += (_, _) => Changed();
    }

    /// <summary>Waits for a free slot (on the UI thread). Throws OperationCanceledException when cancelled while queued.</summary>
    private async Task AcquireSlotAsync(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_running < Math.Max(1, _maxParallel()))
        {
            _running++;
            return;
        }
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        LinkedListNode<TaskCompletionSource<bool>> node = _waiting.AddLast(tcs);
        using (ct.Register(() => _dispatcher.TryEnqueue(() =>
               {
                   if (node.List != null)
                   {
                       _waiting.Remove(node);
                       tcs.TrySetCanceled();
                   }
               })))
        {
            await tcs.Task;
        }
    }

    private void ReleaseSlot()
    {
        _running = Math.Max(0, _running - 1);
        StartWaiting();
    }

    /// <summary>Starts queued operations while slots are free (also after the limit was raised).</summary>
    public void StartWaiting()
    {
        while (_running < Math.Max(1, _maxParallel()) && _waiting.First is { } first)
        {
            _waiting.RemoveFirst();
            _running++;
            first.Value.TrySetResult(true);
        }
    }

    public ObservableCollection<TransferItemViewModel> Items { get; } = new();

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
            var job = new PluginJob(item, new ItemCallbacks(item, _dispatcher, _dialogs), new ItemProgress(item, _dispatcher));
            item.Pausable = job;
            return run(job);
        });

    private TransferItemViewModel Add(OperationKind kind, IReadOnlyList<string> sources, string? target,
        Func<TransferItemViewModel, Task<OperationResult>> run)
    {
        var item = new TransferItemViewModel(kind, sources, target);
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
            await AcquireSlotAsync(item.Cancellation.Token);
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
            ReleaseSlot();
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

        public Task<ErrorChoice> ResolveErrorAsync(OperationError error) =>
            AskAsync(() => _dialogs.ErrorAsync(error, _item.Title), ErrorChoice.Cancel);

        public Task<string?> AskPasswordAsync(string archivePath, bool wrongPassword) =>
            AskAsync<string?>(() => _dialogs.PasswordAsync(archivePath, wrongPassword, _item.Title), null);

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
        private volatile bool _paused;

        public PluginJob(TransferItemViewModel item, ItemCallbacks callbacks, ItemProgress progress)
        {
            _item = item;
            _callbacks = callbacks;
            _progress = progress;
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

        public void Report(OperationProgress progress) => _progress.Report(progress);

        public Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict) => _callbacks.ResolveConflictAsync(conflict);

        public Task<ErrorChoice> ResolveErrorAsync(OperationError error) => _callbacks.ResolveErrorAsync(error);

        public Task<string?> AskPasswordAsync(string archivePath, bool wrongPassword) => _callbacks.AskPasswordAsync(archivePath, wrongPassword);
    }
}
