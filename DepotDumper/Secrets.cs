using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace DepotDumper
{
    /// <summary>
    /// Encrypts saved secrets (the Steam password, login tokens) so they are never stored as plain text.
    ///   Windows      : DPAPI, scoped to the current Windows user. A copied file is useless to anyone else.
    ///   Linux / macOS: AES-256-GCM with a random per-install key kept in secret.key beside the settings, readable only by you (mode 600).
    ///                  That keeps the secrets out of plain sight, backups and accidental copies; like DPAPI it does not protect against
    ///                  other software running as the same user.
    /// Either way, moving the files to another machine or user simply asks you to sign in again.
    /// </summary>
    internal static class Secrets
    {
        // App-specific extra input to DPAPI, so other programs running as the same user don't decrypt our data by accident.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DepotDumperGUI/secrets/v1");
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DDGKEY1");
        private const int NonceSize = 12, TagSize = 16;

        public static byte[] ProtectBytes(byte[] plain) =>
            OperatingSystem.IsWindows() ? ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser) : KeyFileProtect(plain);

        public static bool TryUnprotectBytes(byte[] protectedBytes, out byte[] plain)
        {
            try
            {
                plain = OperatingSystem.IsWindows() ? ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser) : KeyFileUnprotect(protectedBytes);
                return plain != null;
            }
            catch (CryptographicException) { plain = null; return false; }
        }

        public static string Protect(string plain) => Convert.ToBase64String(ProtectBytes(Encoding.UTF8.GetBytes(plain)));

        public static bool TryUnprotect(string base64, out string plain)
        {
            plain = null;
            try
            {
                if (!TryUnprotectBytes(Convert.FromBase64String(base64), out var bytes)) return false;
                plain = Encoding.UTF8.GetString(bytes);
                return true;
            }
            catch (FormatException) { return false; }
        }

        // ---- non-Windows: key file

        private static string KeyPath => Path.Combine(AppPaths.SettingsDir, "secret.key");

        private static byte[] LoadKey(bool create)
        {
            var path = KeyPath;
            if (File.Exists(path))
            {
                var key = File.ReadAllBytes(path);
                if (key.Length == 32) return key;
            }
            if (!create) return null;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var fresh = RandomNumberGenerator.GetBytes(32);
            using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);   // 600, set before the key goes in
                fs.Write(fresh);
            }
            return fresh;
        }

        private static byte[] KeyFileProtect(byte[] plain)
        {
            var key = LoadKey(create: true);
            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var cipher = new byte[plain.Length];
            var tag = new byte[TagSize];
            using (var gcm = new AesGcm(key, TagSize)) gcm.Encrypt(nonce, plain, cipher, tag, Entropy);
            var result = new byte[Magic.Length + NonceSize + TagSize + cipher.Length];
            Buffer.BlockCopy(Magic, 0, result, 0, Magic.Length);
            Buffer.BlockCopy(nonce, 0, result, Magic.Length, NonceSize);
            Buffer.BlockCopy(tag, 0, result, Magic.Length + NonceSize, TagSize);
            Buffer.BlockCopy(cipher, 0, result, Magic.Length + NonceSize + TagSize, cipher.Length);
            return result;
        }

        private static byte[] KeyFileUnprotect(byte[] data)
        {
            var header = Magic.Length + NonceSize + TagSize;
            if (data.Length < header) throw new CryptographicException("not a protected value");
            for (var i = 0; i < Magic.Length; i++) if (data[i] != Magic[i]) throw new CryptographicException("not a protected value");
            var key = LoadKey(create: false) ?? throw new CryptographicException("no key file");
            var nonce = data.AsSpan(Magic.Length, NonceSize);
            var tag = data.AsSpan(Magic.Length + NonceSize, TagSize);
            var cipher = data.AsSpan(header);
            var plain = new byte[cipher.Length];
            using var gcm = new AesGcm(key, TagSize);
            gcm.Decrypt(nonce, cipher, tag, plain, Entropy);   // throws CryptographicException if the key or the data is wrong
            return plain;
        }
    }
}