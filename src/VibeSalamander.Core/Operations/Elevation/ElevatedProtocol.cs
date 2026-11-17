// SPDX-FileCopyrightText: 2026 Vibe Salamander Authors
// SPDX-License-Identifier: GPL-3.0-or-later

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using VibeSalamander.Core.Localization;

namespace VibeSalamander.Core.Operations.Elevation;

/// <summary>
/// What the program and the administrator helper say to each other: one JSON object per line. The program asks for
/// one change at a time; the helper answers "progress" (a copy) and then "done" or "failed".
/// </summary>
internal static class ElevatedProtocol
{
    /// <summary>Changes when the messages change: a helper of another version is refused.</summary>
    public const int Version = 1;

    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingDefault,
    };

    public static readonly UTF8Encoding Utf8 = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>The longest line accepted: a request has at most two paths.</summary>
    public const int MaxLine = 256 * 1024;

    /// <summary>Reads one line, or null at the end of the stream (or a line far too long to be a message).</summary>
    public static string? ReadLine(StreamReader reader)
    {
        var line = new StringBuilder();
        while (true)
        {
            int c = reader.Read();
            if (c < 0)
                return line.Length == 0 ? null : line.ToString();
            if (c == '\n')
                return line.ToString();
            if (line.Length >= MaxLine)
                return null;
            line.Append((char)c);
        }
    }
}

/// <summary>A change to make ("copy", "mkdir", "delete", "rmdir", "move", "attributes", "time"), or "cancel" the current one.</summary>
internal sealed record ElevatedRequest
{
    public int Id { get; init; }
    public string Op { get; init; } = string.Empty;
    public string? Path { get; init; }
    public string? Target { get; init; }
    public bool Overwrite { get; init; }
    public bool Directory { get; init; }
    public int Attributes { get; init; }
    public long Time { get; init; }
}

/// <summary>"hello" (the helper is ready), "progress", "done" or "failed".</summary>
internal sealed record ElevatedReply
{
    public int Id { get; init; }
    public string Kind { get; init; } = string.Empty;
    public int Version { get; init; }
    public long Done { get; init; }
    public long Total { get; init; }
    public string? Error { get; init; }
    public int Code { get; init; }
    public bool Denied { get; init; }
}

/// <summary>
/// The helper's side: does the changes it is asked for, one at a time, and nothing else. Every path must be a full
/// path to a drive or a share (no relative paths, no devices); attributes are limited to the ones a copy sets.
/// </summary>
public static class ElevatedServer
{
    /// <summary>The attributes a program can set (the others, such as Directory or Compressed, are ignored).</summary>
    private const FileAttributes SettableAttributes = FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System |
                                                      FileAttributes.Archive | FileAttributes.Normal | FileAttributes.Temporary |
                                                      FileAttributes.Offline | FileAttributes.NotContentIndexed;

    /// <summary>Serves requests from <paramref name="stream"/> until it ends.</summary>
    public static void Run(Stream stream)
    {
        using var reader = new StreamReader(stream, ElevatedProtocol.Utf8, false, 4096, leaveOpen: true);
        using var writer = new StreamWriter(stream, ElevatedProtocol.Utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
        var writeLock = new object();
        void Send(ElevatedReply reply)
        {
            lock (writeLock)
                writer.WriteLine(JsonSerializer.Serialize(reply, ElevatedProtocol.Json));
        }

        Send(new ElevatedReply { Kind = "hello", Version = ElevatedProtocol.Version });
        Task current = Task.CompletedTask;
        int cancelId = -1;
        while (ElevatedProtocol.ReadLine(reader) is { } line)
        {
            ElevatedRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<ElevatedRequest>(line, ElevatedProtocol.Json);
            }
            catch (JsonException)
            {
                break; // not a message from the program: the helper ends
            }
            if (request == null)
                break;
            if (request.Op == "cancel")
            {
                Volatile.Write(ref cancelId, request.Id);
                continue;
            }
            current.Wait();
            ElevatedRequest work = request;
            // the change runs beside the reading, so a "cancel" for it is read while it runs
            current = Task.Run(() =>
            {
                try
                {
                    Send(Execute(work, () => Volatile.Read(ref cancelId) == work.Id, Send));
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                    // the program is gone: nobody to answer
                }
            });
        }
        current.Wait(TimeSpan.FromSeconds(5));
    }

    private static ElevatedReply Execute(ElevatedRequest r, Func<bool> cancelled, Action<ElevatedReply> send)
    {
        try
        {
            IFileOps ops = LocalFileOps.Instance;
            switch (r.Op)
            {
                case "copy":
                    long last = 0;
                    ops.CopyFile(CheckPath(r.Path), CheckPath(r.Target), r.Overwrite, (total, done) =>
                    {
                        if (done - last >= 1 << 20 || done == total)
                        {
                            last = done;
                            try
                            {
                                send(new ElevatedReply { Id = r.Id, Kind = "progress", Done = done, Total = total });
                            }
                            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                            {
                                return false; // the program is gone: the copy stops (no exception through Windows' copy)
                            }
                        }
                        return !cancelled();
                    });
                    break;
                case "mkdir":
                    ops.CreateDirectory(CheckPath(r.Path));
                    break;
                case "delete":
                    ops.DeleteFile(CheckPath(r.Path));
                    break;
                case "rmdir":
                    ops.DeleteDirectory(CheckPath(r.Path));
                    break;
                case "move":
                    ops.Move(CheckPath(r.Path), CheckPath(r.Target), r.Directory);
                    break;
                case "attributes":
                    var attributes = (FileAttributes)r.Attributes & SettableAttributes;
                    ops.SetAttributes(CheckPath(r.Path), attributes == 0 ? FileAttributes.Normal : attributes);
                    break;
                case "time":
                    ops.SetLastWriteTime(CheckPath(r.Path), DateTime.FromFileTimeUtc(r.Time).ToLocalTime(), r.Directory);
                    break;
                default:
                    throw new ArgumentException("Not something the helper does: " + r.Op);
            }
            return new ElevatedReply { Id = r.Id, Kind = "done" };
        }
        catch (OperationCanceledException)
        {
            return new ElevatedReply { Id = r.Id, Kind = "failed", Code = LocalFileOps.ERROR_REQUEST_ABORTED, Error = "Cancelled." };
        }
        catch (Exception ex)
        {
            return new ElevatedReply
            {
                Id = r.Id, Kind = "failed", Error = ex.Message, Code = ElevationRules.ErrorCode(ex), Denied = ElevationRules.IsAccessDenied(ex),
            };
        }
    }

    /// <summary>A full path to a drive (C:\...) or a share (\\server\share\...), also with the \\?\ prefix; nothing else.</summary>
    internal static string CheckPath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Contains('\0'))
            throw new ArgumentException("No path.");
        string rest = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
        bool drive = rest.Length >= 3 && char.IsAsciiLetter(rest[0]) && rest[1] == ':' && rest[2] == '\\';
        bool share = path.StartsWith(@"\\?\", StringComparison.Ordinal)
            ? rest.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase) && rest.Length > 4
            : rest.StartsWith(@"\\", StringComparison.Ordinal) && rest.Length > 2 && rest[2] is not ('.' or '?' or '\\');
        if (!drive && !share)
            throw new ArgumentException("Only full paths to a drive or a share: " + path);
        return path;
    }
}

/// <summary>The program's side: sends the changes to the helper and waits for each to be done.</summary>
internal class ElevatedClient : IElevatedSession
{
    private readonly Stream _stream;
    private readonly StreamReader _reader;
    private readonly StreamWriter _writer;
    private readonly object _lock = new();
    private int _nextId;
    private bool _disposed;

    public ElevatedClient(Stream stream)
    {
        _stream = stream;
        _reader = new StreamReader(stream, ElevatedProtocol.Utf8, false, 4096, leaveOpen: true);
        _writer = new StreamWriter(stream, ElevatedProtocol.Utf8, 4096, leaveOpen: true) { AutoFlush = true, NewLine = "\n" };
    }

    /// <summary>Waits for the helper's hello; fails when it is another version or doesn't answer.</summary>
    public void WaitForHello()
    {
        ElevatedReply hello = Read();
        if (hello.Kind != "hello" || hello.Version != ElevatedProtocol.Version)
            throw new IOException(L.T("The administrator helper doesn't belong to this version of Vibe Salamander."));
    }

    public void CopyFile(string source, string target, bool overwrite, Func<long, long, bool> progress) =>
        Call(new ElevatedRequest { Op = "copy", Path = source, Target = target, Overwrite = overwrite }, progress);

    public void CreateDirectory(string path) => Call(new ElevatedRequest { Op = "mkdir", Path = path });

    public void DeleteFile(string path) => Call(new ElevatedRequest { Op = "delete", Path = path });

    public void DeleteDirectory(string path) => Call(new ElevatedRequest { Op = "rmdir", Path = path });

    public void Move(string source, string target, bool isDirectory) =>
        Call(new ElevatedRequest { Op = "move", Path = source, Target = target, Directory = isDirectory });

    public void SetAttributes(string path, FileAttributes attributes) =>
        Call(new ElevatedRequest { Op = "attributes", Path = path, Attributes = (int)attributes });

    public void SetLastWriteTime(string path, DateTime time, bool isDirectory) =>
        Call(new ElevatedRequest { Op = "time", Path = path, Time = time.ToFileTimeUtc(), Directory = isDirectory });

    private void Call(ElevatedRequest request, Func<long, long, bool>? progress = null)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            int id = ++_nextId;
            Write(request with { Id = id });
            bool cancelSent = false;
            while (true)
            {
                ElevatedReply reply = Read();
                if (reply.Id != id)
                    continue; // an answer to a request that was given up (can't happen with one at a time)
                switch (reply.Kind)
                {
                    case "progress":
                        if (progress != null && !progress(reply.Total, reply.Done) && !cancelSent)
                        {
                            cancelSent = true;
                            Write(new ElevatedRequest { Id = id, Op = "cancel" });
                        }
                        continue;
                    case "done":
                        if (cancelSent)
                            throw new OperationCanceledException(); // finished before the cancel arrived: still, the caller stopped
                        return;
                    case "failed":
                        if (reply.Code == LocalFileOps.ERROR_REQUEST_ABORTED && cancelSent)
                            throw new OperationCanceledException();
                        string message = reply.Error ?? L.T("The administrator helper couldn't do it.");
                        if (reply.Denied)
                            throw new UnauthorizedAccessException(message);
                        throw new IOException(message, reply.Code == 0 ? -2147467259 /* E_FAIL */ : unchecked((int)0x80070000) | reply.Code);
                    default:
                        throw new IOException(L.T("The administrator helper answered something it shouldn't."));
                }
            }
        }
    }

    private void Write(ElevatedRequest request)
    {
        try
        {
            _writer.WriteLine(JsonSerializer.Serialize(request, ElevatedProtocol.Json));
        }
        catch (IOException ex)
        {
            throw new IOException(L.T("The administrator helper has ended."), ex);
        }
    }

    private ElevatedReply Read()
    {
        string? line;
        try
        {
            line = ElevatedProtocol.ReadLine(_reader);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            throw new IOException(L.T("The administrator helper has ended."), ex);
        }
        if (line == null)
            throw new IOException(L.T("The administrator helper has ended."));
        try
        {
            return JsonSerializer.Deserialize<ElevatedReply>(line, ElevatedProtocol.Json)
                   ?? throw new IOException(L.T("The administrator helper answered something it shouldn't."));
        }
        catch (JsonException ex)
        {
            throw new IOException(L.T("The administrator helper answered something it shouldn't."), ex);
        }
    }

    public virtual void Dispose()
    {
        lock (_lock)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        // closing the stream ends the helper
        _writer.Dispose();
        _reader.Dispose();
        _stream.Dispose();
    }
}
