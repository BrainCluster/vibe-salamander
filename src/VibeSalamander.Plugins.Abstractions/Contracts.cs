// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using VibeSalamander.Core.FileSystem;
using VibeSalamander.Core.Operations;

namespace VibeSalamander.Plugins;

// Plugin contracts (ports of Open Salamander plugins). A plugin is a .NET assembly with one public
// class implementing IVibePlugin. Built-in plugins ship with the app; others are loaded from
// "<app folder>\Plugins\<name>\" and "%AppData%\VibeSalamander\Plugins\<name>\", each in its own
// AssemblyLoadContext, and talk to the app only through IPluginHost. See docs/ARCHITECTURE.md.

/// <summary>Entry point of a plugin assembly.</summary>
public interface IVibePlugin
{
    /// <summary>Unique id, e.g. "vibe.archive".</summary>
    string Id { get; }

    string DisplayName { get; }

    Version Version { get; }

    /// <summary>One or two sentences for the Plugins Manager.</summary>
    string Description { get; }

    string Author { get; }

    /// <summary>
    /// Its icon in the Plugins menu: a Segoe Fluent Icons character ("\uEB05"); null = a general plugin icon
    /// (or an archive / network icon for archivers and file systems).
    /// </summary>
    string? IconGlyph => null;

    /// <summary>Called once after loading; the plugin registers its features through the host.</summary>
    void Initialize(IPluginHost host);

    /// <summary>Called before unloading (app exit).</summary>
    void Shutdown();
}

/// <summary>What the app offers to plugins.</summary>
public interface IPluginHost
{
    Version HostVersion { get; }

    /// <summary>Per-plugin folder for settings and data (created on demand).</summary>
    string GetDataDirectory(IVibePlugin plugin);

    /// <summary>Pack (Alt+F5) and unpack (Alt+F6) support for some archive formats.</summary>
    void RegisterArchiver(IArchiver archiver);

    /// <summary>Commands shown in the Plugins menu under the plugin's name.</summary>
    void RegisterCommands(ICommandContributor contributor);

    /// <summary>A file system the panels can show (FTP, SFTP, ...), with paths like "sftp://user@host/dir".</summary>
    void RegisterFileSystem(IPluginFileSystem fileSystem) => throw new NotSupportedException("This host has no plugin file systems.");

    /// <summary>The active panel's current path and focused/marked items.</summary>
    IPanelContext ActivePanel { get; }

    IPanelContext InactivePanel { get; }

    /// <summary>A short message in the notification bar (no dialog).</summary>
    void Notify(string message, bool isError = false);

    /// <summary>Windows, dialogs, the clipboard and translations for plugins (they never touch the UI framework).</summary>
    IPluginUi Ui { get; }
}

// ---- user interface for plugins ------------------------------------------------------------------

/// <summary>
/// The parts of the user interface a plugin can use. Every method may be called from any thread; the
/// host moves the work to the UI thread. Dialogs are shown in the given window (or the main window).
/// </summary>
public interface IPluginUi
{
    /// <summary>A non-modal window with a list of rows (columns of text), a status line, a progress bar and buttons.</summary>
    IPluginListWindow OpenListWindow(string title, IReadOnlyList<PluginColumn> columns);

    /// <summary>
    /// A non-modal window with a picture the plugin draws itself (pixels), text labels over it, a choice of
    /// views, a legend, a status line, a progress bar and buttons (Disk Map).
    /// </summary>
    IPluginCanvasWindow OpenCanvasWindow(string title) => throw new NotSupportedException("This host has no picture windows.");

    /// <summary>A Yes / No question.</summary>
    Task<bool> ConfirmAsync(string title, string message, IPluginListWindow? owner = null);

    /// <summary>A message with an OK button.</summary>
    Task ShowMessageAsync(string title, string message, IPluginListWindow? owner = null);

    /// <summary>Check boxes (true / false options); false when cancelled, otherwise the options hold the new values.</summary>
    Task<bool> EditOptionsAsync(string title, string? description, IReadOnlyList<PluginOption> options, IPluginListWindow? owner = null);

    /// <summary>
    /// A form: text, password, choice and check box fields. False when cancelled, otherwise the fields hold
    /// the new values. <paramref name="validate"/> (optional) returns a problem to show (the form stays
    /// open) or null.
    /// </summary>
    Task<bool> EditFieldsAsync(string title, string? description, IReadOnlyList<PluginField> fields,
        Func<string?>? validate = null, IPluginListWindow? owner = null) => Task.FromResult(false);

    /// <summary>Where to save a file and in which of the formats; null when cancelled.</summary>
    Task<PluginSaveChoice?> AskSaveFileAsync(string title, string suggestedPath, IReadOnlyList<PluginFileType> types,
        int selectedType, IPluginListWindow? owner = null);

    void CopyToClipboard(string text);

    /// <summary>The text in the user's language (the same tables as the app's own texts).</summary>
    string T(string english);
}

/// <param name="Header">Column title.</param>
/// <param name="Width">Width in pixels; 0 = takes the remaining space.</param>
/// <param name="AlignRight">Numbers (sizes) are right-aligned.</param>
/// <param name="Monospace">In a fixed-width font (checksums, codes).</param>
public sealed record PluginColumn(string Header, double Width, bool AlignRight = false, bool Monospace = false);

/// <param name="Name">"SFV files"</param>
/// <param name="Extension">With the dot: ".sfv"</param>
public sealed record PluginFileType(string Name, string Extension);

public sealed record PluginSaveChoice(string Path, int TypeIndex);

public sealed class PluginOption
{
    public required string Label { get; init; }
    public bool Value { get; set; }
}

public enum PluginFieldKind
{
    Text,
    /// <summary>Hidden while typed.</summary>
    Password,
    /// <summary>One of <see cref="PluginField.Choices"/> (the value is the chosen text).</summary>
    Choice,
    /// <summary>A check box: the value is "true" or "false".</summary>
    Check,
    /// <summary>A file on disk, with a Browse button.</summary>
    File,
    /// <summary>Text of several lines (lines end with "\n").</summary>
    MultilineText,
}

/// <summary>One field of <see cref="IPluginUi.EditFieldsAsync"/>.</summary>
public sealed class PluginField
{
    public required string Label { get; init; }
    public PluginFieldKind Kind { get; init; } = PluginFieldKind.Text;
    public string Value { get; set; } = string.Empty;

    /// <summary>The choices of a <see cref="PluginFieldKind.Choice"/> field.</summary>
    public IReadOnlyList<string> Choices { get; init; } = Array.Empty<string>();

    /// <summary>Shown in an empty text field.</summary>
    public string? Placeholder { get; init; }

    /// <summary>Called when a choice changes (to fill in other fields, e.g. the port of a protocol); may change other fields' values.</summary>
    public Action<string>? Changed { get; init; }
}

/// <param name="ShortcutText">"Ctrl+C", "Delete": shown in the menu and also works as a key in the window.</param>
public sealed record PluginMenuItem(string Text, string? ShortcutText, Action Execute, bool IsEnabled = true);

/// <param name="IsDefault">Enter presses it.</param>
public sealed record PluginButton(string Id, string Text, Action Click, bool IsDefault = false);

/// <summary>A window opened by a plugin (see <see cref="IPluginUi.OpenListWindow"/>).</summary>
public interface IPluginListWindow
{
    /// <summary>Adds a row at the end; cells beyond the given ones are empty.</summary>
    IPluginRow AddRow(IReadOnlyList<string> cells);

    IReadOnlyList<IPluginRow> Rows { get; }

    /// <summary>The selected row, or null.</summary>
    IPluginRow? SelectedRow { get; }

    void SetStatus(string text);

    /// <summary>Adds a line to the log below the list (shown from the first line on; a connection log, ...).</summary>
    void AppendLog(string line) { }

    /// <summary>Empties the log and hides it.</summary>
    void ClearLog() { }

    /// <summary>0..1, or null to hide the progress bar.</summary>
    void SetProgress(double? value);

    void SetButtons(IReadOnlyList<PluginButton> buttons);

    void UpdateButton(string id, string? text = null, bool? enabled = null);

    /// <summary>The right-click menu of a row (and its shortcut keys).</summary>
    Func<IPluginRow, IReadOnlyList<PluginMenuItem>>? RowMenu { get; set; }

    /// <summary>Called on the UI thread when the window has closed.</summary>
    event EventHandler? Closed;

    bool IsClosed { get; }

    void Close();
}

/// <summary>A point in a canvas window's picture, in pixels (not scaled by the display's DPI).</summary>
public readonly record struct PluginPoint(double X, double Y);

/// <summary>
/// A text over a canvas window's picture. Position and size are in pixels; the text is trimmed to the box
/// and left out when the box is too small for it.
/// </summary>
/// <param name="Color">0xAARRGGBB.</param>
/// <param name="Background">0xAARRGGBB behind the text (a rounded plate); 0 = none.</param>
/// <param name="FontSize">In display-independent units (like the app's texts).</param>
/// <param name="Centered">Centered in the box (otherwise at its top left).</param>
public sealed record PluginLabel(double X, double Y, double Width, double Height, string Text, uint Color,
    uint Background = 0, double FontSize = 12, bool Bold = false, bool Centered = false);

/// <summary>A color and what it means, below a canvas window's picture.</summary>
/// <param name="Color">0xAARRGGBB.</param>
public sealed record PluginLegendItem(uint Color, string Text);

/// <summary>
/// A window with a picture the plugin draws (see <see cref="IPluginUi.OpenCanvasWindow"/>). Methods may be
/// called from any thread. The callbacks (pointer, keys, menu) run on the UI thread and must be quick.
/// Coordinates are in pixels of the picture.
/// </summary>
public interface IPluginCanvasWindow
{
    /// <summary>The size the picture should have now, in pixels.</summary>
    int PixelWidth { get; }

    int PixelHeight { get; }

    /// <summary>Pixels per display-independent unit (1.5 at 150 % display scaling).</summary>
    double Scale { get; }

    /// <summary>The app uses its dark theme.</summary>
    bool IsDark { get; }

    /// <summary>The picture area changed its size (raised once the resizing settles; UI thread).</summary>
    event EventHandler? Resized;

    /// <summary>The picture (premultiplied BGRA, width × height × 4 bytes; transparent shows the window) and its labels.</summary>
    void SetPicture(int width, int height, byte[] pixels, IReadOnlyList<PluginLabel> labels);

    /// <summary>Outlines over the picture: the item under the pointer and the selected item (null = none).</summary>
    void SetHighlight(IReadOnlyList<PluginPoint>? hover, IReadOnlyList<PluginPoint>? selected);

    /// <summary>The line above the picture (the path shown).</summary>
    void SetHeading(string text);

    /// <summary>The views to choose from (above the picture, right); <see cref="ViewChanged"/> reports the choice.</summary>
    void SetViews(IReadOnlyList<string> names, int selected);

    /// <summary>The user chose another view (its index; UI thread).</summary>
    event EventHandler<int>? ViewChanged;

    void SetLegend(IReadOnlyList<PluginLegendItem> items);

    void SetStatus(string text);

    /// <summary>0..1; a negative value = busy without a known end; null hides the progress bar.</summary>
    void SetProgress(double? value);

    void SetButtons(IReadOnlyList<PluginButton> buttons);

    void UpdateButton(string id, string? text = null, bool? enabled = null);

    /// <summary>The text of the tip next to the pointer (null = no tip).</summary>
    Func<PluginPoint, string?>? ToolTip { get; set; }

    Action<PluginPoint>? PointerMoved { get; set; }

    /// <summary>The pointer left the picture.</summary>
    Action? PointerExited { get; set; }

    Action<PluginPoint>? Clicked { get; set; }

    Action<PluginPoint>? DoubleClicked { get; set; }

    /// <summary>The right-click menu at a point.</summary>
    Func<PluginPoint, IReadOnlyList<PluginMenuItem>>? ContextMenu { get; set; }

    /// <summary>
    /// A key ("Backspace", "Enter", "Delete", "Ctrl+C", "F5", ...; the mouse's back button is "Backspace");
    /// true when the plugin used it. Escape closes the window.
    /// </summary>
    Func<string, bool>? KeyPressed { get; set; }

    /// <summary>Called on the UI thread when the window has closed.</summary>
    event EventHandler? Closed;

    bool IsClosed { get; }

    void Close();
}

/// <summary>One row of a plugin window.</summary>
public interface IPluginRow
{
    /// <summary>A cell's text (setting it updates the window).</summary>
    string this[int column] { get; set; }

    /// <summary>The plugin's own data for the row.</summary>
    object? Tag { get; set; }

    /// <summary>Takes the row out of the window.</summary>
    void Remove();
}

public interface IPanelContext
{
    string Path { get; }
    FileEntry? FocusedItem { get; }
    IReadOnlyList<FileEntry> MarkedItems { get; }
    Task NavigateAsync(string path, string? focusName = null);
    Task RefreshAsync();
}

/// <summary>Plugin commands for the Plugins menu.</summary>
public interface ICommandContributor
{
    IReadOnlyList<PluginCommand> Commands { get; }
}

/// <param name="Id">Unique within the plugin.</param>
/// <param name="MenuText">"Pack...", "Calculate checksums..."</param>
/// <param name="ShortcutText">Shown in the menu only ("Alt+F5"); keys are bound in Configuration → Keyboard.</param>
/// <param name="Execute">Runs on the UI thread.</param>
/// <param name="HostCommand">
/// The app command this plugin command answers to ("VerifyChecksums"): it then has a key in
/// Configuration → Keyboard, and the shortcut shown in the menu follows it.
/// </param>
public sealed record PluginCommand(string Id, string MenuText, string? ShortcutText, Func<IPluginHost, Task> Execute,
    string? HostCommand = null);

// ---- file systems (FTP, SFTP, ...) ---------------------------------------------------------------

/// <summary>
/// A file system shown in the panels like a directory tree. Its paths are URLs the plugin understands
/// ("sftp://user@host:22/home/user"). The host lists them, and runs copies, deletes and so on in the
/// transfer panel through an <see cref="IArchiveJob"/> (progress, pause, cancel, questions).
/// All methods run on background threads.
/// </summary>
public interface IPluginFileSystem
{
    string DisplayName { get; }

    /// <summary>True when the path belongs to this file system.</summary>
    bool CanHandle(string path);

    /// <summary>The items of a directory (without ".."). Throws with a readable message when it can't be listed.</summary>
    Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken cancellationToken);

    /// <summary>The parent directory, or null at the root (the panel then goes to This PC).</summary>
    string? GetParent(string path);

    /// <summary>"sftp://user@host/dir": for the directory line and the window title.</summary>
    string GetDisplayPath(string path);

    /// <summary>The path of an item in a directory.</summary>
    string Combine(string directory, string name);

    /// <summary>Copies files and directories (with their content) from here into a local directory.</summary>
    Task<OperationResult> DownloadAsync(IReadOnlyList<RemoteItem> items, string targetDirectory, IArchiveJob job);

    /// <summary>Copies local files and directories (with their content) into a directory here.</summary>
    Task<OperationResult> UploadAsync(IReadOnlyList<string> sources, string targetDirectory, IArchiveJob job);

    /// <summary>Deletes files and directories (with their content).</summary>
    Task<OperationResult> DeleteAsync(IReadOnlyList<RemoteItem> items, IArchiveJob job);

    Task CreateDirectoryAsync(string path, CancellationToken cancellationToken);

    /// <summary>Renames (or moves within the same server) an item.</summary>
    Task RenameAsync(string path, string newPath, CancellationToken cancellationToken);

    /// <summary>The panel leaves the server: its connection is closed (the next visit logs in again).</summary>
    void Disconnect(string path) { }
}

/// <summary>One file or directory of a plugin file system.</summary>
/// <param name="Path">Its full path (as the plugin's paths go).</param>
public sealed record RemoteItem(string Name, string Path, bool IsDirectory, long Size, DateTime LastWrite, bool IsHidden = false, bool IsLink = false);

// ---- archivers ---------------------------------------------------------------------------------

/// <summary>A format an archiver can create.</summary>
/// <param name="Id">"zip", "tar", "tar.gz"</param>
/// <param name="DisplayName">"ZIP", "TAR.GZ (gzip)"</param>
/// <param name="Extension">Of new archives, with the dot: ".zip", ".tar.gz"</param>
/// <param name="HasCompressionLevels">The compression level choice applies.</param>
/// <param name="SupportsPassword">Archives of this format can be encrypted with a password (<see cref="PackRequest.Password"/>).</param>
public sealed record ArchiveFormat(string Id, string DisplayName, string Extension, bool HasCompressionLevels, bool SupportsPassword = false);

public enum ArchiveCompression
{
    Store,
    Fastest,
    Normal,
    Maximum,
}

/// <summary>Alt+F5: pack files and directories into a new archive.</summary>
public sealed class PackRequest
{
    /// <summary>Files and directories (full paths); directories are packed with their content.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>Names in the archive are relative to this directory (normally the panel's directory).</summary>
    public required string BaseDirectory { get; init; }

    public required string ArchivePath { get; init; }

    public required string FormatId { get; init; }

    public ArchiveCompression Compression { get; init; } = ArchiveCompression.Normal;

    /// <summary>Encrypts the files with this password (formats with <see cref="ArchiveFormat.SupportsPassword"/>); null = not encrypted.</summary>
    public string? Password { get; init; }
}

/// <summary>Copying files and directories into an archive (F5 / F6 / drag and drop onto a panel showing the archive).</summary>
public sealed class ArchiveAddRequest
{
    public required string ArchivePath { get; init; }

    /// <summary>Files and directories (full paths); directories go in with their content, under their own names.</summary>
    public required IReadOnlyList<string> Sources { get; init; }

    /// <summary>The directory inside the archive they go into ("docs/images"); "" = the archive's root.</summary>
    public string TargetPath { get; init; } = string.Empty;

    public ArchiveCompression Compression { get; init; } = ArchiveCompression.Normal;
}

/// <summary>Deleting files and directories from an archive (F8 in a panel showing the archive).</summary>
public sealed class ArchiveDeleteRequest
{
    public required string ArchivePath { get; init; }

    /// <summary>"docs/readme.txt", "docs/images" (a directory with its content); '/' separated.</summary>
    public required IReadOnlyList<string> Items { get; init; }

    /// <summary>
    /// The directory the panel shows ("docs"): it stays in the archive (as an empty directory) when
    /// everything in it is deleted. "" = the archive's root.
    /// </summary>
    public string BasePath { get; init; } = string.Empty;
}

/// <summary>Alt+F6: unpack an archive into a directory.</summary>
public sealed class UnpackRequest
{
    public required string ArchivePath { get; init; }

    /// <summary>Created when missing.</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>File masks of the files to unpack ("*.*", "*.txt;*.md"), matched against the file names.</summary>
    public string Mask { get; init; } = "*.*";

    /// <summary>
    /// Only these items ("docs/readme.txt", "docs/images" - a directory with its content); null = all.
    /// Paths inside the archive use '/'.
    /// </summary>
    public IReadOnlyList<string>? Items { get; init; }

    /// <summary>
    /// The directory inside the archive the items are taken from ("docs"): it is left out of the target
    /// paths, so "docs/images/a.png" goes to "&lt;target&gt;\images\a.png". Null or "" = the archive's root.
    /// </summary>
    public string? BasePath { get; init; }
}

/// <summary>One file or directory inside an archive (for browsing archives in a panel).</summary>
/// <param name="Path">"docs/readme.txt" ('/' separated, no leading or trailing '/').</param>
/// <param name="Size">Unpacked size; 0 when unknown.</param>
public sealed record ArchiveItem(string Path, bool IsDirectory, long Size, DateTime LastWrite);

/// <summary>Archive support: packing into some formats and/or unpacking some formats.</summary>
public interface IArchiver
{
    string DisplayName { get; }

    /// <summary>Formats Alt+F5 can create (empty: unpacking only).</summary>
    IReadOnlyList<ArchiveFormat> PackFormats { get; }

    /// <summary>File name endings this archiver unpacks, without the dot ("zip", "7z", "tar.gz").</summary>
    IReadOnlyList<string> UnpackExtensions { get; }

    /// <summary>True when the file looks like an archive this archiver unpacks (normally by its name).</summary>
    bool CanUnpack(string archivePath);

    /// <summary>True when Enter opens the archive in the panel like a directory (by its name).</summary>
    bool CanBrowse(string archivePath);

    /// <summary>
    /// Everything in the archive (directories may be missing; the panel adds them from the paths).
    /// Runs on a background thread. Throws when the archive can't be read.
    /// </summary>
    Task<IReadOnlyList<ArchiveItem>> ListAsync(string archivePath, CancellationToken cancellationToken);

    /// <summary>Runs on a background thread; reports through the job and honors its cancellation and pause.</summary>
    Task<OperationResult> PackAsync(PackRequest request, IArchiveJob job);

    /// <summary>Runs on a background thread; asks about existing files and passwords through the job.</summary>
    Task<OperationResult> UnpackAsync(UnpackRequest request, IArchiveJob job);

    /// <summary>True when files can be copied into and deleted from this archive (by its name and format).</summary>
    bool CanEdit(string archivePath) => false;

    /// <summary>
    /// Copies files into the archive (the archive is rewritten; it is replaced only when everything went well).
    /// Asks about existing names and, for encrypted archives, the password through the job. Background thread.
    /// </summary>
    Task<OperationResult> AddAsync(ArchiveAddRequest request, IArchiveJob job) =>
        throw new NotSupportedException("This archive can't be changed.");

    /// <summary>Deletes files and directories from the archive (rewritten like <see cref="AddAsync"/>). Background thread.</summary>
    Task<OperationResult> DeleteAsync(ArchiveDeleteRequest request, IArchiveJob job) =>
        throw new NotSupportedException("This archive can't be changed.");
}

/// <summary>
/// A running pack/unpack in the transfer panel. All members may be called from a background thread;
/// questions are shown on the UI thread and the returned tasks complete with the answer.
/// </summary>
public interface IArchiveJob
{
    CancellationToken Cancellation { get; }

    /// <summary>Blocks while the user has paused the job in the transfer panel; throws when it is cancelled.</summary>
    void WaitWhilePaused();

    /// <summary>Progress for the transfer panel.</summary>
    void Report(OperationProgress progress);

    /// <summary>"The target file already exists" (overwrite / skip / keep both / cancel).</summary>
    Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict);

    /// <summary>An error on one item (retry / skip / cancel).</summary>
    Task<ErrorChoice> ResolveErrorAsync(OperationError error);

    /// <summary>Asks for the password of an encrypted archive; null when the user cancels.</summary>
    Task<string?> AskPasswordAsync(string archivePath, bool wrongPassword);
}

/// <summary>
/// Builds OperationProgress snapshots with speed and remaining time (for plugins that report bytes).
/// Not thread-safe: use it from the worker thread of one job.
/// </summary>
public sealed class ProgressTracker
{
    private readonly OperationKind _kind;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private long _lastReportTicks = long.MinValue;
    private long _windowBytes;
    private long _windowStartTicks;
    private double _bytesPerSecond;

    public ProgressTracker(OperationKind kind, long totalBytes, int totalItems)
    {
        _kind = kind;
        TotalBytes = totalBytes;
        ItemsTotal = totalItems;
    }

    public long TotalBytes { get; set; }
    public int ItemsTotal { get; set; }
    public long BytesDone { get; private set; }
    public int ItemsDone { get; private set; }
    public string Current { get; private set; } = string.Empty;

    /// <summary>The file being processed (shown in the transfer panel).</summary>
    public void Start(string current) => Current = current;

    public void AddBytes(long bytes) => BytesDone += bytes;

    public void ItemDone() => ItemsDone++;

    /// <summary>Sets the done bytes directly (e.g. the position in a compressed stream).</summary>
    public void SetBytes(long bytes) => BytesDone = bytes;

    /// <summary>Reports at most every 100 ms unless forced.</summary>
    public void ReportTo(IArchiveJob job, bool force = false)
    {
        long now = _clock.ElapsedTicks;
        long frequency = System.Diagnostics.Stopwatch.Frequency;
        if (!force && _lastReportTicks != long.MinValue && now - _lastReportTicks < frequency / 10)
            return;
        _lastReportTicks = now;
        // speed over the last second or so, smoothed
        double windowSeconds = (double)(now - _windowStartTicks) / frequency;
        if (windowSeconds >= 1)
        {
            double speed = (BytesDone - _windowBytes) / windowSeconds;
            _bytesPerSecond = _bytesPerSecond <= 0 ? speed : _bytesPerSecond * 0.6 + speed * 0.4;
            _windowBytes = BytesDone;
            _windowStartTicks = now;
        }
        TimeSpan? remaining = _bytesPerSecond > 0 && TotalBytes > BytesDone
            ? TimeSpan.FromSeconds((TotalBytes - BytesDone) / _bytesPerSecond)
            : null;
        job.Report(new OperationProgress
        {
            Kind = _kind,
            CurrentSource = Current,
            TotalBytes = TotalBytes,
            TotalBytesDone = TotalBytes > 0 ? Math.Min(BytesDone, TotalBytes) : BytesDone,
            ItemsDone = ItemsDone,
            ItemsTotal = ItemsTotal,
            BytesPerSecond = _bytesPerSecond,
            Remaining = remaining,
        });
    }
}
