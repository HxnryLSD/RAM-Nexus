using System.IO;
using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using RAM.Core.Models;
using RAM.Core.Roblox;
using RAM.Core.Security;

namespace RAM.Core.Infrastructure;

public enum AccountStoreMode
{
    Empty,
    PlainText,
    Protected,
    PasswordLocked
}

/// <summary>
/// Persists the account list to AccountData.json, keeping the upstream RAM file
/// format and encryption modes (plaintext bypass, DPAPI LocalMachine default,
/// and password-locked AES-256-GCM with an Argon2id-derived key).
/// </summary>
public sealed class AccountStore
{
    public const string NoEncryptionMarker = "NoEncryption.IUnderstandTheRisks.iautamor";

    private readonly string _path;
    private readonly IAccountNotifier? _notifier;
    private readonly object _saveLock = new();
    private string? _sessionPassword;

    public string FilePath => _path;

    /// <summary>
    /// The password the user entered this session (null when the file isn't
    /// password-locked or hasn't been unlocked yet). Kept so subsequent saves
    /// stay password-encrypted instead of silently falling back to DPAPI.
    /// Cleared by <see cref="SetSessionPassword"/> with null when removing the lock.
    /// </summary>
    public string? SessionPassword => _sessionPassword;

    /// <summary>Remembers the unlock password for the rest of the session, or clears it.</summary>
    public void SetSessionPassword(string? password) => _sessionPassword = password;

    /// <summary>True when the user opted out of encryption (marker file present).</summary>
    public bool NoEncryptionEnabled =>
        File.Exists(Path.Combine(Path.GetDirectoryName(_path) ?? ".", NoEncryptionMarker));

    /// <summary>
    /// Default path is the user's LocalAppData data root (AppPaths.AccountData), so
    /// the data file is the same no matter how the app is launched (shortcut, CLI, post-update
    /// relaunch). Tests pass explicit paths.
    /// </summary>
    public AccountStore(string? path = null, IAccountNotifier? notifier = null)
    {
        _path = path ?? AppPaths.AccountData;
        _notifier = notifier;
    }

    /// <summary>Detect how the on-disk file (if any) is stored.</summary>
    public AccountStoreMode DetectMode()
    {
        if (!File.Exists(_path)) return AccountStoreMode.Empty;
        byte[] data = File.ReadAllBytes(_path);
        if (data.Length == 0) return AccountStoreMode.Empty;
        if (Cryptography.HasRAMHeader(data)) return AccountStoreMode.PasswordLocked;
        try
        {
            Cryptography.UnprotectDefault(data);
            return AccountStoreMode.Protected;
        }
        catch (CryptographicException)
        {
            return AccountStoreMode.PlainText;
        }
    }

    /// <summary>
    /// Load accounts. Returns empty list when no file exists. For a password-locked
    /// file, <paramref name="password"/> must be supplied (otherwise empty list is returned).
    /// </summary>
    public List<Account> Load(string? password = null)
    {
        if (!File.Exists(_path)) return new List<Account>();

        byte[] data = File.ReadAllBytes(_path);
        if (data.Length == 0) return new List<Account>();

        string json;
        try
        {
            if (Cryptography.HasRAMHeader(data))
            {
                if (string.IsNullOrEmpty(password))
                {
                    _notifier?.Warn("This account file is password locked. Enter your password to load accounts.");
                    return new List<Account>();
                }
                json = Cryptography.DecryptWithPassword(data, password);
            }
            else
            {
                try
                {
                    json = Encoding.UTF8.GetString(Cryptography.UnprotectDefault(data));
                }
                catch (CryptographicException)
                {
                    json = Encoding.UTF8.GetString(data); // plaintext fallback
                }
            }
        }
        catch (Exception e) when (e is CryptographicException or JsonException)
        {
            // Attempt to preserve the undecryptable data before giving up.
            File.WriteAllBytes(_path + ".bak", data);
            _notifier?.Error($"Failed to load accounts! A backup file was created in case the data can be recovered.\n\n{e.Message}");
            return new List<Account>();
        }

        return JsonConvert.DeserializeObject<List<Account>>(json) ?? new List<Account>();
    }

    /// <summary>
    /// True when <paramref name="password"/> decrypts the on-disk file. Used by the
    /// unlock flow to re-prompt on a wrong password without touching backups or
    /// firing notifications. Returns false when the file isn't password-locked.
    /// </summary>
    public bool VerifyPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false; // an empty password is never a valid lock
        if (!File.Exists(_path) || DetectMode() != AccountStoreMode.PasswordLocked) return false;
        try
        {
            Cryptography.DecryptWithPassword(File.ReadAllBytes(_path), password);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    /// <summary>
    /// Save the account list. When <paramref name="password"/> is set the file is
    /// password-encrypted; otherwise the session password (if any) keeps the file
    /// locked; otherwise DPAPI (default) or plaintext (when the no-encryption marker
    /// file exists) is used, matching upstream behavior.
    /// Returns false (writing nothing) when the on-disk file is password-locked but no
    /// password is available to re-encrypt with — a locked file must never silently
    /// downgrade to DPAPI. Pass <paramref name="removePassword"/> to intentionally drop
    /// the lock (the Settings → Remove password flow).
    /// </summary>
    public bool Save(List<Account> accounts, string? password = null, bool bypassCountCheck = false, bool removePassword = false)
    {
        if (!bypassCountCheck && accounts.Count == 0) return true;

        lock (_saveLock)
        {
            string effectivePassword = password ?? _sessionPassword ?? string.Empty;

            // Without a password to re-encrypt with, saving a locked file would silently
            // downgrade it to DPAPI/plaintext — refuse unless the caller is removing the
            // lock on purpose (e.g. the Settings → Remove password flow).
            if (string.IsNullOrEmpty(effectivePassword) && !removePassword && DetectMode() == AccountStoreMode.PasswordLocked)
                return false;

            byte[] oldInfo = File.Exists(_path) ? File.ReadAllBytes(_path) : Array.Empty<byte>();
            string saveData = JsonConvert.SerializeObject(accounts);

            var backup = new FileInfo(_path + ".backup");
            if (!backup.Exists || (backup.Exists && (DateTime.Now - backup.LastWriteTime).TotalMinutes > 60 * 8))
                File.WriteAllBytes(backup.FullName, oldInfo);

            byte[] payload = !string.IsNullOrEmpty(effectivePassword)
                ? Cryptography.EncryptWithPassword(saveData, effectivePassword)
                : NoEncryptionEnabled
                    ? Encoding.UTF8.GetBytes(saveData)
                    : Cryptography.ProtectDefault(Encoding.UTF8.GetBytes(saveData));

            // Atomic write: a crash mid-write must never leave a truncated AccountData.json.
            string tmp = _path + ".tmp";
            try
            {
                File.WriteAllBytes(tmp, payload);
                File.Move(tmp, _path, overwrite: true);
            }
            finally
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            }

            return true;
        }
    }
}
