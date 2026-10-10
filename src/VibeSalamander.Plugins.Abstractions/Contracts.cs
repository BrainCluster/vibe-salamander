using VibeSalamander.Core.Commands;
using VibeSalamander.Core.FileSystem;

namespace VibeSalamander.Plugins;

// Contracts for future addons (ports of Open Salamander plugins). Nothing loads addons yet; these
// interfaces exist so the core is designed around them. See docs/ARCHITECTURE.md → "Addons".

/// <summary>Entry point of an addon assembly (one public class implementing this interface).</summary>
public interface IVibeAddon
{
    /// <summary>Unique id, e.g. "vibe.zip".</summary>
    string Id { get; }

    string DisplayName { get; }

    Version Version { get; }

    /// <summary>Called once after loading; the addon registers its features through the host.</summary>
    void Initialize(IVibeHost host);

    /// <summary>Called before unloading (app exit or addon disabled).</summary>
    void Shutdown();
}

/// <summary>What the app offers to addons.</summary>
public interface IVibeHost
{
    Version HostVersion { get; }

    /// <summary>Per-addon settings/data folder.</summary>
    string GetDataDirectory(IVibeAddon addon);

    void RegisterFileSystem(IFileSystemProviderFactory factory);
    void RegisterArchive(IArchiveProviderFactory factory);
    void RegisterViewer(IViewerFactory factory);
    void RegisterCommands(ICommandContributor contributor);

    /// <summary>The active panel's current path and focused/marked items.</summary>
    IPanelContext ActivePanel { get; }
    IPanelContext InactivePanel { get; }
}

public interface IPanelContext
{
    string Path { get; }
    FileEntry? FocusedItem { get; }
    IReadOnlyList<FileEntry> MarkedItems { get; }
    Task NavigateAsync(string path, string? focusName = null);
    Task RefreshAsync();
}

/// <summary>File-system addons (FTP, SFTP, registry, …): create providers for paths with their prefix ("ftp:").</summary>
public interface IFileSystemProviderFactory
{
    string PathPrefix { get; }
    string DisplayName { get; }
    IFileSystemProvider Create();
}

/// <summary>Archive addons: browse archives as directories and pack/unpack.</summary>
public interface IArchiveProviderFactory
{
    /// <summary>Extensions without dot ("zip", "7z").</summary>
    IReadOnlyCollection<string> Extensions { get; }

    bool CanPack { get; }

    IFileSystemProvider OpenArchive(string archivePath);

    Task PackAsync(IReadOnlyList<string> sources, string archivePath, IProgress<double>? progress, CancellationToken cancellationToken);
}

/// <summary>Viewer addons (F3): pictures, PE files, databases, …</summary>
public interface IViewerFactory
{
    string DisplayName { get; }

    /// <summary>File masks handled by default ("*.jpg;*.png").</summary>
    string DefaultMasks { get; }

    /// <summary>Returns a UI element for the hosting window. Typed as object to keep this assembly UI-free.</summary>
    object CreateView(string filePath);
}

/// <summary>Menu-extension addons (checksum, renamer, file compare, …).</summary>
public interface ICommandContributor
{
    IReadOnlyList<AddonCommand> Commands { get; }
}

public sealed record AddonCommand(string Id, string MenuText, KeyChord? DefaultShortcut, Func<IVibeHost, Task> Execute);
