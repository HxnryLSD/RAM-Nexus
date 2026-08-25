namespace RAM.Core.Roblox.Rdd;

/// <summary>What an RDD install is currently doing.</summary>
public enum InstallPhase
{
    Resolving,
    Downloading,
    Extracting,
    Done
}

/// <summary>
/// Progress for one RDD install. During <see cref="InstallPhase.Downloading"/>,
/// BytesDone/BytesTotal describe the whole download (cumulative across files); during
/// <see cref="InstallPhase.Extracting"/> they describe the current zip being extracted.
/// </summary>
public sealed record InstallProgress(InstallPhase Phase, string FileName, long BytesDone, long BytesTotal, string Message);

/// <summary>How an install attempt ended.</summary>
public enum InstallResultKind
{
    Installed,
    Skipped,
    Cancelled,
    Failed
}

/// <summary>Outcome of an install attempt. <see cref="VersionFolder"/> is set when Installed.</summary>
public sealed record InstallResult(InstallResultKind Kind, string? VersionFolder, string? Message);
