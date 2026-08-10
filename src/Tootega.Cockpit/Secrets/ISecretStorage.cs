using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using Tootega.Cockpit.Util;

namespace Tootega.Cockpit.Secrets
{
    /// <summary>
    /// Where secrets live. Behind an interface so the vault's logic can be tested without
    /// writing to the machine's real credential store.
    /// </summary>
    internal interface ISecretStorage
    {
        string Get(string key);
        void Store(string key, string value);
        void Delete(string key);
    }

    /// <summary>
    /// Secret storage backed by the Windows Credential Manager.
    ///
    /// VS has no equivalent of VS Code's SecretStorage, so the platform's own store is used
    /// directly — the same place VS Code ends up on Windows. Values are encrypted by the OS
    /// against the current user, never written to our own files, and never logged.
    ///
    /// This is deliberately not DPAPI-over-a-file: the credential manager is inspectable and
    /// revocable by the user through a normal Windows UI, which matters for something holding
    /// their secrets.
    /// </summary>
    internal sealed class WindowsSecretStorage : ISecretStorage
    {
        /// <summary>Namespace prefix, so the entries are identifiable in the Windows UI.</summary>
        private const string TargetPrefix = "TootegaCockpit:";

        private const int CRED_TYPE_GENERIC = 1;
        private const int CRED_PERSIST_LOCAL_MACHINE = 2;
        private const int ERROR_NOT_FOUND = 1168;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct CREDENTIAL
        {
            public int Flags;
            public int Type;
            public string TargetName;
            public string Comment;
            public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
            public int CredentialBlobSize;
            public IntPtr CredentialBlob;
            public int Persist;
            public int AttributeCount;
            public IntPtr Attributes;
            public string TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredReadW", SetLastError = true)]
        private static extern bool CredRead(string target, int type, int reservedFlag, out IntPtr credentialPtr);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredWriteW", SetLastError = true)]
        private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "CredDeleteW", SetLastError = true)]
        private static extern bool CredDelete(string target, int type, int flags);

        [DllImport("advapi32.dll", EntryPoint = "CredFree")]
        private static extern void CredFree(IntPtr buffer);

        public string Get(string key)
        {
            var target = TargetPrefix + key;
            IntPtr handle = IntPtr.Zero;

            try
            {
                if (!CredRead(target, CRED_TYPE_GENERIC, 0, out handle))
                {
                    var error = Marshal.GetLastWin32Error();
                    // Absent is the normal case, not a failure worth reporting.
                    if (error != ERROR_NOT_FOUND) Log.Debug("credential read failed for a vault key (" + error + ")");
                    return null;
                }

                var credential = (CREDENTIAL)Marshal.PtrToStructure(handle, typeof(CREDENTIAL));
                if (credential.CredentialBlobSize == 0 || credential.CredentialBlob == IntPtr.Zero) return null;

                var bytes = new byte[credential.CredentialBlobSize];
                Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
                return Encoding.Unicode.GetString(bytes);
            }
            catch (Exception ex)
            {
                // The message is logged; the value never is.
                Log.Debug("credential read failed: " + ex.Message);
                return null;
            }
            finally
            {
                if (handle != IntPtr.Zero) CredFree(handle);
            }
        }

        public void Store(string key, string value)
        {
            var bytes = Encoding.Unicode.GetBytes(value ?? string.Empty);
            var blob = Marshal.AllocHGlobal(bytes.Length);

            try
            {
                Marshal.Copy(bytes, 0, blob, bytes.Length);

                var credential = new CREDENTIAL
                {
                    Type = CRED_TYPE_GENERIC,
                    TargetName = TargetPrefix + key,
                    CredentialBlobSize = bytes.Length,
                    CredentialBlob = blob,
                    Persist = CRED_PERSIST_LOCAL_MACHINE,
                    UserName = Environment.UserName,
                };

                if (!CredWrite(ref credential, 0))
                    Log.Error("could not store a vault entry (" + Marshal.GetLastWin32Error() + ")");
            }
            catch (Exception ex)
            {
                Log.Error("could not store a vault entry", ex);
            }
            finally
            {
                // Zeroed before release: the plaintext must not linger in freed memory.
                for (var i = 0; i < bytes.Length; i++) bytes[i] = 0;
                Marshal.FreeHGlobal(blob);
            }
        }

        public void Delete(string key)
        {
            try
            {
                CredDelete(TargetPrefix + key, CRED_TYPE_GENERIC, 0);
            }
            catch (Exception ex)
            {
                Log.Debug("could not delete a vault entry: " + ex.Message);
            }
        }
    }

    /// <summary>In-memory storage. Test seam only; it persists nothing.</summary>
    internal sealed class InMemorySecretStorage : ISecretStorage
    {
        private readonly Dictionary<string, string> _values = new Dictionary<string, string>(StringComparer.Ordinal);

        public string Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

        public void Store(string key, string value) => _values[key] = value;

        public void Delete(string key) => _values.Remove(key);
    }
}
