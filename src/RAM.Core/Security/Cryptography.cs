using System.Security.Cryptography;
using System.Text;
using Konscious.Security.Cryptography;

namespace RAM.Core.Security;

/// <summary>
/// Encryption helpers for the account store.
///
/// Default mode is DPAPI LocalMachine (byte-compatible with the upstream RAM format so
/// existing AccountData.json files load). Optional password mode uses AES-256-GCM (NIST
/// SP 800-38D authenticated encryption) with a key derived by Argon2id — the OWASP-
/// recommended memory-hard KDF — and a versioned on-disk layout:
///
///   v2 (current): RAMHeader | version(1)=2 | salt(16) | nonce(12) | tag(16) | ciphertext
///   v1 (legacy):  RAMHeader | salt(16)     | nonce(12) | tag(16) | ciphertext  (PBKDF2-SHA256)
///
/// Every intermediate buffer (password bytes, derived key, plaintext) is zeroized with
/// <see cref="CryptographicOperations.ZeroMemory"/> immediately after use. Inputs arrive
/// as strings because the UI layer is string-based; .NET strings are immutable and cannot
/// be wiped, so callers should keep passwords in masked PasswordBoxes and pass them through
/// briefly rather than persisting them in memory.
/// </summary>
public static class Cryptography
{
    /// <summary>Header written in front of password-encrypted payloads.</summary>
    public static readonly byte[] RAMHeader =
        { 82, 111, 98, 108, 111, 120, 32, 65, 99, 99, 111, 117, 110, 116, 32, 77, 97, 110, 97, 103, 101, 114, 32, 99, 114,
          101, 97, 116, 101, 100, 32, 98, 121, 32, 105, 99, 51, 119, 48, 108, 102, 50, 50, 32, 64, 32, 103, 105, 116, 104,
          117, 98, 46, 99, 111, 109, 32, 46, 46, 46, 46, 46, 46, 46 };

    /// <summary>DPAPI entropy, matching the upstream account manager.</summary>
    public static readonly byte[] Entropy =
        { 0x52, 0x4f, 0x42, 0x4c, 0x4f, 0x58, 0x20, 0x41, 0x43, 0x43, 0x4f, 0x55, 0x4e, 0x54, 0x20, 0x4d, 0x41, 0x4e, 0x41,
          0x47, 0x45, 0x52, 0x20, 0x7c, 0x20, 0x3a, 0x29, 0x20, 0x7c, 0x20, 0x42, 0x52, 0x4f, 0x55, 0x47, 0x48, 0x54, 0x20,
          0x54, 0x4f, 0x20, 0x59, 0x4f, 0x55, 0x20, 0x42, 0x55, 0x59, 0x20, 0x69, 0x63, 0x33, 0x77, 0x30, 0x6c, 0x66 };

    // On-disk format versions for password-encrypted payloads.
    private const byte FormatVersionArgon2id = 2; // header | version | salt | nonce | tag | ciphertext (current)
    // v1 (legacy) has NO version byte: header | salt | nonce | tag | ciphertext, PBKDF2-SHA256.

    // Argon2id parameters per the OWASP Password Storage Cheat Sheet (m=19 MiB, t=2, p=1).
    private const int Argon2MemoryKiB = 19456;
    private const int Argon2Iterations = 2;
    private const int Argon2Parallelism = 1;

    // Layout constants (NIST-approved sizes: 256-bit key, 128-bit tag, 96-bit nonce, 128-bit salt).
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int KeySize = 32;

    /// <summary>Legacy PBKDF2 iteration count, kept only for decrypting pre-v2 files.</summary>
    private const int LegacyPbkdf2Iterations = 200_000;

    public static bool HasRAMHeader(byte[] data)
    {
        if (data.Length < RAMHeader.Length) return false;
        for (int i = 0; i < RAMHeader.Length; i++)
            if (data[i] != RAMHeader[i]) return false;
        return true;
    }

    /// <summary>Default machine-local encryption (DPAPI, LocalMachine scope). Compatible with upstream.</summary>
    public static byte[] ProtectDefault(byte[] data)
        => ProtectedData.Protect(data, Entropy, DataProtectionScope.LocalMachine);

    public static byte[] UnprotectDefault(byte[] data)
        => ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine);

    /// <summary>
    /// Password-based encryption using AES-256-GCM with an Argon2id-derived key.
    /// Layout: RAMHeader | version(1) | salt(16) | nonce(12) | tag(16) | ciphertext.
    /// Password bytes, the derived key and the plaintext buffer are zeroized after use.
    /// </summary>
    public static byte[] EncryptWithPassword(string content, string password)
    {
        byte[] plain = Encoding.UTF8.GetBytes(content);
        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] nonce = RandomNumberGenerator.GetBytes(NonceSize);

        byte[] key = DeriveKeyArgon2id(passwordBytes, salt);
        CryptographicOperations.ZeroMemory(passwordBytes);

        byte[] ciphertext = new byte[plain.Length];
        byte[] tag = new byte[TagSize];

        using (var aes = new AesGcm(key, TagSize))
            aes.Encrypt(nonce, plain, ciphertext, tag);

        // The key and plaintext are no longer needed; wipe them from managed memory.
        CryptographicOperations.ZeroMemory(key);
        CryptographicOperations.ZeroMemory(plain);

        using var ms = new MemoryStream();
        ms.Write(RAMHeader, 0, RAMHeader.Length);
        ms.WriteByte(FormatVersionArgon2id);
        ms.Write(salt, 0, salt.Length);
        ms.Write(nonce, 0, nonce.Length);
        ms.Write(tag, 0, tag.Length);
        ms.Write(ciphertext, 0, ciphertext.Length);
        return ms.ToArray();
    }

    /// <summary>
    /// Decrypt a password-encrypted payload. Both the current Argon2id format (v2) and the
    /// legacy PBKDF2 format (v1, no version byte) are supported. Throws
    /// <see cref="CryptographicException"/> for a wrong password, tampered data, or a
    /// malformed/truncated payload — never an index exception.
    /// </summary>
    public static string DecryptWithPassword(byte[] data, string password)
    {
        if (!HasRAMHeader(data)) throw new CryptographicException("Missing RAMHeader.");

        int o = RAMHeader.Length;
        // Minimum v1 payload: header + salt + nonce + tag + at least one ciphertext byte.
        if (data.Length <= o + SaltSize + NonceSize + TagSize)
            throw new CryptographicException("Malformed encrypted data (truncated payload).");

        // v2 files carry an explicit version byte right after the header. A legacy v1 file
        // has no version byte — its byte at this offset is the first salt byte. A v1 file
        // whose first salt byte happens to be 2 (1-in-256) is treated as v2 and fails the
        // tag check; no v1 files were ever shipped by the app UI, so this is theoretical.
        byte version = data[o];
        if (version == FormatVersionArgon2id)
            o += 1;

        byte[] salt = data.AsSpan(o, SaltSize).ToArray(); o += SaltSize;
        byte[] nonce = data.AsSpan(o, NonceSize).ToArray(); o += NonceSize;
        byte[] tag = data.AsSpan(o, TagSize).ToArray(); o += TagSize;
        byte[] ciphertext = data.AsSpan(o).ToArray();
        if (ciphertext.Length == 0)
            throw new CryptographicException("Malformed encrypted data (empty payload).");

        byte[] passwordBytes = Encoding.UTF8.GetBytes(password);
        try
        {
            byte[] key = version == FormatVersionArgon2id
                ? DeriveKeyArgon2id(passwordBytes, salt)
                : DeriveKeyPbkdf2(passwordBytes, salt);

            byte[] plain = new byte[ciphertext.Length];
            try
            {
                using var aes = new AesGcm(key, TagSize);
                // A wrong password (or tampered data) yields a tag mismatch → CryptographicException.
                aes.Decrypt(nonce, ciphertext, tag, plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }

            string result = Encoding.UTF8.GetString(plain);
            CryptographicOperations.ZeroMemory(plain);
            return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(passwordBytes);
        }
    }

    /// <summary>Memory-hard key derivation (Argon2id) — OWASP-recommended for password-based encryption.</summary>
    private static byte[] DeriveKeyArgon2id(byte[] password, byte[] salt)
    {
        using var argon2 = new Argon2id(password)
        {
            Salt = salt,
            DegreeOfParallelism = Argon2Parallelism,
            MemorySize = Argon2MemoryKiB,
            Iterations = Argon2Iterations
        };
        return argon2.GetBytes(KeySize);
    }

    /// <summary>Legacy PBKDF2-SHA256 derivation, kept only to decrypt pre-v2 password files.</summary>
    private static byte[] DeriveKeyPbkdf2(byte[] password, byte[] salt)
        => Rfc2898DeriveBytes.Pbkdf2(password, salt, LegacyPbkdf2Iterations, HashAlgorithmName.SHA256, KeySize);
}
