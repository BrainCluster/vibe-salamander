// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Media.Core;
using Windows.Storage;
using Windows.Storage.FileProperties;
using VibeSalamander.Core.Formatting;
using VibeSalamander.Services;

namespace VibeSalamander.Views.Viewer;

// ViewerWindow: audio and video, played by Windows (every format it has codecs for), with the file's details.
internal sealed partial class ViewerWindow
{
    private MediaPlayerElement? _player;
    private Grid? _mediaHost;
    private StackPanel? _mediaDetails;
    private string? _mediaPath;

    private async Task ShowMediaAsync()
    {
        if (_mediaHost == null)
        {
            _player = new MediaPlayerElement { AreTransportControlsEnabled = true, AutoPlay = true, Stretch = Microsoft.UI.Xaml.Media.Stretch.Uniform };
            _mediaDetails = new StackPanel { Spacing = 6, Padding = new Thickness(16), Width = 280 };
            _mediaHost = new Grid();
            _mediaHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _mediaHost.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            _mediaHost.Children.Add(_player);
            var detailsScroller = new ScrollViewer { Content = _mediaDetails, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetColumn(detailsScroller, 1);
            _mediaHost.Children.Add(detailsScroller);
            _content.Children.Add(_mediaHost);
        }
        _mediaHost.Visibility = Visibility.Visible;
        if (_mediaPath == _path)
        {
            _player!.MediaPlayer?.Play();
            return;
        }
        string path = _path;
        _mediaPath = path;
        _mediaDetails!.Children.Clear();
        try
        {
            StorageFile file = await StorageFile.GetFileFromPathAsync(path);
            if (path != _path || _closed)
                return;
            _player!.Source = MediaSource.CreateFromStorageFile(file);
            _player.Focus(FocusState.Programmatic);
            await ShowMediaDetailsAsync(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
            if (path == _path)
                ShowMessage(Loc.F("\"{0}\" can't be played: {1}", Path.GetFileName(path), ex.Message));
        }
    }

    private async Task ShowMediaDetailsAsync(StorageFile file)
    {
        void Row(string label, string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;
            _mediaDetails!.Children.Add(new TextBlock { Text = Loc.T(label), Opacity = 0.65, FontSize = 12, Margin = new Thickness(0, 6, 0, 0) });
            _mediaDetails.Children.Add(new TextBlock { Text = value, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        }
        static string Duration(TimeSpan d) => d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : d.ToString(@"m\:ss", CultureInfo.InvariantCulture);
        var info = new FileInfo(file.Path);
        Row("Name", info.Name);
        Row("Size", $"{Formats.ShortSize(info.Length)}  ({Formats.ExactSize(info.Length)} {Loc.T("bytes")})");
        try
        {
            if (Core.Viewing.ViewerModes.IsMedia(file.Path))
            {
                VideoProperties video = await file.Properties.GetVideoPropertiesAsync();
                if (video.Width > 0)
                {
                    Row("Dimensions", $"{video.Width} × {video.Height}");
                    Row("Duration", Duration(video.Duration));
                    if (video.Bitrate > 0)
                        Row("Bit rate", $"{video.Bitrate / 1000:N0} kbit/s");
                }
                MusicProperties music = await file.Properties.GetMusicPropertiesAsync();
                if (video.Width == 0)
                {
                    Row("Title", music.Title);
                    Row("Artist", music.Artist);
                    Row("Album", music.Album);
                    if (music.Year > 0)
                        Row("Year", music.Year.ToString(CultureInfo.InvariantCulture));
                    Row("Duration", music.Duration > TimeSpan.Zero ? Duration(music.Duration) : null);
                    if (music.Bitrate > 0)
                        Row("Bit rate", $"{music.Bitrate / 1000:N0} kbit/s");
                }
                IDictionary<string, object> extra = await file.Properties.RetrievePropertiesAsync(new[]
                {
                    "System.Video.FrameRate", "System.Audio.SampleRate", "System.Audio.ChannelCount",
                });
                if (extra.TryGetValue("System.Video.FrameRate", out object? fps) && fps is uint frameRate && frameRate > 0)
                    Row("Frame rate", (frameRate / 1000.0).ToString("0.##", CultureInfo.CurrentCulture) + " fps");
                if (extra.TryGetValue("System.Audio.SampleRate", out object? rate) && rate is uint sampleRate && sampleRate > 0)
                    Row("Sample rate", sampleRate.ToString("N0", CultureInfo.CurrentCulture) + " Hz");
                if (extra.TryGetValue("System.Audio.ChannelCount", out object? channels) && channels is uint channelCount && channelCount > 0)
                    Row("Channels", channelCount.ToString(CultureInfo.CurrentCulture));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or InvalidCastException)
        {
            // no details: it plays anyway
        }
    }

    /// <summary>Stops playing (another file, another mode, the window closes).</summary>
    private void StopMedia()
    {
        if (_player == null)
            return;
        _player.MediaPlayer?.Pause();
        _player.Source = null;
        _mediaPath = null;
    }
}
