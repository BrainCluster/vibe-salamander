// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core.Tools;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;
using VibeSalamander.Views.Viewer;

namespace VibeSalamander.Views;

/// <summary>
/// Multi-rename (Total Commander's Ctrl+M): the marked items get new names from a mask ([N] name, [E] extension, [C]
/// counter...), a search and replace (text or regular expression) and a case. Every new name is shown before anything
/// is renamed, with what's wrong with it; Rename renames the ones that are fine, in one go.
/// </summary>
internal sealed class MultiRenameWindow : Window
{
    private readonly MainViewModel _main;
    private readonly PaneViewModel _pane;
    private readonly IReadOnlyList<RenameSource> _items;
    private readonly MultiRenameOptions _options;
    private readonly Grid _root = new() { Padding = new Thickness(16, 0, 16, 12), RowSpacing = 10 };
    private readonly Grid _titleBar = new() { Height = 32 };
    private readonly TextBox _nameMask = new() { FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBox _extensionMask = new() { FontFamily = new FontFamily("Cascadia Mono,Consolas") };
    private readonly TextBox _search = new();
    private readonly TextBox _replace = new();
    private readonly CheckBox _regex = new();
    private readonly CheckBox _matchCase = new();
    private readonly CheckBox _inExtension = new();
    private readonly ComboBox _case = new() { MinWidth = 200 };
    private readonly NumberBox _start = new() { SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Minimum = 0, Maximum = 999999999, Width = 110 };
    private readonly NumberBox _step = new() { SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Minimum = -1000, Maximum = 1000, Width = 90 };
    private readonly NumberBox _digits = new() { SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact, Minimum = 1, Maximum = 10, Width = 90 };
    private readonly StackPanel _rows = new();
    private readonly TextBlock _summary = new() { VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
    private readonly Button _renameButton = new() { Style = (Style)Application.Current.Resources["AccentButtonStyle"], MinWidth = 120 };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private TextBox _lastMask;
    private IReadOnlyList<RenameLine> _plan = Array.Empty<RenameLine>();
    private bool _loading = true;

    private static readonly (string Name, RenameCase Case)[] Cases =
    {
        ("Unchanged", RenameCase.Unchanged), ("lower case", RenameCase.Lower), ("UPPER CASE", RenameCase.Upper),
        ("First letter capital", RenameCase.FirstLetter), ("Each Word Capital", RenameCase.EachWord),
    };

    public MultiRenameWindow(MainViewModel main, PaneViewModel pane, IReadOnlyList<RenameSource> items, ElementTheme theme)
    {
        _main = main;
        _pane = pane;
        _items = items;
        _options = main.Settings.MultiRename;
        _lastMask = _nameMask;
        _root.RequestedTheme = theme;
        SystemBackdrop = new Microsoft.UI.Xaml.Media.MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        Title = Loc.T("Multi-Rename");
        WindowPlacements.SetIcon(this);
        WindowPlacements.Restore(this, main.Settings.MultiRenameWindow, 1000, 720);
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(150);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) => UpdatePlan();
        BuildLayout();
        Content = _root;
        SetTitleBar(_titleBar);
        LoadOptions();
        _loading = false;
        UpdatePlan();
        Closed += (_, _) =>
        {
            ReadOptions();
            WindowPlacements.Save(this, main.Settings.MultiRenameWindow);
            main.SaveSettings();
        };
        _root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        _root.Loaded += (_, _) =>
        {
            _nameMask.Focus(FocusState.Programmatic);
            _nameMask.SelectAll();
        };
    }

    private static TextBlock Label(string text) => new() { Text = Loc.T(text), Opacity = 0.75, FontSize = 12, Margin = new Thickness(0, 0, 0, 2) };

    private static StackPanel Field(string label, UIElement control)
    {
        var panel = new StackPanel();
        panel.Children.Add(Label(label));
        panel.Children.Add(control);
        return panel;
    }

    private void BuildLayout()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var title = new TextBlock { Text = Loc.F("Multi-Rename: {0} items", _items.Count.ToString("N0")), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 };
        _titleBar.Children.Add(title);
        _root.Children.Add(_titleBar);

        // the masks (left) and search, case, counter (right)
        var form = new Grid { ColumnSpacing = 24 };
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        form.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(form, 1);
        _root.Children.Add(form);

        var masks = new StackPanel { Spacing = 8 };
        masks.Children.Add(Field("Name", _nameMask));
        var fields = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        foreach (var (token, tip) in new[]
                 {
                     ("[N]", "the name"), ("[N1-3]", "letters 1 to 3 of the name"), ("[E]", "the extension"), ("[C]", "the counter"),
                     ("[P]", "the folder's name"), ("[YMD]", "the date it was changed"), ("[hms]", "the time it was changed"),
                 })
        {
            var button = new Button { Content = token, Padding = new Thickness(6, 2, 6, 2), FontFamily = new FontFamily("Cascadia Mono,Consolas"), FontSize = 12 };
            ToolTipService.SetToolTip(button, Loc.T(tip));
            string t = token;
            button.Click += (_, _) => Insert(t);
            fields.Children.Add(button);
        }
        masks.Children.Add(fields);
        masks.Children.Add(Field("Extension", _extensionMask));
        _nameMask.GotFocus += (_, _) => _lastMask = _nameMask;
        _extensionMask.GotFocus += (_, _) => _lastMask = _extensionMask;
        form.Children.Add(masks);

        var right = new StackPanel { Spacing = 8 };
        var searchRow = new Grid { ColumnSpacing = 8 };
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        searchRow.Children.Add(Field("Search for", _search));
        var replaceField = Field("Replace with", _replace);
        Grid.SetColumn(replaceField, 1);
        searchRow.Children.Add(replaceField);
        right.Children.Add(searchRow);
        _regex.Content = Loc.T("Regular expression ($1 in the replacement)");
        _matchCase.Content = Loc.T("Match case");
        _inExtension.Content = Loc.T("Also in the extension");
        var checks = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        checks.Children.Add(_regex);
        checks.Children.Add(_matchCase);
        checks.Children.Add(_inExtension);
        right.Children.Add(checks);
        foreach (var (name, _) in Cases)
            _case.Items.Add(Loc.T(name));
        var caseAndCounter = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        caseAndCounter.Children.Add(Field("Case", _case));
        caseAndCounter.Children.Add(Field("Counter from", _start));
        caseAndCounter.Children.Add(Field("Step", _step));
        caseAndCounter.Children.Add(Field("Digits", _digits));
        right.Children.Add(caseAndCounter);
        Grid.SetColumn(right, 1);
        form.Children.Add(right);

        foreach (TextBox box in new[] { _nameMask, _extensionMask, _search, _replace })
            box.TextChanged += (_, _) => Changed();
        foreach (CheckBox box in new[] { _regex, _matchCase, _inExtension })
            box.Click += (_, _) => Changed();
        _case.SelectionChanged += (_, _) => Changed();
        foreach (NumberBox box in new[] { _start, _step, _digits })
            box.ValueChanged += (_, _) => Changed();

        // the plan: old name → new name, and what's wrong
        var header = new Grid { Padding = new Thickness(10, 6, 10, 6), ColumnSpacing = 12 };
        AddColumns(header);
        header.Children.Add(new TextBlock { Text = Loc.T("Old name"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        var newHeader = new TextBlock { Text = Loc.T("New name"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        Grid.SetColumn(newHeader, 1);
        header.Children.Add(newHeader);
        var list = new Grid();
        list.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        list.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        list.Children.Add(header);
        var scroller = new ScrollViewer { Content = _rows, Padding = new Thickness(0, 0, 0, 4) };
        Grid.SetRow(scroller, 1);
        list.Children.Add(scroller);
        var frame = new Border
        {
            Child = list,
            CornerRadius = new CornerRadius(6),
            BorderThickness = new Thickness(1),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
        };
        Grid.SetRow(frame, 2);
        _root.Children.Add(frame);

        // the summary and the buttons
        var bottom = new Grid { ColumnSpacing = 8 };
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        bottom.Children.Add(_summary);
        var reset = new Button { Content = Loc.T("Reset") };
        reset.Click += (_, _) =>
        {
            _loading = true;
            var fresh = new MultiRenameOptions();
            Fill(fresh);
            _loading = false;
            UpdatePlan();
        };
        Grid.SetColumn(reset, 1);
        bottom.Children.Add(reset);
        _renameButton.Content = Loc.T("Rename");
        _renameButton.Click += async (_, _) => await RenameAsync();
        Grid.SetColumn(_renameButton, 2);
        bottom.Children.Add(_renameButton);
        var close = new Button { Content = Loc.T("Close") };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 3);
        bottom.Children.Add(close);
        Grid.SetRow(bottom, 3);
        _root.Children.Add(bottom);
    }

    private static void AddColumns(Grid grid)
    {
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
    }

    private void Insert(string token)
    {
        TextBox box = _lastMask;
        int at = box.SelectionStart;
        box.Text = box.Text.Remove(at, box.SelectionLength).Insert(at, token);
        box.SelectionStart = at + token.Length;
        box.Focus(FocusState.Programmatic);
    }

    private void LoadOptions() => Fill(_options);

    private void Fill(MultiRenameOptions o)
    {
        _nameMask.Text = o.NameMask;
        _extensionMask.Text = o.ExtensionMask;
        _search.Text = o.Search;
        _replace.Text = o.Replace;
        _regex.IsChecked = o.RegularExpression;
        _matchCase.IsChecked = o.MatchCase;
        _inExtension.IsChecked = o.ReplaceInExtension;
        _case.SelectedIndex = Math.Max(0, Array.FindIndex(Cases, c => c.Case == o.Case));
        _start.Value = o.CounterStart;
        _step.Value = o.CounterStep;
        _digits.Value = o.CounterDigits;
    }

    private void ReadOptions()
    {
        _options.NameMask = _nameMask.Text;
        _options.ExtensionMask = _extensionMask.Text;
        _options.Search = _search.Text;
        _options.Replace = _replace.Text;
        _options.RegularExpression = _regex.IsChecked == true;
        _options.MatchCase = _matchCase.IsChecked == true;
        _options.ReplaceInExtension = _inExtension.IsChecked == true;
        _options.Case = Cases[Math.Max(0, _case.SelectedIndex)].Case;
        _options.CounterStart = double.IsNaN(_start.Value) ? 1 : (int)_start.Value;
        _options.CounterStep = double.IsNaN(_step.Value) ? 1 : (int)_step.Value;
        _options.CounterDigits = double.IsNaN(_digits.Value) ? 1 : (int)_digits.Value;
    }

    private void Changed()
    {
        if (_loading)
            return;
        _timer.Stop();
        _timer.Start();
    }

    private void UpdatePlan()
    {
        ReadOptions();
        _plan = MultiRename.Plan(_items, _options);
        _rows.Children.Clear();
        Brush problemBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x50, 0x50));
        Brush changedBrush = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        const int Shown = 2000;
        foreach (RenameLine line in _plan.Take(Shown))
        {
            var row = new Grid { Padding = new Thickness(10, 3, 10, 3), ColumnSpacing = 12 };
            AddColumns(row);
            row.Children.Add(new TextBlock { Text = line.OldName, TextTrimming = TextTrimming.CharacterEllipsis });
            var name = new TextBlock
            {
                Text = line.Problem == null ? line.NewName : line.NewName + "  —  " + line.Problem,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Foreground = line.Problem != null ? problemBrush : line.Changes ? changedBrush : (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
            };
            ToolTipService.SetToolTip(name, name.Text);
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            _rows.Children.Add(row);
        }
        if (_plan.Count > Shown)
            _rows.Children.Add(new TextBlock { Text = Loc.F("... and {0} more", (_plan.Count - Shown).ToString("N0")), Margin = new Thickness(10, 4, 10, 4), Opacity = 0.7 });
        int changes = _plan.Count(l => l.Changes);
        int problems = _plan.Count(l => l.Problem != null);
        _summary.Text = problems > 0
            ? Loc.F("{0} names change, {1} can't (they stay as they are).", changes.ToString("N0"), problems.ToString("N0"))
            : Loc.F("{0} names change.", changes.ToString("N0"));
        _renameButton.IsEnabled = changes > 0;
    }

    private async Task RenameAsync()
    {
        UpdatePlan();
        var plan = _plan;
        _renameButton.IsEnabled = false;
        IReadOnlyList<string> errors = await Task.Run(() => MultiRename.Apply(plan));
        int done = plan.Count(l => l.Changes) - errors.Count(e => !e.StartsWith('"'));
        await _pane.RefreshAsync();
        if (errors.Count > 0)
            _main.Notify(Loc.F("{0} items renamed; problems: {1}", done.ToString("N0"), string.Join("; ", errors.Take(5))), isError: true);
        else
            _main.Notify(Loc.F("{0} items renamed.", done.ToString("N0")), isError: false);
        Close();
    }
}
