// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.Formatting;

namespace VibeSalamander.Plugins.Checksum;

/// <summary>
/// The Verify Checksums window (Ctrl+Shift+V on an SFV / MD5 / SHA-1 / SHA-256 / SHA-512 file): every listed
/// file is read again and its status shows OK, Corrupted, Missing or Read error. Focus shows the selected
/// file in the panel. The window doesn't block the app.
/// </summary>
internal sealed class VerifySession
{
    private readonly IPluginHost _host;
    private readonly string _checksumFile;
    private readonly string _directory;
    private readonly CancellationTokenSource _cancel = new();
    private IPluginListWindow? _window;
    private volatile bool _running;

    private const int StatusColumn = 2;

    public VerifySession(IPluginHost host, string checksumFile)
    {
        _host = host;
        _checksumFile = checksumFile;
        _directory = Path.GetDirectoryName(checksumFile) ?? string.Empty;
    }

    private string T(string english) => _host.Ui.T(english);

    private sealed class Entry
    {
        public required string FullPath { get; init; }
        public required string Hex { get; init; }
        public bool Exists { get; init; }
        public long Size { get; init; }
    }

    public async Task StartAsync()
    {
        string title = T("Verify Checksums");
        string extension = Path.GetExtension(_checksumFile);
        if (!HashKinds.All.Any(k => string.Equals(HashKinds.Extension(k), extension, StringComparison.OrdinalIgnoreCase)) &&
            !await _host.Ui.ConfirmAsync(title, T("The selected file has no SFV, MD5, SHA1, SHA256, nor SHA512 extension. Do you want to continue anyway?")))
            return;

        HashKind kind;
        List<ChecksumLine> lines;
        try
        {
            string text = ChecksumFile.ReadText(_checksumFile);
            if (ChecksumFile.Detect(text) is not HashKind detected)
            {
                await _host.Ui.ShowMessageAsync(title, T("The selected file is not a valid SFV, MD5, SHA-1, SHA-256, nor SHA-512 file."));
                return;
            }
            kind = detected;
            lines = ChecksumFile.Parse(text, kind, _checksumFile);
        }
        catch (FormatException)
        {
            await _host.Ui.ShowMessageAsync(title, T("The selected file is not a valid SFV, MD5, SHA-1, SHA-256, nor SHA-512 file."));
            return;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _host.Ui.ShowMessageAsync(title, string.Format(T("Error opening the file '{0}'."), _checksumFile) + "\n" + ex.Message);
            return;
        }

        string windowTitle = kind switch
        {
            HashKind.Crc => T("Verify CRC"),
            HashKind.Md5 => T("Verify MD5"),
            HashKind.Sha1 => T("Verify SHA-1"),
            HashKind.Sha256 => T("Verify SHA-256"),
            _ => T("Verify SHA-512"),
        } + " - " + Path.GetFileName(_checksumFile);
        _window = _host.Ui.OpenListWindow(windowTitle, new[]
        {
            new PluginColumn(T("File"), 0),
            new PluginColumn(T("Size"), 110, AlignRight: true),
            new PluginColumn(T("Status"), 150),
        });
        _window.SetButtons(new[]
        {
            new PluginButton("focus", T("Focus"), () => _ = FocusAsync()),
            new PluginButton("close", T("Stop"), StopOrClose, IsDefault: true),
        });
        _window.Closed += (_, _) => _cancel.Cancel();

        var rows = new List<IPluginRow>();
        foreach (ChecksumLine line in lines)
        {
            string full = Path.Combine(_directory, line.FileName);
            bool exists = false;
            long size = 0;
            try
            {
                full = Path.GetFullPath(full);
                var info = new FileInfo(full);
                exists = info.Exists;
                size = exists ? info.Length : 0;
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
            {
                // a name Windows can't use: shown as missing
            }
            var entry = new Entry { FullPath = full, Hex = line.Hex, Exists = exists, Size = size };
            IPluginRow row = _window.AddRow(new[] { line.FileName, exists ? Formats.ExactSize(entry.Size) : string.Empty, exists ? string.Empty : T("Missing") });
            row.Tag = entry;
            rows.Add(row);
        }
        _running = true;
        _ = Task.Run(() => Run(rows, kind));
    }

    private void StopOrClose()
    {
        if (_running)
            _cancel.Cancel();
        else
            _window?.Close();
    }

    private void Run(List<IPluginRow> rows, HashKind kind)
    {
        IPluginListWindow window = _window!;
        CancellationToken token = _cancel.Token;
        int corrupted = 0, missing = 0, skipped = 0;
        long total = rows.Sum(r => ((Entry)r.Tag!).Size), done = 0;
        try
        {
            window.SetStatus(T("Verifying..."));
            window.SetProgress(0);
            foreach (IPluginRow row in rows)
            {
                var entry = (Entry)row.Tag!;
                if (!entry.Exists)
                {
                    missing++;
                    continue;
                }
                if (token.IsCancellationRequested)
                {
                    row[StatusColumn] = T("Skipped");
                    skipped++;
                    continue;
                }
                row[StatusColumn] = T("Verifying...");
                long before = done;
                try
                {
                    string actual = MultiHasher.HashFile(entry.FullPath, new[] { kind }, read =>
                    {
                        done += read;
                        if (total > 0)
                            window.SetProgress((double)done / total);
                    }, token)[kind];
                    bool ok = string.Equals(actual, entry.Hex, StringComparison.OrdinalIgnoreCase);
                    row[StatusColumn] = ok ? T("OK") : T("Corrupted");
                    if (!ok)
                        corrupted++;
                }
                catch (OperationCanceledException)
                {
                    row[StatusColumn] = T("Skipped");
                    skipped++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    row[StatusColumn] = T("Read error");
                    skipped++;
                }
                done = before + entry.Size;
            }
        }
        finally
        {
            _running = false;
            if (!window.IsClosed)
            {
                window.SetProgress(null);
                window.SetStatus(corrupted + missing + skipped == 0
                    ? T("All files OK")
                    : string.Format(T("{0} corrupted, {1} missing, {2} skipped"), corrupted, missing, skipped));
                window.UpdateButton("close", T("Close"));
            }
        }
    }

    /// <summary>Focus: shows the selected file in the active panel.</summary>
    private async Task FocusAsync()
    {
        if (_window?.SelectedRow?.Tag is not Entry entry)
            return;
        string? directory = Path.GetDirectoryName(entry.FullPath);
        if (directory != null && Directory.Exists(directory))
            await _host.ActivePanel.NavigateAsync(directory, Path.GetFileName(entry.FullPath));
    }
}
