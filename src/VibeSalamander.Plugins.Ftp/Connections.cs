// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace VibeSalamander.Plugins.Ftp;

/// <summary>A saved server (the Connections window).</summary>
public sealed class Connection
{
    public string Name { get; set; } = string.Empty;
    public RemoteProtocol Protocol { get; set; } = RemoteProtocol.Sftp;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string User { get; set; } = string.Empty;

    /// <summary>The password encrypted for this Windows user (DPAPI), base64; null when it isn't saved.</summary>
    public string? ProtectedPassword { get; set; }

    /// <summary>SFTP: a private key file (OpenSSH, PuTTY .ppk v2/v3); its passphrase is the password.</summary>
    public string? PrivateKeyFile { get; set; }

    /// <summary>The directory the panel opens; empty = the home directory.</summary>
    public string? InitialPath { get; set; }

    [JsonIgnore]
    public string DisplayName => Name.Length > 0 ? Name : Host;

    [JsonIgnore]
    public string? Password
    {
        get => Secrets.Unprotect(ProtectedPassword);
        set => ProtectedPassword = string.IsNullOrEmpty(value) ? null : Secrets.Protect(value);
    }

    /// <summary>The root path of the server ("sftp://user@host").</summary>
    public RemotePath Root => new(Protocol, User, Host.Trim(), Port, "/");

    public bool Matches(RemotePath path) =>
        Protocol == path.Protocol && Port == path.Port &&
        string.Equals(Host.Trim(), path.Host, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(User, path.User, StringComparison.Ordinal);

    public Connection Clone() => (Connection)MemberwiseClone();
}

/// <summary>Passwords encrypted for the current Windows user (DPAPI): only this user on this computer can read them.</summary>
internal static class Secrets
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("VibeSalamander.Ftp.v1");

    public static string Protect(string secret) =>
        Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));

    public static string? Unprotect(string? protectedSecret)
    {
        if (string.IsNullOrEmpty(protectedSecret))
            return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(protectedSecret), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null; // another user's or computer's file: asked again
        }
    }
}

/// <summary>The saved connections and the servers' keys / certificates the user trusts (in the plugin's data folder).</summary>
internal sealed class ConnectionStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };
    private readonly string _file;
    private readonly string _trustFile;
    private readonly object _lock = new();

    public ConnectionStore(string directory)
    {
        _file = System.IO.Path.Combine(directory, "connections.json");
        _trustFile = System.IO.Path.Combine(directory, "known-hosts.json");
        Connections = Load<List<Connection>>(_file) ?? new List<Connection>();
        _trusted = Load<Dictionary<string, string>>(_trustFile) ?? new Dictionary<string, string>();
    }

    public List<Connection> Connections { get; }

    private readonly Dictionary<string, string> _trusted;

    private static T? Load<T>(string file) where T : class
    {
        try
        {
            return File.Exists(file) ? JsonSerializer.Deserialize<T>(File.ReadAllText(file), Json) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void Save(string file, object value)
    {
        try
        {
            string temp = file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, Json));
            File.Move(temp, file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // not saved: asked again next time
        }
    }

    public void SaveConnections()
    {
        lock (_lock)
            Save(_file, Connections);
    }

    public Connection? Find(RemotePath path)
    {
        lock (_lock)
            return Connections.FirstOrDefault(c => c.Matches(path));
    }

    /// <summary>The fingerprint the user trusted for "host:port" (SSH host key, TLS certificate), or null.</summary>
    public string? TrustedKey(string server)
    {
        lock (_lock)
            return _trusted.TryGetValue(server, out string? key) ? key : null;
    }

    public void Trust(string server, string fingerprint)
    {
        lock (_lock)
        {
            _trusted[server] = fingerprint;
            Save(_trustFile, _trusted);
        }
    }
}
