using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using RAM.Core;
using RAM.Core.Infrastructure;
using RAM.Core.Models;
using RAM.Core.Roblox.FastFlags;
using RAM.Core.Security;

namespace RAM.Core.Tests;

public class CryptographyTests
{
    [Fact]
    public void Argon2idRoundTrip_Works()
    {
        byte[] encrypted = Cryptography.EncryptWithPassword("hello-secret", "correct horse battery staple");

        // v2 layout: header + explicit version byte 2, then salt/nonce/tag/ciphertext.
        Assert.Equal(2, encrypted[Cryptography.RAMHeader.Length]);

        Assert.Equal("hello-secret", Cryptography.DecryptWithPassword(encrypted, "correct horse battery staple"));
    }

    [Fact]
    public void LegacyPbkdf2File_StillDecrypts()
    {
        // Simulate a pre-Argon2id v1 file: header | salt | nonce | tag | ciphertext (PBKDF2).
        const string content = "{\"legacy\":true}";
        byte[] plain = Encoding.UTF8.GetBytes(content);
        byte[] salt = RandomNumberGenerator.GetBytes(16);
        if (salt[0] == 2) salt[0] = 0; // avoid the 1-in-256 v1/v2 detection collision → keep the test deterministic
        byte[] nonce = RandomNumberGenerator.GetBytes(12);
        byte[] key = Rfc2898DeriveBytes.Pbkdf2("hunter2", salt, 200_000, HashAlgorithmName.SHA256, 32);
        byte[] ciphertext = new byte[plain.Length];
        byte[] tag = new byte[16];
        using (var aes = new AesGcm(key, 16))
            aes.Encrypt(nonce, plain, ciphertext, tag);

        using var ms = new MemoryStream();
        ms.Write(Cryptography.RAMHeader, 0, Cryptography.RAMHeader.Length);
        ms.Write(salt, 0, salt.Length);
        ms.Write(nonce, 0, nonce.Length);
        ms.Write(tag, 0, tag.Length);
        ms.Write(ciphertext, 0, ciphertext.Length);

        Assert.Equal(content, Cryptography.DecryptWithPassword(ms.ToArray(), "hunter2"));
    }

    [Fact]
    public void WrongPassword_ThrowsCryptographicException()
    {
        byte[] encrypted = Cryptography.EncryptWithPassword("secret", "right-password");
        // A wrong password yields a tag mismatch — AuthenticationTagMismatchException derives
        // from CryptographicException, so ThrowsAny (not Throws, which needs the exact type).
        Assert.ThrowsAny<CryptographicException>(() => Cryptography.DecryptWithPassword(encrypted, "wrong-password"));
    }

    [Fact]
    public void TamperedCiphertext_ThrowsCryptographicException()
    {
        byte[] encrypted = Cryptography.EncryptWithPassword("secret", "pw");
        encrypted[^1] ^= 0xFF; // flip one ciphertext byte → tag mismatch, not garbage data
        Assert.ThrowsAny<CryptographicException>(() => Cryptography.DecryptWithPassword(encrypted, "pw"));
    }

    [Fact]
    public void TruncatedPayload_ThrowsCryptographicException_NotIndexError()
    {
        var truncated = new byte[Cryptography.RAMHeader.Length + 10];
        Array.Copy(Cryptography.RAMHeader, truncated, Cryptography.RAMHeader.Length);
        Assert.Throws<CryptographicException>(() => Cryptography.DecryptWithPassword(truncated, "pw"));
    }
}

public class IniFileTests
{
    [Fact]
    public void RoundTrip_PreservesSectionsAndProperties()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ini_{Guid.NewGuid():N}.ini");
        try
        {
            var ini = new IniFile();
            var general = ini.Section("General");
            general.Set("UnlockFPS", "true");
            general.Set("MaxFPSValue", "240");
            ini.Section("Prompts").Set("remembered", "false");
            ini.Save(path);

            var loaded = new IniFile(path);
            Assert.True(loaded.Section("General").Get<bool>("UnlockFPS"));
            Assert.Equal(240, loaded.Section("General").Get<int>("MaxFPSValue"));
            Assert.False(loaded.Section("Prompts").Get<bool>("remembered"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MissingProperty_ReturnsDefault()
    {
        var ini = new IniFile();
        Assert.False(ini.Section("General").Exists("Nope"));
        Assert.Equal(0, ini.Section("General").Get<int>("Nope"));
    }

    [Fact]
    public void UnconvertibleValue_ReturnsDefault_InsteadOfThrowing()
    {
        // A hand-edited INI with garbage in a typed field must not crash app startup.
        var ini = new IniFile();
        ini.Section("General").Set("UnlockFPS", "not-a-bool");
        ini.Section("General").Set("MaxFPSValue", "abc");

        Assert.False(ini.Section("General").Get<bool>("UnlockFPS"));
        Assert.Equal(0, ini.Section("General").Get<int>("MaxFPSValue"));
    }

    [Fact]
    public void Save_IsAtomic_LeavesNoTmpResidue_AndOverwritesCleanly()
    {
        var path = Path.Combine(Path.GetTempPath(), $"ini_{Guid.NewGuid():N}.ini");
        try
        {
            var ini = new IniFile();
            ini.Section("General").Set("UnlockFPS", "true");
            ini.Save(path);

            Assert.True(File.Exists(path));
            // The write goes through a .tmp sibling renamed into place (AccountStore's
            // pattern); a crash mid-write can then never truncate the real file. The tmp
            // must not survive the save.
            Assert.False(File.Exists(path + ".tmp"));

            // Overwriting an existing file gets the same guarantees and no stale tmp.
            ini.Section("General").Set("MaxFPSValue", "360");
            ini.Save(path);

            var reloaded = new IniFile(path);
            Assert.True(reloaded.Section("General").Get<bool>("UnlockFPS"));
            Assert.Equal(360, reloaded.Section("General").Get<int>("MaxFPSValue"));
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}

public class SettingsStoreTests
{
    [Fact]
    public void GetWithFallback_Works()
    {
        var path = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.ini");
        try
        {
            var store = new SettingsStore(path);
            store.Set("UnlockFPS", "true");
            store.Save();

            var reloaded = new SettingsStore(path);
            Assert.True(reloaded.Get("UnlockFPS", false));
            Assert.Equal(42, reloaded.Get("MissingKey", 42));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DefaultPath_IsAnchoredToAppDataDataRoot()
    {
        // Data files live in the user's LocalAppData data root regardless of the working
        // directory or install location, otherwise accounts/settings would "disappear" when
        // the app is launched from another CWD or reinstalled elsewhere.
        Assert.Equal(AppPaths.AccountData, new AccountStore().FilePath);
        Assert.Equal(AppPaths.FastFlags, new FastFlagStore().FilePath);
    }

    [Fact]
    public void MigrateLegacyFiles_MovesExeFolderData_WithoutOverwriting()
    {
        string root = Path.Combine(Path.GetTempPath(), "ram-paths-" + Guid.NewGuid().ToString("N"));
        string legacy = Path.Combine(root, "legacy");
        Directory.CreateDirectory(legacy);
        File.WriteAllText(Path.Combine(legacy, "AccountData.json"), "vault");
        File.WriteAllText(Path.Combine(legacy, "RAMSettings.ini"), "ini");
        Directory.CreateDirectory(Path.Combine(legacy, "RDD"));

        try
        {
            AppPaths.MigrateLegacyFiles(root, legacy);

            Assert.Equal("vault", File.ReadAllText(Path.Combine(root, "AccountData.json")));
            Assert.Equal("ini", File.ReadAllText(Path.Combine(root, "RAMSettings.ini")));
            Assert.True(Directory.Exists(Path.Combine(root, "RDD")));
            Assert.False(File.Exists(Path.Combine(legacy, "AccountData.json")));

            // Existing data wins: a newer legacy file must never overwrite what's in the root.
            File.WriteAllText(Path.Combine(legacy, "AccountData.json"), "newer");
            AppPaths.MigrateLegacyFiles(root, legacy);
            Assert.Equal("vault", File.ReadAllText(Path.Combine(root, "AccountData.json")));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }
}

public class AccountStoreTests
{
    private static List<Account> Samples() => new()
    {
        new Account { Username = "alt1", UserID = 111, Group = "Main", LastUse = DateTime.UtcNow },
        new Account { Username = "alt2", UserID = 222, Group = "Bank", LastUse = DateTime.UtcNow.AddDays(-1) }
    };

    [Fact]
    public void PlainText_RoundTrips_WhenMarkerPresent()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // marker opts into plaintext
            File.WriteAllText(Path.Combine(dir, AccountStore.NoEncryptionMarker), "risks accepted");

            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));
            Assert.True(store.NoEncryptionEnabled);

            store.Save(Samples());
            Assert.Equal(AccountStoreMode.PlainText, store.DetectMode());

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("alt1", loaded[0].Username);
            Assert.Equal(111, loaded[0].UserID);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DefaultMode_IsDpapiProtected()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));
            Assert.False(store.NoEncryptionEnabled);

            store.Save(Samples());

            // A protected file must NOT be readable as plaintext and must decrypt via DPAPI.
            Assert.NotEqual(AccountStoreMode.PlainText, store.DetectMode());
            Assert.Equal(AccountStoreMode.Protected, store.DetectMode());

            var loaded = store.Load();
            Assert.Equal(2, loaded.Count);
            Assert.Equal("alt2", loaded[1].Username);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void PasswordMode_RoundTrips()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));
            store.Save(Samples(), password: "hunter2");

            Assert.Equal(AccountStoreMode.PasswordLocked, store.DetectMode());

            // Wrong/no password -> empty list
            Assert.Empty(store.Load());

            var loaded = store.Load(password: "hunter2");
            Assert.Equal(2, loaded.Count);
            Assert.Equal("alt1", loaded[0].Username);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void TruncatedPasswordFile_LoadFailsGracefully_WithBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));

            // A file that starts with the RAMHeader but is truncated (corrupt on disk) must
            // surface as a load failure with a backup — never an unhandled index exception.
            var truncated = new byte[Cryptography.RAMHeader.Length + 10];
            Array.Copy(Cryptography.RAMHeader, truncated, Cryptography.RAMHeader.Length);
            File.WriteAllBytes(store.FilePath, truncated);

            Assert.Empty(store.Load(password: "hunter2"));
            Assert.True(File.Exists(store.FilePath + ".bak"), "undecryptable data should be preserved");
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void VerifyPassword_MatchesOnlyCorrectPassword()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));
            store.Save(Samples(), password: "hunter2");

            Assert.True(store.VerifyPassword("hunter2"));
            Assert.False(store.VerifyPassword("wrong-password"));
            Assert.False(store.VerifyPassword(""));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void VerifyPassword_IsFalse_WhenNotPasswordLocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));
            store.Save(Samples()); // DPAPI
            Assert.False(store.VerifyPassword("hunter2"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void SessionPassword_KeepsFileLockedOnSubsequentSaves()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));
            store.Save(Samples(), password: "hunter2");
            store.SetSessionPassword("hunter2");

            // A plain save (no password argument) must not silently drop the lock.
            store.Save(Samples());

            Assert.Equal(AccountStoreMode.PasswordLocked, store.DetectMode());
            Assert.Equal(2, store.Load(password: "hunter2").Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void NewPassword_SupersedesSessionPassword()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));
            store.Save(Samples(), password: "hunter2");
            store.SetSessionPassword("hunter2");

            store.Save(Samples(), password: "new-password"); // explicit password wins

            Assert.Equal(AccountStoreMode.PasswordLocked, store.DetectMode());
            Assert.Equal(2, store.Load(password: "new-password").Count);
            Assert.Empty(store.Load(password: "hunter2"));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void ClearingSessionPassword_ReturnsToDpapi()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));
            store.Save(Samples(), password: "hunter2");
            store.SetSessionPassword("hunter2");

            // Remove the password: clear the session, then a save with removePassword
            // (the Settings → Remove password flow) intentionally drops the lock.
            store.SetSessionPassword(null);
            store.Save(Samples(), removePassword: true);

            Assert.Equal(AccountStoreMode.Protected, store.DetectMode());
            Assert.Equal(2, store.Load().Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Save_WhileLocked_WithoutPassword_IsRefused_AndFileStaysLocked()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ram_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var store = new AccountStore(Path.Combine(dir, "AccountData.json"));
            store.Save(Samples(), password: "hunter2");

            // Locked on disk with no session password: a plain save must be refused —
            // silently re-encrypting with DPAPI would drop the user's password lock.
            store.SetSessionPassword(null);
            Assert.False(store.Save(Samples()));

            Assert.Equal(AccountStoreMode.PasswordLocked, store.DetectMode());
            Assert.Equal(2, store.Load(password: "hunter2").Count);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Account_RejectsOverLengthValues_Loudly()
    {
        var a = new Account();

        // Over-length values must signal instead of being silently dropped.
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Alias = new string('x', 51));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Description = new string('x', 5001));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Password = new string('x', 5001));

        // Boundary values and clearing still work.
        a.Alias = new string('x', 50);
        Assert.Equal(50, a.Alias!.Length);
        a.Alias = null;
        Assert.Null(a.Alias);
        a.Password = null;
        Assert.Null(a.Password);
    }

    [Fact]
    public void DisplayName_PrefersAlias()
    {
        var a = new Account { Username = "realName", Alias = "nick" };
        Assert.Equal("nick", a.DisplayName);
        var b = new Account { Username = "realName" };
        Assert.Equal("realName", b.DisplayName);
    }
}

public class VaultTransferTests
{
    private static List<Account> Samples() => new()
    {
        new Account { Username = "alt1", UserID = 111, SecurityToken = "cookie-a", Group = "Main" },
        new Account { Username = "alt2", UserID = 222, SecurityToken = "cookie-b", Group = "Bank" }
    };

    [Fact]
    public void Export_ThenImport_RoundTripsAccounts()
    {
        byte[] payload = VaultTransfer.Export(Samples(), "hunter2");

        // The payload is a password-encrypted RAMHeader blob, never plaintext JSON.
        Assert.True(Cryptography.HasRAMHeader(payload));

        var loaded = VaultTransfer.Import(payload, "hunter2");
        Assert.Equal(2, loaded.Count);
        Assert.Equal("alt1", loaded[0].Username);
        Assert.Equal(111, loaded[0].UserID);
        Assert.Equal("cookie-b", loaded[1].SecurityToken);
    }

    [Fact]
    public void Import_WrongPassword_Throws()
    {
        byte[] payload = VaultTransfer.Export(Samples(), "hunter2");
        Assert.ThrowsAny<CryptographicException>(() => VaultTransfer.Import(payload, "wrong-password"));
    }

    [Fact]
    public void Import_PlaintextJson_Works()
    {
        byte[] payload = Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Samples()));
        var loaded = VaultTransfer.Import(payload, "ignored-password");
        Assert.Equal(2, loaded.Count);
    }

    [Fact]
    public void Import_DpapiEncryptedJson_Works()
    {
        // A copy of an upstream/no-encryption-marker AccountData.json encrypted with this
        // PC's DPAPI key — the standard upstream layout — must import without a password.
        byte[] payload = Cryptography.ProtectDefault(Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(Samples())));

        var loaded = VaultTransfer.Import(payload, "ignored-password");

        Assert.Equal(2, loaded.Count);
        Assert.Equal("alt1", loaded[0].Username);
        Assert.Equal("cookie-b", loaded[1].SecurityToken);
    }

    [Fact]
    public void Merge_AppendsNew_SkipsDuplicatesAndEmpty()
    {
        var vault = new List<Account> { new() { Username = "existing", SecurityToken = "cookie-a" } };
        var imported = new List<Account>
        {
            new() { Username = "existing-dup", SecurityToken = "cookie-a" }, // duplicate (case-insensitive)
            new() { Username = "new", SecurityToken = "cookie-b" },
            new() { Username = "no-cookie", SecurityToken = "" }
        };

        var (added, skipped) = VaultTransfer.Merge(vault, imported);

        Assert.Equal(1, added);
        Assert.Equal(2, skipped);
        Assert.Equal(2, vault.Count);
        Assert.Equal("new", vault[1].Username);
    }

    [Fact]
    public void Merge_IgnoresCaseInDuplicates()
    {
        var vault = new List<Account> { new() { SecurityToken = "CookieA" } };
        var imported = new List<Account> { new() { SecurityToken = "cookiea" } };

        var (added, skipped) = VaultTransfer.Merge(vault, imported);

        Assert.Equal(0, added);
        Assert.Equal(1, skipped);
    }
}
