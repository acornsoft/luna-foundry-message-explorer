// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Credentials.Stores;

/// <summary>
/// The browser calls the web store needs, as provided by <c>lfme-vault.js</c>: a non-extractable WebCrypto
/// AES-GCM 256 key kept in IndexedDB, and encrypt, decrypt and delete over an IndexedDB table of records.
/// The module picks a fresh 96-bit IV for every write and binds each record to its login ID (associated
/// data). The browser project supplies the real one through [JSImport]; tests use a fake.
/// </summary>
public interface IBrowserCryptoInterop
{
    Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

    /// <summary>Creates the key once (extractable: false) and keeps the CryptoKey object in IndexedDB.</summary>
    Task EnsureKeyAsync(string keyName, CancellationToken cancellationToken);

    /// <summary>Encrypts with a fresh IV and stores only { version, iv, ciphertext } under the login ID.</summary>
    Task EncryptAsync(string keyName, string id, string plaintext, CancellationToken cancellationToken);

    /// <summary>Null when nothing is stored. Throws when the record was changed or belongs to another ID.</summary>
    Task<string?> DecryptAsync(string keyName, string id, CancellationToken cancellationToken);

    Task DeleteAsync(string id, CancellationToken cancellationToken);
}

/// <summary>
/// The web backend. Never puts a raw secret in localStorage or sessionStorage: each secret is AES-GCM
/// encrypted by the browser with a non-extractable key, with the login ID as associated data, and only the
/// ciphertext goes to IndexedDB. Script running in the same origin can still ask the browser to decrypt, so
/// this is weaker than an OS store and the UI says so. Entra sign-in is the preferred way to log in on the web.
/// </summary>
public sealed class BrowserSecretStore(IBrowserCryptoInterop browser, string keyName = "lfme-logins") : ISecretStore
{
    private bool? _available;

    public string Name => "browser storage (weaker than the OS store)";

    public SecretProtection Protection => SecretProtection.BrowserProtected;

    public string? UnavailableReason => _available == false
        ? "This browser has no WebCrypto or IndexedDB here (private window, old browser, or not a secure page), so secrets can't be saved."
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
        ArgumentNullException.ThrowIfNull(secret);
        Ready();
        try
        {
            await browser.EncryptAsync(keyName, key, secret.Reveal(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not (OperationCanceledException or SecretStoreException))
        {
            // Browser errors don't carry the secret, but say it plainly rather than pass them through.
            throw new SecretStoreException("The secret could not be saved in browser storage.");
        }
    }

    public async Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default)
    {
        Ready();
        string? plaintext;
        try
        {
            plaintext = await browser.DecryptAsync(keyName, key, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new SecretStoreException("A saved secret in browser storage could not be decrypted. Save the login again.");
        }

        return plaintext is null ? null : new RuntimeSecret(plaintext);
    }

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) =>
        _available == true ? browser.DeleteAsync(key, cancellationToken) : Task.CompletedTask;

    private void Ready()
    {
        if (_available != true)
            throw new SecretStoreException(UnavailableReason ?? "Browser storage is not ready yet.");
    }
}
