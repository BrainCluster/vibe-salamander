// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Formatting;

namespace VibeSalamander.Plugins.Checksum;

/// <summary>
/// The Calculate Checksums window: every file of the marked items (directories with their content) gets
/// the configured checksums. The window doesn't block the app. Right-click copies a checksum, Save writes
/// a checksum file (SFV / MD5 / SHA-1 / SHA-256 / SHA-512).
/// </summary>
internal sealed class CalculateSession
{
    private readonly ChecksumPlugin _plugin;
    private readonly IPluginHost _host;
    private readonly ChecksumSettings _settings;
    private readonly string _baseDirectory;
    private readonly IReadOnlyList<FileEntry> _items;
    private readonly List<HashKind> _kinds;
    private readonly CancellationTokenSource _cancel = new();
    private IPluginListWindow? _window;
    private volatile bool _running;

    private const int SizeColumn = 1;
    private const int FirstHashColumn = 2;

    public CalculateSession(ChecksumPlugin plugin, IPluginHost host, ChecksumSettings settings, string baseDirectory, IReadOnlyList<FileEntry> items)
    {
        _plugin = plugin;
        _host = host;
        _settings = settings;
        _baseDirectory = baseDirectory;
        _items = items;
        _kinds = HashKinds.All.Where(k => settings.Calculate.Contains(k)).ToList();
        if (_kinds.Count == 0)
            _kinds.Add(HashKind.Crc);
    }

    private string T(string english) => _host.Ui.T(english);

    /// <summary>The results of one file (null while not calculated, or after an error).</summary>
    private sealed class FileResult
    {
        public required string RelativePath { get; init; }
        public required string FullPath { get; init; }
        public long Size { get; init; }
        public Dictionary<HashKind, string>? Hashes { get; set; }
    }

    public void Start()
    {
        var columns = new List<PluginColumn> { new(T("File"), 0), new(T("Size"), 110, AlignRight: true) };
        columns.AddRange(_kinds.Select(k => new PluginColumn(T(HashKinds.ColumnName(k)), HashKinds.ColumnWidth(k), Monospace: true)));
        _window = _host.Ui.OpenListWindow(T("Calculate Checksums"), columns);
        _window.SetButtons(new[]
        {
            new PluginButton("configure", T("Configure..."), () => _ = _plugin.ConfigureAsync(_host, _window)),
            new PluginButton("save", T("Save..."), () => _ = SaveAsync()),
            new PluginButton("close", T("Stop"), StopOrClose, IsDefault: true),
        });
        _window.UpdateButton("save", enabled: false);
        _window.RowMenu = RowMenu;
        _window.Closed += (_, _) => _cancel.Cancel();
        _window.SetStatus(T("Reading directory tree, please wait..."));
        _running = true;
        _ = Task.Run(Run);
    }

    private void StopOrClose()
    {
        if (_running)
            _cancel.Cancel();
        else
            _window?.Close();
    }

    private void Run()
    {
        IPluginListWindow window = _window!;
        CancellationToken token = _cancel.Token;
        var rows = new List<IPluginRow>();
        long total = 0;
        try
        {
            // the files: marked files, and the content of marked directories
            foreach (FileEntry item in _items)
            {
                if (token.IsCancellationRequested)
                    break;
                IEnumerable<string> files = item.IsDirectory
                    ? EnumerateFiles(item.FullPath)
                    : new[] { item.FullPath };
                foreach (string file in files)
                {
                    if (token.IsCancellationRequested)
                        break;
                    long size = 0;
                    try
                    {
                        size = new FileInfo(file).Length;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // the size stays unknown; reading reports the problem
                    }
                    var result = new FileResult { RelativePath = Path.GetRelativePath(_baseDirectory, file), FullPath = file, Size = size };
                    var cells = new List<string> { result.RelativePath, Formats.ExactSize(size) };
                    cells.AddRange(_kinds.Select(_ => string.Empty));
                    IPluginRow row = window.AddRow(cells);
                    row.Tag = result;
                    rows.Add(row);
                    total += size;
                }
            }
            if (rows.Count == 0 && !token.IsCancellationRequested)
            {
                window.SetStatus(T("No files"));
                return;
            }

            // the checksums, file after file (each file is read once for all of them)
            long done = 0;
            window.SetProgress(0);
            window.SetStatus(T("Calculating..."));
            foreach (IPluginRow row in rows)
            {
                var result = (FileResult)row.Tag!;
                if (token.IsCancellationRequested)
                {
                    SetHashCells(row, T("Canceled"));
                    continue;
                }
                SetHashCells(row, T("Calculating..."));
                long before = done;
                try
                {
                    result.Hashes = MultiHasher.HashFile(result.FullPath, _kinds, read =>
                    {
                        done += read;
                        if (total > 0)
                            window.SetProgress((double)done / total);
                    }, token);
                    for (int i = 0; i < _kinds.Count; i++)
                        row[FirstHashColumn + i] = result.Hashes[_kinds[i]].ToUpperInvariant();
                }
                catch (OperationCanceledException)
                {
                    SetHashCells(row, T("Canceled"));
                }
                catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
                {
                    SetHashCells(row, T("Error opening file"));
                }
                catch (IOException)
                {
                    SetHashCells(row, T("Read error"));
                }
                done = before + result.Size;
            }
        }
        finally
        {
            _running = false;
            if (!window.IsClosed)
            {
                window.SetProgress(null);
                window.SetStatus(_cancel.IsCancellationRequested
                    ? T("Canceled")
                    : T("Right-click an item to copy a checksum to the clipboard."));
                window.UpdateButton("close", T("Close"));
                window.UpdateButton("save", enabled: rows.Any(r => ((FileResult)r.Tag!).Hashes != null));
            }
        }
    }

    private static IEnumerable<string> EnumerateFiles(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*", new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                AttributesToSkip = 0, // hidden and system files too
            }).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private void SetHashCells(IPluginRow row, string text)
    {
        for (int i = 0; i < _kinds.Count; i++)
            row[FirstHashColumn + i] = text;
    }

    /// <summary>Right-click: copy one of the row's checksums, or take the row out of the list.</summary>
    private IReadOnlyList<PluginMenuItem> RowMenu(IPluginRow row)
    {
        var result = row.Tag as FileResult;
        var items = new List<PluginMenuItem>();
        foreach (HashKind kind in _kinds)
        {
            string? hex = result?.Hashes?.GetValueOrDefault(kind);
            string text = kind switch
            {
                HashKind.Crc => T("Copy CRC Value to Clipboard"),
                HashKind.Md5 => T("Copy MD5 Checksum to Clipboard"),
                HashKind.Sha1 => T("Copy SHA-1 Checksum to Clipboard"),
                HashKind.Sha256 => T("Copy SHA-256 Checksum to Clipboard"),
                _ => T("Copy SHA-512 Checksum to Clipboard"),
            };
            string keys = kind switch
            {
                HashKind.Crc => "Ctrl+C",
                HashKind.Md5 => "Ctrl+M",
                HashKind.Sha1 => "Ctrl+1",
                HashKind.Sha256 => "Ctrl+2",
                _ => "Ctrl+5",
            };
            items.Add(new PluginMenuItem(text, keys, () => _host.Ui.CopyToClipboard(hex!.ToUpperInvariant()), IsEnabled: hex != null));
        }
        items.Add(new PluginMenuItem(T("Remove Item"), "Delete", row.Remove, IsEnabled: !_running));
        return items;
    }

    /// <summary>Save...: the checksums of one kind into a checksum file.</summary>
    private async Task SaveAsync()
    {
        IPluginListWindow window = _window!;
        var results = window.Rows.Select(r => r.Tag).OfType<FileResult>().ToList();
        if (results.Count == 0)
            return;

        // the default name: the files' common name, else the directory's name (like Open Salamander)
        string first = Path.GetFileNameWithoutExtension(results[0].RelativePath);
        string name = results.All(r => string.Equals(Path.GetFileNameWithoutExtension(r.RelativePath), first, StringComparison.OrdinalIgnoreCase))
            ? first
            : Path.GetFileName(_baseDirectory.TrimEnd('\\')) is { Length: > 0 } folder ? folder : "checksums";
        int selected = Math.Max(0, _kinds.IndexOf(_settings.LastSaveKind));
        var types = _kinds.Select(k => new PluginFileType(T(HashKinds.FileTypeName(k)), HashKinds.Extension(k))).ToList();

        PluginSaveChoice? choice = await _host.Ui.AskSaveFileAsync(T("Save Checksum File"),
            Path.Combine(_baseDirectory, name + HashKinds.Extension(_kinds[selected])), types, selected, window);
        if (choice == null)
            return;
        HashKind kind = _kinds[Math.Clamp(choice.TypeIndex, 0, _kinds.Count - 1)];
        string path = choice.Path;
        if (Path.GetExtension(path).Length == 0)
            path += HashKinds.Extension(kind);
        if (File.Exists(path) &&
            !await _host.Ui.ConfirmAsync(T("Save Checksum File"), string.Format(T("The file '{0}' already exists. Do you wish to overwrite it?"), path), window))
            return;

        var lines = results.Where(r => r.Hashes?.ContainsKey(kind) == true)
                           .Select(r => new ChecksumLine(r.RelativePath, r.Hashes![kind])).ToList();
        try
        {
            using var writer = new StreamWriter(path, false, new UTF8Encoding(false));
            ChecksumFile.Write(writer, kind, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            await _host.Ui.ShowMessageAsync(T("Save Checksum File"), T("Error creating file.") + "\n" + ex.Message, window);
            return;
        }
        _settings.LastSaveKind = kind;
        _plugin.SaveSettings();
        if (lines.Count < results.Count)
            await _host.Ui.ShowMessageAsync(T("Save Checksum File"), T("Files skipped or canceled during the calculation were not saved."), window);
        _host.Notify(string.Format(T("Checksums saved to {0}"), path));
    }
}
