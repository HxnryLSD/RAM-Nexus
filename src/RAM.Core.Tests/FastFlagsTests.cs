using Newtonsoft.Json.Linq;
using RAM.Core.Roblox;
using RAM.Core.Roblox.FastFlags;

namespace RAM.Core.Tests;

public class FastFlagStoreTests
{
    [Fact]
    public void ActivatePersistAndRemove()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ff_{Guid.NewGuid():N}.json");
        try
        {
            var store = new FastFlagStore(path);
            Assert.False(store.IsActivated("DFIntTextureQualityOverride"));
            Assert.Null(store.Get("DFIntTextureQualityOverride"));

            store.Set("DFIntTextureQualityOverride", "6");
            store.Set("FFlagDebugSkyGray", "true");

            var reloaded = new FastFlagStore(path);
            Assert.True(reloaded.IsActivated("DFIntTextureQualityOverride"));
            Assert.Equal("6", reloaded.Get("DFIntTextureQualityOverride"));
            Assert.Equal("true", reloaded.Get("FFlagDebugSkyGray"));
            Assert.Equal(2, reloaded.GetActivated().Count);

            reloaded.Remove("DFIntTextureQualityOverride");
            var reloaded2 = new FastFlagStore(path);
            Assert.False(reloaded2.IsActivated("DFIntTextureQualityOverride"));
            Assert.Single(reloaded2.GetActivated());
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class FastFlagCatalogTests
{
    [Fact]
    public void ContainsExpectedFlags()
    {
        // Allows list should only contain the spec'd fast flags.
        var names = FastFlagCatalog.All.Select(f => f.Name).ToHashSet();

        Assert.Contains("DFIntCSGLevelOfDetailSwitchingDistance", names);
        Assert.Contains("FFlagHandleAltEnterFullscreenManually", names);
        Assert.Contains("DFFlagTextureQualityOverrideEnabled", names);
        Assert.Contains("DFIntTextureQualityOverride", names);
        Assert.Contains("FIntDebugForceMSAASamples", names);
        Assert.Contains("FIntGrassMovementReducedMotionFactor", names);

        Assert.Equal(4, FastFlagCatalog.InCategory("Geometry").Count());
        Assert.Single(FastFlagCatalog.InCategory("User Interface"));
    }

    [Fact]
    public void EveryFlagHasDescriptionAndCategory()
    {
        foreach (var f in FastFlagCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(f.Description), $"{f.Name} missing description");
            Assert.False(string.IsNullOrWhiteSpace(f.Category), $"{f.Name} missing category");
            Assert.Contains(f.Category, FastFlagCatalog.Categories);
        }
    }
}

public class ClientSettingsPatcherFastFlagTests
{
    private static string MakeFakeInstall()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"version-ff{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "RobloxPlayerBeta.exe"), "fake");
        return dir;
    }

    [Fact]
    public void MergesFastFlagsAndPreservesFps()
    {
        var dir = MakeFakeInstall();
        try
        {
            var flags = new Dictionary<string, string?>
            {
                ["DFIntTextureQualityOverride"] = "6",
                ["FFlagDebugSkyGray"] = "true",
                ["FFlagHandleAltEnterFullscreenManually"] = "false"
            };

            Assert.True(ClientSettingsPatcher.PatchSettings(dir, unlockFps: true, maxFps: 300, fastFlags: flags));

            var file = Path.Combine(dir, "ClientSettings", ClientSettingsPatcher.SettingsFileName);
            var json = JObject.Parse(File.ReadAllText(file));

            Assert.Equal(300, json["DFIntTaskSchedulerTargetFps"]!.Value<int>());
            Assert.Equal(6, json["DFIntTextureQualityOverride"]!.Value<int>());
            Assert.True(json["FFlagDebugSkyGray"]!.Value<bool>());
            Assert.False(json["FFlagHandleAltEnterFullscreenManually"]!.Value<bool>());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void WritesNothingWhenNoFlagsAndNoFps()
    {
        var dir = MakeFakeInstall();
        try
        {
            Assert.True(ClientSettingsPatcher.PatchSettings(dir, unlockFps: false, fastFlags: null));
            Assert.False(File.Exists(Path.Combine(dir, "ClientSettings", ClientSettingsPatcher.SettingsFileName)));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RemovesSettingsFileWhenAllFlagsTurnedOff()
    {
        var dir = MakeFakeInstall();
        try
        {
            var flags = new Dictionary<string, string?> { ["FFlagDebugSkyGray"] = "true" };
            Assert.True(ClientSettingsPatcher.PatchSettings(dir, unlockFps: false, fastFlags: flags));
            var file = Path.Combine(dir, "ClientSettings", ClientSettingsPatcher.SettingsFileName);
            Assert.True(File.Exists(file));

            // Turning the last flag off must not leave a stale file behind,
            // or Roblox would keep applying the flag.
            Assert.True(ClientSettingsPatcher.PatchSettings(dir, unlockFps: false, fastFlags: null));
            Assert.False(File.Exists(file));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void RemovesOnlyTheFlagThatWasTurnedOff()
    {
        var dir = MakeFakeInstall();
        try
        {
            var flags = new Dictionary<string, string?>
            {
                ["FFlagDebugSkyGray"] = "true",
                ["DFIntTextureQualityOverride"] = "6"
            };
            Assert.True(ClientSettingsPatcher.PatchSettings(dir, unlockFps: false, fastFlags: flags));

            // Simulate the user turning off SkyGray but keeping TextureQualityOverride.
            var remaining = new Dictionary<string, string?> { ["DFIntTextureQualityOverride"] = "6" };
            Assert.True(ClientSettingsPatcher.PatchSettings(dir, unlockFps: false, fastFlags: remaining));

            var file = Path.Combine(dir, "ClientSettings", ClientSettingsPatcher.SettingsFileName);
            var json = JObject.Parse(File.ReadAllText(file));
            Assert.Null(json["FFlagDebugSkyGray"]);              // stale flag must be gone
            Assert.Equal(6, json["DFIntTextureQualityOverride"]!.Value<int>());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PreservesRobloxOwnKeysWhileCleaningFlags()
    {
        var dir = MakeFakeInstall();
        try
        {
            var flags = new Dictionary<string, string?> { ["FFlagDebugSkyGray"] = "true" };
            var file = Path.Combine(dir, "ClientSettings", ClientSettingsPatcher.SettingsFileName);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.WriteAllText(file, "{\"FFlagDebugSkyGray\":true,\"SomeRobloxOwnSetting\":42}");

            // User turns SkyGray off; Roblox's own key must survive.
            Assert.True(ClientSettingsPatcher.PatchSettings(dir, unlockFps: false, fastFlags: null));

            var json = JObject.Parse(File.ReadAllText(file));
            Assert.Null(json["FFlagDebugSkyGray"]);
            Assert.Equal(42, json["SomeRobloxOwnSetting"]!.Value<int>());
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }
}
