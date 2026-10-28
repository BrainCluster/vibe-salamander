// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;

namespace VibeSalamander.Views;

/// <summary>
/// Quick view (Ctrl+Q, as in Total Commander): the other panel shows the item under the caret — a picture, the start
/// of a text, what's in a folder, or the file's details — and follows the caret. Ctrl+Q again brings the panel back.
/// </summary>
internal sealed class QuickViewPanel : Border
{
    private const int TextLimit = 256 * 1024;

    private readonly TextBlock _name = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _facts = new() { Opacity = 0.7, FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly Grid _body = new();
    private readonly Image _image = new() { Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new()
    {
        FontFamily = new FontFamily("Cascadia Mono,Consolas"),
        FontSize = 12,
        IsTextSelectionEnabled = true,
        TextWrapping = TextWrapping.NoWrap,
    };
    private readonly ScrollViewer _textScroller;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75, Margin = new Thickness(16), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Center };
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _timer;
    private FileEntry? _pending;
    private string? _shown;
    private CancellationTokenSource _cancel = new();

    public QuickViewPanel()
    {
        CornerRadius = new CornerRadius(6);
        BorderThickness = new Thickness(1);
        BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"];
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var header = new StackPanel { Padding = new Thickness(12, 8, 12, 8), Spacing = 2 };
        header.Children.Add(_name);
        header.Children.Add(_facts);
        layout.Children.Add(header);
        _textScroller = new ScrollViewer
        {
            Content = _text,
            Padding = new Thickness(12, 4, 12, 8),
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
        };
        _body.Children.Add(_textScroller);
        _body.Children.Add(new Border { Child = _image, Padding = new Thickness(8) });
        _body.Children.Add(_message);
        Grid.SetRow(_body, 1);
        layout.Children.Add(_body);
        var hint = new TextBlock { Text = Loc.T("Quick view · F3 opens the viewer · Ctrl+Q closes"), Opacity = 0.55, FontSize = 11, Margin = new Thickness(12, 4, 12, 6) };
        Grid.SetRow(hint, 2);
        layout.Children.Add(hint);
        Child = layout;
        _timer = DispatcherQueue.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(120);
        _timer.IsRepeating = false;
        _timer.Tick += (_, _) =>
        {
            if (_pending is { } entry)
                _ = ShowAsync(entry);
        };
    }

    /// <summary>The item under the caret changed: shown a moment later (holding an arrow key doesn't read every file).</summary>
    public void Show(FileEntry? entry)
    {
        if (entry == null || entry.IsUpDir)
        {
            _pending = null;
            _shown = null;
            _name.Text = string.Empty;
            _facts.Text = string.Empty;
            Only(_message);
            _message.Text = Loc.T("Put the caret on a file to see it here.");
            return;
        }
        if (string.Equals(entry.FullPath, _shown, StringComparison.OrdinalIgnoreCase))
            return;
        _pending = entry;
        _timer.Stop();
        _timer.Start();
    }

    public void Clear()
    {
        _cancel.Cancel();
        _image.Source = null;
        _text.Text = string.Empty;
        _shown = null;
    }

    private void Only(FrameworkElement visible)
    {
        _textScroller.Visibility = visible == _textScroller ? Visibility.Visible : Visibility.Collapsed;
        ((FrameworkElement)_image.Parent).Visibility = visible == _image ? Visibility.Visible : Visibility.Collapsed;
        _message.Visibility = visible == _message ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task ShowAsync(FileEntry entry)
    {
        _cancel.Cancel();
        _cancel = new CancellationTokenSource();
        CancellationToken ct = _cancel.Token;
        _shown = entry.FullPath;
        _name.Text = entry.Name;
        _image.Source = null;
        _text.Text = string.Empty;
        string path = entry.FullPath;
        try
        {
            if (entry.IsDirectory)
            {
                _facts.Text = Formats.Date(entry.LastWrite) + "  " + Formats.Time(entry.LastWrite);
                Only(_message);
                _message.Text = Loc.T("Counting...");
                var (files, folders, bytes, more) = await Task.Run(() => Count(path, ct), ct);
                if (ct.IsCancellationRequested)
                    return;
                _message.Text = Loc.F("{0} files, {1} folders, {2}", files.ToString("N0"), folders.ToString("N0"), Formats.ShortSize(bytes))
                    + (more ? "  (" + Loc.T("only the first 100,000 items") + ")" : string.Empty);
                return;
            }
            _facts.Text = $"{Formats.ShortSize(entry.Size)}  ·  {Formats.Date(entry.LastWrite)} {Formats.Time(entry.LastWrite)}";
            if (ViewerModes.IsImage(path) && !path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var bitmap = new BitmapImage { DecodePixelWidth = Math.Max(64, (int)Math.Min(2048, ActualWidth * (XamlRoot?.RasterizationScale ?? 1))) };
                Windows.Storage.StorageFile file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                if (ct.IsCancellationRequested)
                    return;
                _image.Source = bitmap;
                _facts.Text += $"  ·  {bitmap.PixelWidth} × {bitmap.PixelHeight}";
                Only(_image);
                return;
            }
            var (text, binary) = await Task.Run(() => ReadText(path), ct);
            if (ct.IsCancellationRequested)
                return;
            if (binary)
            {
                Only(_message);
                _message.Text = Loc.T("Not a text file.") + "\n" + Loc.T("F3 opens the viewer (hex, pictures, media, PDF...).");
                return;
            }
            _text.Text = text;
            _textScroller.ChangeView(0, 0, null, disableAnimation: true);
            Only(_textScroller);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            if (ct.IsCancellationRequested)
                return;
            Only(_message);
            _message.Text = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // the caret moved on
        }
    }

    private static (string Text, bool Binary) ReadText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        byte[] buffer = new byte[(int)Math.Min(TextLimit, Math.Max(0, stream.Length))];
        int read = stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false);
        ReadOnlySpan<byte> bytes = buffer.AsSpan(0, read);
        TextEncoding encoding = TextEncodings.Detect(bytes);
        if (TextEncodings.LooksBinary(bytes[..Math.Min(bytes.Length, 8192)], encoding))
            return (string.Empty, true);
        string text = encoding.Encoding.GetString(bytes[Math.Min(encoding.BomLength, bytes.Length)..]);
        if (stream.Length > TextLimit)
            text += "\n\n" + Loc.F("(the first {0} of {1})", Formats.ShortSize(TextLimit), Formats.ShortSize(stream.Length));
        return (text, false);
    }

    private static (long Files, long Folders, long Bytes, bool More) Count(string path, CancellationToken ct)
    {
        long files = 0, folders = 0, bytes = 0, seen = 0;
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        foreach (FileSystemInfo info in new DirectoryInfo(path).EnumerateFileSystemInfos("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (++seen > 100_000)
                return (files, folders, bytes, true);
            if (info is FileInfo file)
            {
                files++;
                bytes += file.Length;
            }
            else
            {
                folders++;
            }
        }
        return (files, folders, bytes, false);
    }
}
