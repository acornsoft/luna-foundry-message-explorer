// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Credentials.Stores;
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

/// <summary>A real CredentialVault over in-memory secrets and metadata, with its log captured.</summary>
internal sealed class TestVault
{
    public TestVault(string? unavailable = null, ISecretStore? store = null)
    {
        Store = store ?? (unavailable is null ? Secrets : new UnavailableSecretStore("test secure store", unavailable));
        Vault = new CredentialVault(Store, Metadata, log: Log.Add);
    }

    public MemorySecretStore Secrets { get; } = new();

    public ISecretStore Store { get; }

    public MemoryLoginMetadataStore Metadata { get; } = new();

    public List<string> Log { get; } = [];

    public CredentialVault Vault { get; }
}

/// <summary>The 0.6.0 saved strings, in memory.</summary>
internal sealed class FakeLegacy(Dictionary<string, string> items) : ILegacySavedConnections
{
    public Dictionary<string, string> Items { get; } = items;

    public IReadOnlyDictionary<string, string> ReadAll() => new Dictionary<string, string>(Items);

    public void Clear() => Items.Clear();
}

/// <summary>A browser-style store for UI labels: secrets in memory, protection reported as browser-protected.</summary>
internal sealed class FakeBrowserStore : ISecretStore
{
    private readonly MemorySecretStore _inner = new();

    public string Name => "browser storage (weaker than the OS store)";

    public SecretProtection Protection => SecretProtection.BrowserProtected;

    public string? UnavailableReason => null;

    public Task WriteAsync(string key, RuntimeSecret secret, CancellationToken cancellationToken = default) => _inner.WriteAsync(key, secret, cancellationToken);

    public Task<RuntimeSecret?> ReadAsync(string key, CancellationToken cancellationToken = default) => _inner.ReadAsync(key, cancellationToken);

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default) => _inner.DeleteAsync(key, cancellationToken);
}
