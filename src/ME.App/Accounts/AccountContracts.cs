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
    bool SignedOut = false);

/// <summary>A signed-in account and the credential that gets its tokens.</summary>
public sealed record SignedInAccount(StoredAccount Account, TokenCredential Credential);

/// <summary>Signs accounts in. The app uses the system browser; tests use a fake.</summary>
public interface IAccountAuthenticator
{
    /// <summary>The Entra application (client) ID that sign-in uses.</summary>
    string ClientId { get; }

    /// <summary>Opens the system browser. <paramref name="tenant"/> null means any work or school account.</summary>
    Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, CancellationToken cancellationToken);

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
