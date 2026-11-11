// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Credentials.Stores;

/// <summary>
/// Seals and opens secrets with a key the operating system holds and never hands out. On Android this is an
/// AES-256-GCM key in the Android Keystore (hardware-backed where the device has it). Tests use a fake.
/// </summary>
public interface ISecretCipher
{
    /// <summary>Null when the key works; otherwise why not (no Keystore, key invalidated).</summary>
    string? UnavailableReason { get; }

    /// <summary>Encrypts <paramref name="plaintext"/>; <paramref name="associatedData"/> is authenticated but not stored.</summary>
    byte[] Seal(byte[] plaintext, byte[] associatedData);

    /// <summary>Decrypts and checks the tag. Throws when the blob or the associated data was changed.</summary>
    byte[] Open(byte[] sealedBlob, byte[] associatedData);
}

/// <summary>Where sealed blobs live: app-private preferences on Android, a dictionary in tests. Never plaintext.</summary>
public interface ISealedBlobStore
{
    string? Read(string key);

    void Write(string key, string base64Blob);

    void Delete(string key);
}

/// <summary>
/// <see cref="ISecretStore"/> over an OS-held key: each secret is sealed with <see cref="ISecretCipher"/>, bound
/// to its key name (so a blob copied under another name fails to open), and only the sealed blob is stored.
/// When the cipher is unavailable every write is refused (fail closed), the same as the other stores.
/// </summary>
public sealed class SealedSecretStore(string name, ISecretCipher cipher, ISealedBlobStore blobs) : ISecretStore
{
    private const string Prefix = "lfme.v1.";

    public string Name => name;

    public SecretProtection Protection => cipher.UnavailableReason is null ? SecretProtection.OperatingSystem : SecretProtection.None;

    public string? UnavailableReason => cipher.UnavailableReason;

    public Task WriteAsync(string key, RuntimeSecret secret, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        ArgumentNullException.ThrowIfNull(secret);
        Ready();
        byte[] blob;
        try
        {
            blob = cipher.Seal(Encoding.UTF8.GetBytes(secret.Reveal()), Bind(key));
        }
        catch (Exception exception) when (exception is not SecretStoreException)
        {
            throw new SecretStoreException($"{name} could not seal the secret ({exception.GetType().Name}).");
        }

        blobs.Write(Prefix + key, Convert.ToBase64String(blob));
        return Task.CompletedTask;
    }

    public Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        Ready();
        if (blobs.Read(Prefix + key) is not { Length: > 0 } stored)
            return Task.FromResult<RuntimeSecret?>(null);
        try
        {
            var plain = cipher.Open(Convert.FromBase64String(stored), Bind(key));
            return Task.FromResult<RuntimeSecret?>(new RuntimeSecret(Encoding.UTF8.GetString(plain)));
        }
        catch (Exception exception) when (exception is not SecretStoreException)
        {
            throw new SecretStoreException($"{name} could not open the saved secret ({exception.GetType().Name}). Save the login again.");
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        blobs.Delete(Prefix + key);
        return Task.CompletedTask;
    }

    private void Ready()
    {
        if (cipher.UnavailableReason is { } reason)
            throw new SecretStoreException(reason);
    }

    private static byte[] Bind(string key) => Encoding.UTF8.GetBytes("lfme-secret:" + key);
}
