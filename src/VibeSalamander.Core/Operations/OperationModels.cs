namespace VibeSalamander.Core.Operations;

public enum OperationKind
{
    Copy,
    Move,
    Delete,
}

/// <summary>Answer to "target already exists" (Salamander's overwrite confirmation).</summary>
public enum ConflictChoice
{
    Overwrite,
    OverwriteAll,
    /// <summary>Overwrite this and all following targets when they are older than the source.</summary>
    OverwriteAllOlder,
    Skip,
    SkipAll,
    /// <summary>Keep both: the copy gets a free name like "name (2).ext".</summary>
    Rename,
    RenameAll,
    Cancel,
}

/// <summary>Answer to an I/O error on one item.</summary>
public enum ErrorChoice
{
    Retry,
    Skip,
    SkipAll,
    Cancel,
}

public sealed record ConflictInfo(
    string SourcePath, long SourceSize, DateTime SourceTime,
    string TargetPath, long TargetSize, DateTime TargetTime,
    bool IsDirectory, FileAttributes TargetAttributes = 0, bool IsMerge = false);

/// <summary>Confirmations and options the engine follows (Configuration → Confirmations, General).</summary>
public sealed class EngineOptions
{
    /// <summary>Ask before overwriting a file (off: overwrite without asking).</summary>
    public bool ConfirmFileOverwrite { get; init; } = true;

    /// <summary>Ask before overwriting a hidden or system file, even when overwrites don't ask.</summary>
    public bool ConfirmHiddenSystemOverwrite { get; init; } = true;

    /// <summary>Ask before copying a directory into an existing directory of the same name.</summary>
    public bool ConfirmDirectoryMerge { get; init; }

    /// <summary>Copies from CD/DVD drop the read-only attribute.</summary>
    public bool ClearReadOnlyFromCd { get; init; } = true;
}

public sealed record OperationError(string Path, string Message);

/// <summary>Callbacks the engine uses to ask the user. Called from a background thread; implementations marshal to the UI.</summary>
public interface IOperationCallbacks
{
    Task<ConflictChoice> ResolveConflictAsync(ConflictInfo conflict);
    Task<ErrorChoice> ResolveErrorAsync(OperationError error);
}

/// <summary>Snapshot of a running operation for the progress window.</summary>
public sealed class OperationProgress
{
    public OperationKind Kind { get; init; }
    public bool Analyzing { get; init; }
    public string CurrentSource { get; init; } = string.Empty;
    public string CurrentTarget { get; init; } = string.Empty;
    public long CurrentBytesDone { get; init; }
    public long CurrentBytesTotal { get; init; }
    public long TotalBytesDone { get; init; }
    public long TotalBytes { get; init; }
    public int ItemsDone { get; init; }
    public int ItemsTotal { get; init; }
    public double BytesPerSecond { get; init; }
    public TimeSpan? Remaining { get; init; }

    public double CurrentFraction => CurrentBytesTotal > 0 ? (double)CurrentBytesDone / CurrentBytesTotal : 0;

    /// <summary>Overall progress 0–1 (by bytes for copy/move, by items for delete).</summary>
    public double TotalFraction =>
        Kind == OperationKind.Delete || TotalBytes == 0
            ? (ItemsTotal > 0 ? (double)ItemsDone / ItemsTotal : 0)
            : (double)TotalBytesDone / TotalBytes;
}

public sealed class OperationResult
{
    public int Succeeded { get; set; }
    public int Skipped { get; set; }
    public int Failed { get; set; }
    public bool Cancelled { get; set; }

    /// <summary>Names of the top-level target items created (to focus them in the panel).</summary>
    public List<string> CreatedTopLevelNames { get; } = new();
}
