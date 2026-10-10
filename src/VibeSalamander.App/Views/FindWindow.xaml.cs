using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Search;

namespace VibeSalamander.Views;

/// <summary>One search result row.</summary>
public sealed class FindResult
{
    public FindResult(FileEntry entry)
    {
        Entry = entry;
    }

    public FileEntry Entry { get; }
    public string Name => Entry.Name;
    public string Directory => Path.GetDirectoryName(Entry.FullPath) ?? string.Empty;
    public string SizeText => Entry.IsDirectory ? "DIR" : Formats.ExactSize(Entry.Size);
    public string DateText => $"{Formats.Date(Entry.LastWrite)} {Formats.Time(Entry.LastWrite)}";
    public string Glyph => Entry.IsDirectory ? "" : "";
}

/// <summary>Find Files and Directories (Alt+F7). Non-modal, like Salamander's Find window.</summary>
public sealed partial class FindWindow : Window
{
    private readonly Func<string, string, Task> _focusInPanel;
    private readonly ObservableCollection<FindResult> _results = new();
    private readonly List<FindResult> _pending = new();
    private readonly object _pendingLock = new();
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _flushTimer;
    private CancellationTokenSource? _cts;

    /// <param name="startDirectory">Initial "Look in" directory (the active panel's path).</param>
    /// <param name="focusInPanel">Navigates the active panel to (directory, name).</param>
    private readonly Func<Core.Settings.ConfirmationOptions> _confirm;
    private readonly Dialogs _dialogs;
    private bool _closeConfirmed;

    public FindWindow(string startDirectory, ElementTheme theme, Func<string, string, Task> focusInPanel,
                      Func<Core.Settings.ConfirmationOptions> confirm)
    {
        InitializeComponent();
        _focusInPanel = focusInPanel;
        _confirm = confirm;
        _dialogs = new Dialogs(() => Content.XamlRoot, () => RootGrid.ActualTheme, () => AppWindow.Id);
        AppWindow.Closing += async (_, args) =>
        {
            // Configuration → Confirmations → "Do you want to close Find?"
            if (_closeConfirmed || !_confirm().CloseFind || (_results.Count == 0 && _cts == null))
                return;
            args.Cancel = true;
            await CloseWithConfirmationAsync();
        };
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarText);
        RootGrid.RequestedTheme = theme;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(980, 640));
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);

        LookInBox.Text = startDirectory;
        Results.ItemsSource = _results;
        _flushTimer = DispatcherQueue.CreateTimer();
        _flushTimer.Interval = TimeSpan.FromMilliseconds(200);
        _flushTimer.Tick += (_, _) => Flush();
        Closed += (_, _) => _cts?.Cancel();
        RootGrid.Loaded += (_, _) => { NamedBox.Focus(FocusState.Programmatic); NamedBox.SelectAll(); };
    }

    public void SetLookIn(string directory) => LookInBox.Text = directory;

    private async void FindButton_Click(object sender, RoutedEventArgs e)
    {
        if (_cts != null)
        {
            await StopWithConfirmationAsync(); // "Stop"
            return;
        }
        await RunSearchAsync();
    }

    private async Task<bool> ConfirmAsync(string message, string button)
    {
        ContentDialog dialog = _dialogs.Create("Find", new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap });
        dialog.PrimaryButtonText = button;
        dialog.CloseButtonText = "Cancel";
        dialog.DefaultButton = ContentDialogButton.Primary;
        return await _dialogs.ShowDialogAsync(dialog) == ContentDialogResult.Primary;
    }

    /// <summary>Confirmations → "Do you want to stop searching?"</summary>
    private async Task StopWithConfirmationAsync()
    {
        if (_cts == null)
            return;
        if (_confirm().StopFind && !await ConfirmAsync("Do you want to stop searching?", "Stop"))
            return;
        _cts?.Cancel();
    }

    private async Task CloseWithConfirmationAsync()
    {
        if (!await ConfirmAsync("Do you want to close Find?", "Close"))
            return;
        _closeConfirmed = true;
        _cts?.Cancel();
        Close();
    }

    private async Task RunSearchAsync()
    {
        string directory = LookInBox.Text.Trim();
        if (!System.IO.Directory.Exists(directory))
        {
            StatusText.Text = $"Directory not found: {directory}";
            return;
        }
        var options = new SearchOptions
        {
            Directory = directory,
            Mask = new WildcardMask(string.IsNullOrWhiteSpace(NamedBox.Text) ? "*.*" : NamedBox.Text),
            IncludeSubdirectories = SubdirsBox.IsChecked == true,
            IncludeDirectories = DirectoriesBox.IsChecked == true,
            IncludeHidden = HiddenBox.IsChecked == true,
            ContainingText = string.IsNullOrEmpty(ContainingBox.Text) ? null : ContainingBox.Text,
            MatchCase = MatchCaseBox.IsChecked == true,
        };

        _results.Clear();
        _cts = new CancellationTokenSource();
        FindButton.Content = "Stop";
        Busy.IsActive = true;
        _flushTimer.Start();
        var progress = new Progress<string>(dir => DispatcherQueue.TryEnqueue(() => { if (_cts != null) StatusText.Text = "Searching: " + dir; }));
        string status;
        try
        {
            int count = await FileSearcher.SearchAsync(options, entry =>
            {
                lock (_pendingLock)
                    _pending.Add(new FindResult(entry));
            }, progress, _cts.Token);
            status = $"{count:N0} item{(count == 1 ? "" : "s")} found";
        }
        catch (OperationCanceledException)
        {
            status = "Search stopped";
        }
        _flushTimer.Stop();
        Flush();
        _cts.Dispose();
        _cts = null;
        FindButton.Content = "Find";
        Busy.IsActive = false;
        StatusText.Text = status + $" ({_results.Count:N0} listed)";
        if (_results.Count > 0)
        {
            Results.SelectedIndex = 0;
            Results.Focus(FocusState.Programmatic);
        }
    }

    /// <summary>Moves found items to the list in batches (keeps the UI responsive for large result sets).</summary>
    private void Flush()
    {
        List<FindResult> batch;
        lock (_pendingLock)
        {
            if (_pending.Count == 0)
                return;
            batch = new List<FindResult>(_pending);
            _pending.Clear();
        }
        foreach (FindResult r in batch)
            _results.Add(r);
    }

    private void Results_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        FocusButton.IsEnabled = Results.SelectedItem is FindResult;

    private async void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e) => await FocusSelectedAsync();

    private async void FocusButton_Click(object sender, RoutedEventArgs e) => await FocusSelectedAsync();

    private async Task FocusSelectedAsync()
    {
        if (Results.SelectedItem is FindResult r)
            await _focusInPanel(r.Directory, r.Name);
    }

    private async void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            if (FocusManager.GetFocusedElement(Content.XamlRoot) is ListViewItem or ListView)
                await FocusSelectedAsync();
            else if (_cts == null)
                await RunSearchAsync();
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (_cts != null)
                await StopWithConfirmationAsync();
            else if (_confirm().CloseFind && _results.Count > 0)
                await CloseWithConfirmationAsync(); // Window.Close() doesn't raise AppWindow.Closing: ask here
            else
                Close();
        }
    }
}
