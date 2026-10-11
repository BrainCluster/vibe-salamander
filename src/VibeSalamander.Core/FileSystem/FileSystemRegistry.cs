namespace VibeSalamander.Core.FileSystem;

/// <summary>Finds the provider for a panel path. Addon file systems will register here.</summary>
public sealed class FileSystemRegistry
{
    private readonly List<IFileSystemProvider> _providers = new();

    public FileSystemRegistry()
    {
        Register(new ThisPcProvider());
        Register(new LocalFileSystemProvider());
    }

    public IReadOnlyList<IFileSystemProvider> Providers => _providers;

    public void Register(IFileSystemProvider provider) => _providers.Add(provider);

    /// <summary>Before the others: providers for paths that also look like disk paths ("C:\\x.zip\\dir").</summary>
    public void RegisterFirst(IFileSystemProvider provider) => _providers.Insert(0, provider);

    public IFileSystemProvider? Find(string path) => _providers.FirstOrDefault(p => p.CanHandle(path));
}
