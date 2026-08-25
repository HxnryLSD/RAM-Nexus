namespace RAM.Core.Roblox.FastFlags;

/// <summary>
/// Metadata for one allowed fast flag: which category it belongs to, its value kind,
/// a user-facing description (tooltip) and, for integer flags, a sensible editing range.
/// </summary>
public sealed record FastFlagDef
{
    public required string Name { get; init; }
    public required string Category { get; init; }
    public required FastFlagType Type { get; init; }

    /// <summary>Human-readable guidance shown in the tooltip.</summary>
    public required string Description { get; init; }

    // Integer-flag editor bounds / step.
    public int Min { get; init; }
    public int Max { get; init; }
    public int Step { get; init; } = 1;

    /// <summary>Suggested initial value shown when the flag is activated.</summary>
    public int Suggested { get; init; }

    public bool IsBoolean => Type == FastFlagType.Boolean;
}
