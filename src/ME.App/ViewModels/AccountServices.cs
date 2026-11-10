// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.Demo;

namespace Acornsoft.MessageExplorer.Shell.ViewModels;

/// <summary>What the Accounts screen and the paste box need from the computer.</summary>
public sealed record AccountServices(IAccountAuthenticator Authenticator, IAccountStore Store, IArmReader Arm, ISecretVault? Vault)
{
    /// <summary>Real Entra sign-in, account records in the config folder, saved strings in the OS secure store.</summary>
    public static AccountServices ForThisComputer(Func<string, string?> environment)
    {
        var configDirectory = AppPaths.ConfigDirectory(environment);
        Directory.CreateDirectory(configDirectory);
        return new AccountServices(
            new EntraAuthenticator(EntraOptions.Resolve(environment, configDirectory)),
            new FileAccountStore(configDirectory),
            new ArmReader(),
            new SecureStoreVault(configDirectory));
    }

    /// <summary>Made-up accounts and namespaces, in memory, for screenshots and walkthroughs.</summary>
    public static AccountServices SyntheticDemo() => SyntheticAccounts.Create();

    /// <summary>Sign-in turned off; used when nothing else is supplied (view-model tests of other screens).</summary>
    public static AccountServices None() =>
        new(new UnavailableAuthenticator(), new MemoryAccountStore(), new ArmReader(), null);

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
