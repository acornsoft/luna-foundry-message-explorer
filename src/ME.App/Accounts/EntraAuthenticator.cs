// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Azure.Core;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>
/// Real Entra sign-in: <see cref="InteractiveBrowserCredential"/> in the system browser. The token cache is
/// persisted under the name "lfme" in the OS secure store (Keychain on macOS). Only the AuthenticationRecord
/// (no token) is returned for the app to keep, and it brings the account back silently on the next launch.
/// </summary>
public sealed class EntraAuthenticator(EntraOptions options) : IAccountAuthenticator
{
    /// <summary>Azure Resource Manager, for listing tenants, subscriptions and namespaces.</summary>
    public static readonly string[] ArmScopes = ["https://management.azure.com/.default"];

    public string ClientId => options.ClientId;

    public async Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, CancellationToken cancellationToken)
    {
        var requested = string.IsNullOrWhiteSpace(tenant) ? null : tenant.Trim();
        var credential = new InteractiveBrowserCredential(Options(requested, record: null, loginHint));
        var record = await credential.AuthenticateAsync(new TokenRequestContext(ArmScopes), cancellationToken).ConfigureAwait(false);
        return new SignedInAccount(await ToStoredAsync(record, requested, cancellationToken).ConfigureAwait(false), credential);
    }

    public async Task<SignedInAccount> SignInSilentAsync(StoredAccount account, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        AuthenticationRecord record;
        using (var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(account.RecordJson)))
            record = await AuthenticationRecord.DeserializeAsync(stream, cancellationToken).ConfigureAwait(false);

        var credential = new InteractiveBrowserCredential(Options(account.RequestedTenant, record, account.Username));

        // Proves the cached sign-in still works. Throws AuthenticationRequiredException when it does not.
        await credential.GetTokenAsync(new TokenRequestContext(ArmScopes), cancellationToken).ConfigureAwait(false);
        return new SignedInAccount(account with { SignedOut = false }, credential);
    }

    private InteractiveBrowserCredentialOptions Options(string? tenant, AuthenticationRecord? record, string? loginHint)
    {
        var result = new InteractiveBrowserCredentialOptions
        {
            ClientId = options.ClientId,
            TenantId = tenant ?? EntraOptions.AnyWorkAccountTenant,
            LoginHint = loginHint,
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = record is not null,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = options.CacheName },
        };

        // One sign-in, then tokens for every tenant the account belongs to.
        result.AdditionallyAllowedTenants.Add("*");
        return result;
    }

    private static async Task<StoredAccount> ToStoredAsync(AuthenticationRecord record, string? requestedTenant, CancellationToken cancellationToken)
    {
        using var stream = new MemoryStream();
        await record.SerializeAsync(stream, cancellationToken).ConfigureAwait(false);
        var json = System.Text.Encoding.UTF8.GetString(stream.ToArray());
        return new StoredAccount(record.HomeAccountId, record.Username, record.TenantId, requestedTenant, json);
    }
}
