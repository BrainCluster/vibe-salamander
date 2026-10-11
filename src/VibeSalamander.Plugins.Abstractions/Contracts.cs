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

    /// <summary>The active panel's current path and focused/marked items.</summary>
    IPanelContext ActivePanel { get; }

    IPanelContext InactivePanel { get; }

    /// <summary>A short message in the notification bar (no dialog).</summary>
    void Notify(string message, bool isError = false);
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
public sealed record PluginCommand(string Id, string MenuText, string? ShortcutText, Func<IPluginHost, Task> Execute);

// ---- archivers ---------------------------------------------------------------------------------

/// <summary>A format an archiver can create.</summary>
/// <param name="Id">"zip", "tar", "tar.gz"</param>
/// <param name="DisplayName">"ZIP", "TAR.GZ (gzip)"</param>
/// <param name="Extension">Of new archives, with the dot: ".zip", ".tar.gz"</param>
/// <param name="HasCompressionLevels">The compression level choice applies.</param>
public sealed record ArchiveFormat(string Id, string DisplayName, string Extension, bool HasCompressionLevels);

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
}

/// <summary>Alt+F6: unpack an archive into a directory.</summary>
public sealed class UnpackRequest
{
    public required string ArchivePath { get; init; }

    /// <summary>Created when missing.</summary>
    public required string TargetDirectory { get; init; }

    /// <summary>File masks of the files to unpack ("*.*", "*.txt;*.md"), matched against the file names.</summary>
    public string Mask { get; init; } = "*.*";
}

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

    /// <summary>Runs on a background thread; reports through the job and honors its cancellation and pause.</summary>
    Task<OperationResult> PackAsync(PackRequest request, IArchiveJob job);

    /// <summary>Runs on a background thread; asks about existing files and passwords through the job.</summary>
    Task<OperationResult> UnpackAsync(UnpackRequest request, IArchiveJob job);
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
