// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Tools;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views.Viewer;

/// <summary>
/// Compare Files (Ctrl+Shift+C): two text files side by side in Visual Studio Code's diff editor (Monaco), the
/// changes highlighted down to the characters, both sides editable. A change goes across with Alt+→ / Alt+← (or the
/// arrows in the margin), F7 / Shift+F7 go from change to change, each side saves on its own (safely, in its own
/// encoding and line endings). Binary files show the bytes that differ, as hex rows that line up.
/// </summary>
internal sealed class CompareFilesWindow : Window
{
    private readonly MainViewModel _main;
    private readonly ViewerOptions _options;
    private readonly Dialogs _dialogs;
    private readonly Grid _root = new();
    private readonly Grid _titleBar = new() { Height = 32 };
    private readonly TextBlock _titleText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12 };
    private readonly CommandBar _bar = new()
    {
        DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
        IsDynamicOverflowEnabled = true,
        Background = new SolidColorBrush(Colors.Transparent),
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly InfoBar _infoBar = new() { IsOpen = false, Margin = new Thickness(8, 0, 8, 4) };
    private readonly TextBlock _leftName = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _rightName = new() { TextTrimming = TextTrimming.CharacterEllipsis, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock _leftInfo = new() { Opacity = 0.7, FontSize = 12 };
    private readonly TextBlock _rightInfo = new() { Opacity = 0.7, FontSize = 12 };
    private readonly Grid _body = new();
    private readonly WebView2 _web = new();
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly List<ICommandBarElement> _textTools = new();
    private readonly Dictionary<int, TaskCompletionSource<string>> _textRequests = new();

    private CoreWebView2? _core;
    private bool _ready;
    private string _left, _right;
    private Side _l = new(), _r = new();
    private bool _binary;
    private int _nextRequest = 1;
    private bool _closeConfirmed;
    private (string Left, string Right, string Language, bool ReadOnly)? _pending;

    /// <summary>One side's file: how it was read, and the version last saved.</summary>
    private sealed class Side
    {
        public TextEncoding Encoding = TextEncodings.Utf8;
        public LineEnding LineEnding = LineEnding.CrLf;
        public int Saved = -1, Version;
        public bool Dirty => Saved >= 0 && Version != Saved;
    }

    public CompareFilesWindow(MainViewModel main, string left, string right, ElementTheme theme)
    {
        _main = main;
        _options = main.Settings.Viewer;
        _left = left;
        _right = right;
        _dialogs = new Dialogs(() => Content.XamlRoot, () => _root.ActualTheme, () => AppWindow.Id);
        _root.RequestedTheme = theme;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        WindowPlacements.SetIcon(this);
        WindowPlacements.Restore(this, _options.CompareWindow, 1400, 860);
        BuildLayout();
        Content = _root;
        SetTitleBar(_titleBar);
        AppWindow.Closing += async (_, args) =>
        {
            if (_closeConfirmed || !(_l.Dirty || _r.Dirty))
                return;
            args.Cancel = true;
            if (await ConfirmCloseAsync())
            {
                _closeConfirmed = true;
                Close();
            }
        };
        Closed += (_, _) =>
        {
            WindowPlacements.Save(this, _options.CompareWindow);
            _main.SaveSettings();
        };
        _root.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                e.Handled = true;
                Close();
            }
        };
        _ = OpenAsync();
    }

    // ---- layout ---------------------------------------------------------------------------------------------

    private AppBarButton Button(string label, string glyph, Action click, string? keys = null)
    {
        var button = new AppBarButton { Label = Loc.T(label), Icon = new FontIcon { Glyph = glyph } };
        if (keys != null)
            ToolTipService.SetToolTip(button, Loc.T(label) + " (" + keys + ")");
        button.Click += (_, _) => click();
        return button;
    }

    private AppBarToggleButton Toggle(string label, string glyph, bool value, Action<bool> set)
    {
        var button = new AppBarToggleButton { Label = Loc.T(label), Icon = new FontIcon { Glyph = glyph }, IsChecked = value };
        button.Click += (_, _) =>
        {
            set(button.IsChecked == true);
            SendOptions();
        };
        return button;
    }

    private void BuildLayout()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleContent = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.png");
        if (File.Exists(iconPath))
            titleContent.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)), Width = 16, Height = 16 });
        titleContent.Children.Add(_titleText);
        _titleBar.Children.Add(titleContent);
        _root.Children.Add(_titleBar);

        _bar.PrimaryCommands.Add(Button("Previous difference", "", () => Post(new { type = "go", direction = "previous" }), "Shift+F7"));
        _bar.PrimaryCommands.Add(Button("Next difference", "", () => Post(new { type = "go", direction = "next" }), "F7"));
        _bar.PrimaryCommands.Add(new AppBarSeparator());
        var toRight = Button("Copy to the right", "", () => Post(new { type = "copy", toRight = true }), "Alt+Right");
        var toLeft = Button("Copy to the left", "", () => Post(new { type = "copy", toRight = false }), "Alt+Left");
        var saveLeft = Button("Save left", "", () => _ = SaveAsync(left: true));
        var saveRight = Button("Save right", "", () => _ = SaveAsync(left: false));
        foreach (ICommandBarElement b in new ICommandBarElement[] { toRight, toLeft, new AppBarSeparator(), saveLeft, saveRight, new AppBarSeparator() })
        {
            _bar.PrimaryCommands.Add(b);
            _textTools.Add(b);
        }
        var whitespace = Toggle("Ignore spaces at line ends", "", _options.CompareIgnoreWhitespace, v => _options.CompareIgnoreWhitespace = v);
        var sideBySide = Toggle("Side by side", "", _options.CompareSideBySide, v => _options.CompareSideBySide = v);
        var wrap = Toggle("Wrap", "", _options.CompareWordWrap, v => _options.CompareWordWrap = v);
        var onlyDifferences = Toggle("Only the differences", "", _options.CompareOnlyDifferences, v => _options.CompareOnlyDifferences = v);
        _bar.PrimaryCommands.Add(whitespace);
        _bar.PrimaryCommands.Add(sideBySide);
        _bar.PrimaryCommands.Add(wrap);
        _bar.PrimaryCommands.Add(onlyDifferences);
        _textTools.Add(whitespace);
        _textTools.Add(wrap);
        _textTools.Add(onlyDifferences);
        var allRight = Button("Copy everything to the right", "", () => Post(new { type = "copyAll", toRight = true }));
        var allLeft = Button("Copy everything to the left", "", () => Post(new { type = "copyAll", toRight = false }));
        _bar.SecondaryCommands.Add(allRight);
        _bar.SecondaryCommands.Add(allLeft);
        _bar.SecondaryCommands.Add(new AppBarSeparator());
        _textTools.Add(allRight);
        _textTools.Add(allLeft);
        _bar.SecondaryCommands.Add(Button("Swap sides", "", () => _ = SwapAsync()));
        _bar.SecondaryCommands.Add(Button("Compare again", "", () => _ = OpenAsync()));
        Grid.SetRow(_bar, 1);
        _root.Children.Add(_bar);

        Grid.SetRow(_infoBar, 2);
        _root.Children.Add(_infoBar);

        // the two files' names above their sides
        var names = new Grid { Padding = new Thickness(12, 0, 12, 4), ColumnSpacing = 24 };
        names.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        names.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var leftHeader = new StackPanel();
        leftHeader.Children.Add(_leftName);
        leftHeader.Children.Add(_leftInfo);
        var rightHeader = new StackPanel();
        rightHeader.Children.Add(_rightName);
        rightHeader.Children.Add(_rightInfo);
        Grid.SetColumn(rightHeader, 1);
        names.Children.Add(leftHeader);
        names.Children.Add(rightHeader);
        Grid.SetRow(names, 3);
        _root.Children.Add(names);

        _body.Children.Add(_web);
        Grid.SetRow(_body, 4);
        _root.Children.Add(_body);

        var status = new Grid { Padding = new Thickness(12, 4, 12, 6) };
        status.Children.Add(_status);
        Grid.SetRow(status, 5);
        _root.Children.Add(status);
    }

    private void ShowInfo(InfoBarSeverity severity, string message)
    {
        _infoBar.Severity = severity;
        _infoBar.Message = message;
        _infoBar.IsOpen = true;
    }

    private void UpdateTitle()
    {
        string left = (_l.Dirty ? "● " : string.Empty) + Path.GetFileName(_left);
        string right = (_r.Dirty ? "● " : string.Empty) + Path.GetFileName(_right);
        Title = $"{left} ↔ {right} - {Loc.T("Compare Files")}";
        _titleText.Text = Title;
        _leftName.Text = (_l.Dirty ? "● " : string.Empty) + _left;
        _rightName.Text = (_r.Dirty ? "● " : string.Empty) + _right;
        ToolTipService.SetToolTip(_leftName, _left);
        ToolTipService.SetToolTip(_rightName, _right);
    }

    private static string Describe(string path, TextEncoding? encoding)
    {
        try
        {
            var info = new FileInfo(path);
            string size = Formats.ShortSize(info.Length);
            string when = $"{Formats.Date(info.LastWriteTime)} {Formats.Time(info.LastWriteTime)}";
            return encoding == null ? $"{size}  ·  {when}" : $"{size}  ·  {when}  ·  {encoding.Name}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    // ---- opening ----------------------------------------------------------------------------------------------

    private async Task OpenAsync()
    {
        if (_l.Dirty || _r.Dirty)
        {
            if (!await ConfirmCloseAsync())
                return;
        }
        _infoBar.IsOpen = false;
        string left = _left, right = _right;
        ComparisonKind kind;
        try
        {
            kind = await Task.Run(() => FileComparison.KindOf(left, right));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoBarSeverity.Error, ex.Message);
            return;
        }
        _binary = kind == ComparisonKind.Binary;
        foreach (ICommandBarElement tool in _textTools)
            ((UIElement)tool).Visibility = _binary ? Visibility.Collapsed : Visibility.Visible;

        string leftText, rightText, language;
        if (_binary)
        {
            BinaryComparison cmp;
            try
            {
                cmp = await Task.Run(() => FileComparison.CompareBinary(left, right));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowInfo(InfoBarSeverity.Error, ex.Message);
                return;
            }
            leftText = cmp.Left;
            rightText = cmp.Right;
            language = "plaintext";
            _l = new Side();
            _r = new Side();
            _leftInfo.Text = Describe(left, null);
            _rightInfo.Text = Describe(right, null);
            _binaryStatus = Loc.F("Binary files: {0} bytes differ in {1} places", cmp.DifferentBytes.ToString("N0"), cmp.Differences.Count.ToString("N0"))
                + (cmp.LeftLength != cmp.RightLength ? "  ·  " + Loc.F("sizes {0} and {1}", cmp.LeftLength.ToString("N0"), cmp.RightLength.ToString("N0")) : string.Empty)
                + (cmp.Truncated ? "  ·  " + Loc.T("only the first differences are shown") : string.Empty);
            SetStatus(_binaryStatus);
        }
        else
        {
            LoadedText a, b;
            try
            {
                a = await Task.Run(() => TextDocuments.Load(left));
                b = await Task.Run(() => TextDocuments.Load(right));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ShowInfo(InfoBarSeverity.Error, ex.Message);
                return;
            }
            leftText = a.Text;
            rightText = b.Text;
            _l = new Side { Encoding = a.Encoding, LineEnding = a.LineEnding };
            _r = new Side { Encoding = b.Encoding, LineEnding = b.LineEnding };
            _leftInfo.Text = Describe(left, a.Encoding);
            _rightInfo.Text = Describe(right, b.Encoding);
            language = EditorLanguages.For(right);
            if (kind == ComparisonKind.Identical)
                ShowInfo(InfoBarSeverity.Success, Loc.T("The files are identical."));
        }
        UpdateTitle();

        if (WebViews.RuntimeVersion() == null)
        {
            ShowInfo(InfoBarSeverity.Warning, Loc.T("Compare Files needs the Microsoft Edge WebView2 Runtime, which isn't installed.") + " " + WebViews.RuntimeDownload);
            return;
        }
        try
        {
            if (_core == null)
            {
                _core = await WebViews.PrepareAsync(_web, scripts: true);
                _core.Settings.AreDefaultContextMenusEnabled = false;
                _core.SetVirtualHostNameToFolderMapping("vibe-editor.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
                _core.WebMessageReceived += Core_WebMessageReceived;
                _core.NavigationStarting += (_, e) =>
                {
                    if (!e.Uri.StartsWith("https://vibe-editor.local/", StringComparison.OrdinalIgnoreCase))
                        e.Cancel = true;
                };
                _pending = (leftText, rightText, language, _binary);
                _core.Navigate("https://vibe-editor.local/Editor/diff.html");
            }
            else
            {
                Load(leftText, rightText, language, _binary);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or FileNotFoundException)
        {
            ShowInfo(InfoBarSeverity.Error, ex.Message);
        }
    }

    private string _binaryStatus = string.Empty;

    private void Load(string left, string right, string language, bool readOnly) => Post(new
    {
        type = "load",
        left,
        right,
        language,
        readOnly,
        leftEol = _l.LineEnding == LineEnding.Lf ? "LF" : "CRLF",
        rightEol = _r.LineEnding == LineEnding.Lf ? "LF" : "CRLF",
    });

    private async Task SwapAsync()
    {
        if ((_l.Dirty || _r.Dirty) && !await ConfirmCloseAsync())
            return;
        (_left, _right) = (_right, _left);
        _l = new Side();
        _r = new Side();
        await OpenAsync();
    }

    // ---- messages ---------------------------------------------------------------------------------------------

    private void Post(object message)
    {
        if (_core == null || !_ready)
            return;
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void SendOptions() => Post(new
    {
        type = "options",
        theme = _root.ActualTheme == ElementTheme.Dark ? "vs-dark" : "vs",
        options = new
        {
            ignoreTrimWhitespace = _options.CompareIgnoreWhitespace,
            renderSideBySide = _options.CompareSideBySide,
            diffWordWrap = _options.CompareWordWrap ? "on" : "off",
            wordWrap = _options.CompareWordWrap ? "on" : "off",
            fontSize = _options.EditorFontSize,
            // the same lines folded away, three kept around each change (click a fold to open it)
            hideUnchangedRegions = new { enabled = _options.CompareOnlyDifferences, contextLineCount = 3, minimumLineCount = 3, revealLineCount = 20 },
        },
    });

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
        JsonElement m = document.RootElement;
        switch (m.GetProperty("type").GetString())
        {
            case "ready":
                _ready = true;
                SendOptions();
                if (_pending is { } p)
                {
                    Load(p.Left, p.Right, p.Language, p.ReadOnly);
                    _pending = null;
                }
                break;
            case "loaded":
                _l.Saved = _l.Version = m.GetProperty("left").GetInt32();
                _r.Saved = _r.Version = m.GetProperty("right").GetInt32();
                UpdateTitle();
                _web.Focus(FocusState.Programmatic);
                break;
            case "changed":
                Side side = m.GetProperty("side").GetString() == "left" ? _l : _r;
                side.Version = m.GetProperty("version").GetInt32();
                UpdateTitle();
                break;
            case "diff":
                if (_binary)
                    break;
                int count = m.GetProperty("count").GetInt32(), current = m.GetProperty("current").GetInt32();
                SetStatus(count == 0 ? Loc.T("No differences")
                    : current > 0 ? Loc.F("Difference {0} of {1}", current, count)
                    : Loc.F("{0} differences", count.ToString("N0")));
                break;
            case "text":
                int id = m.GetProperty("id").GetInt32();
                if (_textRequests.Remove(id, out var request))
                    request.TrySetResult(m.GetProperty("text").GetString() ?? string.Empty);
                break;
            case "key":
                if (m.GetProperty("key").GetString() == "save" && !_binary)
                    _ = SaveAsync(left: m.GetProperty("side").GetString() == "left");
                break;
        }
    }

    private async Task<string> TextOfAsync(bool left)
    {
        int id = _nextRequest++;
        var request = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _textRequests[id] = request;
        Post(new { type = "getText", id, side = left ? "left" : "right" });
        return await request.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    // ---- saving -----------------------------------------------------------------------------------------------

    /// <summary>Saves one side, in its own encoding and line endings, replacing the file in one step.</summary>
    private async Task<bool> SaveAsync(bool left)
    {
        if (_binary)
            return true;
        Side side = left ? _l : _r;
        string path = left ? _left : _right;
        int version = side.Version;
        string text = await TextOfAsync(left);
        if (TextDocuments.Unencodable(text, side.Encoding) > 0)
            side.Encoding = TextEncodings.Utf8; // characters the old encoding can't hold: UTF-8 rather than "?"
        try
        {
            TextEncoding encoding = side.Encoding;
            LineEnding ending = side.LineEnding;
            bool backup = _options.EditorBackup;
            await Task.Run(() => TextDocuments.Save(path, text, encoding, ending, backup));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoBarSeverity.Error, Loc.F("\"{0}\" wasn't saved: {1}", Path.GetFileName(path), ex.Message));
            return false;
        }
        side.Saved = version;
        (left ? _leftInfo : _rightInfo).Text = Describe(path, side.Encoding);
        UpdateTitle();
        _main.Notify(Loc.F("Saved: {0}", Path.GetFileName(path)), isError: false);
        await _main.RefreshAllAsync();
        return true;
    }

    private async Task<bool> ConfirmCloseAsync()
    {
        string which = _l.Dirty && _r.Dirty ? Loc.T("both files")
            : _l.Dirty ? "\"" + Path.GetFileName(_left) + "\"" : "\"" + Path.GetFileName(_right) + "\"";
        ContentDialog ask = _dialogs.Create("Unsaved changes", new TextBlock
        {
            Text = Loc.F("Do you want to save the changes to {0}?", which),
            TextWrapping = TextWrapping.Wrap,
        });
        ask.PrimaryButtonText = Loc.T("Save");
        ask.SecondaryButtonText = Loc.T("Don't save");
        ask.CloseButtonText = Loc.T("Cancel");
        ask.DefaultButton = ContentDialogButton.Primary;
        switch (await _dialogs.ShowDialogAsync(ask))
        {
            case ContentDialogResult.Primary:
                if (_l.Dirty && !await SaveAsync(left: true))
                    return false;
                return !_r.Dirty || await SaveAsync(left: false);
            case ContentDialogResult.Secondary:
                return true;
            default:
                return false;
        }
    }

    /// <summary>The status line, also as its automation name and a polite live region (screen readers hear "Difference 2 of 5").</summary>
    private void SetStatus(string text)
    {
        _status.Text = text;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_status, text);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetLiveSetting(_status, Microsoft.UI.Xaml.Automation.Peers.AutomationLiveSetting.Polite);
        if (Microsoft.UI.Xaml.Automation.Peers.FrameworkElementAutomationPeer.FromElement(_status) is { } peer)
            peer.RaiseAutomationEvent(Microsoft.UI.Xaml.Automation.Peers.AutomationEvents.LiveRegionChanged);
    }
}
