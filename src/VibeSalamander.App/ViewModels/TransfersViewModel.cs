using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using VibeSalamander.Core;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Operations;

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
        string what = sources.Count == 1 ? Path.GetFileName(sources[0].TrimEnd('\\')) : $"{sources.Count} items";
        string verb = kind switch { OperationKind.Move => "Move", OperationKind.Delete => "Delete", _ => "Copy" };
        Title = $"{verb} {what}";
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
    internal FileOperationEngine? Engine { get; set; }
    public OperationResult? Result { get; internal set; }

    public string Glyph => Kind switch { OperationKind.Move => "", OperationKind.Delete => "", _ => "" };

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
        TransferState.Queued => "Queued",
        TransferState.Preparing => "Preparing",
        TransferState.Running => $"{_percent:0} %",
        TransferState.Paused => "Paused",
        TransferState.WaitingForAnswer => "Needs your answer",
        TransferState.Completed => "Done",
        TransferState.CompletedWithErrors => $"Done, {Result?.Failed ?? 0} failed",
        TransferState.Cancelled => "Cancelled",
        _ => "Failed",
    };

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
            DetailText = "Reading directory tree...";
            return;
        }
        if (_state == TransferState.Preparing)
            State = TransferState.Running;
        _indeterminate = false;
        OnPropertyChanged(nameof(IsIndeterminate));
        Percent = Math.Round(p.TotalFraction * 100, 1);
        CurrentFile = p.CurrentSource;
        DetailText = Kind == OperationKind.Delete || p.TotalBytes == 0
            ? $"{p.ItemsDone:N0} / {p.ItemsTotal:N0} items"
            : $"{Formats.ShortSize(p.TotalBytesDone)} / {Formats.ShortSize(p.TotalBytes)}  ·  {p.ItemsDone:N0} / {p.ItemsTotal:N0}";
        if (_state == TransferState.Running)
        {
            SpeedText = p.BytesPerSecond > 0 ? Formats.ShortSize((long)p.BytesPerSecond) + "/s" : string.Empty;
            RemainingText = p.Remaining is TimeSpan r
                ? (r.TotalHours >= 1 ? $"{(int)r.TotalHours}:{r.Minutes:00}:{r.Seconds:00}" : $"{r.Minutes}:{r.Seconds:00}") + " left"
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
        if (Engine == null || _state is not (TransferState.Running or TransferState.Preparing))
            return;
        Engine.Paused = true;
        State = TransferState.Paused;
    }

    public void Resume()
    {
        if (Engine == null || _state != TransferState.Paused)
            return;
        Engine.Paused = false;
        State = _indeterminate ? TransferState.Preparing : TransferState.Running;
    }

    public void Cancel()
    {
        if (!IsActive)
            return;
        Cancellation.Cancel();
        if (Engine != null)
            Engine.Paused = false; // let a paused engine notice the cancellation
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
    /// <summary>How many operations run at the same time; the rest wait as "Queued".</summary>
    public const int MaxParallel = 3;

    private readonly DispatcherQueue _dispatcher;
    private readonly Views.Dialogs _dialogs;
    private readonly SemaphoreSlim _slots = new(MaxParallel, MaxParallel);

    public TransfersViewModel(DispatcherQueue dispatcher, Views.Dialogs dialogs)
    {
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        Items.CollectionChanged += (_, _) => Changed();
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
            if (running > 0) parts.Add($"{running} running");
            if (paused > 0) parts.Add($"{paused} paused");
            if (queued > 0) parts.Add($"{queued} queued");
            if (done > 0) parts.Add($"{done} finished");
            return parts.Count == 0 ? "No transfers" : string.Join(", ", parts);
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
        Func<FileOperationEngine, CancellationToken, Task<OperationResult>> run)
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

    private async Task RunAsync(TransferItemViewModel item, Func<FileOperationEngine, CancellationToken, Task<OperationResult>> run)
    {
        try
        {
            await _slots.WaitAsync(item.Cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            item.State = TransferState.Cancelled;
            Finished?.Invoke(this, item);
            return;
        }

        Exception? failure = null;
        try
        {
            // cancelled between getting the slot and this continuation (it already shows "Cancelled")
            item.Cancellation.Token.ThrowIfCancellationRequested();
            var engine = new FileOperationEngine(new ItemCallbacks(item, _dispatcher, _dialogs), new ItemProgress(item, _dispatcher));
            item.Engine = engine;
            item.State = TransferState.Preparing;
            OperationResult result = await run(engine, item.Cancellation.Token);
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
            item.State = TransferState.Failed;
            failure = ex;
        }
        finally
        {
            // free the slot before any message, so the queue does not wait for the user
            _slots.Release();
        }
        Finished?.Invoke(this, item);
        if (failure != null)
        {
            try
            {
                await _dialogs.MessageAsync(item.Title, failure.Message);
            }
            catch (Exception)
            {
                // the window is closing; the panel shows "Failed" anyway
            }
        }
    }

    public void Remove(TransferItemViewModel item)
    {
        if (!item.IsActive)
            Items.Remove(item);
    }

    public void ClearFinished()
    {
        foreach (var item in Items.Where(i => !i.IsActive).ToList())
            Items.Remove(item);
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
}
