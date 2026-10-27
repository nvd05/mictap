using System.ComponentModel;
using System.Runtime.InteropServices;

namespace MicTap.Core;

/// <summary>
/// The user's cloud keys in the Windows Credential Manager (generic credentials of the user), not in settings.json and
/// never in logs. The user pastes a key in the settings dialog; Windows keeps it encrypted for this user.
/// </summary>
public static class CloudKeys
{
    public const string RouterAi = "mictap/routerai";

    private const uint GenericCredential = 1; // CRED_TYPE_GENERIC
    private const uint PersistLocalMachine = 2; // CRED_PERSIST_LOCAL_MACHINE: kept across sign-ins, this user only
    private const int NotFound = 1168; // ERROR_NOT_FOUND

    /// <summary>The key, or null when none is saved.</summary>
    public static string? Read(string target = RouterAi)
    {
        if (!CredRead(target, GenericCredential, 0, out IntPtr found))
        {
            int error = Marshal.GetLastWin32Error();
            return error == NotFound ? null : throw new Win32Exception(error, $"Credential Manager did not read {target}");
        }

        try
        {
            Credential credential = Marshal.PtrToStructure<Credential>(found);
            return Marshal.PtrToStringUni(credential.CredentialBlob, (int)credential.CredentialBlobSize / 2);
        }
        finally
        {
            CredFree(found);
        }
    }

    public static void Save(string key, string target = RouterAi)
    {
        IntPtr blob = Marshal.StringToCoTaskMemUni(key);
        try
        {
            var credential = new Credential
            {
                Type = GenericCredential,
                TargetName = target,
                CredentialBlob = blob,
                CredentialBlobSize = (uint)(key.Length * 2),
                Persist = PersistLocalMachine,
                UserName = Environment.UserName,
            };
            if (!CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), $"Credential Manager did not save {target}");
            }
        }
        finally
        {
            Marshal.ZeroFreeCoTaskMemUnicode(blob);
        }
    }

    /// <summary>Removes the key; nothing happens when none is saved.</summary>
    public static void Delete(string target = RouterAi)
    {
        if (!CredDelete(target, GenericCredential, 0) && Marshal.GetLastWin32Error() is var error && error != NotFound)
        {
            throw new Win32Exception(error, $"Credential Manager did not delete {target}");
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        public string? TargetAlias;
        public string? UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWrite(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
