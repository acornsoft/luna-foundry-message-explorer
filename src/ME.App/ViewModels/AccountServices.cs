// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Diagnostics;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Credentials.Stores;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.Demo;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>What the Accounts screen, Saved logins and the paste box need from the computer.</summary>
/// <param name="Vault">Every saved login: metadata in logins.json, secrets only in the OS secure store.</param>
/// <param name="TestProvider">The provider Test connection uses; the demo swaps in the synthetic one.</param>
/// <param name="Legacy">The 0.6.0 saved connection strings, imported once into the vault.</param>
public sealed record AccountServices(
    IAccountAuthenticator Authenticator,
    IAccountStore Store,
    IArmReader Arm,
    ICredentialVault? Vault,
    IBrokerProvider? TestProvider = null,
    ILegacySavedConnections? Legacy = null)
{
    /// <summary>
    /// Real Entra sign-in; accounts and logins in the vault (metadata in the config folder, secrets in this
    /// platform's secure store). Moves 0.6.0 account files into the vault before anything reads them.
    /// </summary>
    public static AccountServices ForThisComputer(Func<string, string?> environment)
    {
        var configDirectory = AppPaths.ConfigDirectory(environment);
        Directory.CreateDirectory(configDirectory);
        var vault = new CredentialVault(
            SecretStores.ForCurrentPlatform(),
            new FileLoginMetadataStore(Path.Combine(configDirectory, "logins.json")),
            log: message => Trace.WriteLine("lfme vault: " + message));
        var store = new VaultAccountStore(vault);
        LoginMigration.ImportAccountFiles(new FileAccountStore(configDirectory), store);
        return new AccountServices(
            new EntraAuthenticator(EntraOptions.Resolve(environment, configDirectory)),
            store,
            new ArmReader(),
            vault,
            Legacy: new LegacySavedConnections(configDirectory));
    }

    /// <summary>Moves the 0.6.0 saved connection strings into the vault. Leaves them alone when there is no secure store.</summary>
    public async Task ImportLegacyAsync()
    {
        if (Legacy is null || Vault is null)
            return;
        try
        {
            var moved = await LoginMigration.ImportSavedConnectionsAsync(Legacy, Vault).ConfigureAwait(true);
            if (moved > 0)
                Trace.WriteLine($"lfme vault: moved {moved} saved connection strings from 0.6.0");
        }
        catch (Exception exception) when (exception is SecretStoreException or LoginRejectedException or IOException)
        {
            Trace.WriteLine("lfme vault: 0.6.0 saved connection strings were not moved: " + exception.GetType().Name);
        }
    }

    /// <summary>Made-up accounts and namespaces, in memory, for screenshots and walkthroughs.</summary>
    public static AccountServices SyntheticDemo() => SyntheticAccounts.Create();

    /// <summary>Sign-in turned off; used when nothing else is supplied (view-model tests of other screens).</summary>
    public static AccountServices None() =>
        new(new UnavailableAuthenticator(), new MemoryAccountStore(), new ArmReader(), Vault: null);

    private sealed class UnavailableAuthenticator : IAccountAuthenticator
    {
        public string ClientId => EntraOptions.AzureCliClientId;

        public Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Entra sign-in is not set up in this window.");

        public Task<SignedInAccount> SignInSilentAsync(StoredAccount account, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Entra sign-in is not set up in this window.");
    }
}

/// <summary>Accounts kept in memory only.</summary>
public sealed class MemoryAccountStore : IAccountStore
{
    private readonly Dictionary<string, StoredAccount> _accounts = [];

    public IReadOnlyList<StoredAccount> Load() => _accounts.Values.ToList();

    public void Save(StoredAccount account) => _accounts[account.Id] = account;

    public void Delete(string id) => _accounts.Remove(id);
}
