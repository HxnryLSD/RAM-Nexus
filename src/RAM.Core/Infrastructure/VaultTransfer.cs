using System.Security.Cryptography;
using System.Text;
using Newtonsoft.Json;
using RAM.Core.Models;
using RAM.Core.Security;

namespace RAM.Core.Infrastructure;

/// <summary>
/// Export/import of the account vault as a portable, password-encrypted JSON backup
/// (the same RAMHeader + AES-256-GCM/Argon2id format the account file uses, so a backup
/// decrypts on any machine that knows the password — unlike DPAPI). Plaintext JSON files
/// are also accepted on import.
/// </summary>
public static class VaultTransfer
{
    /// <summary>Serialize + password-encrypt the account list into a portable backup payload.</summary>
    public static byte[] Export(List<Account> accounts, string password)
        => Cryptography.EncryptWithPassword(JsonConvert.SerializeObject(accounts), password);

    /// <summary>
    /// Decrypt + parse a backup payload. Password-encrypted vaults (.ramvault) are decrypted
    /// with <paramref name="password"/>; upstream AccountData.json files — DPAPI-protected
    /// (same PC only) or plaintext — are decoded without a password. Throws
    /// <see cref="CryptographicException"/> for a wrong password, a DPAPI file from another
    /// PC, or tampered data; <see cref="JsonException"/> for a payload that isn't an account
    /// list.
    /// </summary>
    public static List<Account> Import(byte[] data, string password)
    {
        string json = Cryptography.HasRAMHeader(data)
            ? Cryptography.DecryptWithPassword(data, password)
            : DecodeUpstreamFile(data);

        return JsonConvert.DeserializeObject<List<Account>>(json) ?? new List<Account>();
    }

    /// <summary>Decode a non-password file: DPAPI first (same upstream byte layout), then plaintext.</summary>
    private static string DecodeUpstreamFile(byte[] data)
    {
        try
        {
            return Encoding.UTF8.GetString(Cryptography.UnprotectDefault(data));
        }
        catch (CryptographicException)
        {
            // Plaintext (no-encryption marker) files load as-is; a DPAPI blob made on another
            // PC can't be un-protected here and is almost never valid UTF-8 text.
            string decoded = Encoding.UTF8.GetString(data);
            if (decoded.Contains('\uFFFD'))
                throw new CryptographicException(
                    "This file is DPAPI-encrypted with another PC's key and cannot be decrypted on this machine.");
            return decoded;
        }
    }

    /// <summary>
    /// Merge imported accounts into the vault: accounts whose cookie is already present are
    /// skipped (case-insensitive), accounts without a cookie are skipped, everything else is
    /// appended. Returns how many were added vs skipped.
    /// </summary>
    public static (int Added, int Skipped) Merge(List<Account> vault, IEnumerable<Account> imported)
    {
        var existing = new HashSet<string>(
            vault.Where(a => !string.IsNullOrEmpty(a.SecurityToken)).Select(a => a.SecurityToken!),
            StringComparer.OrdinalIgnoreCase);

        int added = 0;
        int skipped = 0;
        foreach (var account in imported)
        {
            if (string.IsNullOrEmpty(account.SecurityToken))
            {
                skipped++;
                continue;
            }
            if (existing.Add(account.SecurityToken!))
            {
                vault.Add(account);
                added++;
            }
            else
            {
                skipped++;
            }
        }
        return (added, skipped);
    }
}
