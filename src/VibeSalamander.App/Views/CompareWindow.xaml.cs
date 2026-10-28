// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Operations;
using VibeSalamander.Core.Tools;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views;

/// <summary>One difference in the Compare Directories list.</summary>
public sealed class CompareRow : System.ComponentModel.INotifyPropertyChanged
{
    private bool _checked = true;
    private string _actionGlyph = string.Empty, _actionText = string.Empty;

    public CompareRow(CompareDifference difference) => Difference = difference;

    public CompareDifference Difference { get; }
    public string Name => Difference.Name;
    public string Folder => Difference.Folder.Length == 0 ? string.Empty : Difference.Folder + "\\";
    public string Glyph => Difference.IsDirectory ? "" : "";

    public string LeftDetail => Detail(Difference.LeftTime, Difference.LeftSize, Difference.Kind is DifferenceKind.OnlyRight);
    public string RightDetail => Detail(Difference.RightTime, Difference.RightSize, Difference.Kind is DifferenceKind.OnlyLeft);

    /// <summary>The newer side stands out; a missing side is a dash.</summary>
    public double LeftOpacity => Difference.Kind == DifferenceKind.RightNewer ? 0.6 : 1;
    public double RightOpacity => Difference.Kind == DifferenceKind.LeftNewer ? 0.6 : 1;

    private string Detail(DateTime? time, long size, bool missing)
    {
        if (missing || time is not DateTime t)
            return "—";
        string when = $"{Formats.Date(t)} {Formats.Time(t)}";
        return Difference.IsDirectory ? when : $"{Formats.ShortSize(size)}  ·  {when}";
    }

    public string KindText => Loc.T(CompareWindow.KindName(Difference.Kind));

    public bool IsChecked
    {
        get => _checked;
        set
        {
            if (_checked == value)
                return;
            _checked = value;
            Changed(nameof(IsChecked));
            CheckedChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public string ActionGlyph => _actionGlyph;
    public string ActionText => _actionText;

    internal void SetAction(string glyph, string text)
    {
        _actionGlyph = glyph;
        _actionText = text;
        Changed(nameof(ActionGlyph));
        Changed(nameof(ActionText));
    }

    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    internal event EventHandler? CheckedChanged;

    private void Changed(string name) => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

/// <summary>
/// Compare Directories, the list: every difference between the two folders (all the way down with "subdirectories"),
/// filtered by kind, and Synchronize: one way (update or mirror) or both ways, through the transfer panel. Each row
/// shows what synchronizing would do with it; unchecked rows are left alone.
/// </summary>
public sealed partial class CompareWindow : Window
{
    private readonly MainViewModel _main;
    private readonly Func<string, string?, Task> _focusInPanel;
    private readonly Dialogs _dialogs;
    private readonly string _left, _right;
    private readonly CompareOptions _options;
    private readonly ObservableCollection<CompareRow> _shown = new();
    private List<CompareRow> _all = new();
    private readonly HashSet<DifferenceKind> _hidden = new();
    private CancellationTokenSource? _cts;
    private bool _waitingForTransfers;

    private static readonly (SyncDirection Direction, string Text)[] Directions =
    {
        (SyncDirection.LeftToRight, "Left → right"),
        (SyncDirection.RightToLeft, "Right → left"),
        (SyncDirection.Both, "Both ways (the newer wins)"),
    };

    public static string KindName(DifferenceKind kind) => kind switch
    {
        DifferenceKind.OnlyLeft => "Only on the left",
        DifferenceKind.OnlyRight => "Only on the right",
        DifferenceKind.LeftNewer => "Newer on the left",
        DifferenceKind.RightNewer => "Newer on the right",
        DifferenceKind.FileAndDirectory => "A file on one side, a directory on the other",
        _ => "Different",
    };

    public CompareWindow(string left, string right, CompareOptions options, ElementTheme theme, MainViewModel main,
        Func<string, string?, Task> focusInPanel)
    {
        _main = main;
        _focusInPanel = focusInPanel;
        _left = left;
        _right = right;
        _options = options;
        InitializeComponent();
        _dialogs = new Dialogs(() => Content.XamlRoot, () => RootGrid.ActualTheme, () => AppWindow.Id);
        Loc.Translate(RootGrid);
        Title = Loc.T(Title);
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(TitleBarArea);
        RootGrid.RequestedTheme = theme;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 720));
        string icon = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.ico");
        if (File.Exists(icon))
            AppWindow.SetIcon(icon);

        LeftText.Text = left;
        RightText.Text = right;
        foreach (var (_, text) in Directions)
            DirectionBox.Items.Add(Loc.T(text));
        DirectionBox.SelectedIndex = 0;
        Results.ItemsSource = _shown;
        _main.Transfers.Finished += TransferFinished;
        Closed += (_, _) =>
        {
            _cts?.Cancel();
            _main.Transfers.Finished -= TransferFinished;
        };
        RootGrid.Loaded += async (_, _) => await CompareAsync();
    }

    /// <summary>Compares again when the synchronization's transfers are all done.</summary>
    private async void TransferFinished(object? sender, TransferItemViewModel item)
    {
        if (_waitingForTransfers && _main.Transfers.ActiveCount == 0)
        {
            _waitingForTransfers = false;
            await CompareAsync();
        }
    }

    private SyncDirection Direction => Directions[Math.Max(0, DirectionBox.SelectedIndex)].Direction;

    private async Task CompareAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        Busy.IsActive = true;
        SyncButton.IsEnabled = false;
        CompareAgainButton.IsEnabled = false;
        var progress = new Progress<string>(folder => DispatcherQueue.TryEnqueue(() =>
        {
            if (_cts == cts)
                StatusText.Text = Loc.F("Comparing: {0}", folder.Length == 0 ? "." : folder);
        }));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            IReadOnlyList<CompareDifference> differences = await DirectorySync.CompareTreeAsync(_left, _right, _options, progress, cts.Token);
            _all = differences.Select(d => new CompareRow(d)).ToList();
            foreach (CompareRow row in _all)
                row.CheckedChanged += (_, _) => UpdateSyncButton();
            BuildFilters();
            Show();
            StatusText.Text = (_all.Count == 0 ? Loc.T("The directories are identical.")
                    : Loc.F("{0} differences", _all.Count.ToString("N0")))
                + "  ·  " + Loc.F("{0} s", clock.Elapsed.TotalSeconds.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture));
        }
        catch (OperationCanceledException)
        {
            if (_cts == cts)
                StatusText.Text = Loc.T("Comparison stopped");
        }
        finally
        {
            if (_cts == cts)
            {
                _cts = null;
                Busy.IsActive = false;
                CompareAgainButton.IsEnabled = true;
            }
            cts.Dispose();
        }
    }

    /// <summary>A toggle for each kind of difference that is there, with how many.</summary>
    private void BuildFilters()
    {
        FilterPanel.Children.Clear();
        foreach (IGrouping<DifferenceKind, CompareRow> kind in _all.GroupBy(r => r.Difference.Kind).OrderBy(g => g.Key))
        {
            DifferenceKind k = kind.Key;
            var toggle = new ToggleButton
            {
                Content = $"{Loc.T(KindName(k))} ({kind.Count():N0})",
                IsChecked = !_hidden.Contains(k),
                Style = (Style)Application.Current.Resources["VibeToolToggleButtonStyle"],
            };
            toggle.Click += (_, _) =>
            {
                if (toggle.IsChecked == true)
                    _hidden.Remove(k);
                else
                    _hidden.Add(k);
                Show();
            };
            FilterPanel.Children.Add(toggle);
        }
    }

    /// <summary>The rows of the kinds shown, each with what synchronizing would do.</summary>
    private void Show()
    {
        _shown.Clear();
        foreach (CompareRow row in _all.Where(r => !_hidden.Contains(r.Difference.Kind)))
            _shown.Add(row);
        UpdateActions();
    }

    private void UpdateActions()
    {
        SyncDirection direction = Direction;
        bool mirror = MirrorBox.IsChecked == true && direction != SyncDirection.Both;
        foreach (CompareRow row in _all)
        {
            SyncPlan plan = DirectorySync.Plan(new[] { row.Difference }, direction, mirror);
            if (plan.Actions.Count > 0)
            {
                (string glyph, string text) = plan.Actions[0].Operation switch
                {
                    SyncOperation.CopyToRight => ("", Loc.T("copy")),
                    SyncOperation.CopyToLeft => ("", Loc.T("copy")),
                    SyncOperation.DeleteRight => ("", Loc.T("delete")),
                    _ => ("", Loc.T("delete")),
                };
                row.SetAction(glyph, text);
            }
            else
            {
                row.SetAction(plan.Conflicts.Count > 0 ? "" : string.Empty, plan.Conflicts.Count > 0 ? Loc.T("conflict") : "—");
            }
        }
        UpdateSyncButton();
    }

    private SyncPlan CurrentPlan()
    {
        SyncDirection direction = Direction;
        bool mirror = MirrorBox.IsChecked == true && direction != SyncDirection.Both;
        return DirectorySync.Plan(_shown.Where(r => r.IsChecked).Select(r => r.Difference), direction, mirror);
    }

    private void UpdateSyncButton()
    {
        SyncPlan plan = CurrentPlan();
        int steps = plan.Actions.Count;
        SyncButton.IsEnabled = steps > 0 && _cts == null;
        SyncText.Text = steps == 0 ? Loc.T("Synchronize") : Loc.F("Synchronize ({0})", steps.ToString("N0"));
    }

    private void Direction_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MirrorBox.IsEnabled = Direction != SyncDirection.Both;
        if (_all.Count > 0)
            UpdateActions();
    }

    private void Mirror_Click(object sender, RoutedEventArgs e) => UpdateActions();

    private async void CompareAgain_Click(object sender, RoutedEventArgs e) => await CompareAsync();

    private IntPtr WindowHandle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    /// <summary>Copies (and, mirroring, deletes) through the transfer panel, after a summary to confirm.</summary>
    private async void Sync_Click(object sender, RoutedEventArgs e)
    {
        SyncPlan plan = CurrentPlan();
        if (plan.Actions.Count == 0)
            return;
        long bytes = plan.Actions.Where(a => a.Operation is SyncOperation.CopyToRight).Sum(a => a.Difference.LeftSize)
                   + plan.Actions.Where(a => a.Operation is SyncOperation.CopyToLeft).Sum(a => a.Difference.RightSize);
        var lines = new List<string>
        {
            Loc.F("{0} items are copied ({1}).", plan.Copies.ToString("N0"), Formats.ShortSize(bytes)),
        };
        if (plan.Deletes > 0)
            lines.Add(Loc.F("{0} items are moved to the Recycle Bin (they are only on the side being made the same).", plan.Deletes.ToString("N0")));
        if (plan.Conflicts.Count > 0)
            lines.Add(Loc.F("{0} conflicts are left as they are.", plan.Conflicts.Count.ToString("N0")));
        lines.Add(Loc.T("Files on the other side are overwritten without asking."));
        ContentDialog dialog = _dialogs.Create("Synchronize", new TextBlock { Text = string.Join("\n\n", lines), TextWrapping = TextWrapping.Wrap });
        dialog.PrimaryButtonText = Loc.T("Synchronize");
        dialog.CloseButtonText = Loc.T("Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await _dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            return;

        // deletes first (to the Recycle Bin), then the copies, folder by folder
        foreach (var side in new[] { (SyncOperation.DeleteRight, _right), (SyncOperation.DeleteLeft, _left) })
        {
            var paths = plan.Actions.Where(a => a.Operation == side.Item1).Select(a => Path.Combine(side.Item2, a.Difference.RelativePath)).ToList();
            if (paths.Count > 0)
                await Core.Interop.ShellOperations.DeleteAsync(WindowHandle, paths, permanent: false);
        }
        var engineOptions = new EngineOptions
        {
            ConfirmFileOverwrite = false,
            ConfirmHiddenSystemOverwrite = false,
            ConfirmDirectoryMerge = false,
            ClearReadOnlyFromCd = _main.Settings.General.ClearReadOnlyFromCd,
        };
        foreach (var (operation, from, to) in new[] { (SyncOperation.CopyToRight, _left, _right), (SyncOperation.CopyToLeft, _right, _left) })
        {
            foreach (IGrouping<string, SyncAction> folder in plan.Actions.Where(a => a.Operation == operation)
                         .GroupBy(a => a.Difference.Folder, StringComparer.OrdinalIgnoreCase))
            {
                var sources = folder.Select(a => Path.Combine(from, a.Difference.RelativePath)).ToList();
                string target = Path.Combine(to, folder.Key);
                string targetWithSlash = target.EndsWith('\\') ? target : target + "\\";
                _main.Transfers.Enqueue(OperationKind.Copy, sources, target, (engine, ct) =>
                {
                    engine.Options = engineOptions;
                    return engine.CopyAsync(sources, targetWithSlash, ct);
                });
                _waitingForTransfers = true;
            }
        }
        if (!_waitingForTransfers)
            await CompareAsync();
        else
            StatusText.Text = Loc.T("Synchronizing in the transfer panel...");
    }

    private async void Results_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (Results.SelectedItem is not CompareRow row)
            return;
        bool rightOnly = row.Difference.Kind == DifferenceKind.OnlyRight;
        string folder = Path.Combine(rightOnly ? _right : _left, row.Difference.Folder);
        await _focusInPanel(folder, row.Name);
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            if (_cts != null)
                _cts.Cancel();
            else
                Close();
        }
        else if (e.Key == Windows.System.VirtualKey.Space && Results.SelectedItem is CompareRow row &&
                 FocusManager.GetFocusedElement(Content.XamlRoot) is ListViewItem)
        {
            e.Handled = true;
            row.IsChecked = !row.IsChecked;
        }
        else if (e.Key == Windows.System.VirtualKey.F5)
        {
            e.Handled = true;
            _ = CompareAsync();
        }
    }
}
