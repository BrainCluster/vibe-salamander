// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage;
using Windows.Storage.Streams;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;

namespace VibeSalamander.Views.Viewer;

// ViewerWindow: PDF documents (Windows' own PDF renderer). Pages are drawn when they come near the view and let go
// when far away, so long documents stay light; zooming draws them again sharper.
internal sealed partial class ViewerWindow
{
    private ScrollViewer? _pdfScroller;
    private StackPanel? _pdfPages;
    private PdfDocument? _pdf;
    private string? _pdfPath;
    private readonly Dictionary<int, double> _pdfRendered = new(); // page → the zoom it was drawn for
    private readonly HashSet<int> _pdfRendering = new();
    private int _pdfGeneration;

    private async Task ShowPdfAsync()
    {
        if (_pdfScroller == null)
        {
            _pdfPages = new StackPanel { Spacing = 12, Padding = new Thickness(12), HorizontalAlignment = HorizontalAlignment.Center };
            _pdfScroller = new ScrollViewer
            {
                ZoomMode = ZoomMode.Enabled,
                MinZoomFactor = 0.2f,
                MaxZoomFactor = 6f,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollMode = ScrollMode.Auto,
                Content = _pdfPages,
                IsTabStop = true,
                UseSystemFocusVisuals = false, // no focus rectangle around the pages
            };
            _pdfScroller.ViewChanged += (_, e) =>
            {
                UpdatePdfStatus();
                if (!e.IsIntermediate)
                    _ = RenderVisiblePagesAsync();
            };
            _pdfScroller.SizeChanged += (_, _) => _ = RenderVisiblePagesAsync();
            _content.Children.Add(_pdfScroller);
        }
        _pdfScroller.Visibility = Visibility.Visible;
        _pdfScroller.Focus(FocusState.Programmatic);
        if (_pdfPath == _path && _pdf != null)
            return;
        ClosePdf();
        string path = _path;
        _pdfPath = path;
        int generation = ++_pdfGeneration;
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            PdfDocument document;
            try
            {
                document = await PdfDocument.LoadFromFileAsync(file);
            }
            catch (Exception ex) when (ex.HResult == unchecked((int)0x8007052B)) // a password is needed
            {
                string? password = await _dialogs.InputAsync(Loc.T("Password"), Loc.F("\"{0}\" is protected. Its password:", Path.GetFileName(path)));
                if (password == null)
                    return;
                document = await PdfDocument.LoadFromFileAsync(file, password);
            }
            if (generation != _pdfGeneration || _closed)
                return;
            _pdf = document;
            for (uint i = 0; i < document.PageCount; i++)
            {
                using PdfPage page = document.GetPage(i);
                var holder = new Border
                {
                    Width = page.Size.Width,
                    Height = page.Size.Height,
                    Background = new SolidColorBrush(Colors.White),
                    Child = new Image { Stretch = Stretch.Uniform },
                };
                _pdfPages!.Children.Add(holder);
            }
            FitPdf();
            await RenderVisiblePagesAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            if (generation == _pdfGeneration)
                ShowMessage(Loc.F("\"{0}\" can't be shown: {1}", Path.GetFileName(path), ex.Message));
        }
    }

    /// <summary>The page width fits the window.</summary>
    private void FitPdf()
    {
        if (_pdfScroller == null || _pdfPages == null || _pdfPages.Children.Count == 0 || _pdfScroller.ViewportWidth <= 0)
            return;
        double widest = _pdfPages.Children.OfType<FrameworkElement>().Max(page => page.Width) + 24;
        _pdfScroller.ChangeView(0, 0, (float)Math.Clamp(_pdfScroller.ViewportWidth / widest, _pdfScroller.MinZoomFactor, 3), disableAnimation: true);
    }

    /// <summary>Draws the pages near the view (at the zoom shown), and lets go of the far ones.</summary>
    private async Task RenderVisiblePagesAsync()
    {
        if (_pdf == null || _pdfScroller == null || _pdfPages == null || _mode != ViewerMode.Pdf)
            return;
        int generation = _pdfGeneration;
        double zoom = _pdfScroller.ZoomFactor;
        double scale = (Content.XamlRoot?.RasterizationScale ?? 1) * zoom;
        double top = _pdfScroller.VerticalOffset / zoom, bottom = top + _pdfScroller.ViewportHeight / zoom;
        double y = 12;
        var near = new List<int>();
        for (int i = 0; i < _pdfPages.Children.Count; i++)
        {
            var page = (FrameworkElement)_pdfPages.Children[i];
            double pageTop = y, pageBottom = y + page.Height;
            y = pageBottom + 12;
            double margin = page.Height * 1.5;
            bool isNear = pageBottom >= top - margin && pageTop <= bottom + margin;
            if (isNear)
                near.Add(i);
            else if (_pdfRendered.Remove(i) && page is Border { Child: Image far })
                far.Source = null;
        }
        foreach (int i in near)
        {
            if ((_pdfRendered.TryGetValue(i, out double drawnAt) && Math.Abs(drawnAt - scale) / scale < 0.3) || !_pdfRendering.Add(i))
                continue;
            try
            {
                using PdfPage page = _pdf.GetPage((uint)i);
                var options = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Clamp(page.Size.Width * scale, 16, 6000),
                };
                using var stream = new InMemoryRandomAccessStream();
                await page.RenderToStreamAsync(stream, options);
                if (generation != _pdfGeneration)
                    return;
                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(stream);
                if (generation != _pdfGeneration)
                    return;
                if (_pdfPages.Children[i] is Border { Child: Image image })
                    image.Source = bitmap;
                _pdfRendered[i] = scale;
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or ArgumentException)
            {
                // a page that can't be drawn stays white
            }
            finally
            {
                _pdfRendering.Remove(i);
            }
        }
    }

    private void UpdatePdfStatus()
    {
        if (_mode != ViewerMode.Pdf || _pdf == null || _pdfScroller == null || _pdfPages == null)
            return;
        double middle = (_pdfScroller.VerticalOffset + _pdfScroller.ViewportHeight / 2) / _pdfScroller.ZoomFactor;
        double y = 12;
        int current = 0;
        for (int i = 0; i < _pdfPages.Children.Count; i++)
        {
            y += ((FrameworkElement)_pdfPages.Children[i]).Height + 12;
            if (y > middle)
            {
                current = i;
                break;
            }
            current = i;
        }
        _status.Text = Loc.F("Page {0} of {1}", current + 1, _pdf.PageCount) + "  ·  " + Loc.F("{0} %", Math.Round(_pdfScroller.ZoomFactor * 100));
    }

    private void ClosePdf()
    {
        _pdfGeneration++;
        _pdf = null;
        _pdfPath = null;
        _pdfRendered.Clear();
        _pdfRendering.Clear();
        _pdfPages?.Children.Clear();
    }
}
