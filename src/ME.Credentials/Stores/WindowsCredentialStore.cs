// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices;
using System.Text;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Credentials.Stores;

/// <summary>The three Credential Manager calls. The native one P/Invokes advapi32; tests use a fake.</summary>
public interface ICredentialManagerApi
{
    /// <summary>Returns 0 or a Win32 error code.</summary>
    int Write(string target, string userName, byte[] blob);

    /// <summary>Returns 0, <see cref="WindowsCredentialStore.NotFound"/>, or a Win32 error code.</summary>
    int Read(string target, out byte[]? blob);

    int Delete(string target);
}

/// <summary>
/// Windows Credential Manager generic credentials, current user, this computer only (no roaming). Windows
/// encrypts the blob with DPAPI for the signed-in user. Target = "{service}/{login ID}".
/// </summary>
public sealed class WindowsCredentialStore(ICredentialManagerApi api, string service) : ISecretStore
{
    public const int NotFound = 1168;
    public const int MaxBlobBytes = 5 * 512;

    public string Name => "Windows Credential Manager";

    public SecretProtection Protection => SecretProtection.OperatingSystem;

    public string? UnavailableReason { get; init; }

    public static ISecretStore Create(string service)
    {
        try
        {
            return new WindowsCredentialStore(new NativeCredentialManager(), service);
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            return new UnavailableSecretStore("Windows Credential Manager", "Windows Credential Manager could not be opened, so secrets can't be saved.");
        }
    }

    public string Target(string key) => $"{service}/{key}";

    public Task WriteAsync(string key, RuntimeSecret secret, CancellationToken cancellationToken = default)
    {
        var blob = Encoding.UTF8.GetBytes(secret.Reveal());
        try
        {
            if (blob.Length > MaxBlobBytes)
                throw new SecretStoreException($"That secret is too long for Windows Credential Manager ({MaxBlobBytes:N0} bytes at most).");
            Check(api.Write(Target(key), key, blob), "save");
        }
        finally
        {
            Array.Clear(blob);
        }

        return Task.CompletedTask;
    }

    public Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        var status = api.Read(Target(key), out var blob);
        if (status == NotFound || blob is null)
            return Task.FromResult<RuntimeSecret?>(null);
        try
        {
            Check(status, "read");
            return Task.FromResult<RuntimeSecret?>(new RuntimeSecret(Encoding.UTF8.GetString(blob)));
        }
        finally
        {
            Array.Clear(blob);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        var status = api.Delete(Target(key));
        if (status != NotFound)
            Check(status, "delete");
        return Task.CompletedTask;
    }

    private static void Check(int status, string action)
    {
        if (status != 0)
            throw new SecretStoreException($"Windows Credential Manager could not {action} the secret (error {status}).");
    }
}

/// <summary>advapi32 CredWriteW / CredReadW / CredDeleteW / CredFree.</summary>
internal sealed class NativeCredentialManager : ICredentialManagerApi
{
    private const int GenericType = 1;
    private const int PersistLocalMachine = 2;

    public int Write(string target, string userName, byte[] blob)
    {
        var targetPtr = Marshal.StringToCoTaskMemUni(target);
        var userPtr = Marshal.StringToCoTaskMemUni(userName);
        var blobPtr = Marshal.AllocCoTaskMem(Math.Max(blob.Length, 1));
        try
        {
            Marshal.Copy(blob, 0, blobPtr, blob.Length);
            var credential = new Credential
            {
                Type = GenericType,
                TargetName = targetPtr,
                CredentialBlobSize = blob.Length,
                CredentialBlob = blobPtr,
                Persist = PersistLocalMachine,
                UserName = userPtr,
            };
            return CredWriteW(ref credential, 0) ? 0 : Marshal.GetLastWin32Error();
        }
        finally
        {
            Marshal.Copy(new byte[blob.Length], 0, blobPtr, blob.Length);
            Marshal.FreeCoTaskMem(blobPtr);
            Marshal.FreeCoTaskMem(targetPtr);
            Marshal.FreeCoTaskMem(userPtr);
        }
    }

    public int Read(string target, out byte[]? blob)
    {
        blob = null;
        if (!CredReadW(target, GenericType, 0, out var pointer))
            return Marshal.GetLastWin32Error();
        try
        {
            var credential = Marshal.PtrToStructure<Credential>(pointer);
            blob = new byte[credential.CredentialBlobSize];
            if (credential.CredentialBlobSize > 0)
                Marshal.Copy(credential.CredentialBlob, blob, 0, credential.CredentialBlobSize);
            return 0;
        }
        finally
        {
            CredFree(pointer);
        }
    }

    public int Delete(string target) => CredDeleteW(target, GenericType, 0) ? 0 : Marshal.GetLastWin32Error();

    [StructLayout(LayoutKind.Sequential)]
    private struct Credential
    {
        public int Flags;
        public int Type;
        public IntPtr TargetName;
        public IntPtr Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;
        public IntPtr TargetAlias;
        public IntPtr UserName;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredWriteW(ref Credential credential, int flags);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr buffer);
}
