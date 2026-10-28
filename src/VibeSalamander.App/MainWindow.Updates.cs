// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using VibeSalamander.Core.Updates;
using VibeSalamander.Services;

namespace VibeSalamander;

// MainWindow: Help → Check for Updates, the daily check, and updating (the installer, or a portable copy's files).
public sealed partial class MainWindow
{
    private bool _checkingForUpdates;

    /// <summary>After the start: says how an update went, and checks once a day (Configuration → General).</summary>
    private void UpdatesAtStart()
    {
        var g = _vm.Settings.General;
        if (Environment.GetCommandLineArgs().Contains("--update-failed", StringComparer.OrdinalIgnoreCase))
            Notify(Loc.T("The update couldn't replace all the files. Download it again from the release page."), isError: true, log: true);
        else if (g.UpdatingFrom.Length > 0 && g.UpdatingFrom != Updater.CurrentVersion)
            Notify(Loc.F("Vibe Salamander is updated to {0}.", Updater.CurrentVersion), isError: false, log: false);
        if (g.UpdatingFrom.Length > 0)
        {
            g.UpdatingFrom = string.Empty;
            _vm.SaveSettings();
        }
        if (g.CheckForUpdates && (DateTime.UtcNow - g.LastUpdateCheck).TotalHours >= 20)
            _ = CheckForUpdatesAsync(manual: false);
    }

    private async void CheckForUpdates_Click(object sender, RoutedEventArgs e) => await CheckForUpdatesAsync(manual: true);

    /// <summary>
    /// Asks GitHub for the newest release. By hand: says the result either way. Daily: only a newer version (not a
    /// skipped one), as a notification with an Update button; problems stay quiet.
    /// </summary>
    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_checkingForUpdates)
            return;
        _checkingForUpdates = true;
        try
        {
            if (manual)
                Notify(Loc.T("Checking for updates..."), isError: false, log: false);
            ReleaseInfo? release;
            try
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                release = await Updater.LatestAsync(cts.Token);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
            {
                if (manual)
                    Notify(Loc.F("The update check didn't reach GitHub: {0}", ex.Message), isError: true, log: false);
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // a mistake of ours: logged, said when asked, never a crash
                Core.Diagnostics.ErrorLog.Exception("Update check", ex);
                if (manual)
                    Notify(Loc.F("The update check didn't reach GitHub: {0}", ex.Message), isError: true, log: false);
                return;
            }
            var g = _vm.Settings.General;
            g.LastUpdateCheck = DateTime.UtcNow;
            _vm.SaveSettings();
            if (release == null || !UpdateCheck.IsNewer(release.Version, Updater.CurrentVersion))
            {
                if (manual)
                    Notify(Loc.F("You have the newest version ({0}).", Updater.CurrentVersion), isError: false, log: false);
                return;
            }
            if (manual)
                await OfferUpdateAsync(release);
            else if (g.SkippedVersion != release.Version)
                Notify(Loc.F("Vibe Salamander {0} is available.", release.Version), "Update...", () => _ = OfferUpdateAsync(release));
        }
        finally
        {
            _checkingForUpdates = false;
        }
    }

    /// <summary>What's new, and how this copy is updated: Update now, Skip this version, or Later.</summary>
    private async Task OfferUpdateAsync(ReleaseInfo release)
    {
        InstallKind kind = Updater.Kind;
        ReleaseAsset? asset = UpdateCheck.AssetFor(release, kind, Updater.Architecture);
        var body = new StackPanel { Spacing = 10, MaxWidth = 560 };
        body.Children.Add(new TextBlock
        {
            Text = Loc.F("Vibe Salamander {0} is available (you have {1}).", release.Version, Updater.CurrentVersion),
            TextWrapping = TextWrapping.Wrap,
        });
        body.Children.Add(new ScrollViewer
        {
            MaxHeight = 300,
            Content = new TextBlock { Text = UpdateCheck.PlainNotes(release.Notes), TextWrapping = TextWrapping.Wrap, FontSize = 13, IsTextSelectionEnabled = true },
        });
        string? how = kind switch
        {
            InstallKind.Installed => Loc.T("The new version is installed over this one, and Vibe Salamander starts again."),
            InstallKind.Portable => Loc.T("The new files replace these ones (your settings in the Data folder stay), and Vibe Salamander starts again."),
            InstallKind.Scoop => Loc.T("Scoop updates it: scoop update vibe-salamander"),
            _ => Loc.T("This is a build from the source code: the release page has the new version."),
        };
        body.Children.Add(new TextBlock { Text = how, TextWrapping = TextWrapping.Wrap, Opacity = 0.75, FontSize = 12 });
        bool canUpdate = asset != null && kind is InstallKind.Installed or InstallKind.Portable;
        ContentDialog dialog = Dialogs.Create("Update", body);
        dialog.PrimaryButtonText = canUpdate ? Loc.T("Update now") : Loc.T("Open the release page");
        dialog.SecondaryButtonText = Loc.T("Skip this version");
        dialog.CloseButtonText = Loc.T("Later");
        dialog.DefaultButton = ContentDialogButton.Primary;
        ContentDialogResult answer = await Dialogs.ShowDialogAsync(dialog);
        if (answer == ContentDialogResult.Secondary)
        {
            _vm.Settings.General.SkippedVersion = release.Version;
            _vm.SaveSettings();
            return;
        }
        if (answer != ContentDialogResult.Primary)
            return;
        if (!canUpdate)
        {
            OpenInBrowser(release.PageUrl);
            return;
        }
        await UpdateAsync(release, asset!, kind);
    }

    private static void OpenInBrowser(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // no browser: nothing to do
        }
    }

    /// <summary>Downloads (with progress, cancellable), hands over to the installer or the portable script, and closes.</summary>
    private async Task UpdateAsync(ReleaseInfo release, ReleaseAsset asset, InstallKind kind)
    {
        // what would be lost by closing: running transfers, unsaved editors
        if (_vm.Transfers.ActiveCount > 0)
        {
            Notify(Loc.T("Transfers are running: update when they have finished."), isError: false, log: false);
            return;
        }
        if (_editors.Count > 0)
        {
            Notify(Loc.T("Close the editor windows first (they may have unsaved changes), then update."), isError: false, log: false);
            return;
        }
        if (kind == InstallKind.Portable && !Updater.CanWriteMainFolder())
        {
            Notify(Loc.F("Vibe Salamander can't write to its folder {0}: download the new zip from the release page.", Core.Settings.AppPaths.MainFolder), isError: true, log: false);
            OpenInBrowser(release.PageUrl);
            return;
        }

        var bar = new ProgressBar { Maximum = 1, Margin = new Thickness(0, 12, 0, 0) };
        var text = new TextBlock { Text = Loc.F("Downloading {0}...", asset.Name), TextWrapping = TextWrapping.Wrap };
        var panel = new StackPanel();
        panel.Children.Add(text);
        panel.Children.Add(bar);
        ContentDialog progress = Dialogs.Create("Update", panel);
        progress.CloseButtonText = Loc.T("Cancel");
        using var cts = new CancellationTokenSource();
        progress.CloseButtonClick += (_, _) => cts.Cancel();
        _ = Dialogs.ShowDialogAsync(progress);
        string file;
        try
        {
            file = await Updater.DownloadAsync(asset, new Progress<double>(p => bar.Value = p), cts.Token);
        }
        catch (OperationCanceledException)
        {
            progress.Hide();
            return;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            progress.Hide();
            Notify(Loc.F("The update couldn't be downloaded: {0}", ex.Message), isError: true, log: true);
            return;
        }
        text.Text = Loc.T("Vibe Salamander closes and starts again in a moment...");
        bar.IsIndeterminate = true;
        try
        {
            if (kind == InstallKind.Installed)
                Updater.StartInstaller(file);
            else
                Updater.StartPortableUpdate(file);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            progress.Hide();
            Notify(Loc.F("The update couldn't start: {0}", ex.Message), isError: true, log: true);
            return;
        }
        // the next start says it's done
        _vm.Settings.General.UpdatingFrom = Updater.CurrentVersion;
        _vm.SaveSettings();
        progress.Hide();
        _vm.Transfers.CancelAll();
        _closeConfirmed = true;
        Close();
    }
}
