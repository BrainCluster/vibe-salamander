// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using System.Text.Json;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Views.Viewer;

/// <summary>
/// The built-in editor (F4): Monaco, the editor of Visual Studio Code, in a web view: syntax colors for many
/// languages, folding, find and replace, several cursors. The app adds checking JSON, XML and YAML as you type,
/// formatting, a live preview for Markdown and HTML, encodings and line endings, safe saving (the original is
/// replaced in one step), a warning when the file changes on disk, and asks before closing with unsaved changes.
/// Without the WebView2 runtime it's a plain text box with the same saving.
/// </summary>
internal sealed partial class EditorWindow : Window
{
    private readonly MainViewModel _main;
    private readonly ViewerOptions _options;
    private readonly Func<string, Task>? _afterSave;
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
    private readonly Grid _body = new();
    private readonly WebView2 _web = new();
    private WebView2? _preview;
    private TextBox? _plain;
    private readonly ComboBox _languages = new() { MinWidth = 130, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _encodings = new() { MinWidth = 170, VerticalAlignment = VerticalAlignment.Center };
    private readonly ComboBox _lineEndings = new() { MinWidth = 80, VerticalAlignment = VerticalAlignment.Center };
    private readonly AppBarToggleButton _previewButton;
    private readonly AppBarButton _formatButton;
    private readonly AppBarButton _minifyButton;
    private readonly AppBarButton _sortKeysButton;
    private readonly TextBlock _position = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _problems = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _fileInfo = new() { VerticalAlignment = VerticalAlignment.Center, Opacity = 0.75 };
    private readonly Dictionary<int, TaskCompletionSource<(string Text, int Version)>> _textRequests = new();
    private readonly DispatcherQueueTimer _checkTimer;

    private CoreWebView2? _core;
    private string _path;
    private TextEncoding _encoding = TextEncodings.Utf8;
    private LineEnding _lineEnding = LineEnding.CrLf;
    private string _language = "plaintext";
    private int _savedVersion = -1, _version;
    private int _nextRequest = 1;
    private string _lastText = string.Empty;
    private bool _closeConfirmed;
    private bool _ready;
    private FileSystemWatcher? _watcher;
    private DateTime _ownWriteUntil;
    private string? _previewFile;

    public EditorWindow(MainViewModel main, string path, Func<string, Task>? afterSave, ElementTheme theme)
    {
        _main = main;
        _options = main.Settings.Viewer;
        _afterSave = afterSave;
        _path = path;
        _dialogs = new Dialogs(() => Content.XamlRoot, () => _root.ActualTheme, () => AppWindow.Id);
        _root.RequestedTheme = theme;
        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        WindowPlacements.SetIcon(this);
        WindowPlacements.Restore(this, _options.EditorWindow, 1150, 800);
        _previewButton = new AppBarToggleButton { Label = Loc.T("Preview"), Icon = new FontIcon { Glyph = "\uE8A1" }, IsChecked = _options.EditorPreview };
        _formatButton = Button("Format", "\uE8E2", () => _ = FormatAsync());
        _minifyButton = Button("Minify", "\uE73F", () => _ = RewriteAsync(minify: true));
        _sortKeysButton = Button("Sort keys", "\uE8CB", () => _ = RewriteAsync(minify: false));
        BuildLayout();
        Content = _root;
        SetTitleBar(_titleBar);
        _checkTimer = DispatcherQueue.CreateTimer();
        _checkTimer.Interval = TimeSpan.FromMilliseconds(400);
        _checkTimer.IsRepeating = false;
        _checkTimer.Tick += (_, _) => CheckAndPreview();

        AppWindow.Closing += async (_, args) =>
        {
            if (_closeConfirmed || !IsDirty)
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
            _watcher?.Dispose();
            _checkTimer.Stop();
            WindowPlacements.Save(this, _options.EditorWindow);
            _main.SaveSettings();
            if (_previewFile != null)
                TryDelete(_previewFile);
        };
        _ = OpenAsync();
    }

    /// <summary>A change the editor page doesn't count (another encoding or line ending to save in).</summary>
    private bool _otherChange;

    private bool IsDirty => _otherChange || (_savedVersion >= 0 && _version != _savedVersion);

    // ---- layout ---------------------------------------------------------------------------------------------

    private void BuildLayout()
    {
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

        _bar.PrimaryCommands.Add(Button("Save", "\uE74E", () => _ = SaveAsync()));
        _bar.PrimaryCommands.Add(Button("Save as...", "\uE792", () => _ = SaveAsAsync()));
        _bar.PrimaryCommands.Add(new AppBarSeparator());
        _bar.PrimaryCommands.Add(Button("Find", "\uE721", () => Command("actions.find")));
        _bar.PrimaryCommands.Add(Button("Replace", "\uE8AB", () => Command("editor.action.startFindReplaceAction")));
        _bar.PrimaryCommands.Add(Button("Go to line", "\uE8AD", () => Command("editor.action.gotoLine")));
        _bar.PrimaryCommands.Add(_formatButton);
        _bar.SecondaryCommands.Add(_minifyButton);
        _bar.SecondaryCommands.Add(_sortKeysButton);
        _bar.SecondaryCommands.Add(Button("Comment line (Ctrl+/)", "\uE90A", () => Command("editor.action.commentLine")));
        _previewButton.Click += (_, _) =>
        {
            _options.EditorPreview = _previewButton.IsChecked == true;
            ApplyPreviewLayout();
            CheckAndPreview();
        };
        _bar.PrimaryCommands.Add(_previewButton);
        var wrap = new AppBarToggleButton { Label = Loc.T("Wrap"), Icon = new FontIcon { Glyph = "\uE751" }, IsChecked = _options.EditorWordWrap };
        wrap.Click += (_, _) =>
        {
            _options.EditorWordWrap = wrap.IsChecked == true;
            SendOptions();
        };
        _bar.PrimaryCommands.Add(wrap);
        _bar.PrimaryCommands.Add(new AppBarSeparator());
        _bar.PrimaryCommands.Add(new AppBarElementContainer { Content = _languages, VerticalContentAlignment = VerticalAlignment.Center });
        _bar.PrimaryCommands.Add(new AppBarElementContainer { Content = _encodings, VerticalContentAlignment = VerticalAlignment.Center });
        _bar.PrimaryCommands.Add(new AppBarElementContainer { Content = _lineEndings, VerticalContentAlignment = VerticalAlignment.Center });
        var minimap = new AppBarToggleButton { Label = Loc.T("Minimap"), Icon = new FontIcon { Glyph = "\uE8A9" }, IsChecked = _options.EditorMinimap };
        minimap.Click += (_, _) =>
        {
            _options.EditorMinimap = minimap.IsChecked == true;
            SendOptions();
        };
        _bar.SecondaryCommands.Add(minimap);
        var backup = new AppBarToggleButton { Label = Loc.T("Keep a backup (.bak) when saving"), IsChecked = _options.EditorBackup };
        backup.Click += (_, _) => _options.EditorBackup = backup.IsChecked == true;
        _bar.SecondaryCommands.Add(backup);
        _bar.SecondaryCommands.Add(new AppBarSeparator());
        _bar.SecondaryCommands.Add(Button("Fold all", "\uE8C5", () => Command("editor.foldAll")));
        _bar.SecondaryCommands.Add(Button("Unfold all", "\uE8C4", () => Command("editor.unfoldAll")));
        _bar.SecondaryCommands.Add(Button("Command palette", "\uE756", () => Command("editor.action.quickCommand")));
        _bar.SecondaryCommands.Add(new AppBarSeparator());
        _bar.SecondaryCommands.Add(Button("View (F3)", "\uE890", () => _main.ViewWithoutRules(_path)));
        _bar.SecondaryCommands.Add(Button("Open with the default app", "\uE8A7", () => Run(() => ShellOperations.Open(_path, Path.GetDirectoryName(_path)))));
        _bar.SecondaryCommands.Add(Button("Show in folder", "\uEC50", () => Run(() => System.Diagnostics.Process.Start("explorer.exe", "/select,\"" + _path + "\"")?.Dispose())));
        Grid.SetRow(_bar, 1);
        _root.Children.Add(_bar);

        Grid.SetRow(_infoBar, 2);
        _root.Children.Add(_infoBar);

        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(0) });
        _body.Children.Add(_web);
        // the first activation would put the keyboard on the toolbar's first button: the text gets it
        _web.Loaded += (_, _) => DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, FocusText);
        Grid.SetRow(_body, 3);
        _root.Children.Add(_body);

        var status = new Grid { Padding = new Thickness(12, 4, 12, 6), ColumnSpacing = 24 };
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        status.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        status.Children.Add(_position);
        Grid.SetColumn(_problems, 1);
        status.Children.Add(_problems);
        Grid.SetColumn(_fileInfo, 2);
        status.Children.Add(_fileInfo);
        Grid.SetRow(status, 4);
        _root.Children.Add(status);

        foreach (string language in EditorLanguages.All)
            _languages.Items.Add(language);
        _languages.SelectionChanged += (_, _) =>
        {
            if (_languages.SelectedItem is string language && language != _language)
            {
                _language = language;
                Post(new { type = "language", language });
                UpdateLanguageTools();
                CheckAndPreview();
            }
        };
        _lineEndings.Items.Add("CRLF");
        _lineEndings.Items.Add("LF");
        _lineEndings.Items.Add("CR");
        _lineEndings.SelectionChanged += (_, _) =>
        {
            var ending = (LineEnding)Math.Max(0, _lineEndings.SelectedIndex);
            if (ending == _lineEnding)
                return;
            _lineEnding = ending;
            Post(new { type = "eol", eol = ending == LineEnding.Lf ? "LF" : "CRLF" });
            MarkChanged();
        };
        ToolTipService.SetToolTip(_languages, Loc.T("Language"));
        ToolTipService.SetToolTip(_encodings, Loc.T("Encoding (when saving)"));
        ToolTipService.SetToolTip(_lineEndings, Loc.T("Line endings"));
        _root.KeyDown += (_, e) =>
        {
            // the text box editor (no WebView2): the same keys
            if (_plain == null)
                return;
            bool ctrl = KeyboardState.IsDown(KeyboardState.Control);
            if (ctrl && e.Key == Windows.System.VirtualKey.S)
            {
                e.Handled = true;
                _ = KeyboardState.IsDown(KeyboardState.Shift) ? SaveAsAsync() : SaveAsync();
            }
        };
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
            ShowInfo(InfoBarSeverity.Error, ex.Message);
        }
    }

    private void ShowInfo(InfoBarSeverity severity, string message, string? actionText = null, Action? action = null)
    {
        _infoBar.Severity = severity;
        _infoBar.Message = message;
        _infoBar.ActionButton = null;
        if (actionText != null && action != null)
        {
            var button = new Button { Content = Loc.T(actionText) };
            button.Click += (_, _) =>
            {
                _infoBar.IsOpen = false;
                action();
            };
            _infoBar.ActionButton = button;
        }
        _infoBar.IsOpen = true;
    }

    /// <summary>The keyboard goes to the text (not the toolbar): the WebView gets the focus, then Monaco inside it.</summary>
    private void FocusText()
    {
        if (_web.Visibility != Visibility.Visible)
            return;
        _web.Focus(FocusState.Programmatic);
        Post(new { type = "focus" });
    }

    private void UpdateTitle()
    {
        string name = Path.GetFileName(_path);
        Title = (IsDirty ? "● " : string.Empty) + name + " - " + Loc.T("Vibe Salamander Editor");
        _titleText.Text = Title;
    }

    private void UpdateLanguageTools()
    {
        StructuredFormat? format = EditorLanguages.Checked(_language);
        _formatButton.Visibility = format is StructuredFormat.Json or StructuredFormat.Xml ? Visibility.Visible : Visibility.Collapsed;
        _minifyButton.Visibility = _formatButton.Visibility;
        _sortKeysButton.Visibility = format is StructuredFormat.Json ? Visibility.Visible : Visibility.Collapsed;
        _previewButton.Visibility = EditorLanguages.Preview(_language, _path) != null ? Visibility.Visible : Visibility.Collapsed;
        ApplyPreviewLayout();
        if (format == null)
        {
            _problems.Text = string.Empty;
            Post(new { type = "markers", markers = Array.Empty<object>() });
        }
    }

    // ---- loading -------------------------------------------------------------------------------------------

    private async Task OpenAsync(TextEncoding? encoding = null)
    {
        LoadedText loaded;
        try
        {
            string path = _path;
            loaded = await Task.Run(() => TextDocuments.Load(path, encoding));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoBarSeverity.Error, ex.Message);
            return;
        }
        _encoding = loaded.Encoding;
        _lineEnding = loaded.LineEnding;
        _language = EditorLanguages.For(_path);
        _languages.SelectedItem = _language;
        _lineEndings.SelectedIndex = (int)_lineEnding;
        FillEncodings();
        UpdateTitle();
        UpdateLanguageTools();
        _fileInfo.Text = _encoding.Name;
        if (loaded.LooksBinary)
            ShowInfo(InfoBarSeverity.Warning, Loc.T("This looks like a binary file: saving it from the editor can damage it."));
        else if (loaded.MixedLineEndings)
            ShowInfo(InfoBarSeverity.Informational, Loc.F("The file mixes line endings: they're saved as {0}.", _lineEnding.ToString().ToUpperInvariant()));
        else if ((File.GetAttributes(_path) & FileAttributes.ReadOnly) != 0)
            ShowInfo(InfoBarSeverity.Warning, Loc.T("The file is read-only: Save As keeps your changes in another file."));

        if (WebViews.RuntimeVersion() == null)
        {
            UsePlainEditor(loaded.Text);
            return;
        }
        try
        {
            if (_core == null)
            {
                _core = await WebViews.PrepareAsync(_web, scripts: true);
                _core.Settings.AreDefaultContextMenusEnabled = false; // Monaco's own menu
                _core.SetVirtualHostNameToFolderMapping("vibe-editor.local", Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
                _core.WebMessageReceived += Core_WebMessageReceived;
                _core.NavigationStarting += (_, e) =>
                {
                    if (!e.Uri.StartsWith("https://vibe-editor.local/", StringComparison.OrdinalIgnoreCase))
                        e.Cancel = true; // the editor page only
                };
                _pendingText = loaded.Text;
                _core.Navigate("https://vibe-editor.local/Editor/editor.html");
            }
            else
            {
                Load(loaded.Text);
            }
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or FileNotFoundException)
        {
            UsePlainEditor(loaded.Text);
            ShowInfo(InfoBarSeverity.Warning, Loc.F("The full editor couldn't start ({0}): this is a plain text box.", ex.Message));
        }
        StartWatching();
    }

    private string? _pendingText;

    private void Load(string text)
    {
        _lastText = text;
        Post(new { type = "load", text, language = _language, eol = _lineEnding == LineEnding.Lf ? "LF" : "CRLF", line = 1 });
    }

    private void FillEncodings()
    {
        _encodings.SelectionChanged -= Encodings_SelectionChanged;
        _encodings.Items.Clear();
        _encodings.Items.Add(new ComboBoxItem { Content = _encoding.Name, Tag = _encoding });
        foreach (TextEncoding choice in TextEncodings.Choices())
            if (choice.Name != _encoding.Name)
                _encodings.Items.Add(new ComboBoxItem { Content = choice.Name, Tag = choice });
        _encodings.SelectedIndex = 0;
        _encodings.SelectionChanged += Encodings_SelectionChanged;
    }

    /// <summary>Another encoding: saved in it from now on; when there are no changes yet, the file is also read again in it.</summary>
    private async void Encodings_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_encodings.SelectedItem is not ComboBoxItem { Tag: TextEncoding chosen } || chosen == _encoding)
            return;
        if (!IsDirty)
        {
            await OpenAsync(chosen);
            return;
        }
        _encoding = chosen;
        _fileInfo.Text = _encoding.Name;
        MarkChanged();
    }

    private void UsePlainEditor(string text)
    {
        _web.Visibility = Visibility.Collapsed;
        _plain = new TextBox
        {
            Text = text,
            AcceptsReturn = true,
            TextWrapping = _options.EditorWordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = _options.EditorFontSize,
            IsSpellCheckEnabled = false,
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_plain, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_plain, ScrollBarVisibility.Auto);
        _plain.TextChanged += (_, _) =>
        {
            _version++;
            UpdateTitle();
            _lastText = _plain.Text;
            _checkTimer.Start();
        };
        _body.Children.Add(_plain);
        _savedVersion = _version;
        _lastText = text;
        ShowInfo(InfoBarSeverity.Informational, Loc.T("The Microsoft Edge WebView2 Runtime isn't installed, so this is a plain text box.") + " " + WebViews.RuntimeDownload);
        _plain.Focus(FocusState.Programmatic);
        CheckAndPreview();
    }

    // ---- messages from the editor page --------------------------------------------------------------------

    private void Post(object message)
    {
        if (_core == null || !_ready)
            return;
        _core.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private void SendOptions()
    {
        if (_plain != null)
        {
            _plain.TextWrapping = _options.EditorWordWrap ? TextWrapping.Wrap : TextWrapping.NoWrap;
            return;
        }
        Post(new
        {
            type = "options",
            theme = _root.ActualTheme == ElementTheme.Dark ? "vs-dark" : "vs",
            options = new
            {
                wordWrap = _options.EditorWordWrap ? "on" : "off",
                minimap = new { enabled = _options.EditorMinimap },
                fontSize = _options.EditorFontSize,
            },
        });
    }

    private void Command(string id)
    {
        if (_plain != null)
            return;
        _web.Focus(FocusState.Programmatic); // Monaco focuses itself inside; the WebView must have the keyboard first
        Post(new { type = "command", id });
    }

    private void Core_WebMessageReceived(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        using JsonDocument document = JsonDocument.Parse(e.WebMessageAsJson);
        JsonElement m = document.RootElement;
        switch (m.GetProperty("type").GetString())
        {
            case "ready":
                _ready = true;
                SendOptions();
                if (_pendingText != null)
                {
                    Load(_pendingText);
                    _pendingText = null;
                }
                break;
            case "loaded":
                _version = _savedVersion = m.GetProperty("version").GetInt32();
                _otherChange = false;
                UpdateTitle();
                FocusText();
                break;
            case "changed":
                _version = m.GetProperty("version").GetInt32();
                UpdateTitle();
                break;
            case "cursor":
                _position.Text = Loc.F("Ln {0}, Col {1}", m.GetProperty("line").GetInt32(), m.GetProperty("column").GetInt32());
                break;
            case "text":
                int id = m.GetProperty("id").GetInt32();
                string text = m.GetProperty("text").GetString() ?? string.Empty;
                int version = m.GetProperty("version").GetInt32();
                if (id != 0 && _textRequests.Remove(id, out var request))
                    request.TrySetResult((text, version));
                _lastText = text;
                _checkTimer.Stop();
                _checkTimer.Start();
                break;
            case "key":
                switch (m.GetProperty("key").GetString())
                {
                    case "save": _ = SaveAsync(); break;
                    case "saveAs": _ = SaveAsAsync(); break;
                    case "format": _ = FormatAsync(); break;
                    case "viewer": _main.ViewWithoutRules(_path); break;
                    case "preview":
                        _previewButton.IsChecked = !(_previewButton.IsChecked == true);
                        _options.EditorPreview = _previewButton.IsChecked == true;
                        ApplyPreviewLayout();
                        CheckAndPreview();
                        break;
                }
                break;
        }
    }

    /// <summary>The editor's current text (and its version, to know what was saved).</summary>
    private async Task<(string Text, int Version)> CurrentTextAsync()
    {
        if (_plain != null)
            return (_plain.Text, _version);
        if (_core == null || !_ready)
            return (_lastText, _version);
        int id = _nextRequest++;
        var request = new TaskCompletionSource<(string, int)>(TaskCreationOptions.RunContinuationsAsynchronously);
        _textRequests[id] = request;
        Post(new { type = "getText", id });
        return await request.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private void MarkChanged()
    {
        _otherChange = true;
        UpdateTitle();
    }

    // ---- checking and the live preview ---------------------------------------------------------------------

    private void CheckAndPreview()
    {
        string text = _lastText;
        if (EditorLanguages.Checked(_language) is { } format)
        {
            IReadOnlyList<ParseProblem> problems = text.Length > 8 * 1024 * 1024 ? Array.Empty<ParseProblem>() : Structured.Validate(text, format);
            _problems.Text = problems.Count == 0 ? "✓ " + Loc.F("Valid {0}", format.ToString().ToUpperInvariant())
                : Loc.F("Line {0}: {1}", problems[0].Line, problems[0].Message);
            _problems.Foreground = problems.Count == 0 ? (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] : new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE0, 0x50, 0x50));
            Post(new { type = "markers", markers = problems.Select(p => new { line = p.Line, column = p.Column, message = p.Message }).ToArray() });
        }
        if (_previewButton.IsChecked == true && EditorLanguages.Preview(_language, _path) is { } preview)
            _ = UpdatePreviewAsync(preview, text);
    }

    private void ApplyPreviewLayout()
    {
        bool show = _previewButton.IsChecked == true && EditorLanguages.Preview(_language, _path) != null;
        _body.ColumnDefinitions[1].Width = show ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        if (_preview != null)
            _preview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task UpdatePreviewAsync(string kind, string text)
    {
        if (WebViews.RuntimeVersion() == null)
            return;
        try
        {
            if (_preview == null)
            {
                _preview = new WebView2 { Margin = new Thickness(4, 0, 0, 0) };
                Grid.SetColumn(_preview, 1);
                _body.Children.Add(_preview);
                CoreWebView2 core = await WebViews.PrepareAsync(_preview, scripts: false);
                core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                core.WebResourceRequested += (sender, e) =>
                {
                    if (!_options.AllowRemoteContent && (e.Request.Uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) ||
                                                         e.Request.Uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase)))
                        e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
                };
                core.NavigationStarting += (_, e) =>
                {
                    if (_previewFile == null || !e.Uri.StartsWith(new Uri(_previewFile).AbsoluteUri, StringComparison.OrdinalIgnoreCase))
                        e.Cancel = true; // links don't navigate away from the preview
                };
                ApplyPreviewLayout();
            }
            bool dark = _root.ActualTheme == ElementTheme.Dark;
            string path = _path;
            bool remote = _options.AllowRemoteContent;
            string page = kind switch
            {
                "markdown" => await Task.Run(() => HtmlRendering.MarkdownPage(path, text, dark, remote)),
                // the drawing in the middle, on a checkerboard (its transparent parts show); no scripts run
                "svg" => "<!doctype html><html><head><meta charset=\"utf-8\"><style>html,body{margin:0;height:100%}" +
                         "body{display:grid;place-items:center;background:repeating-conic-gradient(#ccc 0 25%,#fff 0 50%) 0 0/16px 16px}" +
                         "svg{max-width:100%;max-height:100vh}</style></head><body>" + text + "</body></html>",
                _ => text,
            };
            // a file beside the others of the app (relative pictures come from the document's folder)
            if (_previewFile == null)
            {
                string folder = Path.Combine(Path.GetTempPath(), "VibeSalamander", "Preview");
                Directory.CreateDirectory(folder);
                _previewFile = Path.Combine(folder, Guid.NewGuid().ToString("N")[..8] + ".html");
            }
            if (kind == "html")
            {
                // the page's own folder for its relative links: a <base> at the start of the head
                string baseTag = $"<base href=\"{new Uri(Path.GetDirectoryName(path)!.TrimEnd('\\') + "\\").AbsoluteUri}\">";
                int head = page.IndexOf("<head>", StringComparison.OrdinalIgnoreCase);
                page = head >= 0 ? page.Insert(head + 6, baseTag) : baseTag + page;
            }
            await File.WriteAllTextAsync(_previewFile, page);
            _preview.CoreWebView2?.Navigate(new Uri(_previewFile).AbsoluteUri);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            // the preview stays as it was
        }
    }

    // ---- formatting ----------------------------------------------------------------------------------------

    private async Task FormatAsync()
    {
        if (EditorLanguages.Checked(_language) is not { } format)
            return;
        var (text, _) = await CurrentTextAsync();
        string? formatted = await Task.Run(() => Structured.Format(text, format, out string? reason) is { } result ? result : "\0" + reason);
        if (formatted.StartsWith('\0'))
        {
            ShowInfo(InfoBarSeverity.Warning, Loc.T("Not formatted:") + " " + formatted[1..]);
            return;
        }
        if (_plain != null)
        {
            _plain.Text = formatted;
            _plain.Focus(FocusState.Programmatic);
        }
        else
        {
            Post(new { type = "replaceAll", text = formatted });
            FocusText(); // the keyboard back in the text (Ctrl+S, Ctrl+Z), not on the toolbar button
        }
    }

    /// <summary>Minify (JSON, XML) or Sort keys (JSON): one edit, so Ctrl+Z brings the text back.</summary>
    private async Task RewriteAsync(bool minify)
    {
        if (EditorLanguages.Checked(_language) is not { } format)
            return;
        var (text, _) = await CurrentTextAsync();
        string result = await Task.Run(() =>
        {
            string? done = minify ? Structured.Minify(text, format, out string? reason) : Structured.SortKeys(text, out reason);
            return done ?? "\0" + reason;
        });
        if (result.StartsWith('\0'))
        {
            ShowInfo(InfoBarSeverity.Warning, Loc.T(minify ? "Not minified:" : "Keys not sorted:") + " " + Loc.T(result[1..]));
            return;
        }
        if (_plain != null)
        {
            _plain.Text = result;
            _plain.Focus(FocusState.Programmatic);
        }
        else
        {
            Post(new { type = "replaceAll", text = result });
            FocusText(); // the keyboard back in the text (Ctrl+S, Ctrl+Z), not on the toolbar button
        }
    }

    // ---- saving ------------------------------------------------------------------------------------------

    private async Task<bool> SaveAsync()
    {
        var (text, version) = await CurrentTextAsync();
        // characters the encoding can't hold: saved as UTF-8 instead, or as they are (they become "?")
        int lost = TextDocuments.Unencodable(text, _encoding);
        if (lost > 0)
        {
            ContentDialog ask = _dialogs.Create("Save", new TextBlock
            {
                Text = Loc.F("{0} characters can't be saved in {1}. Save the file as UTF-8 instead?", lost, _encoding.Name),
                TextWrapping = TextWrapping.Wrap,
            });
            ask.PrimaryButtonText = Loc.T("Save as UTF-8");
            ask.SecondaryButtonText = Loc.F("Save in {0}", _encoding.Name);
            ask.CloseButtonText = Loc.T("Cancel");
            ask.DefaultButton = ContentDialogButton.Primary;
            ContentDialogResult answer = await _dialogs.ShowDialogAsync(ask);
            if (answer == ContentDialogResult.None)
                return false;
            if (answer == ContentDialogResult.Primary)
            {
                _encoding = TextEncodings.Utf8;
                FillEncodings();
            }
        }
        string path = _path;
        try
        {
            _ownWriteUntil = DateTime.UtcNow.AddSeconds(3);
            TextEncoding encoding = _encoding;
            LineEnding ending = _lineEnding;
            bool backup = _options.EditorBackup;
            await Task.Run(() => TextDocuments.Save(path, text, encoding, ending, backup));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowInfo(InfoBarSeverity.Error, Loc.F("\"{0}\" wasn't saved: {1}", Path.GetFileName(path), ex.Message), "Save as...", () => _ = SaveAsAsync());
            return false;
        }
        _savedVersion = version;
        _otherChange = false;
        _infoBar.IsOpen = false;
        _fileInfo.Text = _encoding.Name;
        UpdateTitle();
        _main.Notify(Loc.F("Saved: {0}", Path.GetFileName(path)), isError: false);
        if (_afterSave != null)
        {
            try
            {
                await _afterSave(path);
            }
            catch (Exception ex)
            {
                ShowInfo(InfoBarSeverity.Error, ex.Message);
            }
        }
        return true;
    }

    private async Task<bool> SaveAsAsync()
    {
        var picker = new Windows.Storage.Pickers.FileSavePicker { SuggestedFileName = Path.GetFileName(_path) };
        string extension = Path.GetExtension(_path);
        picker.FileTypeChoices.Add(Loc.T("This type"), new List<string> { extension.Length > 0 ? extension : ".txt" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        Windows.Storage.StorageFile? file = await picker.PickSaveFileAsync();
        if (file == null)
            return false;
        _path = file.Path;
        StartWatching();
        bool saved = await SaveAsync();
        UpdateTitle();
        return saved;
    }

    private async Task<bool> ConfirmCloseAsync()
    {
        ContentDialog ask = _dialogs.Create("Unsaved changes", new TextBlock
        {
            Text = Loc.F("Do you want to save the changes to \"{0}\"?", Path.GetFileName(_path)),
            TextWrapping = TextWrapping.Wrap,
        });
        ask.PrimaryButtonText = Loc.T("Save");
        ask.SecondaryButtonText = Loc.T("Don't save");
        ask.CloseButtonText = Loc.T("Cancel");
        ask.DefaultButton = ContentDialogButton.Primary;
        return await _dialogs.ShowDialogAsync(ask) switch
        {
            ContentDialogResult.Primary => await SaveAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    // ---- changes on disk ---------------------------------------------------------------------------------------

    private void StartWatching()
    {
        _watcher?.Dispose();
        string? directory = Path.GetDirectoryName(_path);
        if (directory == null || !Directory.Exists(directory))
            return;
        try
        {
            _watcher = new FileSystemWatcher(directory, Path.GetFileName(_path))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _watcher.Changed += (_, _) => DispatcherQueue.TryEnqueue(ChangedOnDisk);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            _watcher = null; // no warning about changes, everything else works
        }
    }

    private void ChangedOnDisk()
    {
        if (DateTime.UtcNow < _ownWriteUntil)
            return; // our own save
        ShowInfo(InfoBarSeverity.Warning,
            IsDirty ? Loc.T("The file changed on disk. Reload it (your changes are lost) or keep editing and save over it.")
                    : Loc.T("The file changed on disk."),
            "Reload", () => _ = OpenAsync(_encoding));
    }

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (IOException)
        {
            // the temporary folder is emptied another time
        }
    }
}
