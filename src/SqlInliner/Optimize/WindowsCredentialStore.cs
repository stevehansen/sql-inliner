#if !RELEASELIBRARY

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace SqlInliner.Optimize;

/// <summary>
/// Windows Credential Manager implementation using advapi32.dll P/Invoke.
/// </summary>
internal sealed class WindowsCredentialStore : ICredentialStore
{
    private const string TargetPrefix = "sqlinliner:";
    private const int CRED_TYPE_GENERIC = 1;
    private const int CRED_PERSIST_LOCAL_MACHINE = 2;

    public void Store(string key, string username, string password)
    {
        var targetName = TargetPrefix + key;
        var passwordBytes = Encoding.Unicode.GetBytes(password);

        var credential = new CREDENTIAL
        {
            Type = CRED_TYPE_GENERIC,
            TargetName = targetName,
            UserName = username,
            CredentialBlobSize = passwordBytes.Length,
            Persist = CRED_PERSIST_LOCAL_MACHINE,
        };

        var blobPtr = Marshal.AllocHGlobal(passwordBytes.Length);
        try
        {
            Marshal.Copy(passwordBytes, 0, blobPtr, passwordBytes.Length);
            credential.CredentialBlob = blobPtr;

            if (!CredWrite(ref credential, 0))
                throw new InvalidOperationException($"Failed to store credential. Error: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeHGlobal(blobPtr);
        }
    }

    public StoredCredential? Retrieve(string key)
    {
        var targetName = TargetPrefix + key;

        if (!CredRead(targetName, CRED_TYPE_GENERIC, 0, out var credPtr))
            return null;

        try
        {
            var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);
            var username = cred.UserName;
            var password = cred.CredentialBlobSize > 0
                ? Marshal.PtrToStringUni(cred.CredentialBlob, cred.CredentialBlobSize / 2)
                : string.Empty;

            return new StoredCredential(username ?? string.Empty, password ?? string.Empty);
        }
        finally
        {
            CredFree(credPtr);
        }
    }

    public bool Remove(string key)
    {
        var targetName = TargetPrefix + key;
        return CredDelete(targetName, CRED_TYPE_GENERIC, 0);
    }

    public IReadOnlyList<(string Key, string Username)> List()
    {
        var result = new List<(string Key, string Username)>();

        if (!CredEnumerate(TargetPrefix + "*", 0, out var count, out var credPtrArray))
            return result;

        try
        {
            for (var i = 0; i < count; i++)
            {
                var credPtr = Marshal.ReadIntPtr(credPtrArray, i * IntPtr.Size);
                var cred = Marshal.PtrToStructure<CREDENTIAL>(credPtr);

                if (cred.TargetName != null && cred.TargetName.StartsWith(TargetPrefix, StringComparison.Ordinal))
                {
                    var credKey = cred.TargetName.Substring(TargetPrefix.Length);
                    result.Add((credKey, cred.UserName ?? string.Empty));
                }
            }
        }
        finally
        {
            CredFree(credPtrArray);
        }

        return result;
    }

    #region P/Invoke

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CREDENTIAL
    {
        public int Flags;
        public int Type;
        public string TargetName;
        public string Comment;
        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref CREDENTIAL credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string targetName, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string targetName, int type, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr credentials);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern void CredFree(IntPtr buffer);

    #endregion
}

#endif
