// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;
using VirtualKey = Windows.System.VirtualKey;

namespace VibeSalamander.Views.Viewer;

/// <summary>
/// The built-in viewer (F3): any file in the modes that fit it: text and hex of any size, a tree for JSON, XML and
/// YAML, Markdown, notebooks and web pages rendered, pictures, PDFs, audio and video. A find bar for text and bytes,
/// encodings, wrapping, going through the other files of the panel. Each file opens in its own window, like Salamander.
/// </summary>
internal sealed partial class ViewerWindow : Window
{
    private readonly MainViewModel _main;
    private readonly ViewerOptions _options;
    private readonly IReadOnlyList<string> _siblings;
    private readonly Dialogs _dialogs;
    private readonly DispatcherQueueTimer _poll;

    private readonly Grid _root = new();
    private readonly TextBlock _titleText = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), FontSize = 12 };
    private readonly Grid _titleBar = new() { Height = 32 };
    private readonly CommandBar _bar = new()
    {
        DefaultLabelPosition = CommandBarDefaultLabelPosition.Right,
        IsDynamicOverflowEnabled = true,
        Background = new SolidColorBrush(Colors.Transparent),
        HorizontalAlignment = HorizontalAlignment.Left,
    };
    private readonly Grid _content = new();
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(24), MaxWidth = 640, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly VirtualTextView _textView = new();
    private readonly VirtualTextView _hexView = new() { ShowLabels = false };

    // the find bar
    private readonly Grid _findBar = new() { Padding = new Thickness(8, 4, 8, 4), ColumnSpacing = 6, Visibility = Visibility.Collapsed };
    private readonly TextBox _findBox = new() { Width = 300, PlaceholderText = "Find" };
    private readonly ToggleButton _matchCase = new() { Content = "Aa" };
    private readonly ToggleButton _wholeWord = new() { Content = "ab" };
    private readonly ToggleButton _regex = new() { Content = ".*" };
    private readonly ToggleButton _hexFind = new() { Content = "Hex" };
    private readonly TextBlock _findStatus = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.8 };

    // the status line
    private readonly TextBlock _status = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _info = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    private readonly ProgressBar _progress = new() { Width = 120, Maximum = 1000, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed };

    // toolbar buttons
    private readonly Dictionary<ViewerMode, AppBarToggleButton> _modeButtons = new();
    private readonly ComboBox _encodings = new() { MinWidth = 190, VerticalAlignment = VerticalAlignment.Center };
    private readonly AppBarElementContainer _encodingItem;
    private readonly AppBarToggleButton _wrapButton;
    private readonly AppBarToggleButton _numbersButton;
    private readonly List<ICommandBarElement> _textTools = new();

    private string _path = string.Empty;
    private ViewerMode _mode;
    private IReadOnlyList<ViewerMode> _modes = Array.Empty<ViewerMode>();
    private TextFile? _file;
    private TextEncoding? _chosenEncoding;
    private TextRowSource? _textSource;
    private HexRowSource? _hexSource;
    private CancellationTokenSource _fileCancel = new();
    private CancellationTokenSource? _findCancel;
    private bool _measuring;
    private (long Line, int Column)? _lastMatch;
    private long _lastByteMatch = -1;
    private bool _closed;

    public ViewerWindow(MainViewModel main, string path, IReadOnlyList<string> siblings, ElementTheme theme)
    {
        _main = main;
        _options = main.Settings.Viewer;
        _siblings = siblings;
        _dialogs = new Dialogs(() => Content.XamlRoot, () => _root.ActualTheme, () => AppWindow.Id);
        _root.RequestedTheme = theme;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        WindowPlacements.SetIcon(this);
        WindowPlacements.Restore(this, _options.ViewerWindow, 1100, 760);

        _encodingItem = new AppBarElementContainer { Content = _encodings, VerticalContentAlignment = VerticalAlignment.Center };
        _wrapButton = new AppBarToggleButton { Label = Loc.T("Wrap"), Icon = new FontIcon { Glyph = "" }, IsChecked = _options.WordWrap };
        _numbersButton = new AppBarToggleButton { Label = Loc.T("Line numbers"), Icon = new FontIcon { Glyph = "" }, IsChecked = _options.LineNumbers };
        BuildLayout();
        Content = _root;
        SetTitleBar(_titleBar);

        _poll = DispatcherQueue.CreateTimer();
        _poll.Interval = TimeSpan.FromMilliseconds(250);
        _poll.Tick += (_, _) => Poll();
        _textView.TextSize = _hexView.TextSize = _options.FontSize;
        _textView.ShowLabels = _options.LineNumbers;
        _textView.Scrolled += (_, _) => UpdateStatus();
        _hexView.Scrolled += (_, _) => UpdateStatus();
        _textView.CopyCut += (_, _) => _status.Text = Loc.T("Only the first 64 MB were copied.");
        _hexView.CopyCut += (_, _) => _status.Text = Loc.T("Only the first 64 MB were copied.");
        _textView.SizeChanged += (_, e) =>
        {
            if (_textSource is { Wrapped: true } && Math.Abs(e.NewSize.Width - e.PreviousSize.Width) > 1)
                ApplyWrap();
        };

        Closed += (_, _) =>
        {
            _closed = true;
            _poll.Stop();
            _fileCancel.Cancel();
            _findCancel?.Cancel();
            CloseModes();
            CleanPreviewFiles();
            _file?.Dispose();
            _options.FontSize = _textView.TextSize;
            WindowPlacements.Save(this, _options.ViewerWindow);
            _main.SaveSettings();
        };
        _ = OpenAsync(path);
    }

    // ---- layout ---------------------------------------------------------------------------------------------

    private void BuildLayout()
    {
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // title bar
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // toolbar
        _root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // content
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // find bar
        _root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });                      // status

        // title bar: the icon and the file name
        var titleContent = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(12, 0, 0, 0) };
        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "VibeSalamander.png");
        if (File.Exists(iconPath))
            titleContent.Children.Add(new Image { Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(iconPath)), Width = 16, Height = 16 });
        titleContent.Children.Add(_titleText);
        _titleBar.Children.Add(titleContent);
        _root.Children.Add(_titleBar);

        // toolbar: the modes, then what fits the mode, then the file
        foreach (ViewerMode mode in Enum.GetValues<ViewerMode>())
        {
            var button = new AppBarToggleButton { Label = Loc.T(ModeName(mode)), Icon = new FontIcon { Glyph = ModeGlyph(mode) } };
            ViewerMode m = mode;
            button.Click += (_, _) => ShowMode(m);
            _modeButtons[mode] = button;
            _bar.PrimaryCommands.Add(button);
        }
        _bar.PrimaryCommands.Add(new AppBarSeparator());

        _encodings.SelectionChanged += Encodings_SelectionChanged;
        ToolTipService.SetToolTip(_encodings, Loc.T("Encoding"));
        _wrapButton.Click += (_, _) =>
        {
            _options.WordWrap = _wrapButton.IsChecked == true;
            ApplyWrap();
        };
        _numbersButton.Click += (_, _) =>
        {
            _options.LineNumbers = _numbersButton.IsChecked == true;
            _textView.ShowLabels = _options.LineNumbers;
            _textView.Layout();
        };
        var goTo = Button("Go to...", "", async () => await GoToAsync());
        _textTools.AddRange(new ICommandBarElement[] { _encodingItem, _wrapButton, _numbersButton, goTo });
        foreach (ICommandBarElement element in _textTools)
            _bar.PrimaryCommands.Add(element);
        BuildImageTools();
        BuildPreviewTools();
        _bar.PrimaryCommands.Add(Button("Find", "", ShowFindBar));
        _bar.PrimaryCommands.Add(new AppBarSeparator());
        _bar.PrimaryCommands.Add(Button("Previous file", "", () => _ = StepFileAsync(-1, imagesOnly: false)));
        _bar.PrimaryCommands.Add(Button("Next file", "", () => _ = StepFileAsync(+1, imagesOnly: false)));
        _bar.SecondaryCommands.Add(Button("Edit (F4)", "", Edit));
        _bar.SecondaryCommands.Add(Button("Open with the default app", "", () => Run(() => ShellOperations.Open(_path, Path.GetDirectoryName(_path)))));
        _bar.SecondaryCommands.Add(Button("Show in folder", "", () => Run(() => System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _path + "\"")?.Dispose())));
        _bar.SecondaryCommands.Add(Button("Copy path", "", () =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(_path);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        }));
        Grid.SetRow(_bar, 1);
        _root.Children.Add(_bar);

        // content: one view per mode, the message for problems
        _content.Children.Add(_textView);
        _content.Children.Add(_hexView);
        _content.Children.Add(_message);
        Grid.SetRow(_content, 2);
        _root.Children.Add(_content);

        // find bar
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (int i = 0; i < 7; i++)
            _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _findBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _findBox.PlaceholderText = Loc.T("Find");
        _findBox.KeyDown += FindBox_KeyDown;
        ToolTipService.SetToolTip(_matchCase, Loc.T("Match case"));
        ToolTipService.SetToolTip(_wholeWord, Loc.T("Whole words"));
        ToolTipService.SetToolTip(_regex, Loc.T("Regular expression"));
        ToolTipService.SetToolTip(_hexFind, Loc.T("Hex bytes (4D 5A ...)"));
        var previous = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
        var next = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
        var close = new Button { Content = new FontIcon { Glyph = "", FontSize = 12 } };
        ToolTipService.SetToolTip(previous, Loc.T("Previous (Shift+F3)"));
        ToolTipService.SetToolTip(next, Loc.T("Next (F3)"));
        previous.Click += (_, _) => _ = FindAsync(backwards: true);
        next.Click += (_, _) => _ = FindAsync(backwards: false);
        close.Click += (_, _) => HideFindBar();
        UIElement[] findParts = { _findBox, _matchCase, _wholeWord, _regex, _hexFind, previous, next, _findStatus };
        for (int i = 0; i < findParts.Length; i++)
        {
            Grid.SetColumn((FrameworkElement)findParts[i], i);
            _findBar.Children.Add(findParts[i]);
        }
        Grid.SetColumn(close, 8);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        _findBar.Children.Add(close);
        Grid.SetRow(_findBar, 3);
        _root.Children.Add(_findBar);

        // status line
        var status = new Grid { Padding = new Thickness(12, 4, 12, 6), ColumnSpacing = 16 };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Children.Add(_status);
        Grid.SetColumn(_progress, 1);
        status.Children.Add(_progress);
        Grid.SetColumn(_info, 2);
        status.Children.Add(_info);
        Grid.SetRow(status, 4);
        _root.Children.Add(status);

        _root.KeyDown += Root_KeyDown;
        _root.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(Root_PreviewKeyDown), handledEventsToo: true);
    }

    private static AppBarButton Button(string label, string glyph, Action click)
    {
        var button = new AppBarButton { Label = Loc.T(label), Icon = new FontIcon { Glyph = glyph } };
        button.Click += (_, _) => click();
        return button;
    }

    private void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            _status.Text = ex.Message;
        }
    }

    private static string ModeName(ViewerMode mode) => mode switch
    {
        ViewerMode.Text => "Text",
        ViewerMode.Hex => "Hex",
        ViewerMode.Tree => "Tree",
        ViewerMode.Preview => "Preview",
        ViewerMode.Image => "Picture",
        ViewerMode.Pdf => "PDF",
        _ => "Play",
    };

    private static string ModeGlyph(ViewerMode mode) => mode switch
    {
        ViewerMode.Text => "",
        ViewerMode.Hex => "",
        ViewerMode.Tree => "",
        ViewerMode.Preview => "",
        ViewerMode.Image => "",
        ViewerMode.Pdf => "",
        _ => "",
    };

    // ---- opening a file --------------------------------------------------------------------------------------

    /// <summary>Shows a file (the first, or the next one of the panel's).</summary>
    public async Task OpenAsync(string path)
    {
        CloseModes();
        _fileCancel.Cancel();
        _fileCancel = new CancellationTokenSource();
        _file?.Dispose();
        _file = null;
        _textSource = null;
        _hexSource = null;
        _textView.Source = null;
        _hexView.Source = null;
        _chosenEncoding = null;
        _lastMatch = null;
        _lastByteMatch = -1;
        _path = path;
        string name = Path.GetFileName(path);
        Title = $"{name} - {Loc.T("Vibe Salamander Viewer")}";
        _titleText.Text = Title;
        _status.Text = string.Empty;

        bool binary;
        try
        {
            byte[] head = await Task.Run(() => TextFile.ReadHead(path));
            binary = TextEncodings.LooksBinary(head, TextEncodings.Detect(head));
            var info = new FileInfo(path);
            _info.Text = $"{Formats.ShortSize(info.Length)}  ·  {info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(Loc.F("\"{0}\" can't be read: {1}", name, ex.Message));
            return;
        }
        _modes = ViewerModes.For(path);
        foreach (var (mode, button) in _modeButtons)
            button.Visibility = _modes.Contains(mode) ? Visibility.Visible : Visibility.Collapsed;
        ShowMode(ViewerModes.Initial(path, binary));
    }

    /// <summary>The next (or previous) file of the panel; pictures skip to the next picture.</summary>
    private async Task StepFileAsync(int direction, bool imagesOnly)
    {
        if (_siblings.Count == 0)
            return;
        int index = -1;
        for (int i = 0; i < _siblings.Count; i++)
            if (string.Equals(_siblings[i], _path, StringComparison.OrdinalIgnoreCase))
                index = i;
        for (int step = 1; step <= _siblings.Count; step++)
        {
            int candidate = ((index < 0 ? (direction > 0 ? -1 : 0) : index) + direction * step + _siblings.Count * step) % _siblings.Count;
            string next = _siblings[candidate];
            if (imagesOnly && !ViewerModes.IsImage(next))
                continue;
            if (string.Equals(next, _path, StringComparison.OrdinalIgnoreCase))
                return;
            await OpenAsync(next);
            return;
        }
    }

    private void ShowMessage(string text)
    {
        _message.Text = text;
        foreach (UIElement child in _content.Children)
            child.Visibility = child == _message ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- modes ---------------------------------------------------------------------------------------------

    private void ShowMode(ViewerMode mode)
    {
        if (!_modes.Contains(mode))
            mode = _modes.Contains(ViewerMode.Text) ? ViewerMode.Text : _modes[0];
        if (mode != _mode)
            LeaveMode(_mode);
        _mode = mode;
        foreach (var (m, button) in _modeButtons)
            button.IsChecked = m == mode;
        bool text = mode is ViewerMode.Text or ViewerMode.Hex;
        foreach (ICommandBarElement element in _textTools)
            ((UIElement)element).Visibility = text ? Visibility.Visible : Visibility.Collapsed;
        _wrapButton.Visibility = _numbersButton.Visibility = mode == ViewerMode.Text ? Visibility.Visible : Visibility.Collapsed;
        _hexFind.Visibility = mode == ViewerMode.Hex ? Visibility.Visible : Visibility.Collapsed;
        _matchCase.Visibility = _wholeWord.Visibility = _regex.Visibility = mode is ViewerMode.Text or ViewerMode.Tree ? Visibility.Visible : Visibility.Collapsed;
        SetImageToolsVisible(mode is ViewerMode.Image or ViewerMode.Pdf);
        SetPreviewToolsVisible(mode == ViewerMode.Preview);
        foreach (UIElement child in _content.Children)
            child.Visibility = Visibility.Collapsed;
        _progress.Visibility = Visibility.Collapsed;
        switch (mode)
        {
            case ViewerMode.Text:
            case ViewerMode.Hex:
                ShowTextMode(mode);
                break;
            case ViewerMode.Tree:
                _ = ShowTreeAsync();
                break;
            case ViewerMode.Preview:
                _ = ShowPreviewAsync();
                break;
            case ViewerMode.Image:
                _ = ShowImageAsync();
                break;
            case ViewerMode.Pdf:
                _ = ShowPdfAsync();
                break;
            case ViewerMode.Media:
                _ = ShowMediaAsync();
                break;
        }
        UpdateStatus();
    }

    /// <summary>Leaving a mode stops what it was doing (a video playing).</summary>
    private void LeaveMode(ViewerMode mode)
    {
        if (mode == ViewerMode.Media)
            StopMedia();
    }

    private void CloseModes()
    {
        StopMedia();
        ClosePdf();
        CloseImage();
        CloseTree();
    }

    // ---- text and hex ----------------------------------------------------------------------------------------

    private bool EnsureFile()
    {
        if (_file != null)
            return true;
        try
        {
            _file = TextFile.Open(_path, _chosenEncoding);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowMessage(Loc.F("\"{0}\" can't be read: {1}", Path.GetFileName(_path), ex.Message));
            return false;
        }
        _textSource = new TextRowSource(_file);
        _hexSource = new HexRowSource(_file);
        FillEncodings();
        TextFile file = _file;
        CancellationToken cancel = _fileCancel.Token;
        _ = file.IndexAsync(cancel).ContinueWith(t =>
        {
            if (t.IsFaulted)
                DispatcherQueue.TryEnqueue(() => _status.Text = t.Exception?.GetBaseException().Message ?? string.Empty);
        }, TaskScheduler.Default);
        _poll.Start();
        return true;
    }

    private void ShowTextMode(ViewerMode mode)
    {
        if (!EnsureFile())
            return;
        VirtualTextView view = mode == ViewerMode.Text ? _textView : _hexView;
        IRowSource source = mode == ViewerMode.Text ? _textSource! : _hexSource!;
        view.Visibility = Visibility.Visible;
        if (view.Source != source)
        {
            // the same place when switching between text and hex
            if (mode == ViewerMode.Hex && _textSource != null && _file!.LineCount > 0)
            {
                view.Source = source;
                long line = _textSource.LineOf(_textView.TopRow).Line;
                view.ScrollTo(_file.LineOffset(line) / HexFormat.BytesPerRow, 0);
            }
            else if (mode == ViewerMode.Text && _hexView.Source != null && _file!.LineCount > 0)
            {
                view.Source = source;
                long line = _file.LineAt(_hexView.TopRow * HexFormat.BytesPerRow);
                view.ScrollTo(_textSource!.RowOf(line, 0).Row, 0);
            }
            else
            {
                view.Source = source;
            }
        }
        if (mode == ViewerMode.Text)
        {
            ApplyWrap();
            if (_pendingLine > 0)
            {
                _textView.Reveal(_textSource!.RowOf(_pendingLine - 1, 0).Row);
                _pendingLine = 0;
            }
        }
        view.Focus(FocusState.Programmatic);
    }

    private void ApplyWrap()
    {
        if (_textSource == null)
            return;
        bool wrap = _options.WordWrap;
        if (wrap && !_textSource.CanWrap)
        {
            if (_file is { IsComplete: true } && _file.LineCount > TextRowSource.MaxWrapLines)
                _status.Text = Loc.T("This file has too many lines to wrap.");
            else if (!_measuring && _file is { IsComplete: true })
            {
                _measuring = true;
                TextRowSource source = _textSource;
                _ = source.MeasureAsync(_fileCancel.Token).ContinueWith(_ => DispatcherQueue.TryEnqueue(() =>
                {
                    _measuring = false;
                    if (!_closed && source == _textSource)
                        ApplyWrap();
                }), TaskScheduler.Default);
            }
            return; // wrapped once the lengths are known (after the scan)
        }
        long line = _textSource.LineOf(_textView.TopRow).Line;
        _textSource.SetWrap(wrap, _textView.VisibleColumns);
        _textView.Layout();
        _textView.ScrollTo(_textSource.RowOf(line, 0).Row, 0);
    }

    /// <summary>While a large file is scanned: more rows, the progress, then wrapping.</summary>
    private void Poll()
    {
        if (_file == null || _closed)
        {
            _poll.Stop();
            return;
        }
        if (_mode == ViewerMode.Text)
            _textView.Refresh();
        if (!_file.IsComplete)
        {
            _progress.Visibility = Visibility.Visible;
            _progress.Value = _file.Length == 0 ? 1000 : 1000.0 * _file.IndexedBytes / _file.Length;
        }
        else
        {
            _progress.Visibility = Visibility.Collapsed;
            _poll.Stop();
            if (_options.WordWrap && _mode == ViewerMode.Text)
                ApplyWrap();
        }
        UpdateStatus();
    }

    private void FillEncodings()
    {
        if (_file == null)
            return;
        _encodings.SelectionChanged -= Encodings_SelectionChanged;
        _encodings.Items.Clear();
        _encodings.Items.Add(new ComboBoxItem { Content = _file.Encoding.Name, Tag = _file.Encoding });
        foreach (TextEncoding encoding in TextEncodings.Choices())
            if (encoding.Name != _file.Encoding.Name)
                _encodings.Items.Add(new ComboBoxItem { Content = encoding.Name, Tag = encoding });
        _encodings.SelectedIndex = 0;
        _encodings.SelectionChanged += Encodings_SelectionChanged;
    }

    private void Encodings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_encodings.SelectedItem is not ComboBoxItem { Tag: TextEncoding encoding } || _file == null || encoding == _file.Encoding)
            return;
        long top = _textView.TopRow;
        _fileCancel.Cancel();
        _fileCancel = new CancellationTokenSource();
        _file.Dispose();
        _file = null;
        _textView.Source = null;
        _hexView.Source = null;
        _chosenEncoding = encoding;
        ShowTextMode(_mode == ViewerMode.Hex ? ViewerMode.Hex : ViewerMode.Text);
        _textView.ScrollTo(top, 0);
    }

    private async Task GoToAsync()
    {
        if (_file == null)
            return;
        bool hex = _mode == ViewerMode.Hex;
        string? answer = await _dialogs.InputAsync(Loc.T("Go To"), Loc.T(hex ? "Offset (hex with 0x, or decimal):" : "Line:"));
        if (string.IsNullOrWhiteSpace(answer))
            return;
        answer = answer.Trim();
        if (hex)
        {
            bool isHex = answer.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            if (long.TryParse(isHex ? answer[2..] : answer, isHex ? NumberStyles.HexNumber : NumberStyles.Integer, CultureInfo.InvariantCulture, out long offset))
                _hexView.Reveal(Math.Clamp(offset, 0, Math.Max(0, _file.Length - 1)) / HexFormat.BytesPerRow);
        }
        else if (long.TryParse(answer, NumberStyles.Integer, CultureInfo.CurrentCulture, out long line) && _textSource != null)
        {
            _textView.Reveal(_textSource.RowOf(Math.Clamp(line - 1, 0, Math.Max(0, _file.LineCount - 1)), 0).Row);
        }
        _textView.Focus(FocusState.Programmatic);
    }

    private void UpdateStatus()
    {
        if (_closed)
            return;
        if (_mode == ViewerMode.Text && _file != null && _textSource != null)
        {
            long line = _textSource.LineOf(_textView.TopRow).Line + 1;
            string total = _file.IsComplete
                ? _file.LineCount.ToString("N0", CultureInfo.CurrentCulture)
                : _file.LineCount.ToString("N0", CultureInfo.CurrentCulture) + "+";
            _status.Text = Loc.F("Line {0} of {1}", line.ToString("N0", CultureInfo.CurrentCulture), total);
            _info.Text = $"{_file.Encoding.Name}  ·  {Formats.ShortSize(_file.Length)}";
        }
        else if (_mode == ViewerMode.Hex && _file != null)
        {
            long offset = _hexView.TopRow * HexFormat.BytesPerRow;
            _status.Text = Loc.F("Offset {0} of {1}", "0x" + offset.ToString("X", CultureInfo.InvariantCulture),
                Formats.ExactSize(_file.Length) + " " + Loc.T("bytes"));
            _info.Text = $"{_file.Encoding.Name}  ·  {Formats.ShortSize(_file.Length)}";
        }
    }

    // ---- finding ---------------------------------------------------------------------------------------------

    private void ShowFindBar()
    {
        _findBar.Visibility = Visibility.Visible;
        if (_mode == ViewerMode.Text && _textView.HasSelection)
        {
            string selected = _textView.SelectedText(out _);
            if (selected.Length is > 0 and < 200 && !selected.Contains('\n'))
                _findBox.Text = selected;
        }
        _findBox.Focus(FocusState.Programmatic);
        _findBox.SelectAll();
    }

    private void HideFindBar()
    {
        _findBar.Visibility = Visibility.Collapsed;
        _findCancel?.Cancel();
        _textView.ClearMatch();
        _hexView.ClearMatch();
        FocusContent();
    }

    private void FocusContent()
    {
        if (_mode == ViewerMode.Text)
            _textView.Focus(FocusState.Programmatic);
        else if (_mode == ViewerMode.Hex)
            _hexView.Focus(FocusState.Programmatic);
        else
            FocusRenderedMode();
    }

    private void FindBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            _ = FindAsync(backwards: KeyboardState.IsDown(KeyboardState.Shift));
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            HideFindBar();
        }
    }

    private async Task FindAsync(bool backwards)
    {
        if (_findBar.Visibility != Visibility.Visible || _findBox.Text.Length == 0)
        {
            ShowFindBar();
            return;
        }
        _findCancel?.Cancel();
        var cancel = new CancellationTokenSource();
        _findCancel = cancel;
        _findStatus.Text = Loc.T("Searching...");
        try
        {
            switch (_mode)
            {
                case ViewerMode.Text:
                    await FindTextAsync(backwards, cancel.Token);
                    break;
                case ViewerMode.Hex:
                    await FindBytesAsync(backwards, cancel.Token);
                    break;
                case ViewerMode.Tree:
                    FindInTree(backwards);
                    break;
                case ViewerMode.Preview:
                    await FindInPreviewAsync(backwards);
                    break;
                default:
                    _findStatus.Text = string.Empty;
                    break;
            }
        }
        catch (OperationCanceledException)
        {
            // a newer search, or closed
        }
    }

    private FindOptions FindOptions() =>
        new(_findBox.Text, _matchCase.IsChecked == true, _wholeWord.IsChecked == true, _regex.IsChecked == true);

    private async Task FindTextAsync(bool backwards, CancellationToken cancel)
    {
        if (_file == null || _textSource == null)
            return;
        FindOptions options = FindOptions();
        if (TextSearch.Problem(options) is { } problem)
        {
            _findStatus.Text = problem;
            return;
        }
        // from the last match, or from the top of the view
        (long line, int column) = _lastMatch is { } last
            ? (last.Line, backwards ? last.Column : last.Column + 1)
            : (_textSource.LineOf(_textView.TopRow).Line, 0);
        TextFile file = _file;
        bool wrapped = false;
        TextMatch? match = await Task.Run(() => TextSearch.Find(file, options, line, column, backwards, out wrapped, cancel), cancel);
        if (cancel.IsCancellationRequested || file != _file)
            return;
        if (match is not { } m)
        {
            _findStatus.Text = Loc.T("Not found");
            return;
        }
        _lastMatch = (m.Line, m.Column);
        var (row, rowColumn) = _textSource.RowOf(m.Line, m.Column);
        _textView.ShowMatch(row, rowColumn, m.Length);
        _findStatus.Text = wrapped ? Loc.T(backwards ? "Found (from the end again)" : "Found (from the top again)") : string.Empty;
    }

    private async Task FindBytesAsync(bool backwards, CancellationToken cancel)
    {
        if (_file == null || _hexSource == null)
            return;
        byte[]? pattern = ByteSearch.Pattern(_findBox.Text, _hexFind.IsChecked == true, _file.Encoding.Encoding);
        if (pattern == null)
        {
            _findStatus.Text = Loc.T("Type hex bytes, like 4D 5A 90.");
            return;
        }
        TextFile file = _file;
        long from = _lastByteMatch >= 0 ? (backwards ? _lastByteMatch : _lastByteMatch + 1) : _hexView.TopRow * HexFormat.BytesPerRow;
        bool wrapped = false;
        long found = await Task.Run(() =>
        {
            long at = ByteSearch.Find(file.ReadBytes, file.Length, pattern, from, backwards, cancel);
            if (at < 0)
            {
                wrapped = true;
                at = ByteSearch.Find(file.ReadBytes, file.Length, pattern, backwards ? file.Length : 0, backwards, cancel);
            }
            return at;
        }, cancel);
        if (cancel.IsCancellationRequested || file != _file)
            return;
        if (found < 0)
        {
            _findStatus.Text = Loc.T("Not found");
            return;
        }
        _lastByteMatch = found;
        long row = found / HexFormat.BytesPerRow;
        int inRow = (int)(found % HexFormat.BytesPerRow);
        int length = Math.Min(pattern.Length, HexFormat.BytesPerRow - inRow);
        int start = HexFormat.HexColumn(inRow, _hexSource.OffsetDigits);
        int end = HexFormat.HexColumn(inRow + length - 1, _hexSource.OffsetDigits) + 2;
        _hexView.ShowMatch(row, start, end - start);
        _findStatus.Text = (wrapped ? Loc.T(backwards ? "Found (from the end again)" : "Found (from the top again)") + "  " : string.Empty)
            + "0x" + found.ToString("X", CultureInfo.InvariantCulture);
    }

    // ---- keys ------------------------------------------------------------------------------------------------

    private bool InFindBox => FocusManager.GetFocusedElement(_root.XamlRoot) is DependencyObject focused && VisualTree.IsInside(focused, _findBar);

    private void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (InFindBox)
            return;
        if (_mode is ViewerMode.Image && !KeyboardState.IsDown(KeyboardState.Control) && ImageKey(e.Key))
            e.Handled = true;
    }

    private void Root_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = KeyboardState.IsDown(KeyboardState.Control);
        bool shift = KeyboardState.IsDown(KeyboardState.Shift);
        switch (e.Key)
        {
            case VirtualKey.Escape when !InFindBox:
            case VirtualKey.F10:
                Close();
                break;
            case VirtualKey.F when ctrl:
            case VirtualKey.F7:
                ShowFindBar();
                break;
            case VirtualKey.F3:
                _ = FindAsync(backwards: shift);
                break;
            case VirtualKey.G when ctrl && _mode is ViewerMode.Text or ViewerMode.Hex:
                _ = GoToAsync();
                break;
            case VirtualKey.F4:
                Edit();
                break;
            case VirtualKey.PageDown when ctrl:
                _ = StepFileAsync(+1, imagesOnly: false);
                break;
            case VirtualKey.PageUp when ctrl:
                _ = StepFileAsync(-1, imagesOnly: false);
                break;
            case VirtualKey.W when !InFindBox && _mode == ViewerMode.Text && !ctrl:
                _wrapButton.IsChecked = !(_wrapButton.IsChecked == true);
                _options.WordWrap = _wrapButton.IsChecked == true;
                ApplyWrap();
                break;
            case >= VirtualKey.Number1 and <= VirtualKey.Number7 when ctrl:
                int index = e.Key - VirtualKey.Number1;
                if (index < _modes.Count)
                    ShowMode(_modes[index]);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void Edit()
    {
        string path = _path;
        Close();
        _main.EditFile(path);
    }
}
