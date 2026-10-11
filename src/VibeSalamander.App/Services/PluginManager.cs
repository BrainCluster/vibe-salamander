// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Reflection;
using System.Runtime.Loader;
using VibeSalamander.Core.FileSystem;
using VibeSalamander.Plugins;
using VibeSalamander.ViewModels;

namespace VibeSalamander.Services;

/// <summary>A plugin as the Plugins Manager shows it (also when it is disabled or failed to load).</summary>
public sealed class PluginInfo
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public Version Version { get; init; } = new(1, 0);
    public string Description { get; init; } = string.Empty;
    public string Author { get; init; } = string.Empty;

    /// <summary>"Built-in", or the plugin's file.</summary>
    public required string Source { get; init; }

    public bool IsBuiltIn { get; init; }
    public bool Enabled { get; set; }

    /// <summary>Why it couldn't be loaded or initialized, or null.</summary>
    public string? Error { get; set; }

    internal IVibePlugin? Instance { get; set; }

    /// <summary>The host this plugin was initialized with (for running its commands).</summary>
    public IPluginHost? Host { get; internal set; }
    public List<IArchiver> Archivers { get; } = new();
    public List<ICommandContributor> CommandContributors { get; } = new();

    /// <summary>"Pack and unpack", "3 commands" - what the plugin adds, for the manager.</summary>
    public string FeaturesText
    {
        get
        {
            var parts = new List<string>();
            foreach (IArchiver a in Archivers)
            {
                if (a.PackFormats.Count > 0)
                    parts.Add("Pack: " + string.Join(", ", a.PackFormats.Select(f => f.DisplayName.Split(' ')[0])));
                if (a.UnpackExtensions.Count > 0)
                    parts.Add("Unpack: " + string.Join(", ", a.UnpackExtensions.Take(12)) + (a.UnpackExtensions.Count > 12 ? ", ..." : string.Empty));
            }
            int commands = CommandContributors.Sum(c => c.Commands.Count);
            if (commands > 0)
                parts.Add(commands == 1 ? "1 command" : $"{commands} commands");
            return string.Join("  ·  ", parts);
        }
    }
}

/// <summary>
/// Loads the built-in plugins and the plugin assemblies in "&lt;app&gt;\Plugins\&lt;name&gt;\" and
/// "%AppData%\VibeSalamander\Plugins\&lt;name&gt;\" (each in its own load context; the plugin contracts
/// and the core are shared with the app). Disabled plugins are listed but not initialized.
/// </summary>
public sealed class PluginManager
{
    private readonly List<PluginInfo> _plugins = new();

    public IReadOnlyList<PluginInfo> Plugins => _plugins;

    /// <summary>Plugin folder in the user's profile (Plugins Manager → Open plugins folder).</summary>
    public static string UserPluginDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "VibeSalamander", "Plugins");

    public static string AppPluginDirectory => Path.Combine(AppContext.BaseDirectory, "Plugins");

    /// <summary>Archivers of the enabled plugins.</summary>
    public IEnumerable<IArchiver> Archivers => _plugins.Where(p => p.Enabled && p.Error == null).SelectMany(p => p.Archivers);

    /// <summary>Every format Pack can create, with its archiver (first plugin wins for the same id).</summary>
    public IReadOnlyList<(IArchiver Archiver, ArchiveFormat Format)> PackFormats =>
        Archivers.SelectMany(a => a.PackFormats.Select(f => (a, f)))
                 .GroupBy(x => x.f.Id, StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();

    /// <summary>The archiver that opens this file in the panel like a directory, or null.</summary>
    public IArchiver? FindBrowser(string path) => Archivers.FirstOrDefault(a => SafeCanBrowse(a, path));

    private static bool SafeCanBrowse(IArchiver archiver, string path)
    {
        try
        {
            return archiver.CanBrowse(path);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The archiver that unpacks this file, or null.</summary>
    public IArchiver? FindUnpacker(string path) => Archivers.FirstOrDefault(a => SafeCanUnpack(a, path));

    /// <summary>"backup.tar.gz" → "backup" (the archive's name without its archive extension).</summary>
    public string ArchiveBaseName(string path)
    {
        string name = Path.GetFileName(path);
        string? ext = Archivers.SelectMany(a => a.UnpackExtensions)
                               .Where(e => name.EndsWith("." + e, StringComparison.OrdinalIgnoreCase) && name.Length > e.Length + 1)
                               .OrderByDescending(e => e.Length).FirstOrDefault();
        return ext != null ? name[..^(ext.Length + 1)] : Path.GetFileNameWithoutExtension(name);
    }

    /// <summary>The name already ends with the format's extension (".tar.gz", also ".tgz").</summary>
    public static bool HasArchiveExtension(string path, ArchiveFormat format) =>
        path.EndsWith(format.Extension, StringComparison.OrdinalIgnoreCase) ||
        (format.Id == "tar.gz" && path.EndsWith(".tgz", StringComparison.OrdinalIgnoreCase));

    private static bool SafeCanUnpack(IArchiver archiver, string path)
    {
        try
        {
            return archiver.CanUnpack(path);
        }
        catch (Exception)
        {
            return false; // a broken plugin must not break the panel
        }
    }

    /// <summary>Loads and initializes everything (once, at startup).</summary>
    public void LoadAll(MainViewModel main, ICollection<string> disabled)
    {
        // built-in
        Add(new global::VibeSalamander.Plugins.Archive.ArchivePlugin(), "Built-in", builtIn: true, main, disabled);

        // external: one folder per plugin
        foreach (string root in new[] { AppPluginDirectory, UserPluginDirectory })
        {
            if (!Directory.Exists(root))
                continue;
            foreach (string folder in SafeDirectories(root))
            {
                foreach (string file in CandidateAssemblies(folder))
                    LoadExternal(file, main, disabled);
            }
        }
    }

    private static IEnumerable<string> SafeDirectories(string root)
    {
        try
        {
            return Directory.GetDirectories(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>"Plugins\Foo\Foo.dll" when it exists, otherwise every assembly in the folder.</summary>
    private static IEnumerable<string> CandidateAssemblies(string folder)
    {
        string main = Path.Combine(folder, Path.GetFileName(folder) + ".dll");
        if (File.Exists(main))
            return new[] { main };
        try
        {
            return Directory.GetFiles(folder, "*.dll")
                .Where(f => !Path.GetFileName(f).StartsWith("VibeSalamander.Core", StringComparison.OrdinalIgnoreCase) &&
                            !Path.GetFileName(f).StartsWith("VibeSalamander.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return Array.Empty<string>();
        }
    }

    private void LoadExternal(string file, MainViewModel main, ICollection<string> disabled)
    {
        List<Type> types;
        try
        {
            var context = new PluginLoadContext(file);
            Assembly assembly = context.LoadFromAssemblyPath(file);
            types = assembly.GetExportedTypes()
                .Where(t => typeof(IVibePlugin).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false } && t.GetConstructor(Type.EmptyTypes) != null)
                .ToList();
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or FileNotFoundException or ReflectionTypeLoadException or TypeLoadException or IOException)
        {
            // not a .NET assembly, or one that doesn't fit this version: show it only if it was meant as a plugin
            if (Path.GetFileNameWithoutExtension(file).Equals(Path.GetFileName(Path.GetDirectoryName(file)), StringComparison.OrdinalIgnoreCase))
                _plugins.Add(new PluginInfo { Id = file, Name = Path.GetFileNameWithoutExtension(file), Source = file, Error = ex.Message });
            return;
        }
        foreach (Type type in types)
        {
            try
            {
                Add((IVibePlugin)Activator.CreateInstance(type)!, file, builtIn: false, main, disabled);
            }
            catch (Exception ex)
            {
                _plugins.Add(new PluginInfo { Id = type.FullName ?? file, Name = type.Name, Source = file, Error = ex.InnerException?.Message ?? ex.Message });
            }
        }
    }

    private void Add(IVibePlugin plugin, string source, bool builtIn, MainViewModel main, ICollection<string> disabled)
    {
        if (_plugins.Any(p => string.Equals(p.Id, plugin.Id, StringComparison.OrdinalIgnoreCase)))
            return; // the same plugin in two folders: the first one counts
        var info = new PluginInfo
        {
            Id = plugin.Id,
            Name = plugin.DisplayName,
            Version = plugin.Version,
            Description = plugin.Description,
            Author = plugin.Author,
            Source = source,
            IsBuiltIn = builtIn,
            Enabled = !disabled.Contains(plugin.Id, StringComparer.OrdinalIgnoreCase),
            Instance = plugin,
        };
        _plugins.Add(info);
        if (!info.Enabled)
            return;
        try
        {
            info.Host = new PluginHost(main, info);
            plugin.Initialize(info.Host);
        }
        catch (Exception ex)
        {
            info.Error = ex.Message;
            info.Archivers.Clear();
            info.CommandContributors.Clear();
        }
    }

    /// <summary>At exit.</summary>
    public void ShutdownAll()
    {
        foreach (PluginInfo info in _plugins)
        {
            if (info.Instance == null || !info.Enabled || info.Error != null)
                continue;
            try
            {
                info.Instance.Shutdown();
            }
            catch (Exception)
            {
                // exiting anyway
            }
        }
    }

    /// <summary>Own assemblies per plugin; the contracts and the core come from the app.</summary>
    private sealed class PluginLoadContext : AssemblyLoadContext
    {
        private readonly AssemblyDependencyResolver _resolver;

        public PluginLoadContext(string pluginPath) : base(Path.GetFileNameWithoutExtension(pluginPath))
        {
            _resolver = new AssemblyDependencyResolver(pluginPath);
        }

        protected override Assembly? Load(AssemblyName name)
        {
            if (name.Name is "VibeSalamander.Core" or "VibeSalamander.Plugins.Abstractions")
                return null; // shared: the types must be the app's
            string? path = _resolver.ResolveAssemblyToPath(name);
            return path != null ? LoadFromAssemblyPath(path) : null;
        }

        protected override IntPtr LoadUnmanagedDll(string unmanagedDllName)
        {
            string? path = _resolver.ResolveUnmanagedDllToPath(unmanagedDllName);
            return path != null ? LoadUnmanagedDllFromPath(path) : IntPtr.Zero;
        }
    }
}

/// <summary>The host as one plugin sees it.</summary>
internal sealed class PluginHost : IPluginHost
{
    private readonly MainViewModel _main;
    private readonly PluginInfo _info;

    public PluginHost(MainViewModel main, PluginInfo info)
    {
        _main = main;
        _info = info;
    }

    public Version HostVersion => typeof(PluginHost).Assembly.GetName().Version ?? new Version(0, 1);

    public string GetDataDirectory(IVibePlugin plugin)
    {
        string safe = string.Concat(plugin.Id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        string dir = Path.Combine(Core.Settings.SettingsStore.Directory, "PluginData", safe);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public void RegisterArchiver(IArchiver archiver) => _info.Archivers.Add(archiver);

    public void RegisterCommands(ICommandContributor contributor) => _info.CommandContributors.Add(contributor);

    public IPanelContext ActivePanel => new PanelContext(_main.Active);

    public IPanelContext InactivePanel => new PanelContext(_main.Inactive);

    public void Notify(string message, bool isError = false) => _main.Notify(message, isError);

    private sealed class PanelContext : IPanelContext
    {
        private readonly PaneViewModel _pane;

        public PanelContext(PaneViewModel pane) => _pane = pane;

        public string Path => _pane.Path;
        public FileEntry? FocusedItem => _pane.FocusedItem?.Entry;
        public IReadOnlyList<FileEntry> MarkedItems => _pane.MarkedItems.Select(i => i.Entry).ToList();
        public Task NavigateAsync(string path, string? focusName = null) => _pane.NavigateAsync(path, focusName);
        public Task RefreshAsync() => _pane.RefreshAsync();
    }
}
