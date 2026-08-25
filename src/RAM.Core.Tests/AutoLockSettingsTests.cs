using RAM.Core.Infrastructure;

namespace RAM.Core.Tests;

public class AutoLockSettingsTests
{
    private static AutoLockSettings Fresh()
    {
        var path = Path.Combine(Path.GetTempPath(), $"autolock_{Guid.NewGuid():N}.ini");
        var store = new SettingsStore(path);
        var settings = new AutoLockSettings(store);
        return settings;
    }

    [Fact]
    public void Defaults_AreSane()
    {
        var s = Fresh();

        // Opt-in, 10-minute idle timeout, both triggers on by default.
        Assert.False(s.Enabled);
        Assert.Equal(AutoLockSettings.DefaultTimeoutMinutes, s.TimeoutMinutes);
        Assert.True(s.LockOnMinimize);
        Assert.True(s.LockOnIdle);
    }

    [Fact]
    public void Values_RoundTrip_ThroughTheIniFile()
    {
        var path = Path.Combine(Path.GetTempPath(), $"autolock_{Guid.NewGuid():N}.ini");
        try
        {
            var store = new SettingsStore(path);
            var s = new AutoLockSettings(store);
            s.Enabled = true;
            s.TimeoutMinutes = 30;
            s.LockOnMinimize = false;
            s.LockOnIdle = true;

            // A fresh store over the same file sees the persisted values.
            var reloaded = new AutoLockSettings(new SettingsStore(path));
            Assert.True(reloaded.Enabled);
            Assert.Equal(30, reloaded.TimeoutMinutes);
            Assert.False(reloaded.LockOnMinimize);
            Assert.True(reloaded.LockOnIdle);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void Timeout_IsClamped_ToSafeRange()
    {
        var s = Fresh();
        s.TimeoutMinutes = 0;
        Assert.Equal(AutoLockSettings.MinTimeoutMinutes, s.TimeoutMinutes);
        s.TimeoutMinutes = 999_999;
        Assert.Equal(AutoLockSettings.MaxTimeoutMinutes, s.TimeoutMinutes);
    }

    [Theory]
    [InlineData(false, false, 600, false)] // master off → never
    [InlineData(true, false, 600, false)]  // idle trigger off → never
    [InlineData(true, true, 599, false)]   // 9:59 idle < 10 min timeout
    [InlineData(true, true, 600, true)]    // exactly the timeout
    [InlineData(true, true, 3600, true)]   // way past
    [InlineData(true, true, -5, false)]    // negative idle (clock edge) → no lock
    public void ShouldLockOnIdle_MatchesTimeoutPolicy(bool enabled, bool idleTrigger, int idleSeconds, bool expected)
    {
        var s = Fresh();
        s.Enabled = enabled;
        s.LockOnIdle = idleTrigger;
        s.TimeoutMinutes = 10;

        Assert.Equal(expected, s.ShouldLockOnIdle(idleSeconds));
    }
}
