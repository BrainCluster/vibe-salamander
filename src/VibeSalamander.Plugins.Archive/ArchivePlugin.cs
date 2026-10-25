// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Globalization;

namespace VibeSalamander.Plugins.Archive;

/// <summary>
/// The built-in archive plugin (Salamander's ZIP, TAR, 7-Zip and UnRAR plugins in one): Alt+F5 packs
/// into ZIP (optionally AES-256 encrypted) / TAR / TAR.GZ, Alt+F6 unpacks ZIP, 7z, RAR, TAR (also
/// gz/bz2/xz/lz compressed) and single gz / bz2 / xz / lz files. ZIP and TAR archives can be changed in
/// place: files copied in, deleted, moved out. Its commands test archives, make self-extracting
/// archives of ZIPs and show / change ZIP comments.
/// </summary>
public sealed class ArchivePlugin : IVibePlugin, ICommandContributor
{
    public string Id => "vibe.archive";
    public string DisplayName => "Archives";
    public Version Version => new(1, 1);
    public string Description =>
        "Pack into ZIP (also with a password, AES-256), TAR and TAR.GZ (Alt+F5); unpack ZIP, 7-Zip, RAR, TAR, gzip, bzip2, xz and lzip (Alt+F6). " +
        "Password-protected ZIP, 7-Zip and RAR archives can be unpacked. " +
        "Files can be copied into ZIP and TAR archives and deleted from them.";
    public string Author => "Vibe Salamander";
    public string? IconGlyph => "\uF012";

    private IPluginHost? _host;
    private readonly Archiver _archiver = new();

    public IReadOnlyList<PluginCommand> Commands { get; private set; } = Array.Empty<PluginCommand>();

    public void Initialize(IPluginHost host)
    {
        _host = host;
        try
        {
            _archiver.DataDirectory = host.GetDataDirectory(this); // the self-extractor, compiled once
        }
        catch (Exception)
        {
            // a temporary folder then
        }
        host.RegisterArchiver(_archiver);
        Commands = new[]
        {
            new PluginCommand("test", "Test archive", null, h =>
            {
                Test(h);
                return Task.CompletedTask;
            }, HostCommand: "TestArchive", AppliesTo: _archiver.CanUnpack, SeparatorBefore: true),
            new PluginCommand("sfx", "Make self-extracting archive...", null, MakeSelfExtractingAsync, HostCommand: "MakeSelfExtractingArchive",
                AppliesTo: path => IsZip(path) && Archiver.WithoutPartNumber(path) == path),
            new PluginCommand("comment", "Archive comment...", null, EditCommentAsync, HostCommand: "ArchiveComment",
                AppliesTo: path => IsZip(path) && Archiver.WithoutPartNumber(path) == path),
        };
        host.RegisterCommands(this);
    }

    public void Shutdown()
    {
    }

    private string T(string text) => _host?.Ui.T(text) ?? text;

    private string F(string format, params object[] args) => string.Format(CultureInfo.CurrentCulture, T(format), args);

    /// <summary>ZIP archives under other names (their comments can be changed, they can become self-extracting).</summary>
    private static readonly HashSet<string> ZipKinds = new(StringComparer.OrdinalIgnoreCase) { "zip", "jar", "cbz", "nupkg", "vsix", "apk", "epub", "xpi" };

    private bool IsZip(string path) => _archiver.KindOf(path) is string kind && ZipKinds.Contains(kind);

    /// <summary>
    /// The archives to work on: the marked archives of the active panel, else the focused one, else the archive
    /// the panel is browsing.
    /// </summary>
    internal static List<string> SelectedArchives(IPanelContext panel, Func<string, bool> accepts)
    {
        var marked = panel.MarkedItems.Where(e => !e.IsDirectory && accepts(e.FullPath) && File.Exists(e.FullPath))
            .Select(e => e.FullPath).ToList();
        if (marked.Count > 0)
            return marked;
        if (panel.FocusedItem is { IsDirectory: false } focused && accepts(focused.FullPath) && File.Exists(focused.FullPath))
            return new List<string> { focused.FullPath };
        // inside an archive: its file is a part of the panel's path ("C:\backup.zip\docs")
        for (string? path = panel.Path; !string.IsNullOrEmpty(path); path = Path.GetDirectoryName(path))
        {
            if (File.Exists(path))
                return accepts(path) ? new List<string> { path } : new List<string>();
        }
        return new List<string>();
    }

    // ---- Test archive ------------------------------------------------------------------------------

    private void Test(IPluginHost host)
    {
        List<string> archives = SelectedArchives(host.ActivePanel, _archiver.CanUnpack);
        if (archives.Count == 0)
        {
            host.Notify(T("Focus or select an archive to test it."), isError: true);
            return;
        }
        IPluginListWindow window = host.Ui.OpenListWindow(T("Test Archive"), new[]
        {
            new PluginColumn(T("Archive"), 200),
            new PluginColumn(T("File"), 0),
            new PluginColumn(T("Size"), 110, AlignRight: true),
            new PluginColumn(T("Result"), 280),
        });
        var cancel = new CancellationTokenSource();
        window.Closed += (_, _) => cancel.Cancel();
        window.SetButtons(new[] { new PluginButton("close", T("Close"), window.Close, IsDefault: true) });
        window.RowMenu = row => new[]
        {
            new PluginMenuItem(T("Copy"), "Ctrl+C", () => host.Ui.CopyToClipboard($"{row[0]}\t{row[1]}\t{row[2]}\t{row[3]}")),
        };
        _ = Task.Run(() => TestAllAsync(host, window, archives, cancel.Token));
    }

    private async Task TestAllAsync(IPluginHost host, IPluginListWindow window, List<string> archives, CancellationToken ct)
    {
        int ok = 0, problems = 0;
        window.SetProgress(0);
        for (int i = 0; i < archives.Count; i++)
        {
            string path = archives[i];
            string name = Path.GetFileName(path);
            string kind = _archiver.KindOf(path) ?? "zip";
            string? password = null;
            int index = i;
            while (true)
            {
                try
                {
                    window.SetStatus(F("Testing {0}...", name));
                    ArchiveTest.Run(path, kind, password, file =>
                    {
                        if (file.Problem == null)
                            ok++;
                        else
                            problems++;
                        window.AddRow(new[]
                        {
                            name, file.Name, file.Size.ToString("N0", CultureInfo.CurrentCulture),
                            file.Problem == null ? T("OK") : T(file.Problem),
                        });
                    }, share => window.SetProgress((index + share) / archives.Count), ct);
                    break;
                }
                catch (ArchiveTest.PasswordNeeded)
                {
                    var field = new PluginField { Label = T("Password"), Kind = PluginFieldKind.Password };
                    string question = password == null
                        ? F("\"{0}\" is protected by a password.", name)
                        : F("The password of \"{0}\" is wrong. Try another one.", name);
                    if (!await host.Ui.EditFieldsAsync(T("Test Archive"), question, new[] { field }, null, window) || field.Value.Length == 0)
                    {
                        problems++;
                        window.AddRow(new[] { name, string.Empty, string.Empty, T("Not tested: no password") });
                        break;
                    }
                    password = field.Value;
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    problems++;
                    window.AddRow(new[] { name, string.Empty, string.Empty, ex.Message });
                    break;
                }
            }
        }
        window.SetProgress(null);
        window.SetStatus(problems == 0
            ? F("All {0} files are OK.", ok.ToString("N0", CultureInfo.CurrentCulture))
            : F("{0} files OK, {1} with problems.", ok.ToString("N0", CultureInfo.CurrentCulture), problems.ToString("N0", CultureInfo.CurrentCulture)));
    }

    // ---- Make self-extracting archive -------------------------------------------------------------

    private async Task MakeSelfExtractingAsync(IPluginHost host)
    {
        string title = T("Make Self-Extracting Archive");
        List<string> zips = SelectedArchives(host.ActivePanel, IsZip);
        if (zips.Count != 1)
        {
            host.Notify(T("Focus a ZIP archive to make a self-extracting archive of it (Alt+F5 packs files into a ZIP)."), isError: true);
            return;
        }
        string zip = zips[0];
        string name = Path.GetFileNameWithoutExtension(zip);
        var output = new PluginField { Label = T("Self-extracting archive"), Value = Path.ChangeExtension(zip, ".exe") };
        var windowTitle = new PluginField { Label = T("Window title"), Value = name };
        var message = new PluginField { Label = T("Message (optional)"), Kind = PluginFieldKind.MultilineText, Placeholder = T("Shown above the folder choice") };
        var target = new PluginField { Label = T("Extract to"), Value = name, Placeholder = @"%TEMP%\" + name };
        var run = new PluginField { Label = T("Start after extracting (optional)"), Placeholder = "setup.exe" };
        var quiet = new PluginField { Label = T("Extract without asking (no window)"), Kind = PluginFieldKind.Check, Value = "false" };
        string? Validate()
        {
            string path = output.Value.Trim();
            if (!path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                return T("The self-extracting archive is a program: its name ends with .exe.");
            if (Path.GetDirectoryName(path) is not { Length: > 0 } directory || !Directory.Exists(directory))
                return T("The folder of the self-extracting archive doesn't exist.");
            if (target.Value.Trim().Length == 0)
                return T("Type the folder it extracts to.");
            return null;
        }
        if (!await host.Ui.EditFieldsAsync(title,
                T("It unpacks the archive on any Windows 10 or 11 computer, nothing to install. \"Extract to\" is relative to the program's folder; %TEMP% and other variables work."),
                new[] { output, windowTitle, message, target, run, quiet }, Validate))
            return;
        string exe = output.Value.Trim();
        if (File.Exists(exe) && !await host.Ui.ConfirmAsync(title, F("\"{0}\" already exists. Replace it?", Path.GetFileName(exe))))
            return;
        var options = new SfxOptions
        {
            Title = windowTitle.Value.Trim(),
            Message = message.Value.Trim(),
            Target = target.Value.Trim(),
            Run = run.Value.Trim(),
            Quiet = quiet.Value == "true",
        };
        host.Notify(F("Making {0}...", Path.GetFileName(exe)));
        try
        {
            string cache = host.GetDataDirectory(this);
            await Task.Run(() =>
            {
                byte[] stub = SelfExtractor.GetStub(cache);
                SelfExtractor.Make(zip, exe, stub, options, _ => { }, CancellationToken.None);
            });
            host.Notify(F("{0} is ready: it extracts the archive when started.", Path.GetFileName(exe)));
            await host.ActivePanel.RefreshAsync();
        }
        catch (Exception ex)
        {
            await host.Ui.ShowMessageAsync(title, ex.Message);
        }
    }

    // ---- Archive comment -----------------------------------------------------------------------------

    private async Task EditCommentAsync(IPluginHost host)
    {
        string title = T("Archive Comment");
        List<string> zips = SelectedArchives(host.ActivePanel, IsZip);
        if (zips.Count == 0)
        {
            host.Notify(T("Focus a ZIP archive to see or change its comment (other archives keep no comments that can be changed)."), isError: true);
            return;
        }
        string zip = zips[0];
        string current;
        try
        {
            current = await Task.Run(() =>
            {
                using var file = new FileStream(zip, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                return Zip.DecodeComment(Zip.ReadDirectory(file).Comment);
            });
        }
        catch (Exception ex)
        {
            await host.Ui.ShowMessageAsync(title, ex.Message);
            return;
        }
        var field = new PluginField { Label = T("Comment"), Kind = PluginFieldKind.MultilineText, Value = current, Placeholder = T("(no comment)") };
        string? Validate()
        {
            try
            {
                Zip.EncodeComment(field.Value);
                return null;
            }
            catch (InvalidDataException)
            {
                return T("A ZIP comment can have at most 65,535 bytes.");
            }
        }
        if (!await host.Ui.EditFieldsAsync(title, Path.GetFileName(zip), new[] { field }, Validate))
            return;
        if (field.Value == current)
            return;
        try
        {
            await Task.Run(() => Zip.WriteComment(zip, field.Value));
            host.Notify(field.Value.Length == 0 ? T("The comment was removed.") : T("The comment was saved."));
        }
        catch (Exception ex)
        {
            await host.Ui.ShowMessageAsync(title, ex.Message);
        }
    }
}
