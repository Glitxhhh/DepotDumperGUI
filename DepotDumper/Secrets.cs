using System;
using System.Security.Cryptography;
using System.Text;

namespace DepotDumper
{
    /// <summary>
    /// Encrypts saved secrets (the Steam password, login tokens) with Windows DPAPI, scoped to the current Windows user.
    /// The result can only be decrypted by the same user account on the same PC: a copied config.json / account.config is
    /// useless to anyone else (and simply asks you to sign in again if you move it to another PC).
    /// </summary>
    internal static class Secrets
    {
        // App-specific extra input to DPAPI, so other programs running as the same user don't decrypt our data by accident.
        private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("DepotDumperGUI/secrets/v1");

        public static byte[] ProtectBytes(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);

        public static bool TryUnprotectBytes(byte[] protectedBytes, out byte[] plain)
        {
            try { plain = ProtectedData.Unprotect(protectedBytes, Entropy, DataProtectionScope.CurrentUser); return true; }
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
    }
}
