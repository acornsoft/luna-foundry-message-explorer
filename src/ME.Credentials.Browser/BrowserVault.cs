// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Acornsoft.MessageExplorer.Credentials.Stores;

namespace Acornsoft.MessageExplorer.Credentials.Browser;

/// <summary>
/// The web app's secret store: <see cref="BrowserSecretStore"/> over lfme-vault.js. This is the only script
/// the app uses (an approved exception for the web credential vault). It runs only in a browser.
/// </summary>
[SupportedOSPlatform("browser")]
public static class BrowserVault
{
    public const string ModuleName = "lfme-vault";

    /// <summary>Loads the module (served by the app head, by default next to the page) and readies the store.</summary>
    /// <returns>A ready store, or one whose <see cref="ISecretStore.UnavailableReason"/> says why secrets can't be saved.</returns>
    public static async Task<BrowserSecretStore> CreateAsync(string moduleUrl = "./lfme-vault.js", CancellationToken cancellationToken = default)
    {
        await JSHost.ImportAsync(ModuleName, moduleUrl, cancellationToken).ConfigureAwait(false);
        var store = new BrowserSecretStore(new JsBrowserCrypto());
        await store.InitializeAsync(cancellationToken).ConfigureAwait(false);
        return store;
    }
}

/// <summary>[JSImport] bindings to lfme-vault.js. Script can't be cancelled, so tokens are checked before each call.</summary>
[SupportedOSPlatform("browser")]
internal sealed partial class JsBrowserCrypto : IBrowserCryptoInterop
{
    public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return IsAvailable();
    }

    public Task EnsureKeyAsync(string keyName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return EnsureKey(keyName);
    }

    public Task EncryptAsync(string keyName, string id, string plaintext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Encrypt(keyName, id, plaintext);
    }

    public Task<string?> DecryptAsync(string keyName, string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Decrypt(keyName, id);
    }

    public Task DeleteAsync(string id, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Remove(id);
    }

    [JSImport("isAvailable", BrowserVault.ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Boolean>>]
    private static partial Task<bool> IsAvailable();

    [JSImport("ensureKey", BrowserVault.ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task EnsureKey(string keyName);

    [JSImport("encrypt", BrowserVault.ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task Encrypt(string keyName, string id, string plaintext);

    [JSImport("decrypt", BrowserVault.ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.String>>]
    private static partial Task<string?> Decrypt(string keyName, string id);

    [JSImport("remove", BrowserVault.ModuleName)]
    [return: JSMarshalAs<JSType.Promise<JSType.Void>>]
    private static partial Task Remove(string id);
}
