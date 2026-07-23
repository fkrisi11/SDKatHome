#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace SDKatHome.Patches
{
    /// <summary>
    /// Everything about a saved account except the secret. This is the only shape that is ever
    /// cached or handed to UI code - there is deliberately no password field on it, so a password
    /// cannot leak into a long-lived object by accident.
    /// </summary>
    public class SavedAccountInfo
    {
        public string id;
        public string label;
        public string username;
        public string lastUsedUtc;

        public string DisplayName => string.IsNullOrEmpty(label) ? username : label;
    }

    /// <summary>
    /// Machine-bound credential store for VRChat SDK logins.
    ///
    /// WHERE IT LIVES
    /// EditorPrefs, which on every platform Unity supports is a per-OS-user, machine-global store
    /// (Windows: HKCU registry, macOS: user defaults, Linux: ~/.local/share). That is what makes
    /// saved accounts visible from *every* Unity project with SDK at Home in it, without writing
    /// anything into the project folder or into version control.
    ///
    /// HOW IT IS PROTECTED
    /// EditorPrefs is plain text, so the account list is never written there as-is. The first byte
    /// of the stored blob says which container format was used:
    ///
    ///   2 - AES-256-CBC + HMAC-SHA256, binary payload    (macOS / Linux)
    ///   3 - DPAPI( format-2 blob )                       (Windows)
    ///
    /// The AES and MAC keys are derived with PBKDF2 from a device fingerprint (Unity's
    /// deviceUniqueIdentifier plus the OS machine name and user name) mixed with a random
    /// per-machine salt. Nothing about that key is stored; it is recomputed from the machine.
    ///
    /// On Windows the finished blob is additionally wrapped with DPAPI in CurrentUser scope, whose
    /// key is held by the OS and derived from your Windows logon credential. That closes the hole
    /// the fingerprint alone leaves open: the fingerprint inputs (MachineGuid, machine name, user
    /// name) and the salt all live in the registry next to the ciphertext, so a registry export
    /// would otherwise be enough to decrypt offline. With the DPAPI layer an attacker needs the
    /// user's DPAPI master key as well, i.e. their Windows password or SYSTEM on the machine.
    /// DPAPI is layered *over* the AES rather than replacing it, so both must be broken.
    ///
    /// Windows accounts with a blank password still work, but the DPAPI layer buys much less there,
    /// since the credential the master key derives from is a known constant. It is never worse than
    /// the AES-only path, so it is always applied when available.
    ///
    /// If DPAPI is unavailable or fails for any reason, the vault silently falls back to format 2.
    /// A weird Windows configuration degrades to the portable scheme instead of locking anyone out.
    ///
    /// HOW SECRETS ARE HELD IN MEMORY
    /// Passwords are handled as byte[] and zeroed after use, never parked in a string. The session
    /// cache holds <see cref="SavedAccountInfo"/> only - no secret in it at all. A password exists
    /// in the clear only inside a single <see cref="Mutate"/> call or a single
    /// <see cref="TakePassword"/> call, and is wiped in a finally block either way. See
    /// <see cref="TakePassword"/> for the two boundaries where a plaintext string is unavoidable.
    /// Zeroing a managed array is itself best effort: the GC may have moved or copied the array
    /// before the wipe, leaving stale bytes behind. Every managed-language credential store shares
    /// that limit; the wipe still shrinks the window in which the heap holds a live secret.
    ///
    /// WHAT THIS DOES NOT DO
    /// It protects the credentials at rest, against the stored data being lifted off the machine.
    /// It cannot protect against code already running as the same OS user, because that code can
    /// simply ask this class to decrypt - no local scheme can fix that.
    /// </summary>
    public static class AccountVault
    {
        // Deliberately NOT reported through the patch's GetPreferenceKeys(): SDK at Home's
        // "Clear Preferences" button must not silently destroy the user's saved logins.
        // Use DeleteAll() from the manager window for that.
        public const string PREF_VAULT = "SDKatHome_VRChatAccounts_Vault";
        public const string PREF_SALT = "SDKatHome_VRChatAccounts_Salt";

        private const byte FormatAesBinary = 2;
        private const byte FormatDpapiAesBinary = 3;

        private const int Pbkdf2Iterations = 200000;
        private const int SaltLength = 16;
        private const int IvLength = 16;
        private const int MacLength = 32;
        private const string KeyContext = "SDKatHome.VRChatAccountVault.v1";

        // PBKDF2 is intentionally slow, so the derived key is computed once per domain reload.
        private static byte[] _aesKey;
        private static byte[] _macKey;

        // Metadata-only cache, keyed on the raw blob so a change made from another Unity instance
        // sharing the same EditorPrefs is still picked up.
        private static string _cachedBlob;
        private static List<SavedAccountInfo> _cachedInfo;

        /// <summary>Last decrypt/encrypt failure, or null if the vault is healthy.</summary>
        public static string LastError { get; private set; }

        /// <summary>True when a vault blob exists but could not be decrypted on this machine.</summary>
        public static bool IsUnreadable { get; private set; }

        /// <summary>True when the stored blob is protected by the Windows DPAPI layer.</summary>
        public static bool IsDpapiProtected { get; private set; }

        #region Public API

        /// <summary>
        /// The saved accounts, without their passwords. Cheap to call every repaint - it only
        /// re-decrypts when the stored blob has actually changed. The returned list is the
        /// caller's own copy; mutating it cannot corrupt the cache.
        /// </summary>
        public static List<SavedAccountInfo> LoadInfo()
        {
            string blob = EditorPrefs.GetString(PREF_VAULT, "");

            if (string.IsNullOrEmpty(blob))
            {
                LastError = null;
                IsUnreadable = false;
                IsDpapiProtected = false;
                _cachedBlob = "";
                _cachedInfo = new List<SavedAccountInfo>();
                return CopyOfCache();
            }

            if (_cachedInfo != null && _cachedBlob == blob)
                return CopyOfCache();

            List<VaultEntry> entries = null;
            try
            {
                entries = DecryptEntries(blob);
                _cachedInfo = ToInfo(entries);
                _cachedBlob = blob;
                LastError = null;
                IsUnreadable = false;
                return CopyOfCache();
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                IsUnreadable = true;

                // Do NOT overwrite the stored blob - the machine may have changed temporarily
                // (hardware swap, restored profile) and we must not destroy recoverable data.
                _cachedBlob = blob;
                _cachedInfo = new List<SavedAccountInfo>();
                return CopyOfCache();
            }
            finally
            {
                ZeroEntries(entries);
            }
        }

        private static List<SavedAccountInfo> CopyOfCache()
        {
            return new List<SavedAccountInfo>(_cachedInfo);
        }

        /// <summary>
        /// Decrypts one account's password. The caller owns the returned array and MUST zero it
        /// (see <see cref="Zero"/>) as soon as it is done. Returns null if the id is unknown.
        ///
        /// This is the narrowest the secret's lifetime can get on our side. Two boundaries beyond
        /// it are outside our control and do involve a managed string:
        ///   - Unity's EditorGUILayout.PasswordField has no byte[] form, so a typed password is a
        ///     string before it ever reaches us.
        ///   - The SDK's own login path (VRCSdkControlPanel.password, APIUser.Login) is string
        ///     based, so signing in requires materialising one.
        /// Managed strings are immutable and GC-managed, so those cannot be wiped on demand. What
        /// this design does guarantee is that no password sits in a string for the whole session.
        /// </summary>
        public static byte[] TakePassword(string id)
        {
            if (string.IsNullOrEmpty(id))
                return null;

            List<VaultEntry> entries = null;
            try
            {
                string blob = EditorPrefs.GetString(PREF_VAULT, "");
                if (string.IsNullOrEmpty(blob))
                    return null;

                entries = DecryptEntries(blob);

                foreach (var entry in entries)
                {
                    if (entry.id != id || entry.password == null)
                        continue;

                    // Hand back a copy; the originals are wiped in the finally below.
                    var copy = new byte[entry.password.Length];
                    Buffer.BlockCopy(entry.password, 0, copy, 0, copy.Length);
                    return copy;
                }

                return null;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                IsUnreadable = true;
                return null;
            }
            finally
            {
                ZeroEntries(entries);
            }
        }

        /// <summary>
        /// Adds an account, or updates the password/label of an existing entry with the same
        /// username (case-insensitive). The caller still owns <paramref name="password"/> and
        /// should zero it afterwards. Returns true when an existing entry was updated.
        /// </summary>
        public static bool AddOrUpdate(string label, string username, byte[] password)
        {
            bool updated = false;

            Mutate(entries =>
            {
                var existing = entries.Find(e =>
                    string.Equals(e.username, username, StringComparison.OrdinalIgnoreCase));

                if (existing != null)
                {
                    Zero(existing.password);
                    existing.password = CopyOf(password);
                    if (!string.IsNullOrEmpty(label))
                        existing.label = label;
                    updated = true;
                    return;
                }

                entries.Add(new VaultEntry
                {
                    id = Guid.NewGuid().ToString("N"),
                    label = string.IsNullOrEmpty(label) ? username : label,
                    username = username,
                    password = CopyOf(password),
                    lastUsedUtc = ""
                });
            });

            return updated;
        }

        /// <summary>Replaces one account's stored password. The caller still owns the array.</summary>
        public static void SetPassword(string id, byte[] password)
        {
            Mutate(entries =>
            {
                var entry = entries.Find(e => e.id == id);
                if (entry == null)
                    return;

                Zero(entry.password);
                entry.password = CopyOf(password);
            });
        }

        public static void SetLabel(string id, string label)
        {
            Mutate(entries =>
            {
                var entry = entries.Find(e => e.id == id);
                if (entry != null)
                    entry.label = string.IsNullOrEmpty(label) ? entry.username : label;
            });
        }

        public static void Remove(string id)
        {
            Mutate(entries =>
            {
                int index = entries.FindIndex(e => e.id == id);
                if (index < 0)
                    return;

                Zero(entries[index].password);
                entries.RemoveAt(index);
            });
        }

        public static void MarkUsed(string id)
        {
            Mutate(entries =>
            {
                var entry = entries.Find(e => e.id == id);
                if (entry != null)
                    entry.lastUsedUtc = DateTime.UtcNow.ToString("o");
            });
        }

        public static SavedAccountInfo FindByUsername(string username)
        {
            if (string.IsNullOrEmpty(username))
                return null;

            return LoadInfo().Find(a =>
                string.Equals(a.username, username, StringComparison.OrdinalIgnoreCase));
        }

        public static SavedAccountInfo FindById(string id)
        {
            return string.IsNullOrEmpty(id) ? null : LoadInfo().Find(a => a.id == id);
        }

        /// <summary>Wipes every saved account. The per-machine salt is kept so nothing is orphaned.</summary>
        public static void DeleteAll()
        {
            EditorPrefs.DeleteKey(PREF_VAULT);
            _cachedBlob = "";
            _cachedInfo = new List<SavedAccountInfo>();
            LastError = null;
            IsUnreadable = false;
            IsDpapiProtected = false;
        }

        /// <summary>Forces the next LoadInfo() to re-read and re-decrypt from EditorPrefs.</summary>
        public static void InvalidateCache()
        {
            _cachedBlob = null;
            _cachedInfo = null;
        }

        /// <summary>Overwrites a secret buffer. Safe to call with null.</summary>
        public static void Zero(byte[] buffer)
        {
            if (buffer != null)
                Array.Clear(buffer, 0, buffer.Length);
        }

        #endregion

        #region Mutation

        /// <summary>
        /// The single path through which the vault is modified. Decrypts, applies the change,
        /// re-encrypts and refreshes the metadata cache - then wipes every plaintext password in a
        /// finally block, so a throw partway through cannot leave secrets sitting in the heap.
        /// </summary>
        private static void Mutate(Action<List<VaultEntry>> mutation)
        {
            List<VaultEntry> entries = null;
            try
            {
                string blob = EditorPrefs.GetString(PREF_VAULT, "");

                if (string.IsNullOrEmpty(blob))
                {
                    entries = new List<VaultEntry>();
                }
                else
                {
                    try
                    {
                        entries = DecryptEntries(blob);
                    }
                    catch (Exception ex)
                    {
                        // Callers confirm with the user before reaching this point, so replacing an
                        // undecryptable vault here is intentional rather than silent data loss.
                        LastError = ex.Message;
                        entries = new List<VaultEntry>();
                        DropSaltIfCorrupt();
                    }
                }

                mutation(entries);

                string newBlob = EncryptEntries(entries);
                EditorPrefs.SetString(PREF_VAULT, newBlob);

                _cachedBlob = newBlob;
                _cachedInfo = ToInfo(entries);
                LastError = null;
                IsUnreadable = false;
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                Debug.LogError($"<color=#00FF00>[SDK at Home]</color> Failed to update the VRChat account vault: {ex.Message}");
            }
            finally
            {
                ZeroEntries(entries);
            }
        }

        private static List<SavedAccountInfo> ToInfo(List<VaultEntry> entries)
        {
            var info = new List<SavedAccountInfo>();
            if (entries == null)
                return info;

            foreach (var entry in entries)
            {
                info.Add(new SavedAccountInfo
                {
                    id = entry.id,
                    label = entry.label,
                    username = entry.username,
                    lastUsedUtc = entry.lastUsedUtc
                });
            }
            return info;
        }

        private static void ZeroEntries(List<VaultEntry> entries)
        {
            if (entries == null)
                return;

            foreach (var entry in entries)
                Zero(entry.password);
        }

        private static byte[] CopyOf(byte[] source)
        {
            if (source == null)
                return new byte[0];

            var copy = new byte[source.Length];
            Buffer.BlockCopy(source, 0, copy, 0, source.Length);
            return copy;
        }

        /// <summary>Internal shape: the only object that ever holds a plaintext password.</summary>
        private class VaultEntry
        {
            public string id;
            public string label;
            public string username;
            public byte[] password;
            public string lastUsedUtc;
        }

        #endregion

        #region Container

        private static List<VaultEntry> DecryptEntries(string blob)
        {
            byte[] raw = Convert.FromBase64String(blob);

            if (raw.Length < 1)
                throw new CryptographicException("Stored account data is empty.");

            byte format = raw[0];
            byte[] aesBlob = null;
            byte[] plain = null;

            try
            {
                if (format == FormatDpapiAesBinary)
                {
                    IsDpapiProtected = true;

                    byte[] wrapped = new byte[raw.Length - 1];
                    Buffer.BlockCopy(raw, 1, wrapped, 0, wrapped.Length);
                    aesBlob = Dpapi.Unprotect(wrapped);
                }
                else
                {
                    IsDpapiProtected = false;
                    aesBlob = raw;
                }

                plain = AesDecrypt(aesBlob);
                return ParseBinaryPayload(plain);
            }
            finally
            {
                Zero(plain);
                if (!ReferenceEquals(aesBlob, raw))
                    Zero(aesBlob);
            }
        }

        private static string EncryptEntries(List<VaultEntry> entries)
        {
            byte[] plain = null;
            byte[] aesBlob = null;
            byte[] wrapped = null;

            try
            {
                plain = WriteBinaryPayload(entries);
                aesBlob = AesEncrypt(plain);

                if (Dpapi.IsAvailable)
                {
                    try
                    {
                        wrapped = Dpapi.Protect(aesBlob);

                        byte[] output = new byte[wrapped.Length + 1];
                        output[0] = FormatDpapiAesBinary;
                        Buffer.BlockCopy(wrapped, 0, output, 1, wrapped.Length);

                        IsDpapiProtected = true;
                        return Convert.ToBase64String(output);
                    }
                    catch (Exception ex)
                    {
                        // Never let a DPAPI quirk cost the user their accounts - fall back to the
                        // portable AES-only container, which is what macOS and Linux use anyway.
                        Debug.LogWarning($"<color=#00FF00>[SDK at Home]</color> DPAPI unavailable, " +
                                         $"storing VRChat accounts with device-key encryption only: {ex.Message}");
                    }
                }

                IsDpapiProtected = false;
                return Convert.ToBase64String(aesBlob);
            }
            finally
            {
                Zero(plain);
                Zero(aesBlob);
                Zero(wrapped);
            }
        }

        #endregion

        #region Payload

        // Binary payload, so a password is never routed through a string the way JSON would force.
        //   int32  entry count
        //   per entry: id, label, username, lastUsedUtc  (BinaryWriter length-prefixed UTF8)
        //              int32 password length, then the raw password bytes

        private static byte[] WriteBinaryPayload(List<VaultEntry> entries)
        {
            using (var stream = new MemoryStream())
            {
                using (var writer = new BinaryWriter(stream, Encoding.UTF8, true))
                {
                    writer.Write(entries.Count);

                    foreach (var entry in entries)
                    {
                        writer.Write(entry.id ?? "");
                        writer.Write(entry.label ?? "");
                        writer.Write(entry.username ?? "");
                        writer.Write(entry.lastUsedUtc ?? "");

                        byte[] password = entry.password ?? new byte[0];
                        writer.Write(password.Length);
                        writer.Write(password);
                    }
                }

                byte[] result = stream.ToArray();

                // ToArray() copied the data; wipe the stream's own buffer so the plaintext is not
                // left behind in a discarded array waiting for the GC.
                Zero(stream.GetBuffer());
                return result;
            }
        }

        private static List<VaultEntry> ParseBinaryPayload(byte[] plain)
        {
            var entries = new List<VaultEntry>();

            using (var stream = new MemoryStream(plain, false))
            using (var reader = new BinaryReader(stream, Encoding.UTF8))
            {
                int count = reader.ReadInt32();
                if (count < 0 || count > 4096)
                    throw new CryptographicException("Stored account data is malformed.");

                for (int i = 0; i < count; i++)
                {
                    var entry = new VaultEntry
                    {
                        id = reader.ReadString(),
                        label = reader.ReadString(),
                        username = reader.ReadString(),
                        lastUsedUtc = reader.ReadString()
                    };

                    int length = reader.ReadInt32();
                    if (length < 0 || length > plain.Length)
                        throw new CryptographicException("Stored account data is malformed.");

                    entry.password = reader.ReadBytes(length);
                    if (entry.password.Length != length)
                        throw new CryptographicException("Stored account data is truncated.");
                    entries.Add(entry);
                }
            }

            return entries;
        }

        #endregion

        #region AES layer

        private static void EnsureKeys()
        {
            if (_aesKey != null && _macKey != null)
                return;

            byte[] salt = GetOrCreateSalt();

            // Device fingerprint. deviceUniqueIdentifier is stable per machine and identical across
            // every Unity project on it, which is exactly the scope we want the vault to have.
            string fingerprint = string.Join("|", new[]
            {
                KeyContext,
                SystemInfo.deviceUniqueIdentifier,
                SafeEnv(() => Environment.MachineName),
                SafeEnv(() => Environment.UserName),
                SafeEnv(() => Environment.OSVersion.Platform.ToString())
            });

            using (var kdf = new Rfc2898DeriveBytes(fingerprint, salt, Pbkdf2Iterations))
            {
                byte[] okm = kdf.GetBytes(64);
                _aesKey = new byte[32];
                _macKey = new byte[32];
                Buffer.BlockCopy(okm, 0, _aesKey, 0, 32);
                Buffer.BlockCopy(okm, 32, _macKey, 0, 32);
                Zero(okm);
            }
        }

        private static string SafeEnv(Func<string> getter)
        {
            try { return getter() ?? ""; }
            catch { return ""; }
        }

        private static byte[] GetOrCreateSalt()
        {
            string stored = EditorPrefs.GetString(PREF_SALT, "");
            if (!string.IsNullOrEmpty(stored))
            {
                byte[] existing = ParseSalt(stored);
                if (existing != null)
                    return existing;

                // Corrupted salt. While a vault blob still exists, minting a replacement would
                // orphan that blob permanently even if the corruption is recoverable (restored
                // profile, partial registry import) - so refuse and let LoadInfo() report the
                // vault as unreadable, the same stance it takes for a corrupt blob.
                if (EditorPrefs.HasKey(PREF_VAULT))
                    throw new CryptographicException(
                        "The stored key salt is corrupted, so the saved accounts cannot be decrypted.");
            }

            byte[] salt = new byte[SaltLength];
            using (var rng = RandomNumberGenerator.Create())
                rng.GetBytes(salt);

            EditorPrefs.SetString(PREF_SALT, Convert.ToBase64String(salt));
            return salt;
        }

        /// <summary>The stored salt as bytes, or null if it is missing or corrupt.</summary>
        private static byte[] ParseSalt(string stored)
        {
            try
            {
                byte[] salt = Convert.FromBase64String(stored);
                return salt.Length == SaltLength ? salt : null;
            }
            catch (FormatException)
            {
                return null;
            }
        }

        /// <summary>
        /// Deletes the stored salt if it no longer parses. Only called from <see cref="Mutate"/>
        /// once the user has confirmed replacing an unreadable vault - at that point the old blob
        /// is being abandoned anyway, so the corrupt salt no longer guards anything recoverable
        /// and would otherwise block <see cref="GetOrCreateSalt"/> from minting a fresh one.
        /// </summary>
        private static void DropSaltIfCorrupt()
        {
            string stored = EditorPrefs.GetString(PREF_SALT, "");
            if (!string.IsNullOrEmpty(stored) && ParseSalt(stored) == null)
                EditorPrefs.DeleteKey(PREF_SALT);
        }

        private static byte[] AesEncrypt(byte[] plain)
        {
            EnsureKeys();

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _aesKey;
                aes.GenerateIV();

                byte[] cipher;
                using (var encryptor = aes.CreateEncryptor())
                    cipher = encryptor.TransformFinalBlock(plain, 0, plain.Length);

                byte[] output = new byte[1 + IvLength + cipher.Length + MacLength];
                output[0] = FormatAesBinary;
                Buffer.BlockCopy(aes.IV, 0, output, 1, IvLength);
                Buffer.BlockCopy(cipher, 0, output, 1 + IvLength, cipher.Length);

                // Encrypt-then-MAC over the format byte, IV and ciphertext.
                using (var hmac = new HMACSHA256(_macKey))
                {
                    byte[] mac = hmac.ComputeHash(output, 0, 1 + IvLength + cipher.Length);
                    Buffer.BlockCopy(mac, 0, output, 1 + IvLength + cipher.Length, MacLength);
                }

                return output;
            }
        }

        private static byte[] AesDecrypt(byte[] blob)
        {
            EnsureKeys();

            if (blob == null || blob.Length < 1 + IvLength + MacLength)
                throw new CryptographicException("Stored account data is truncated.");

            if (blob[0] != FormatAesBinary)
                throw new CryptographicException($"Unsupported account vault format ({blob[0]}).");

            int bodyLength = blob.Length - MacLength;

            using (var hmac = new HMACSHA256(_macKey))
            {
                byte[] expected = hmac.ComputeHash(blob, 0, bodyLength);
                if (!ConstantTimeEquals(expected, 0, blob, bodyLength, MacLength))
                {
                    throw new CryptographicException(
                        "Saved accounts could not be decrypted on this machine. They were encrypted " +
                        "for a different device or OS user, or the stored data was modified.");
                }
            }

            byte[] iv = new byte[IvLength];
            Buffer.BlockCopy(blob, 1, iv, 0, IvLength);

            int cipherLength = bodyLength - 1 - IvLength;

            using (var aes = Aes.Create())
            {
                aes.KeySize = 256;
                aes.BlockSize = 128;
                aes.Mode = CipherMode.CBC;
                aes.Padding = PaddingMode.PKCS7;
                aes.Key = _aesKey;
                aes.IV = iv;

                using (var decryptor = aes.CreateDecryptor())
                    return decryptor.TransformFinalBlock(blob, 1 + IvLength, cipherLength);
            }
        }

        private static bool ConstantTimeEquals(byte[] a, int aOffset, byte[] b, int bOffset, int length)
        {
            int diff = 0;
            for (int i = 0; i < length; i++)
                diff |= a[aOffset + i] ^ b[bOffset + i];
            return diff == 0;
        }

        #endregion

        #region Windows DPAPI layer

        /// <summary>
        /// Windows Data Protection API, CurrentUser scope.
        ///
        /// The P/Invoke declarations are compiled on every platform but crypt32.dll is only bound on
        /// first call, so simply having them here is harmless on macOS and Linux - IsAvailable gates
        /// every call site, and those platforms never touch it.
        /// </summary>
        private static class Dpapi
        {
            private const int CRYPTPROTECT_UI_FORBIDDEN = 0x1;

            // Application-specific secondary entropy, so the blob cannot be decrypted by simply
            // handing it to some other DPAPI-using program running as the same user.
            private static readonly byte[] Entropy =
                Encoding.UTF8.GetBytes("SDKatHome.VRChatAccountVault.entropy.v1");

            public static bool IsAvailable
            {
                get
                {
                    try { return Environment.OSVersion.Platform == PlatformID.Win32NT; }
                    catch { return false; }
                }
            }

            public static byte[] Protect(byte[] plain) => Run(plain, true);

            public static byte[] Unprotect(byte[] blob) => Run(blob, false);

            private static byte[] Run(byte[] input, bool protect)
            {
                if (input == null)
                    throw new ArgumentNullException(nameof(input));

                DataBlob inBlob = new DataBlob();
                DataBlob entropyBlob = new DataBlob();
                DataBlob outBlob = new DataBlob();

                try
                {
                    inBlob = Alloc(input);
                    entropyBlob = Alloc(Entropy);

                    bool ok = protect
                        ? CryptProtectData(ref inBlob, null, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                            CRYPTPROTECT_UI_FORBIDDEN, out outBlob)
                        : CryptUnprotectData(ref inBlob, IntPtr.Zero, ref entropyBlob, IntPtr.Zero, IntPtr.Zero,
                            CRYPTPROTECT_UI_FORBIDDEN, out outBlob);

                    if (!ok)
                    {
                        int error = Marshal.GetLastWin32Error();
                        throw new CryptographicException(
                            $"{(protect ? "CryptProtectData" : "CryptUnprotectData")} failed (0x{error:X8}).");
                    }

                    byte[] result = new byte[outBlob.cbData];
                    Marshal.Copy(outBlob.pbData, result, 0, outBlob.cbData);
                    return result;
                }
                finally
                {
                    FreeLocal(ref outBlob);
                    FreeHGlobal(ref inBlob);
                    FreeHGlobal(ref entropyBlob);
                }
            }

            private static DataBlob Alloc(byte[] data)
            {
                IntPtr buffer = Marshal.AllocHGlobal(data.Length);
                Marshal.Copy(data, 0, buffer, data.Length);
                return new DataBlob { cbData = data.Length, pbData = buffer };
            }

            private static void FreeHGlobal(ref DataBlob blob)
            {
                if (blob.pbData == IntPtr.Zero)
                    return;

                ZeroNative(blob.pbData, blob.cbData);
                Marshal.FreeHGlobal(blob.pbData);
                blob.pbData = IntPtr.Zero;
            }

            private static void FreeLocal(ref DataBlob blob)
            {
                if (blob.pbData == IntPtr.Zero)
                    return;

                ZeroNative(blob.pbData, blob.cbData);
                LocalFree(blob.pbData);
                blob.pbData = IntPtr.Zero;
            }

            private static void ZeroNative(IntPtr buffer, int length)
            {
                for (int i = 0; i < length; i++)
                    Marshal.WriteByte(buffer, i, 0);
            }

            [StructLayout(LayoutKind.Sequential)]
            private struct DataBlob
            {
                public int cbData;
                public IntPtr pbData;
            }

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool CryptProtectData(
                ref DataBlob pDataIn, string szDataDescr, ref DataBlob pOptionalEntropy,
                IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

            [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
            private static extern bool CryptUnprotectData(
                ref DataBlob pDataIn, IntPtr ppszDataDescr, ref DataBlob pOptionalEntropy,
                IntPtr pvReserved, IntPtr pPromptStruct, int dwFlags, out DataBlob pDataOut);

            [DllImport("kernel32.dll")]
            private static extern IntPtr LocalFree(IntPtr hMem);
        }

        #endregion
    }
}
#endif
