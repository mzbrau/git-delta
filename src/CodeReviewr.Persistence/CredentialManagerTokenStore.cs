using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using CodeReviewr.Core.Abstractions;

namespace CodeReviewr.Persistence;

/// <summary>Windows Credential Manager-backed token store.</summary>
[SupportedOSPlatform("windows")]
public sealed class CredentialManagerTokenStore : ITokenStore
{
    private const string TargetPrefix = "CodeReviewr/";

    public Task SetTokenAsync(string host, string login, string token, CancellationToken ct = default)
    {
        var target = MakeTarget(host, login);
        var tokenBytes = Encoding.Unicode.GetBytes(token + '\0');
        var blob = Marshal.AllocCoTaskMem(tokenBytes.Length);
        Marshal.Copy(tokenBytes, 0, blob, tokenBytes.Length);

        var credential = new NativeMethods.Credential
        {
            Type = NativeMethods.CredentialType.Generic,
            TargetName = target,
            UserName = login,
            CredentialBlob = blob,
            CredentialBlobSize = (uint)tokenBytes.Length,
            Persist = NativeMethods.CredentialPersist.LocalMachine,
        };

        try
        {
            if (!NativeMethods.CredWriteW(ref credential, 0))
                throw new InvalidOperationException($"CredWrite failed: {Marshal.GetLastWin32Error()}");
        }
        finally
        {
            Marshal.FreeCoTaskMem(blob);
        }

        return Task.CompletedTask;
    }

    public Task<string?> GetTokenAsync(string host, string login, CancellationToken ct = default)
    {
        var target = MakeTarget(host, login);
        if (!NativeMethods.CredReadW(target, NativeMethods.CredentialType.Generic, 0, out var ptr))
        {
            var error = Marshal.GetLastWin32Error();
            return error == 1168 // ERROR_NOT_FOUND
                ? Task.FromResult<string?>(null)
                : Task.FromException<string?>(new InvalidOperationException($"CredRead failed: {error}"));
        }

        try
        {
            var cred = Marshal.PtrToStructure<NativeMethods.Credential>(ptr)!;
            if (cred.CredentialBlobSize == 0)
                return Task.FromResult<string?>(null);

            var bytes = new byte[cred.CredentialBlobSize];
            Marshal.Copy(cred.CredentialBlob, bytes, 0, (int)cred.CredentialBlobSize);
            var token = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
            return Task.FromResult<string?>(token);
        }
        finally
        {
            NativeMethods.CredFree(ptr);
        }
    }

    public Task DeleteTokenAsync(string host, string login, CancellationToken ct = default)
    {
        var target = MakeTarget(host, login);
        if (!NativeMethods.CredDeleteW(target, NativeMethods.CredentialType.Generic, 0))
        {
            var error = Marshal.GetLastWin32Error();
            if (error != 1168)
                throw new InvalidOperationException($"CredDelete failed: {error}");
        }

        return Task.CompletedTask;
    }

    private static string MakeTarget(string host, string login) =>
        TargetPrefix + MemoryTokenStore.MakeKey(host, login);

    private static class NativeMethods
    {
        internal enum CredentialType : uint
        {
            Generic = 1,
        }

        internal enum CredentialPersist : uint
        {
            LocalMachine = 2,
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct FileTime
        {
            public uint LowDateTime;
            public uint HighDateTime;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct Credential
        {
            public uint Flags;
            public CredentialType Type;
            public string TargetName;
            public string? Comment;
            public FileTime LastWritten;
            public uint CredentialBlobSize;
            public IntPtr CredentialBlob;
            public CredentialPersist Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public string? TargetAlias;
            public string UserName;
        }

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredWriteW(ref Credential credential, uint flags);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredReadW(
            string target,
            CredentialType type,
            int reservedFlag,
            out IntPtr credentialPtr);

        [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool CredDeleteW(string target, CredentialType type, int reservedFlag);

        [DllImport("advapi32.dll")]
        internal static extern void CredFree(IntPtr buffer);
    }
}
