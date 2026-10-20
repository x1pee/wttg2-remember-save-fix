using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;

namespace Wttg2RememberSaveFix
{
    // Identity only: this policy never receives or reads the game's hidden passwords.
    public static class RememberScopePolicy
    {
        public static string NetworkKey(int security, string bssid)
        {
            if (security < 1 || security > 3 || String.IsNullOrEmpty(bssid)) return null;
            string normalized = bssid.Trim().ToUpperInvariant();
            string[] parts = normalized.Split(':');
            // The installed game generates five octets. Accept six as well for compatibility.
            if (parts.Length != 5 && parts.Length != 6) return null;
            foreach (string part in parts)
            {
                if (part.Length != 2) return null;
                // MagicSlinger.GenerateRandomHexCode actually calls GenerateRandomAlphaNumHash.
                // The installed game uses A-Z/0-9, including letters outside hexadecimal A-F.
                foreach (char c in part) if (!((c >= '0' && c <= '9') || (c >= 'A' && c <= 'Z'))) return null;
            }
            return security.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + normalized;
        }

        public static string SaveScope(IEnumerable<string> networkKeys)
        {
            if (networkKeys == null) return null;
            SortedDictionary<string, bool> keys = new SortedDictionary<string, bool>(StringComparer.Ordinal);
            foreach (string key in networkKeys)
            {
                if (String.IsNullOrEmpty(key)) return null;
                keys[key] = true;
            }
            if (keys.Count < 2) return null;
            StringBuilder identity = new StringBuilder("WTTG2-RememberSaveFix-v1\n");
            foreach (string key in keys.Keys) identity.Append(key).Append('\n');
            // The legacy plugin accepts 32 hex digits as a scope. MD5 is used as an identity,
            // never for passwords, encryption, authenticity, or security decisions.
            using (MD5 md5 = MD5.Create())
            {
                byte[] hash = md5.ComputeHash(Encoding.UTF8.GetBytes(identity.ToString()));
                StringBuilder hex = new StringBuilder(32);
                foreach (byte b in hash) hex.Append(b.ToString("x2"));
                return hex.ToString();
            }
        }
    }
}
