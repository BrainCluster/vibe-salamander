namespace VibeSalamander.Plugins.Archive;

/// <summary>
/// The built-in archive plugin (Salamander's ZIP, TAR, 7-Zip and UnRAR plugins in one): Alt+F5 packs
/// into ZIP / TAR / TAR.GZ, Alt+F6 unpacks ZIP, 7z, RAR, TAR (also gz/bz2/xz/lz compressed) and
/// single gz / bz2 / xz / lz files.
/// </summary>
public sealed class ArchivePlugin : IVibePlugin
{
    public string Id => "vibe.archive";
    public string DisplayName => "Archives";
    public Version Version => new(1, 0);
    public string Description =>
        "Pack into ZIP, TAR and TAR.GZ (Alt+F5); unpack ZIP, 7-Zip, RAR, TAR, gzip, bzip2, xz and lzip (Alt+F6). " +
        "Password-protected ZIP, 7-Zip and RAR archives can be unpacked.";
    public string Author => "Vibe Salamander";

    public void Initialize(IPluginHost host) => host.RegisterArchiver(new Archiver());

    public void Shutdown()
    {
    }
}
