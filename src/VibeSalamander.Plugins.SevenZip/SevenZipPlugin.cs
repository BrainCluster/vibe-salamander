// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace VibeSalamander.Plugins.SevenZip;

/// <summary>
/// The 7-Zip plugin (Salamander's 7-Zip plugin), a separate download: 7z archives with all of 7-Zip's settings,
/// and the formats only 7-Zip reads, through 7z.exe in the plugin's folder (or an installed 7-Zip). Unzipped into
/// %AppData%\VibeSalamander\Plugins (or the app's lib\Plugins) it is loaded at the next start.
/// </summary>
public sealed class SevenZipPlugin : IVibePlugin, ICommandContributor
{
    public string Id => "vibe.7zip";
    public string DisplayName => "7-Zip";
    public Version Version => new(1, 0);
    public string Description =>
        "7z archives through 7-Zip itself: pack with its compression methods, dictionary, word and solid block sizes, threads, " +
        "encrypted file names, volumes and self-extractors; unpack, browse, test and change 7z, and unpack CAB, WIM, MSI, CHM, ARJ, LZH, RPM, DEB, DMG and more.";
    public string Author => "Vibe Salamander (7-Zip by Igor Pavlov)";
    public string? IconGlyph => "";

    private IPluginHost? _host;
    private readonly SevenZipArchiver _archiver = new();

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    public void Initialize(IPluginHost host)
    {
        _host = host;
        host.RegisterArchiver(_archiver);
        Commands = new[]
        {
            new PluginCommand("test", "Test archive", null, h =>
            {
                Test(h);
                return Task.CompletedTask;
            }, AppliesTo: _archiver.CanUnpack),
            new PluginCommand("info", "Archive information", null, ShowInformationAsync, AppliesTo: _archiver.CanUnpack),
            new PluginCommand("sfx", "Make self-extracting archive", null, MakeSelfExtractingAsync,
                AppliesTo: path => SevenZipArchiver.KindOf(path) == "7z" && SevenZipArchiver.WithoutPartNumber(path) == path, SeparatorBefore: true),
            new PluginCommand("where", "Where is 7-Zip?", null, h =>
            {
                h.Notify(SevenZipTool.Exe is { } exe
                    ? string.Format(T("7-Zip: {0}"), exe)
                    : T("7-Zip wasn't found: 7z.exe belongs in the plugin's folder (the plugin download has it), or install 7-Zip from 7-zip.org."), SevenZipTool.Exe == null);
                return Task.CompletedTask;
            }, SeparatorBefore: true),
        };
        host.RegisterCommands(this);
    }

    public void Shutdown()
    {
    }

    private string T(string text) => _host?.Ui.T(text) ?? text;

    private string F(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(format), args);

    /// <summary>The marked archives of the active panel, else the focused one.</summary>
    private List<string> SelectedArchives(IPluginHost host, Func<string, bool> accepts)
    {
        IPanelContext panel = host.ActivePanel;
        var marked = panel.MarkedItems.Where(e => !e.IsDirectory && accepts(e.FullPath) && File.Exists(e.FullPath)).Select(e => e.FullPath).ToList();
        if (marked.Count > 0)
            return marked;
        if (panel.FocusedItem is { IsDirectory: false } focused && accepts(focused.FullPath) && File.Exists(focused.FullPath))
            return new List<string> { focused.FullPath };
        for (string? path = panel.Path; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
            if (File.Exists(path))
                return accepts(path) ? new List<string> { path } : new List<string>();
        return new List<string>();
    }

    private bool Ready(IPluginHost host)
    {
        if (SevenZipArchiver.Available)
            return true;
        host.Notify(T("7-Zip wasn't found: 7z.exe belongs in the plugin's folder (the plugin download has it), or install 7-Zip from 7-zip.org."), isError: true);
        return false;
    }

    // ---- Test archive ------------------------------------------------------------------------------

    private void Test(IPluginHost host)
    {
        if (!Ready(host))
            return;
        List<string> archives = SelectedArchives(host, _archiver.CanUnpack);
        if (archives.Count == 0)
        {
            host.Notify(T("Focus or select an archive to test it."), isError: true);
            return;
        }
        IPluginListWindow window = host.Ui.OpenListWindow(T("Test Archive") + " (7-Zip)", new[]
        {
            new PluginColumn(T("Archive"), 240),
            new PluginColumn(T("Result"), 0),
        });
        var cancel = new CancellationTokenSource();
        window.Closed += (_, _) => cancel.Cancel();
        window.SetButtons(new[] { new PluginButton("close", T("Close"), window.Close, IsDefault: true) });
        window.RowMenu = row => new[] { new PluginMenuItem(T("Copy"), "Ctrl+C", () => host.Ui.CopyToClipboard($"{row[0]}\t{row[1]}")) };
        _ = Task.Run(async () =>
        {
            int ok = 0, problems = 0;
            for (int i = 0; i < archives.Count; i++)
            {
                string path = archives[i];
                string name = Path.GetFileName(path);
                string? password = null;
                window.SetStatus(F("Testing {0}...", name));
                int index = i;
                while (!cancel.IsCancellationRequested)
                {
                    SevenZipResult result;
                    try
                    {
                        result = SevenZipTool.Run(new[] { "t", SevenZipTool.PasswordSwitch(password), "-bsp1", "-bso1", "-bse1", "--", path }, null,
                            percent => window.SetProgress((index + percent / 100.0) / archives.Count), cancellationToken: cancel.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        return;
                    }
                    if (result.WrongPassword)
                    {
                        var field = new PluginField { Label = T("Password"), Kind = PluginFieldKind.Password };
                        string question = password == null
                            ? F("\"{0}\" is protected by a password.", name)
                            : F("The password of \"{0}\" is wrong. Try another one.", name);
                        if (await host.Ui.EditFieldsAsync(T("Test Archive"), question, new[] { field }, null, window) && field.Value.Length > 0)
                        {
                            password = field.Value;
                            continue;
                        }
                        problems++;
                        window.AddRow(new[] { name, T("Not tested: no password") });
                        break;
                    }
                    if (result.ExitCode == 0)
                    {
                        ok++;
                        string files = result.Output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("Files:", StringComparison.Ordinal)) ?? string.Empty;
                        window.AddRow(new[] { name, T("OK") + (files.Length > 0 ? "  ·  " + files : string.Empty) });
                    }
                    else
                    {
                        problems++;
                        foreach (string line in result.Problem.Split('\n'))
                            window.AddRow(new[] { name, line });
                    }
                    break;
                }
            }
            window.SetProgress(null);
            window.SetStatus(problems == 0
                ? F("All {0} archives are OK.", ok.ToString("N0", CultureInfo.CurrentCulture))
                : F("{0} archives OK, {1} with problems.", ok.ToString("N0", CultureInfo.CurrentCulture), problems.ToString("N0", CultureInfo.CurrentCulture)));
        });
    }

    // ---- Archive information ---------------------------------------------------------------------------

    private async Task ShowInformationAsync(IPluginHost host)
    {
        if (!Ready(host))
            return;
        List<string> archives = SelectedArchives(host, _archiver.CanUnpack);
        if (archives.Count == 0)
        {
            host.Notify(T("Focus an archive to see its information."), isError: true);
            return;
        }
        string path = archives[0];
        IPluginListWindow window = host.Ui.OpenListWindow(T("Archive information") + " – " + Path.GetFileName(path), new[]
        {
            new PluginColumn(T("Property"), 220),
            new PluginColumn(T("Value"), 0),
        });
        window.SetButtons(new[] { new PluginButton("close", T("Close"), window.Close, IsDefault: true) });
        window.RowMenu = row => new[] { new PluginMenuItem(T("Copy"), "Ctrl+C", () => host.Ui.CopyToClipboard($"{row[0]}: {row[1]}")) };
        window.SetProgress(-1);
        string? password = null;
        while (true)
        {
            try
            {
                var (archive, items) = await Task.Run(() => SevenZipTool.List(path, password, CancellationToken.None));
                foreach (var (key, value) in archive)
                    window.AddRow(new[] { key, value });
                long files = items.Count(i => !i.IsDirectory);
                window.AddRow(new[] { T("Files"), files.ToString("N0", CultureInfo.CurrentCulture) });
                window.AddRow(new[] { T("Directories"), items.Count(i => i.IsDirectory).ToString("N0", CultureInfo.CurrentCulture) });
                window.AddRow(new[] { T("Size"), items.Sum(i => i.Size).ToString("N0", CultureInfo.CurrentCulture) });
                if (items.Any(i => i.Encrypted))
                    window.AddRow(new[] { T("Encrypted"), T("Yes") });
                window.SetStatus(string.Empty);
                break;
            }
            catch (PasswordNeededException)
            {
                var field = new PluginField { Label = T("Password"), Kind = PluginFieldKind.Password };
                if (!await host.Ui.EditFieldsAsync(T("Archive information"), F("\"{0}\" is protected by a password.", Path.GetFileName(path)), new[] { field }, null, window)
                    || field.Value.Length == 0)
                {
                    window.SetStatus(T("Not tested: no password"));
                    break;
                }
                password = field.Value;
            }
            catch (Exception ex)
            {
                window.SetStatus(ex.Message);
                break;
            }
        }
        window.SetProgress(null);
    }

    // ---- Make self-extracting archive ----------------------------------------------------------------

    /// <summary>A 7z becomes an .exe: 7-Zip's self-extractor (7z.sfx) followed by the archive, as 7-Zip itself does.</summary>
    private async Task MakeSelfExtractingAsync(IPluginHost host)
    {
        if (!Ready(host))
            return;
        string title = T("Make Self-Extracting Archive");
        List<string> archives = SelectedArchives(host, p => SevenZipArchiver.KindOf(p) == "7z" && SevenZipArchiver.WithoutPartNumber(p) == p);
        if (archives.Count != 1)
        {
            host.Notify(T("Focus a 7z archive to make a self-extracting archive of it."), isError: true);
            return;
        }
        string archive = archives[0];
        string module = Path.Combine(SevenZipTool.Folder!, "7z.sfx");
        if (!File.Exists(module))
        {
            await host.Ui.ShowMessageAsync(title, T("7-Zip's self-extractor (7z.sfx) wasn't found next to 7z.exe."));
            return;
        }
        string exe = Path.ChangeExtension(archive, ".exe");
        if (File.Exists(exe) && !await host.Ui.ConfirmAsync(title, F("\"{0}\" already exists. Replace it?", Path.GetFileName(exe))))
            return;
        try
        {
            await Task.Run(() =>
            {
                string temp = exe + ".vibe-partial";
                using (var output = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16))
                {
                    using (var sfx = File.OpenRead(module))
                        sfx.CopyTo(output);
                    using var data = File.OpenRead(archive);
                    data.CopyTo(output);
                }
                File.Move(temp, exe, overwrite: true);
            });
            host.Notify(F("{0} is ready: it extracts the archive when started.", Path.GetFileName(exe)));
            await host.ActivePanel.RefreshAsync();
        }
        catch (Exception ex)
        {
            await host.Ui.ShowMessageAsync(title, ex.Message);
        }
    }
}
