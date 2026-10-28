// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Core.Tools;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;
using VibeSalamander.Views;

namespace VibeSalamander;

// MainWindow: quick view in the other panel (Ctrl+Q), names into the command line (Ctrl+Enter), its history (Alt+F8),
// and the multi-rename tool.
public sealed partial class MainWindow
{
    private QuickViewPanel? _quickView;
    private bool _quickViewOn;

    /// <summary>Ctrl+Q: the other panel shows the item under the caret of the active one, and follows it.</summary>
    public void ToggleQuickView()
    {
        _quickViewOn = !_quickViewOn;
        if (_quickView == null)
        {
            _quickView = new QuickViewPanel();
            Canvas.SetZIndex(_quickView, 10);
            PanelsGrid.Children.Add(_quickView);
            _vm.Left.PropertyChanged += QuickView_PaneChanged;
            _vm.Right.PropertyChanged += QuickView_PaneChanged;
        }
        PlaceQuickView();
        if (!_quickViewOn)
            _quickView.Clear();
        PaneView(_vm.Active).FocusList();
    }

    private void QuickView_PaneChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (!_quickViewOn || _quickView == null)
            return;
        if (e.PropertyName == nameof(PaneViewModel.IsActive))
            PlaceQuickView();
        else if (sender == _vm.Active && e.PropertyName is nameof(PaneViewModel.CaretIndex) or nameof(PaneViewModel.Path))
            _quickView.Show(_vm.Active.FocusedItem?.Entry);
    }

    /// <summary>Over the panel that isn't active (Tab switches which one shows the file).</summary>
    private void PlaceQuickView()
    {
        if (_quickView == null)
            return;
        _quickView.Visibility = _quickViewOn ? Visibility.Visible : Visibility.Collapsed;
        if (!_quickViewOn)
            return;
        Grid.SetColumn(_quickView, _vm.Active == _vm.Left ? 2 : 0);
        _quickView.Show(_vm.Active.FocusedItem?.Entry);
    }

    /// <summary>Ctrl+Enter, Ctrl+Shift+Enter, Ctrl+P: the text goes at the end of the command line (in quotes when it has spaces).</summary>
    public void AddToCommandLine(string text)
    {
        if (string.IsNullOrEmpty(text))
            return;
        EnsureCommandLine();
        if (text.Contains(' ') && !text.StartsWith('"'))
            text = "\"" + text + "\"";
        string current = CommandLine.Text;
        CommandLine.Text = current.Length == 0 || current.EndsWith(' ') ? current + text + " " : current + " " + text + " ";
        CommandLine.SelectionStart = CommandLine.Text.Length;
    }

    /// <summary>The command line is shown (it was hidden: it stays shown).</summary>
    private void EnsureCommandLine()
    {
        if (CommandLineBar.Visibility == Visibility.Visible)
            return;
        _vm.Settings.ShowCommandLine = true;
        ShowCommandLineItem.IsChecked = true;
        CommandLineBar.Visibility = Visibility.Visible;
    }

    /// <summary>Alt+F8: the earlier commands; the one chosen goes into the command line.</summary>
    public void ShowCommandHistory(PaneViewModel pane)
    {
        var entries = new List<MenuEntry> { MenuEntry.Header("Command line history") };
        foreach (string command in _vm.Settings.CommandLineHistory.Take(30))
        {
            string text = command;
            entries.Add(new MenuEntry(text, () =>
            {
                EnsureCommandLine();
                CommandLine.Text = text;
                CommandLine.Focus(FocusState.Programmatic);
                CommandLine.SelectionStart = text.Length;
            }, ""));
        }
        if (entries.Count == 1)
            entries.Add(new MenuEntry("(none yet)", IsEnabled: false));
        ShowMenu(pane, entries, atCaret: false);
    }

    /// <summary>The command line's -L / -R / -A folders and -P panel (also from a second start).</summary>
    public Core.Settings.StartupArguments? StartupArguments { get; set; }

    public async Task ApplyArgumentsAsync(Core.Settings.StartupArguments arguments)
    {
        if (arguments.Panel == 1)
            _vm.Activate(_vm.Left);
        else if (arguments.Panel == 2)
            _vm.Activate(_vm.Right);
        async Task GoAsync(PaneViewModel pane, string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;
            if (File.Exists(path))
                await pane.NavigateAsync(Path.GetDirectoryName(path)!, Path.GetFileName(path));
            else if (Directory.Exists(path))
                await pane.NavigateAsync(path);
            else
                Notify(Loc.F("\"{0}\" doesn't exist.", path), isError: true, log: false);
        }
        await GoAsync(_vm.Left, arguments.Left);
        await GoAsync(_vm.Right, arguments.Right);
        await GoAsync(_vm.Active, arguments.Active);
        PaneView(_vm.Active).FocusList();
    }

    public void ShowMultiRename(PaneViewModel pane, IReadOnlyList<RenameSource> items)
    {
        var window = new MultiRenameWindow(_vm, pane, items, RootGrid.ActualTheme);
        WindowOwner.Set(window, WindowHandle);
        window.Closed += (_, _) => PaneView(pane).FocusList();
        window.Activate();
    }

    private async void FirstSteps_Click(object sender, RoutedEventArgs e) => await FirstStepsAsync();

    /// <summary>Help → First Steps: whose shortcuts, which look and which language, on one page (all can be changed later in Configuration).</summary>
    private async Task FirstStepsAsync()
    {
        Core.Settings.AppSettings s = _vm.Settings;
        ComboBox Combo(IEnumerable<string> items, int selected)
        {
            var combo = new ComboBox { MinWidth = 300 };
            foreach (string item in items)
                combo.Items.Add(item);
            combo.SelectedIndex = Math.Max(0, selected);
            return combo;
        }
        ComboBox keys = Combo(new[] { Loc.T("Altap / Open Salamander"), Loc.T("Total Commander") }, (int)s.KeyScheme);
        var themes = new[] { Core.Settings.AppTheme.System, Core.Settings.AppTheme.Light, Core.Settings.AppTheme.Dark };
        ComboBox theme = Combo(new[] { Loc.T("Use system setting"), Loc.T("Light"), Loc.T("Dark") }, Array.IndexOf(themes, s.Theme));
        var schemes = UiState.Schemes;
        ComboBox colors = Combo(schemes.Select(x => Loc.T(x.Name)), Array.FindIndex(schemes, x => x.Id == s.Colors.Scheme));
        var languages = new List<(string Name, string Code)> { (Loc.T("Windows language"), string.Empty) };
        languages.AddRange(Loc.Languages.Select(l => (l.Name, l.Code)));
        ComboBox language = Combo(languages.Select(l => l.Name), languages.FindIndex(l => l.Code == (s.General.Language ?? string.Empty)));
        var body = new StackPanel { Spacing = 12, MinWidth = 420 };
        void Row(string label, string? hint, FrameworkElement control)
        {
            var panel = new StackPanel { Spacing = 4 };
            panel.Children.Add(new TextBlock { Text = Loc.T(label), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (hint != null)
                panel.Children.Add(new TextBlock { Text = Loc.T(hint), Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(control);
            body.Children.Add(panel);
        }
        Row("Shortcuts like", "F5 copies and F6 moves in both; the rest follows the program you know.", keys);
        Row("Theme", null, theme);
        Row("Colors of the panels", null, colors);
        Row("Language", "Changing it restarts Vibe Salamander.", language);
        body.Children.Add(new TextBlock
        {
            Text = Loc.T("All of this, and much more, is in Options → Configuration."),
            Opacity = 0.7, FontSize = 12, TextWrapping = TextWrapping.Wrap,
        });
        ContentDialog dialog = Dialogs.Create("First Steps", body);
        dialog.PrimaryButtonText = Loc.T("OK");
        dialog.CloseButtonText = Loc.T("Cancel");
        dialog.DefaultButton = ContentDialogButton.Primary;
        if (await Dialogs.ShowDialogAsync(dialog) != ContentDialogResult.Primary)
            return;
        var scheme = (Core.Commands.KeyScheme)Math.Max(0, keys.SelectedIndex);
        if (scheme != s.KeyScheme)
            _vm.SetKeyScheme(scheme); // your own key changes start over from those shortcuts
        s.Theme = themes[Math.Max(0, theme.SelectedIndex)];
        s.Colors.Scheme = schemes[Math.Max(0, colors.SelectedIndex)].Id;
        string code = languages[Math.Max(0, language.SelectedIndex)].Code;
        bool restart = code != (s.General.Language ?? string.Empty);
        s.General.Language = code;
        ApplySettings(displayChanged: true);
        _vm.SaveSettings();
        if (restart)
            RestartApp();
    }
}
