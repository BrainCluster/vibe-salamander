// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography.X509Certificates;
using FluentFTP;
using Renci.SshNet;
using Renci.SshNet.Common;
using Renci.SshNet.Sftp;

namespace VibeSalamander.Plugins.Ftp;

/// <summary>One item on a server (its path is the server's path, "/home/user/a.txt").</summary>
internal sealed record RemoteEntry(string Name, string Path, bool IsDirectory, long Size, DateTime LastWrite, bool IsLink = false);

/// <summary>The password (or the private key's passphrase) and the key file for one server.</summary>
internal sealed record Credentials(string Password, string? PrivateKeyFile);

/// <summary>
/// Decides whether to trust a server's key or certificate: (server "host:port", fingerprint, what it is,
/// whether it differs from the one trusted before) → trust. Asked on a background thread.
/// </summary>
internal delegate bool TrustCheck(string server, string fingerprint, string description, bool changed);

/// <summary>A connection to one server (FTP or SFTP). Not thread-safe: one operation at a time.</summary>
internal interface IRemoteClient : IDisposable
{
    bool IsConnected { get; }
    Task ConnectAsync(CancellationToken ct);
    Task<string> GetHomeAsync(CancellationToken ct);
    Task<List<RemoteEntry>> ListAsync(string directory, CancellationToken ct);

    /// <summary>The item, or null when nothing is there.</summary>
    Task<RemoteEntry?> StatAsync(string path, CancellationToken ct);

    Task DownloadAsync(string path, Stream target, CancellationToken ct);
    Task UploadAsync(Stream source, string path, CancellationToken ct);
    Task DeleteFileAsync(string path, CancellationToken ct);

    /// <summary>Deletes an empty directory.</summary>
    Task DeleteDirectoryAsync(string path, CancellationToken ct);

    Task CreateDirectoryAsync(string path, CancellationToken ct);
    Task RenameAsync(string from, string to, CancellationToken ct);

    /// <summary>Keeps the original time of an uploaded file (best effort: not every server allows it).</summary>
    Task SetLastWriteAsync(string path, DateTime time, CancellationToken ct);
}

internal static class RemoteClients
{
    public static IRemoteClient Create(RemotePath server, Credentials credentials, TrustCheck trust) =>
        server.Protocol == RemoteProtocol.Sftp
            ? new SftpRemoteClient(server, credentials, trust)
            : new FtpRemoteClient(server, credentials, trust);

    public static string Join(string directory, string name) => directory.EndsWith('/') ? directory + name : directory + "/" + name;
}

/// <summary>SFTP through SSH.NET: password, keyboard-interactive or private key; the host key is checked.</summary>
internal sealed class SftpRemoteClient : IRemoteClient
{
    private readonly SftpClient _client;
    private readonly string _server;
    private Exception? _rejectedKey;

    public SftpRemoteClient(RemotePath server, Credentials credentials, TrustCheck trust)
    {
        string user = server.User.Length > 0 ? server.User : Environment.UserName;
        var methods = new List<AuthenticationMethod>();
        if (!string.IsNullOrWhiteSpace(credentials.PrivateKeyFile))
        {
            var key = string.IsNullOrEmpty(credentials.Password)
                ? new PrivateKeyFile(credentials.PrivateKeyFile)
                : new PrivateKeyFile(credentials.PrivateKeyFile, credentials.Password);
            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }
        else
        {
            methods.Add(new PasswordAuthenticationMethod(user, credentials.Password));
            // servers that only ask "Password:" through keyboard-interactive
            var interactive = new KeyboardInteractiveAuthenticationMethod(user);
            interactive.AuthenticationPrompt += (_, e) =>
            {
                foreach (AuthenticationPrompt prompt in e.Prompts)
                    prompt.Response = credentials.Password;
            };
            methods.Add(interactive);
        }
        var info = new ConnectionInfo(server.Host, server.Port, user, methods.ToArray())
        {
            Timeout = TimeSpan.FromSeconds(20),
        };
        _server = $"{server.Host}:{server.Port}";
        _client = new SftpClient(info) { OperationTimeout = TimeSpan.FromMinutes(2) };
        _client.HostKeyReceived += (_, e) =>
        {
            string fingerprint = "SHA256:" + e.FingerPrintSHA256;
            e.CanTrust = trust(_server, fingerprint, $"SSH {e.HostKeyName}", changed: false);
            if (!e.CanTrust)
                _rejectedKey = new InvalidOperationException($"The key of {_server} is not trusted.");
        };
    }

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            await _client.ConnectAsync(ct);
        }
        catch (SshConnectionException) when (_rejectedKey != null)
        {
            throw _rejectedKey;
        }
        catch (SshAuthenticationException ex)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }

    public Task<string> GetHomeAsync(CancellationToken ct) => Task.FromResult(_client.WorkingDirectory);

    public Task<List<RemoteEntry>> ListAsync(string directory, CancellationToken ct) => Task.Run(() =>
    {
        var result = new List<RemoteEntry>();
        foreach (ISftpFile file in _client.ListDirectory(directory))
        {
            ct.ThrowIfCancellationRequested();
            if (file.Name is "." or "..")
                continue;
            bool isDirectory = file.IsDirectory;
            long size = file.Length;
            DateTime time = file.LastWriteTime;
            if (file.IsSymbolicLink)
            {
                try
                {
                    // where the link points decides: a directory link is entered like a directory
                    SftpFileAttributes target = _client.GetAttributes(file.FullName);
                    isDirectory = target.IsDirectory;
                    size = target.Size;
                    time = target.LastWriteTime;
                }
                catch (SshException)
                {
                    // a broken link: shown as a file
                }
            }
            result.Add(new RemoteEntry(file.Name, file.FullName, isDirectory, isDirectory ? 0 : size, time, file.IsSymbolicLink));
        }
        return result;
    }, ct);

    public Task<RemoteEntry?> StatAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            SftpFileAttributes a = _client.GetAttributes(path);
            string name = path.TrimEnd('/');
            name = name[(name.LastIndexOf('/') + 1)..];
            return (RemoteEntry?)new RemoteEntry(name, path, a.IsDirectory, a.Size, a.LastWriteTime);
        }
        catch (SftpPathNotFoundException)
        {
            return null;
        }
    }, ct);

    public Task DownloadAsync(string path, Stream target, CancellationToken ct) =>
        Task.Run(() => _client.DownloadFile(path, target), ct);

    public Task UploadAsync(Stream source, string path, CancellationToken ct) =>
        Task.Run(() => _client.UploadFile(source, path, canOverride: true), ct);

    public Task DeleteFileAsync(string path, CancellationToken ct) => Task.Run(() => _client.DeleteFile(path), ct);

    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => Task.Run(() => _client.DeleteDirectory(path), ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Task.Run(() => _client.CreateDirectory(path), ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => Task.Run(() => _client.RenameFile(from, to), ct);

    public Task SetLastWriteAsync(string path, DateTime time, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            _client.SetLastWriteTimeUtc(path, time.ToUniversalTime());
        }
        catch (SshException)
        {
            // the server doesn't allow it: the upload time stays
        }
    }, ct);

    public void Dispose()
    {
        try
        {
            if (_client.IsConnected)
                _client.Disconnect();
        }
        catch (Exception)
        {
            // closing anyway
        }
        _client.Dispose();
    }
}

/// <summary>FTP and FTPS through FluentFTP (passive mode; TLS certificates the system doesn't trust are asked about).</summary>
internal sealed class FtpRemoteClient : IRemoteClient
{
    private readonly AsyncFtpClient _client;

    public FtpRemoteClient(RemotePath server, Credentials credentials, TrustCheck trust)
    {
        string user = server.User.Length > 0 ? server.User : "anonymous";
        string password = server.User.Length > 0 || credentials.Password.Length > 0 ? credentials.Password : "anonymous@";
        _client = new AsyncFtpClient(server.Host, user, password, server.Port);
        _client.Config.EncryptionMode = server.Protocol switch
        {
            RemoteProtocol.FtpsExplicit => FtpEncryptionMode.Explicit,
            RemoteProtocol.FtpsImplicit => FtpEncryptionMode.Implicit,
            _ => FtpEncryptionMode.None,
        };
        _client.Config.DataConnectionType = FtpDataConnectionType.AutoPassive;
        _client.Config.ConnectTimeout = 20_000;
        _client.Config.ReadTimeout = 60_000;
        string id = $"{server.Host}:{server.Port}";
        _client.ValidateCertificate += (_, e) =>
        {
            if (e.PolicyErrors == System.Net.Security.SslPolicyErrors.None)
            {
                e.Accept = true;
                return;
            }
            // self-signed and the like: the user decides, once per certificate
            string fingerprint = e.Certificate is X509Certificate certificate
                ? "SHA256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(certificate.GetRawCertData()))
                : "?";
            e.Accept = trust(id, fingerprint, "TLS certificate (" + e.PolicyErrors + ")", changed: false);
        };
    }

    public bool IsConnected => _client.IsConnected;

    public async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            await _client.Connect(ct);
        }
        catch (FluentFTP.Exceptions.FtpAuthenticationException ex)
        {
            throw new UnauthorizedAccessException(ex.Message, ex);
        }
    }

    public Task<string> GetHomeAsync(CancellationToken ct) => _client.GetWorkingDirectory(ct);

    public async Task<List<RemoteEntry>> ListAsync(string directory, CancellationToken ct)
    {
        var result = new List<RemoteEntry>();
        foreach (FtpListItem item in await _client.GetListing(directory, FtpListOption.AllFiles, ct))
        {
            if (item.Name is "." or "..")
                continue;
            bool isDirectory = item.Type == FtpObjectType.Directory;
            long size = item.Size;
            if (item.Type == FtpObjectType.Link)
            {
                try
                {
                    // a link to a directory is entered like a directory
                    isDirectory = await _client.DirectoryExists(item.FullName, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // a broken link: shown as a file
                }
            }
            result.Add(new RemoteEntry(item.Name, item.FullName, isDirectory, isDirectory ? 0 : Math.Max(0, size),
                item.Modified == DateTime.MinValue ? DateTime.Now : item.Modified, item.Type == FtpObjectType.Link));
        }
        return result;
    }

    public async Task<RemoteEntry?> StatAsync(string path, CancellationToken ct)
    {
        string name = path.TrimEnd('/');
        name = name[(name.LastIndexOf('/') + 1)..];
        if (await _client.DirectoryExists(path, ct))
            return new RemoteEntry(name, path, true, 0, DateTime.MinValue);
        if (!await _client.FileExists(path, ct))
            return null;
        long size = await _client.GetFileSize(path, -1, ct);
        DateTime time = await _client.GetModifiedTime(path, ct);
        return new RemoteEntry(name, path, false, Math.Max(0, size), time);
    }

    public async Task DownloadAsync(string path, Stream target, CancellationToken ct)
    {
        if (!await _client.DownloadStream(target, path, token: ct))
            throw new IOException($"\"{path}\" could not be downloaded.");
    }

    public async Task UploadAsync(Stream source, string path, CancellationToken ct)
    {
        FtpStatus status = await _client.UploadStream(source, path, FtpRemoteExists.Overwrite, createRemoteDir: false, token: ct);
        if (status == FtpStatus.Failed)
            throw new IOException($"\"{path}\" could not be uploaded.");
    }

    public Task DeleteFileAsync(string path, CancellationToken ct) => _client.DeleteFile(path, ct);

    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => _client.DeleteDirectory(path, ct);

    public Task CreateDirectoryAsync(string path, CancellationToken ct) => _client.CreateDirectory(path, ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => _client.Rename(from, to, ct);

    public async Task SetLastWriteAsync(string path, DateTime time, CancellationToken ct)
    {
        try
        {
            await _client.SetModifiedTime(path, time.ToUniversalTime(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // MFMT isn't supported everywhere: the upload time stays
        }
    }

    public void Dispose()
    {
        try
        {
            _client.Dispose();
        }
        catch (Exception)
        {
            // closing anyway
        }
    }
}
