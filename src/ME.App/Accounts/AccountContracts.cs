// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Azure.Core;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>
/// What the app keeps about a signed-in account: who it is and where, plus the Azure.Identity
/// AuthenticationRecord as JSON. No token and no secret. Tokens live in the OS secure store (Keychain).
/// </summary>
public sealed record StoredAccount(
    string Id,
    string Username,
    string TenantId,
    string? RequestedTenant,
    string RecordJson,
    bool SignedOut = false,
    string Browser = BrowserChoices.SystemDefaultId);

/// <summary>
/// How one sign-in reaches the user: the browser to open, and callbacks that hand the sign-in link (for
/// Copy sign-in link) or the device code message (for Sign in with a code) to the screen.
/// </summary>
public sealed record SignInPrompt(BrowserChoice Browser, Action<Uri>? LinkReady = null, Action<string>? DeviceCode = null)
{
    public static SignInPrompt Default { get; } = new(BrowserChoices.SystemDefault);
}

/// <summary>A signed-in account and the credential that gets its tokens.</summary>
public sealed record SignedInAccount(StoredAccount Account, TokenCredential Credential);

/// <summary>Signs accounts in. The app uses the system browser; tests use a fake.</summary>
public interface IAccountAuthenticator
{
    /// <summary>The Entra application (client) ID that sign-in uses.</summary>
    string ClientId { get; }

    /// <summary>
    /// Opens the sign-in page in <see cref="SignInPrompt.Browser"/> and waits on the localhost redirect.
    /// <paramref name="tenant"/> null means any work or school account. Cancelling the token stops the
    /// wait and frees the localhost listener.
    /// </summary>
    Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, SignInPrompt prompt, CancellationToken cancellationToken);

    /// <summary>Device code flow: shows a code to enter at the Microsoft device login page in any browser, on any device.</summary>
    Task<SignedInAccount> SignInWithDeviceCodeAsync(string? tenant, SignInPrompt prompt, CancellationToken cancellationToken);

    /// <summary>Signs in from the stored record and the token cache, without a browser. Throws when that is not possible.</summary>
    Task<SignedInAccount> SignInSilentAsync(StoredAccount account, CancellationToken cancellationToken);
}

/// <summary>Keeps <see cref="StoredAccount"/> records between launches.</summary>
public interface IAccountStore
{
    IReadOnlyList<StoredAccount> Load();

    void Save(StoredAccount account);

    void Delete(string id);
}

/// <summary>The connection strings saved by version 0.6.0 in one secure-store item. Read once, imported, then cleared.</summary>
public interface ILegacySavedConnections
{
    IReadOnlyDictionary<string, string> ReadAll();

    void Clear();
}
