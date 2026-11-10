// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.Shell.Tests;

/// <summary>Signs in made-up accounts. Interactive sign-ins come from a queue; silent ones can be told to fail.</summary>
internal sealed class FakeAuthenticator : IAccountAuthenticator
{
    public string ClientId => "00000000-0000-0000-0000-0000000000aa";

    public Queue<Func<StoredAccount>> NextInteractive { get; } = new();

    public HashSet<string> SilentFailures { get; } = new(StringComparer.OrdinalIgnoreCase);

    public List<string> SilentCalls { get; } = [];

    public List<(string? Tenant, string? Hint)> InteractiveCalls { get; } = [];

    public Dictionary<string, FakeCredential> Credentials { get; } = [];

    public static StoredAccount Account(string username, string tenant = "00000000-0000-0000-0000-00000000c001") =>
        new($"{username}-home", username, tenant, null, "{\"username\":\"" + username + "\"}");

    public void WillSignIn(string username, string tenant = "00000000-0000-0000-0000-00000000c001") =>
        NextInteractive.Enqueue(() => Account(username, tenant));

    public Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, CancellationToken cancellationToken)
    {
        InteractiveCalls.Add((tenant, loginHint));
        if (!NextInteractive.TryDequeue(out var next))
            throw new AuthenticationFailedException("The browser window was closed before sign-in finished.");
        var account = next() with { RequestedTenant = tenant };
        return Task.FromResult(new SignedInAccount(account, CredentialFor(account.Username)));
    }

    public Task<SignedInAccount> SignInSilentAsync(StoredAccount account, CancellationToken cancellationToken)
    {
        SilentCalls.Add(account.Username);
        if (SilentFailures.Contains(account.Username))
            throw new AuthenticationRequiredException("Interactive sign-in is required.", new Azure.Core.TokenRequestContext(["https://management.azure.com/.default"]));
        return Task.FromResult(new SignedInAccount(account, CredentialFor(account.Username)));
    }

    private FakeCredential CredentialFor(string username)
    {
        var name = username.Split('@')[0];
        if (!Credentials.TryGetValue(name, out var credential))
            Credentials[name] = credential = new FakeCredential(name);
        return credential;
    }
}

internal sealed class MemoryAccountStore : IAccountStore
{
    public Dictionary<string, StoredAccount> Saved { get; } = [];

    public IReadOnlyList<StoredAccount> Load() => Saved.Values.ToList();

    public void Save(StoredAccount account) => Saved[account.Id] = account;

    public void Delete(string id) => Saved.Remove(id);
}

internal sealed class MemoryVault : ISecretVault
{
    public Dictionary<string, string> Items { get; } = new(StringComparer.OrdinalIgnoreCase);

    public string? UnavailableReason { get; init; }

    public IReadOnlyList<string> Names() => Items.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

    public string? Read(string name) => Items.GetValueOrDefault(name);

    public void Write(string name, string value) => Items[name] = value;

    public void Delete(string name) => Items.Remove(name);
}
