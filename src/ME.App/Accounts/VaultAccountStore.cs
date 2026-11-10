// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Security.Cryptography;
using System.Text;
using Acornsoft.MessageExplorer.Credentials;

namespace Acornsoft.MessageExplorer.Shell.Accounts;

/// <summary>
/// Entra accounts as logins in the credential vault (kind EntraAccount). Only the account's name, tenant and
/// AuthenticationRecord are kept, in the config JSON; tokens stay in the MSAL "lfme" cache in the OS store.
/// </summary>
public sealed class VaultAccountStore(ICredentialVault vault) : IAccountStore
{
    public static string LoginId(string accountId) =>
        "entra-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(accountId)))[..16].ToLowerInvariant();

    public IReadOnlyList<StoredAccount> Load() => vault.List()
        .Where(e => e.Kind == LoginKind.EntraAccount && e.Field("homeAccountId").Length > 0)
        .Select(e => new StoredAccount(
            e.Field("homeAccountId"),
            e.Field("username"),
            e.Field("tenantId"),
            e.Fields.GetValueOrDefault("requestedTenant"),
            e.Field("authenticationRecord"),
            e.Field("signedOut") == "true"))
        .ToList();

    public void Save(StoredAccount account)
    {
        ArgumentNullException.ThrowIfNull(account);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["username"] = account.Username,
            ["tenantId"] = account.TenantId,
            ["homeAccountId"] = account.Id,
            ["authenticationRecord"] = account.RecordJson,
            ["signedOut"] = account.SignedOut ? "true" : "false",
        };
        if (account.RequestedTenant is { } tenant)
            fields["requestedTenant"] = tenant;
        vault.Put(LoginId(account.Id), new LoginDraft(account.Username, LoginKind.EntraAccount, fields));
    }

    public void Delete(string id)
    {
        // An Entra login has no secret, so deleting it touches only the config JSON and finishes at once.
        vault.DeleteAsync(LoginId(id)).GetAwaiter().GetResult();
    }
}
