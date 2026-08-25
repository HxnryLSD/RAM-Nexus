namespace RAM.Core.Roblox.Rdd;

/// <summary>
/// A snapshot of the current and previous Windows Roblox deployment versions,
/// as returned by the RDD /api/versions/current and /api/versions/past endpoints.
/// </summary>
public sealed record RddVersionSnapshot
{
    public string? WindowsCurrent { get; init; }
    public string? WindowsPrevious { get; init; }
}
