// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;
using VibeSalamander.Core.Interop;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;

namespace VibeSalamander.Views.Viewer;

// ViewerWindow: Markdown, Jupyter notebooks and web pages, rendered in a web view. Scripts are off and remote content
// (pictures, styles from the internet) is blocked unless the toolbar allows them; links open in the browser.
internal sealed partial class ViewerWindow
{
    private WebView2? _web;
    private Grid? _webHost;
    private CoreWebView2? _core;
    private string? _allowedUri;
    private AppBarToggleButton? _remoteButton;
    private AppBarToggleButton? _scriptsButton;

    private void BuildPreviewTools()
    {
        _remoteButton = new AppBarToggleButton { Label = Loc.T("Remote content"), Icon = new FontIcon { Glyph = "" }, IsChecked = _options.AllowRemoteContent };
        ToolTipService.SetToolTip(_remoteButton, Loc.T("Load pictures and styles from the internet"));
        _remoteButton.Click += (_, _) =>
        {
            _options.AllowRemoteContent = _remoteButton.IsChecked == true;
            _ = ShowPreviewAsync(reload: true);
        };
        _scriptsButton = new AppBarToggleButton { Label = Loc.T("Scripts"), Icon = new FontIcon { Glyph = "" }, IsChecked = _options.AllowScripts };
        ToolTipService.SetToolTip(_scriptsButton, Loc.T("Let the web page run its scripts"));
        _scriptsButton.Click += (_, _) =>
        {
            _options.AllowScripts = _scriptsButton.IsChecked == true;
            _ = ShowPreviewAsync(reload: true);
        };
        _bar.PrimaryCommands.Add(_remoteButton);
        _bar.PrimaryCommands.Add(_scriptsButton);
    }

    private void SetPreviewToolsVisible(bool visible)
    {
        if (_remoteButton != null)
            _remoteButton.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (_scriptsButton != null)
            _scriptsButton.Visibility = visible && HtmlRendering.IsHtml(_path) ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowPreviewAsync(bool reload = false)
    {
        if (WebViews.RuntimeVersion() == null)
        {
            ShowMessage(Loc.T("The preview needs the Microsoft Edge WebView2 Runtime, which isn't installed.") + "\n" + WebViews.RuntimeDownload
                + "\n\n" + Loc.T("Text (on the toolbar) shows the text."));
            return;
        }
        if (_webHost == null)
        {
            _web = new WebView2();
            _webHost = new Grid();
            _webHost.Children.Add(_web);
            _content.Children.Add(_webHost);
        }
        _webHost.Visibility = Visibility.Visible;
        string path = _path;
        bool html = HtmlRendering.IsHtml(path);
        bool scripts = html && _options.AllowScripts;
        try
        {
            if (_core == null)
            {
                _core = await WebViews.PrepareAsync(_web!, scripts);
                _core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
                _core.WebResourceRequested += Core_WebResourceRequested;
                _core.NavigationStarting += Core_NavigationStarting;
                _core.NewWindowRequested += (_, e) =>
                {
                    e.Handled = true;
                    OpenOutside(e.Uri);
                };
            }
            _core.Settings.IsScriptEnabled = scripts;
            if (!reload && _allowedUri != null && _allowedUri == PageUri(path))
                return;

            if (html)
            {
                _allowedUri = new Uri(path).AbsoluteUri;
            }
            else
            {
                bool dark = _root.ActualTheme == ElementTheme.Dark;
                bool remote = _options.AllowRemoteContent;
                string page = await Task.Run(() =>
                {
                    string text = TextDocuments.Load(path).Text;
                    return HtmlRendering.IsNotebook(path)
                        ? HtmlRendering.NotebookPage(path, text, dark, remote)
                        : HtmlRendering.MarkdownPage(path, text, dark, remote);
                });
                // a file (NavigateToString has a size limit, and relative pictures need a file page)
                string folder = Path.Combine(Path.GetTempPath(), "VibeSalamander", "Preview");
                Directory.CreateDirectory(folder);
                string file = Path.Combine(folder, Guid.NewGuid().ToString("N")[..8] + ".html");
                await File.WriteAllTextAsync(file, page);
                _previewFiles.Add(file);
                _allowedUri = new Uri(file).AbsoluteUri;
            }
            if (_closed || path != _path)
                return;
            _core.Navigate(_allowedUri);
            _status.Text = string.Empty;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or COMException or InvalidOperationException)
        {
            ShowMessage(Loc.F("\"{0}\" can't be shown: {1}", Path.GetFileName(path), ex.Message) + "\n\n" + Loc.T("Text (on the toolbar) shows the text."));
        }
    }

    private readonly List<string> _previewFiles = new();

    private string? PageUri(string path) => HtmlRendering.IsHtml(path) ? new Uri(path).AbsoluteUri : _allowedUri;

    /// <summary>Remote content (http, https) is blocked unless allowed.</summary>
    private void Core_WebResourceRequested(CoreWebView2 sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        if (_options.AllowRemoteContent)
            return;
        string uri = e.Request.Uri;
        if (uri.StartsWith("http:", StringComparison.OrdinalIgnoreCase) || uri.StartsWith("https:", StringComparison.OrdinalIgnoreCase) ||
            uri.StartsWith("ws", StringComparison.OrdinalIgnoreCase))
            e.Response = sender.Environment.CreateWebResourceResponse(null, 403, "Blocked", string.Empty);
    }

    /// <summary>The page itself (and its own anchors) is shown here; links go to the browser (or the app that opens them).</summary>
    private void Core_NavigationStarting(CoreWebView2 sender, CoreWebView2NavigationStartingEventArgs e)
    {
        if (_allowedUri != null && (e.Uri == _allowedUri || e.Uri.StartsWith(_allowedUri + "#", StringComparison.Ordinal)))
            return;
        e.Cancel = true;
        if (e.IsUserInitiated)
            OpenOutside(e.Uri);
    }

    private void OpenOutside(string uri)
    {
        if (Uri.TryCreate(uri, UriKind.Absolute, out Uri? target) && target.Scheme is "http" or "https" or "mailto" or "file")
            Run(() => ShellOperations.Open(target.IsFile ? target.LocalPath : target.AbsoluteUri));
    }

    private async Task FindInPreviewAsync(bool backwards)
    {
        if (_core == null || _findBox.Text.Length == 0)
            return;
        // the page's own find (window.find works without the page's scripts: it's the host's script)
        bool wasEnabled = _core.Settings.IsScriptEnabled;
        _core.Settings.IsScriptEnabled = true;
        try
        {
            string text = System.Text.Json.JsonSerializer.Serialize(_findBox.Text);
            string result = await _core.ExecuteScriptAsync($"window.find({text}, {(_matchCase.IsChecked == true ? "true" : "false")}, {(backwards ? "true" : "false")}, true)");
            _findStatus.Text = result == "true" ? string.Empty : Loc.T("Not found");
        }
        finally
        {
            _core.Settings.IsScriptEnabled = wasEnabled;
        }
    }

    private void CleanPreviewFiles()
    {
        foreach (string file in _previewFiles)
        {
            try
            {
                File.Delete(file);
            }
            catch (IOException)
            {
                // still open somewhere: the temporary folder is emptied at exit
            }
        }
        _previewFiles.Clear();
    }
}
