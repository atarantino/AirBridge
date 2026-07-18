using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AirBridge.App;

internal interface IOpenAiCredentialStore
{
    bool IsConfigured { get; }
    string? Read();
    void Write(string apiKey);
    void Delete();
}

/// <summary>Stores the API key in the current Windows user's Credential Manager vault.</summary>
internal sealed class WindowsOpenAiCredentialStore : IOpenAiCredentialStore
{
    private const string DefaultTargetName = "AirBridge/OpenAI API Key";
    private const uint CredentialTypeGeneric = 1;
    private const uint CredentialPersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int MaxCredentialBlobBytes = 2560;
    private readonly string _targetName;

    public WindowsOpenAiCredentialStore() : this(DefaultTargetName) { }

    internal WindowsOpenAiCredentialStore(string targetName)
    {
        if (string.IsNullOrWhiteSpace(targetName)) throw new ArgumentException("A credential target is required.", nameof(targetName));
        _targetName = targetName;
    }

    public bool IsConfigured
    {
        get
        {
            if (CredRead(_targetName, CredentialTypeGeneric, 0, out var credentialPointer))
            {
                CredFree(credentialPointer);
                return true;
            }
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return false;
            throw new Win32Exception(error, "Could not check the saved OpenAI API key in Windows Credential Manager.");
        }
    }

    public string? Read()
    {
        if (!CredRead(_targetName, CredentialTypeGeneric, 0, out var credentialPointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (error == ErrorNotFound) return null;
            throw new Win32Exception(error, "Could not read the saved OpenAI API key from Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(credentialPointer);
            if (credential.CredentialBlob == IntPtr.Zero || credential.CredentialBlobSize == 0) return null;
            return Marshal.PtrToStringUni(credential.CredentialBlob, checked((int)credential.CredentialBlobSize / sizeof(char)));
        }
        finally { CredFree(credentialPointer); }
    }

    public void Write(string apiKey)
    {
        var normalized = apiKey.Trim();
        if (string.IsNullOrWhiteSpace(normalized)) throw new ArgumentException("Enter an OpenAI API key.", nameof(apiKey));
        var blobBytes = checked(normalized.Length * sizeof(char));
        if (blobBytes > MaxCredentialBlobBytes) throw new ArgumentException("The OpenAI API key is too long.", nameof(apiKey));

        var blob = Marshal.StringToCoTaskMemUni(normalized);
        try
        {
            var credential = new NativeCredential
            {
                Type = CredentialTypeGeneric,
                TargetName = _targetName,
                CredentialBlobSize = (uint)blobBytes,
                CredentialBlob = blob,
                Persist = CredentialPersistLocalMachine,
                UserName = "OpenAI"
            };
            if (!CredWrite(ref credential, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not save the OpenAI API key in Windows Credential Manager.");
        }
        finally
        {
            for (var offset = 0; offset < blobBytes; offset += sizeof(short)) Marshal.WriteInt16(blob, offset, 0);
            Marshal.FreeCoTaskMem(blob);
        }
    }

    public void Delete()
    {
        if (CredDelete(_targetName, CredentialTypeGeneric, 0)) return;
        var error = Marshal.GetLastWin32Error();
        if (error != ErrorNotFound)
            throw new Win32Exception(error, "Could not remove the saved OpenAI API key from Windows Credential Manager.");
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public uint Flags;
        public uint Type;
        [MarshalAs(UnmanagedType.LPWStr)] public string TargetName;
        [MarshalAs(UnmanagedType.LPWStr)] public string? Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public IntPtr CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public IntPtr Attributes;
        [MarshalAs(UnmanagedType.LPWStr)] public string? TargetAlias;
        [MarshalAs(UnmanagedType.LPWStr)] public string UserName;
    }

    [DllImport("advapi32.dll", EntryPoint = "CredReadW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredRead(string target, uint type, uint flags, out IntPtr credential);

    [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWrite(ref NativeCredential credential, uint flags);

    [DllImport("advapi32.dll", EntryPoint = "CredDeleteW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDelete(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
