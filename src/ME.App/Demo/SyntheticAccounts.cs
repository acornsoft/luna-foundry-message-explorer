// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Shell.Accounts;
using Acornsoft.MessageExplorer.Shell.ViewModels;
using Azure;
using Azure.Core;

namespace Acornsoft.MessageExplorer.Shell.Demo;

/// <summary>
/// The synthetic accounts demo: alice@contoso.example in two tenants and bob@fabrikam.example, whose second
/// tenant asks for MFA (to show a per-tenant error). Made-up names, IDs and counts. No network, no browser.
/// </summary>
internal static class SyntheticAccounts
{
    private const string Contoso = "00000000-0000-0000-0000-00000000c001";
    private const string ContosoLab = "00000000-0000-0000-0000-00000000c002";
    private const string Fabrikam = "00000000-0000-0000-0000-00000000f001";
    private const string FabrikamPartners = "00000000-0000-0000-0000-00000000f002";

    private static readonly Dictionary<string, TenantRef[]> TenantsByUser = new(StringComparer.OrdinalIgnoreCase)
    {
        ["alice@contoso.example"] = [new(Contoso, "Contoso"), new(ContosoLab, "Contoso Lab")],
        ["bob@fabrikam.example"] = [new(Fabrikam, "Fabrikam"), new(FabrikamPartners, "Fabrikam Partners")],
    };

    private static readonly SubscriptionRef[] Subscriptions =
    [
        new("10000000-0000-0000-0000-000000000001", "Contoso Payments (demo)", Contoso),
        new("10000000-0000-0000-0000-000000000002", "Contoso Shared (demo)", Contoso),
        new("10000000-0000-0000-0000-000000000003", "Contoso Lab (demo)", ContosoLab),
        new("20000000-0000-0000-0000-000000000001", "Fabrikam Dev (demo)", Fabrikam),
    ];

    private static readonly NamespaceRef[] Namespaces =
    [
        Ns("sbns-demo-001", Subscriptions[0], "rg-payments-demo", "East US"),
        Ns("sbns-demo-002", Subscriptions[0], "rg-payments-demo", "East US 2"),
        Ns("sbns-demo-003", Subscriptions[1], "rg-shared-demo", "West Europe"),
        Ns("sbns-lab-001", Subscriptions[2], "rg-lab-demo", "Central US"),
        Ns("sbns-fabrikam-dev-001", Subscriptions[3], "rg-dev-demo", "UK South"),
    ];

    public static AccountServices Create()
    {
        var store = new MemoryAccountStore();
        foreach (var user in TenantsByUser.Keys)
            store.Save(Account(user));
        var vault = new DemoVault();
        vault.Write("sbns-demo-001 reader (demo)", "Endpoint=sb://sbns-demo-001.servicebus.windows.net/;" + "Shared" + "AccessKeyName=reader;" + "Shared" + "AccessKey=bm90LWEtcmVhbC1rZXk=");
        return new AccountServices(new Authenticator(), store, new Arm(), vault);
    }

    private static NamespaceRef Ns(string name, SubscriptionRef subscription, string group, string location) => new(
        name,
        $"{name}.servicebus.windows.net",
        $"/subscriptions/{subscription.SubscriptionId}/resourceGroups/{group}/providers/Microsoft.ServiceBus/namespaces/{name}",
        group,
        location,
        subscription.SubscriptionId,
        subscription.TenantId);

    private static StoredAccount Account(string user) =>
        new($"demo-{user}", user, TenantsByUser[user][0].TenantId, null, "{}");

    private sealed class Authenticator : IAccountAuthenticator
    {
        public string ClientId => EntraOptions.AzureCliClientId;

        public Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, CancellationToken cancellationToken)
        {
            var user = loginHint ?? "alice@contoso.example";
            return Task.FromResult(new SignedInAccount(Account(TenantsByUser.ContainsKey(user) ? user : "alice@contoso.example"), new DemoCredential(user)));
        }

        public Task<SignedInAccount> SignInSilentAsync(StoredAccount account, CancellationToken cancellationToken) =>
            Task.FromResult(new SignedInAccount(account, new DemoCredential(account.Username)));
    }

    private sealed class DemoCredential(string user) : TokenCredential
    {
        public string User { get; } = user;

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            new("demo-token-not-real", DateTimeOffset.UtcNow.AddHours(1));

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
            ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class Arm : IArmReader
    {
        public async IAsyncEnumerable<TenantRef> ListTenantsAsync(TokenCredential credential, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var tenant in TenantsByUser.GetValueOrDefault(((DemoCredential)credential).User, []))
                yield return tenant;
        }

        public async IAsyncEnumerable<SubscriptionRef> ListSubscriptionsAsync(TokenCredential credential, string tenantId, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            if (tenantId == FabrikamPartners)
                throw new RequestFailedException(401, "AADSTS50076 (synthetic): multi-factor authentication required.");
            foreach (var subscription in Subscriptions.Where(s => s.TenantId == tenantId))
                yield return subscription;
        }

        public async IAsyncEnumerable<NamespaceRef> ListNamespacesAsync(TokenCredential credential, SubscriptionRef subscription, [EnumeratorCancellation] CancellationToken cancellationToken)
        {
            await Task.CompletedTask;
            foreach (var ns in Namespaces.Where(n => n.SubscriptionId == subscription.SubscriptionId))
                yield return ns;
        }

        public IAsyncEnumerable<EntitySnapshot> ListQueuesAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, CancellationToken cancellationToken) =>
            Rows(e => e.Kind == EntityKind.Queue);

        public IAsyncEnumerable<EntitySnapshot> ListTopicsAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, CancellationToken cancellationToken) =>
            Rows(e => e.Kind == EntityKind.Topic);

        public IAsyncEnumerable<EntitySnapshot> ListSubscriptionsAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, string topicName, CancellationToken cancellationToken) =>
            Rows(e => e.Kind == EntityKind.Subscription && e.Parent == topicName);

        private static async IAsyncEnumerable<EntitySnapshot> Rows(Func<EntitySnapshot, bool> filter)
        {
            await Task.CompletedTask;
            foreach (var row in SyntheticDemoProvider.Entities.Where(filter))
                yield return row;
        }
    }

    private sealed class DemoVault : ISecretVault
    {
        private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);

        public string? UnavailableReason => null;

        public IReadOnlyList<string> Names() => _items.Keys.Order(StringComparer.OrdinalIgnoreCase).ToList();

        public string? Read(string name) => _items.GetValueOrDefault(name);

        public void Write(string name, string value) => _items[name] = value;

        public void Delete(string name) => _items.Remove(name);
    }
}
