using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using ProtoBuf;
namespace DepotDumper
{
    /// <summary>
    /// Remembered logins (refresh tokens + Steam Guard machine data) and CDN server penalties.
    /// Stored as "account.config" next to the exe or in AppData (see <see cref="AppPaths"/>), encrypted with Windows DPAPI for the
    /// current Windows user, so the tokens are not readable as plain data and are useless if the file is copied elsewhere.
    /// Files written by older versions (plain, not encrypted) are read and upgraded in place.
    /// </summary>
    [ProtoContract]
    class AccountSettingsStore
    {
        // marks an encrypted file; anything else is an old plain file
        private static readonly byte[] Magic = Encoding.ASCII.GetBytes("DDGPROT1");

        [ProtoMember(2, IsRequired = false)]
        public ConcurrentDictionary<string, int> ContentServerPenalty { get; private set; }
        [ProtoMember(4, IsRequired = false)]
        public Dictionary<string, string> LoginTokens { get; private set; }
        [ProtoMember(5, IsRequired = false)]
        public Dictionary<string, string> GuardData { get; private set; }
        string FileName;
        AccountSettingsStore()
        {
            ContentServerPenalty = new ConcurrentDictionary<string, int>();
            LoginTokens = [];
            GuardData = [];
        }
        static bool Loaded
        {
            get { return Instance != null; }
        }
        public static AccountSettingsStore Instance;

        static bool HasMagic(byte[] raw) => raw.Length > Magic.Length && raw.AsSpan(0, Magic.Length).SequenceEqual(Magic);

        public static void LoadFromFile(string filename)
        {
            if (Loaded)
            {
                // Already loaded, just update the filename if needed
                if (Instance.FileName != filename)
                {
                    Instance.FileName = filename;
                }
                return;
            }

            Instance = new AccountSettingsStore();
            Instance.FileName = filename;
            if (!File.Exists(filename)) return;

            try
            {
                var raw = File.ReadAllBytes(filename);
                bool wasPlain = !HasMagic(raw);
                byte[] payload;
                if (wasPlain) payload = raw;   // written by an older version
                else if (!Secrets.TryUnprotectBytes(raw.AsSpan(Magic.Length).ToArray(), out payload))
                {
                    // saved by another Windows user or on another PC: keep it aside rather than overwriting it, and start fresh
                    var aside = filename + ".unreadable";
                    try { File.Move(filename, aside, overwrite: true); } catch { }
                    Logger.Warning($"The saved logins can't be decrypted on this PC/user (kept as '{Path.GetFileName(aside)}'). Please sign in again.");
                    return;
                }

                using var ms = new MemoryStream(payload);
                using var ds = new DeflateStream(ms, CompressionMode.Decompress);
                var loaded = Serializer.Deserialize<AccountSettingsStore>(ds);
                loaded.FileName = filename;
                Instance = loaded;

                if (wasPlain)
                {
                    Save();   // upgrade: rewrite encrypted
                    Logger.Info("The saved logins were stored unencrypted; they are now encrypted for your Windows account.");
                }
            }
            catch (Exception ex) when (ex is IOException || ex is InvalidDataException || ex is ProtoException || ex is UnauthorizedAccessException)
            {
                Logger.Warning($"Failed to load account settings from '{filename}': {ex.Message}");
                Instance = new AccountSettingsStore { FileName = filename };
            }
        }
        /// <summary>Forgets the saved login for one account (token and guard data) and saves.</summary>
        public static void Forget(string username)
        {
            if (!Loaded || string.IsNullOrEmpty(username)) return;
            Instance.LoginTokens.Remove(username);
            Instance.GuardData.Remove(username);
            Save();
        }
        public static void Save()
        {
            if (!Loaded)
                throw new Exception("Saved config before loading");
            try
            {
                byte[] plain;
                using (var ms = new MemoryStream())
                {
                    using (var ds = new DeflateStream(ms, CompressionMode.Compress, leaveOpen: true))
                    {
                        Serializer.Serialize(ds, Instance);
                    }
                    plain = ms.ToArray();
                }
                var sealedBytes = Secrets.ProtectBytes(plain);
                var output = new byte[Magic.Length + sealedBytes.Length];
                Magic.CopyTo(output, 0);
                sealedBytes.CopyTo(output, Magic.Length);

                Directory.CreateDirectory(Path.GetDirectoryName(Instance.FileName));
                var tmp = Instance.FileName + ".tmp";
                File.WriteAllBytes(tmp, output);
                File.Move(tmp, Instance.FileName, overwrite: true);
            }
            catch (IOException ex)
            {
                Logger.Warning($"Failed to save account settings: {ex.Message}");
            }
        }
    }
}
