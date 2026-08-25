namespace RAM.Core.Roblox.FastFlags;

/// <summary>
/// The Roblox fast-flag allow list (the only flags Roblox permits without triggering
/// anti-exploit / manipulation detection), grouped by category. Each entry carries a
/// tooltip so users can pick the right value without guessing.
/// </summary>
public static class FastFlagCatalog
{
    public static readonly IReadOnlyList<FastFlagDef> All = new List<FastFlagDef>
    {
        // ---- Geometry ----
        new()
        {
            Name = "DFIntCSGLevelOfDetailSwitchingDistance",
            Category = "Geometry",
            Type = FastFlagType.Integer,
            Min = 0, Max = 2000, Step = 50, Suggested = 400,
            Description = "Distance (studs) at which CSG parts switch to a lower level of detail. Lower = more detail closer in, at a performance cost."
        },
        new()
        {
            Name = "DFIntCSGLevelOfDetailSwitchingDistanceL12",
            Category = "Geometry",
            Type = FastFlagType.Integer,
            Min = 0, Max = 2000, Step = 50, Suggested = 400,
            Description = "LOD switch distance between CSG LOD levels 1 and 2."
        },
        new()
        {
            Name = "DFIntCSGLevelOfDetailSwitchingDistanceL23",
            Category = "Geometry",
            Type = FastFlagType.Integer,
            Min = 0, Max = 2000, Step = 50, Suggested = 400,
            Description = "LOD switch distance between CSG LOD levels 2 and 3."
        },
        new()
        {
            Name = "DFIntCSGLevelOfDetailSwitchingDistanceL34",
            Category = "Geometry",
            Type = FastFlagType.Integer,
            Min = 0, Max = 2000, Step = 50, Suggested = 400,
            Description = "LOD switch distance between CSG LOD levels 3 and 4."
        },

        // ---- Rendering ----
        new()
        {
            Name = "FFlagHandleAltEnterFullscreenManually",
            Category = "Rendering",
            Type = FastFlagType.Boolean,
            Description = "Handle Alt+Enter fullscreen toggle manually instead of relying on the OS."
        },
        new()
        {
            Name = "DFFlagTextureQualityOverrideEnabled",
            Category = "Rendering",
            Type = FastFlagType.Boolean,
            Description = "Enables a custom texture-quality override. Turn this ON and pick a level under 'Texture Quality Override'."
        },
        new()
        {
            Name = "DFIntTextureQualityOverride",
            Category = "Rendering",
            Type = FastFlagType.Integer,
            Min = 0, Max = 10, Step = 1, Suggested = 6,
            Description = "Texture quality level (0 = auto). Only applied when 'Texture Quality Override Enabled' is active. Higher = sharper textures, heavier VRAM."
        },
        new()
        {
            Name = "FIntDebugForceMSAASamples",
            Category = "Rendering",
            Type = FastFlagType.Integer,
            Min = 0, Max = 8, Step = 2, Suggested = 0,
            Description = "Forced MSAA sample count (0 = off, then 2 / 4 / 8). Higher = smoother edges, heavier GPU load."
        },
        new()
        {
            Name = "DFFlagDisableDPIScale",
            Category = "Rendering",
            Type = FastFlagType.Boolean,
            Description = "Disables DPI-based UI scaling. Can make the UI crisp on unusual display scales, at the cost of smaller elements."
        },
        new()
        {
            Name = "FFlagDebugGraphicsPreferD3D11",
            Category = "Rendering",
            Type = FastFlagType.Boolean,
            Description = "Prefer the Direct3D 11 rendering backend."
        },
        new()
        {
            Name = "FFlagDebugSkyGray",
            Category = "Rendering",
            Type = FastFlagType.Boolean,
            Description = "Render the sky as flat gray instead of the skybox (debug aid)."
        },
        new()
        {
            Name = "DFFlagDebugPauseVoxelizer",
            Category = "Rendering",
            Type = FastFlagType.Boolean,
            Description = "Pause the terrain voxelizer (debug aid; freezes terrain updates)."
        },
        new()
        {
            Name = "DFIntDebugFRMQualityLevelOverride",
            Category = "Rendering",
            Type = FastFlagType.Integer,
            Min = 0, Max = 10, Step = 1, Suggested = 0,
            Description = "Overrides the dynamic 'future rendering' quality level (0 = auto)."
        },
        new()
        {
            Name = "FIntFRMMaxGrassDistance",
            Category = "Rendering",
            Type = FastFlagType.Integer,
            Min = 0, Max = 10000, Step = 100, Suggested = 0,
            Description = "Maximum distance at which grass is rendered (future rendering)."
        },
        new()
        {
            Name = "FIntFRMMinGrassDistance",
            Category = "Rendering",
            Type = FastFlagType.Integer,
            Min = 0, Max = 10000, Step = 100, Suggested = 0,
            Description = "Minimum distance for grass rendering (future rendering)."
        },
        new()
        {
            Name = "FFlagDebugGraphicsPreferVulkan",
            Category = "Rendering",
            Type = FastFlagType.Boolean,
            Description = "Prefer the Vulkan rendering backend."
        },
        new()
        {
            Name = "FFlagDebugGraphicsPreferOpenGL",
            Category = "Rendering",
            Type = FastFlagType.Boolean,
            Description = "Prefer the OpenGL rendering backend."
        },

        // ---- User Interface ----
        new()
        {
            Name = "FIntGrassMovementReducedMotionFactor",
            Category = "User Interface",
            Type = FastFlagType.Integer,
            Min = 0, Max = 100, Step = 5, Suggested = 50,
            Description = "Percentage factor by which grass movement is reduced when 'Reduced Motion' accessibility is enabled."
        }
    };

    public static IReadOnlyList<string> Categories => new[] { "Geometry", "Rendering", "User Interface" };

    public static IEnumerable<FastFlagDef> InCategory(string category)
        => All.Where(f => string.Equals(f.Category, category, StringComparison.Ordinal));
}
