// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using VibeSalamander.Core.Settings;
using VibeSalamander.Core.Updates;

namespace VibeSalamander.Services;

/// <summary>
/// Asks GitHub for the newest release, downloads its installer or zip (checking its SHA-256 when GitHub gives it),
/// and hands over to it: the installer runs silently and starts the program again; a portable copy's files are
/// replaced by a small script once the program has closed (the Data folder stays).
/// </summary>
internal static class Updater
{
    // (first: the client below uses it, and static fields are set in the order they're written)
    /// <summary>This program's version ("0.7.0").</summary>
    public static string CurrentVersion { get; } =
        typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0] is { Length: > 0 } v ? v : "0.0.0";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // GitHub wants a user agent; one that can't be read is left out rather than failing the check
        client.DefaultRequestHeaders.UserAgent.TryParseAdd($"VibeSalamander/{CurrentVersion}");
        client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return client;
    }

    public static InstallKind Kind => UpdateCheck.KindOf(AppPaths.MainFolder, AppPaths.IsInstalled, AppPaths.IsPortable);

    public static Architecture Architecture => RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? Architecture.Arm64 : Architecture.X64;

    /// <summary>The newest release (null when GitHub's answer isn't one). Network problems throw HttpRequestException.</summary>
    public static async Task<ReleaseInfo?> LatestAsync(CancellationToken ct)
    {
        string json = await Http.GetStringAsync(UpdateCheck.LatestReleaseApi, ct);
        return UpdateCheck.Parse(json);
    }

    /// <summary>Downloads a release file into the temporary folder, reporting 0..1; checks its size and SHA-256.</summary>
    public static async Task<string> DownloadAsync(ReleaseAsset asset, IProgress<double> progress, CancellationToken ct)
    {
        string folder = Path.Combine(Path.GetTempPath(), "VibeSalamander", "Update");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, asset.Name);
        using (HttpResponseMessage response = await Http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? asset.Size;
            await using Stream source = await response.Content.ReadAsStreamAsync(ct);
            await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            byte[] buffer = new byte[1 << 16];
            long done = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                if (total > 0)
                    progress.Report(Math.Min(1, (double)done / total));
            }
        }
        long size = new FileInfo(path).Length;
        if (asset.Size > 0 && size != asset.Size)
            throw new IOException(Loc.F("The download is incomplete ({0} of {1} bytes).", size, asset.Size));
        if (asset.Sha256 != null)
        {
            string hash;
            await using (var file = File.OpenRead(path))
                hash = Convert.ToHexString(await SHA256.HashDataAsync(file, ct)).ToLowerInvariant();
            if (hash != asset.Sha256)
            {
                File.Delete(path);
                throw new IOException(Loc.T("The download doesn't match the release (its SHA-256 differs)."));
            }
        }
        return path;
    }

    /// <summary>
    /// The installer, silently, over this installation; it closes what's still open of the program and starts it again
    /// (Windows asks for permission first when it's installed for all users).
    /// </summary>
    public static void StartInstaller(string setup) =>
        Process.Start(new ProcessStartInfo(setup)
        {
            UseShellExecute = true,
            ArgumentList = { "/SILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/CLOSEAPPLICATIONS", "/relaunch=1" },
        })?.Dispose();

    /// <summary>
    /// A portable copy: unpacks the zip, then a hidden PowerShell script waits for this process to end, copies the new
    /// files over the folder (Data, portable.flag and anything else of the user's stay) and starts the program again.
    /// </summary>
    public static void StartPortableUpdate(string zip)
    {
        string staging = Path.Combine(Path.GetTempPath(), "VibeSalamander", "Update", "files");
        if (Directory.Exists(staging))
            Directory.Delete(staging, recursive: true);
        ZipFile.ExtractToDirectory(zip, staging);
        // the zip has one folder (VibeSalamander\...)
        string from = Directory.Exists(Path.Combine(staging, "VibeSalamander")) ? Path.Combine(staging, "VibeSalamander") : staging;
        if (!File.Exists(Path.Combine(from, "VibeSalamander.exe")))
            throw new IOException(Loc.T("The downloaded zip isn't a Vibe Salamander release."));
        string to = AppPaths.MainFolder;
        string script = Path.Combine(Path.GetTempPath(), "VibeSalamander", "Update", "update.ps1");
        File.WriteAllText(script, """
            param([int]$ProcessId, [string]$From, [string]$To)
            Wait-Process -Id $ProcessId -Timeout 120 -ErrorAction SilentlyContinue
            Start-Sleep -Seconds 1
            robocopy $From $To /E /R:10 /W:1 /XD Data /NFL /NDL /NJH /NJS /NP | Out-Null
            if ($LASTEXITCODE -lt 8) { Start-Process (Join-Path $To 'VibeSalamander.exe') }
            else { Start-Process (Join-Path $To 'VibeSalamander.exe') -ArgumentList '--update-failed' }
            """);
        string powershell = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
        Process.Start(new ProcessStartInfo(powershell)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            ArgumentList =
            {
                "-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script,
                "-ProcessId", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-From", from, "-To", to,
            },
        })?.Dispose();
    }

    /// <summary>The main folder can be written to (a portable copy in Program Files can't update itself).</summary>
    public static bool CanWriteMainFolder()
    {
        try
        {
            string probe = Path.Combine(AppPaths.MainFolder, ".update-check");
            File.WriteAllText(probe, string.Empty);
            File.Delete(probe);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
