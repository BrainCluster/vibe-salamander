// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Core.Viewing;
using VibeSalamander.Services;
using VirtualKey = Windows.System.VirtualKey;

namespace VibeSalamander.Views.Viewer;

// ViewerWindow: pictures. Every format Windows can decode (with the Store's extensions also HEIC, AVIF, JPEG XL and
// camera RAW), animated GIFs play, SVG; zoom, fit, rotate, the photo's details, and the other pictures of the folder
// with the arrow keys.
internal sealed partial class ViewerWindow
{
    private ScrollViewer? _imageScroller;
    private Image? _image;
    private Grid? _imageFrame;
    private Grid? _imageHost;
    private StackPanel? _details;
    private ScrollViewer? _detailsScroller;
    private readonly List<ICommandBarElement> _imageTools = new();
    private AppBarToggleButton? _detailsButton;
    private double _rotation;
    private int _imageWidth, _imageHeight;
    private string? _imagePath;

    private void BuildImageTools()
    {
        _imageTools.Add(Button("Zoom in", "", () => Zoom(1.25)));
        _imageTools.Add(Button("Zoom out", "", () => Zoom(0.8)));
        _imageTools.Add(Button("Fit", "", FitImage));
        _imageTools.Add(Button("Actual size", "", () => SetZoom(1)));
        _imageTools.Add(Button("Rotate", "", () => Rotate(90)));
        _detailsButton = new AppBarToggleButton { Label = Loc.T("Details"), Icon = new FontIcon { Glyph = "" }, IsChecked = _options.ShowImageDetails };
        _detailsButton.Click += (_, _) =>
        {
            _options.ShowImageDetails = _detailsButton.IsChecked == true;
            if (_detailsScroller != null)
                _detailsScroller.Visibility = _options.ShowImageDetails && _mode == ViewerMode.Image ? Visibility.Visible : Visibility.Collapsed;
        };
        _imageTools.Add(_detailsButton);
        foreach (ICommandBarElement element in _imageTools)
            _bar.PrimaryCommands.Add(element);
    }

    private void SetImageToolsVisible(bool visible)
    {
        foreach (ICommandBarElement element in _imageTools)
            ((UIElement)element).Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        // rotating and the details are for pictures, zoom for PDFs too
        foreach (ICommandBarElement element in _imageTools.Skip(4))
            ((UIElement)element).Visibility = visible && _mode == ViewerMode.Image ? Visibility.Visible : Visibility.Collapsed;
    }

    private ScrollViewer? ZoomTarget => _mode == ViewerMode.Pdf ? _pdfScroller : _imageScroller;

    private void Zoom(double factor)
    {
        if (ZoomTarget is { } scroller)
            SetZoom(scroller.ZoomFactor * factor);
    }

    private void SetZoom(double zoom)
    {
        if (ZoomTarget is not { } scroller)
            return;
        zoom = Math.Clamp(zoom, scroller.MinZoomFactor, scroller.MaxZoomFactor);
        scroller.ChangeView(null, null, (float)zoom, disableAnimation: true);
    }

    private void EnsureImageHost()
    {
        if (_imageHost != null)
            return;
        _image = new Image { Stretch = Stretch.None, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _image.ImageFailed += (_, e) => ShowImageProblem(e.ErrorMessage);
        _image.ImageOpened += (_, _) => FitImage();
        _imageFrame = new Grid { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _imageFrame.Children.Add(_image);
        _imageScroller = new ScrollViewer
        {
            ZoomMode = ZoomMode.Enabled,
            MinZoomFactor = 0.1f, // WinUI refuses less than 0.1
            MaxZoomFactor = 32f,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Auto,
            VerticalScrollMode = ScrollMode.Auto,
            Content = _imageFrame,
            IsTabStop = true,
        };
        _imageScroller.ViewChanged += (_, _) => UpdateImageStatus();
        _details = new StackPanel { Spacing = 6, Padding = new Thickness(16), Width = 280 };
        _detailsScroller = new ScrollViewer { Content = _details, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        _imageHost = new Grid();
        _imageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _imageHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _imageHost.Children.Add(_imageScroller);
        Grid.SetColumn(_detailsScroller, 1);
        _imageHost.Children.Add(_detailsScroller);
        _content.Children.Add(_imageHost);
    }

    private async Task ShowImageAsync()
    {
        EnsureImageHost();
        _imageHost!.Visibility = Visibility.Visible;
        _detailsScroller!.Visibility = _options.ShowImageDetails ? Visibility.Visible : Visibility.Collapsed;
        _imageScroller!.Focus(FocusState.Programmatic);
        if (_imagePath == _path)
            return;
        string path = _path;
        _imagePath = path;
        _rotation = 0;
        _imageFrame!.RenderTransform = null;
        _image!.Source = null;
        _details!.Children.Clear();
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            if (Path.GetExtension(path).Equals(".svg", StringComparison.OrdinalIgnoreCase))
            {
                var svg = new SvgImageSource();
                using (var stream = await file.OpenReadAsync())
                {
                    SvgImageSourceLoadStatus status = await svg.SetSourceAsync(stream);
                    if (status != SvgImageSourceLoadStatus.Success)
                    {
                        ShowImageProblem(status.ToString());
                        return;
                    }
                }
                _imageWidth = (int)svg.RasterizePixelWidth;
                _imageHeight = (int)svg.RasterizePixelHeight;
                if (path == _path)
                    _image.Source = svg;
            }
            else
            {
                var bitmap = new BitmapImage();
                using (var stream = await file.OpenReadAsync())
                    await bitmap.SetSourceAsync(stream);
                if (path != _path)
                    return;
                _imageWidth = bitmap.PixelWidth;
                _imageHeight = bitmap.PixelHeight;
                _image.Source = bitmap;
            }
            await ShowImageDetailsAsync(file);
            FitImage();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            if (path == _path)
                ShowImageProblem(ex.Message);
        }
    }

    /// <summary>A picture Windows can't decode: which free extension from the Microsoft Store would.</summary>
    private void ShowImageProblem(string error)
    {
        string ext = Path.GetExtension(_path).ToLowerInvariant();
        string? extension = ext switch
        {
            ".heic" or ".heif" => "HEIF Image Extensions",
            ".avif" => "AV1 Video Extension",
            ".jxl" => "JPEG XL Image Extension",
            ".webp" => "Webp Image Extensions",
            ".dng" or ".cr2" or ".cr3" or ".crw" or ".nef" or ".nrw" or ".arw" or ".srf" or ".sr2" or ".orf" or ".rw2" or ".raf" or ".pef" or ".srw"
                or ".3fr" or ".erf" or ".kdc" or ".mrw" or ".x3f" => "Raw Image Extension",
            _ => null,
        };
        string text = Loc.F("Windows can't show \"{0}\": {1}", Path.GetFileName(_path), error);
        if (extension != null)
            text += "\n\n" + Loc.F("Install \"{0}\" from the Microsoft Store (free), then open the picture again.", extension);
        ShowMessage(text + "\n\n" + Loc.T("Hex (on the toolbar) shows its bytes."));
    }

    private async Task ShowImageDetailsAsync(StorageFile file)
    {
        if (_details == null)
            return;
        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            _details.Children.Add(new TextBlock { Text = Loc.T(label), Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
            _details.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        var info = new FileInfo(file.Path);
        Row("Name", info.Name);
        Row("Dimensions", _imageWidth > 0 ? $"{_imageWidth} × {_imageHeight}" : null);
        Row("Size", $"{Formats.ShortSize(info.Length)}  ({Formats.ExactSize(info.Length)} {Loc.T("bytes")})");
        Row("Modified", info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture));
        try
        {
            ImageProperties properties = await file.Properties.GetImagePropertiesAsync();
            if (properties.DateTaken.Year > 1601)
                Row("Taken", properties.DateTaken.LocalDateTime.ToString("g", CultureInfo.CurrentCulture));
            Row("Camera", $"{properties.CameraManufacturer} {properties.CameraModel}".Trim());
            Row("Title", properties.Title);
            if (properties.Latitude is double lat && properties.Longitude is double lon)
                Row("Location", $"{lat.ToString("0.00000", CultureInfo.InvariantCulture)}, {lon.ToString("0.00000", CultureInfo.InvariantCulture)}");
            IDictionary<string, object> extra = await properties.RetrievePropertiesAsync(new[]
            {
                "System.Photo.FNumber", "System.Photo.ExposureTime", "System.Photo.ISOSpeed", "System.Photo.FocalLength", "System.Photo.LensModel",
            });
            if (extra.TryGetValue("System.Photo.FNumber", out object? f) && f is double fNumber)
                Row("Aperture", "f/" + fNumber.ToString("0.#", CultureInfo.CurrentCulture));
            if (extra.TryGetValue("System.Photo.ExposureTime", out object? t) && t is double exposure && exposure > 0)
                Row("Exposure", exposure < 1 ? $"1/{Math.Round(1 / exposure)} s" : exposure.ToString("0.#", CultureInfo.CurrentCulture) + " s");
            if (extra.TryGetValue("System.Photo.ISOSpeed", out object? iso) && iso != null)
                Row("ISO", iso.ToString());
            if (extra.TryGetValue("System.Photo.FocalLength", out object? focal) && focal is double focalLength)
                Row("Focal length", focalLength.ToString("0.#", CultureInfo.CurrentCulture) + " mm");
            if (extra.TryGetValue("System.Photo.LensModel", out object? lens))
                Row("Lens", lens?.ToString());
            // the camera's orientation: the picture is turned upright
            _rotation = properties.Orientation switch
            {
                PhotoOrientation.Rotate90 => 90,
                PhotoOrientation.Rotate180 => 180,
                PhotoOrientation.Rotate270 => 270,
                _ => 0,
            };
            ApplyRotation();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            // no details: the picture shows anyway
        }
    }

    private void Rotate(double degrees)
    {
        _rotation = (_rotation + degrees + 360) % 360;
        ApplyRotation();
        FitImage();
    }

    private void ApplyRotation()
    {
        if (_imageFrame == null)
            return;
        _imageFrame.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        _imageFrame.RenderTransform = _rotation == 0 ? null : new RotateTransform { Angle = _rotation };
        // turned sideways, the frame takes the turned size (so the scroll area fits)
        bool sideways = _rotation is 90 or 270;
        if (_imageWidth > 0)
        {
            _imageFrame.Width = sideways ? _imageHeight : _imageWidth;
            _imageFrame.Height = sideways ? _imageWidth : _imageHeight;
            _image!.Width = _imageWidth;
            _image.Height = _imageHeight;
        }
    }

    /// <summary>The whole picture in the window (never enlarged beyond its own size).</summary>
    private void FitImage()
    {
        if (_mode == ViewerMode.Pdf)
        {
            FitPdf();
            return;
        }
        if (_imageScroller == null || _imageWidth <= 0 || _imageScroller.ViewportWidth <= 0)
            return;
        bool sideways = _rotation is 90 or 270;
        double width = sideways ? _imageHeight : _imageWidth;
        double height = sideways ? _imageWidth : _imageHeight;
        double zoom = Math.Min(1, Math.Min(_imageScroller.ViewportWidth / width, _imageScroller.ViewportHeight / height));
        _imageScroller.ChangeView(0, 0, (float)Math.Max(_imageScroller.MinZoomFactor, zoom), disableAnimation: true);
        UpdateImageStatus();
    }

    private void UpdateImageStatus()
    {
        if (_mode != ViewerMode.Image || _imageScroller == null)
            return;
        string size = _imageWidth > 0 ? $"{_imageWidth} × {_imageHeight}  ·  " : string.Empty;
        _status.Text = size + Loc.F("{0} %", Math.Round(_imageScroller.ZoomFactor * 100));
    }

    /// <summary>Picture keys: arrows and Space for the other pictures, + / − / 0 / 1 zoom, R turns.</summary>
    private bool ImageKey(VirtualKey key)
    {
        switch (key)
        {
            case VirtualKey.Right or VirtualKey.Down or VirtualKey.Space or VirtualKey.PageDown:
                _ = StepFileAsync(+1, imagesOnly: true);
                return true;
            case VirtualKey.Left or VirtualKey.Up or VirtualKey.Back or VirtualKey.PageUp:
                _ = StepFileAsync(-1, imagesOnly: true);
                return true;
            case VirtualKey.Add or (VirtualKey)187:
                Zoom(1.25);
                return true;
            case VirtualKey.Subtract or (VirtualKey)189:
                Zoom(0.8);
                return true;
            case VirtualKey.Number0 or VirtualKey.NumberPad0 or VirtualKey.F:
                FitImage();
                return true;
            case VirtualKey.Number1 or VirtualKey.NumberPad1:
                SetZoom(1);
                return true;
            case VirtualKey.R:
                Rotate(KeyboardState.IsDown(KeyboardState.Shift) ? -90 : 90);
                return true;
            default:
                return false;
        }
    }

    private void FocusRenderedMode()
    {
        switch (_mode)
        {
            case ViewerMode.Image:
                _imageScroller?.Focus(FocusState.Programmatic);
                break;
            case ViewerMode.Pdf:
                _pdfScroller?.Focus(FocusState.Programmatic);
                break;
            case ViewerMode.Tree:
                _tree?.Focus(FocusState.Programmatic);
                break;
            case ViewerMode.Preview:
                _web?.Focus(FocusState.Programmatic);
                break;
            case ViewerMode.Media:
                _player?.Focus(FocusState.Programmatic);
                break;
        }
    }

    private void CloseImage()
    {
        if (_image != null)
            _image.Source = null; // the decoded picture is let go
        _imagePath = null;
        _imageWidth = _imageHeight = 0;
    }
}
