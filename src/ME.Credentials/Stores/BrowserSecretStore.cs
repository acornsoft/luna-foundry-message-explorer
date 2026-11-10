// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Credentials.Stores;

/// <summary>
/// The browser calls the web store needs: a non-extractable WebCrypto AES-GCM key kept in IndexedDB, and an
/// IndexedDB table of encrypted records. A WASM app head supplies the real one; tests use a fake.
/// </summary>
public interface IBrowserCryptoInterop
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Creates the key once (extractable: false) and keeps the CryptoKey object in IndexedDB.</summary>
    Task EnsureKeyAsync(string keyName, CancellationToken cancellationToken);

    Task<byte[]> EncryptAsync(string keyName, byte[] iv, byte[] plaintext, byte[] associatedData, CancellationToken cancellationToken);

    /// <summary>Throws when the record was changed or belongs to another ID (GCM tag check).</summary>
    Task<byte[]> DecryptAsync(string keyName, byte[] iv, byte[] ciphertext, byte[] associatedData, CancellationToken cancellationToken);

    Task PutRecordAsync(string id, byte[] record, CancellationToken cancellationToken);

    Task<byte[]?> GetRecordAsync(string id, CancellationToken cancellationToken);

    Task DeleteRecordAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// The web backend. Never puts a raw secret in localStorage: each secret is AES-GCM encrypted with a
/// non-extractable WebCrypto key, with the login ID as associated data, and only the ciphertext goes to
/// IndexedDB. Script running in the same origin can still ask the browser to decrypt, so this is weaker
/// than an OS store and the UI says so. Entra sign-in is the preferred way to log in on the web.
/// </summary>
public sealed class BrowserSecretStore(IBrowserCryptoInterop browser, string keyName = "lfme-logins") : ISecretStore
{
    private const byte Version = 1;
    private const int IvBytes = 12;
    private bool? _available;

    public string Name => "browser storage (weaker than the OS store)";

    public SecretProtection Protection => SecretProtection.BrowserProtected;

    public string? UnavailableReason => _available == false
        ? "This browser has no WebCrypto or IndexedDB here (private window or old browser), so secrets can't be saved."
        : null;

    /// <summary>Call once at startup. Until it returns true, every write is refused.</summary>
    public async Task<bool> InitializeAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            _available = await browser.IsAvailableAsync(cancellationToken).ConfigureAwait(false);
            if (_available == true)
                await browser.EnsureKeyAsync(keyName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _available = false;
        }

        return _available == true;
    }

    public async Task WriteAsync(string key, RuntimeSecret secret, CancellationToken cancellationToken = default)
    {
        Ready();
        var iv = RandomNumberGenerator.GetBytes(IvBytes);
        var plaintext = Encoding.UTF8.GetBytes(secret.Reveal());
        try
        {
            var ciphertext = await browser.EncryptAsync(keyName, iv, plaintext, Encoding.UTF8.GetBytes(key), cancellationToken).ConfigureAwait(false);
            await browser.PutRecordAsync(key, [Version, .. iv, .. ciphertext], cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    public async Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        Ready();
        var record = await browser.GetRecordAsync(key, cancellationToken).ConfigureAwait(false);
        if (record is null)
            return null;
        if (record.Length <= 1 + IvBytes || record[0] != Version)
            throw new SecretStoreException("A saved secret in browser storage is damaged. Save the login again.");
        byte[] plaintext;
        try
        {
            plaintext = await browser.DecryptAsync(keyName, record[1..(1 + IvBytes)], record[(1 + IvBytes)..], Encoding.UTF8.GetBytes(key), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
        {
            throw new SecretStoreException("A saved secret in browser storage could not be decrypted. Save the login again.");
        }

        try
        {
            return new RuntimeSecret(Encoding.UTF8.GetString(plaintext));
        }
        finally
        {
            Array.Clear(plaintext);
        }
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        _available == true ? browser.DeleteRecordAsync(key, cancellationToken) : Task.CompletedTask;

    private void Ready()
    {
        if (_available != true)
            throw new SecretStoreException(UnavailableReason ?? "Browser storage is not ready yet.");
    }
}
