// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Credentials;
using Acornsoft.MessageExplorer.Credentials.Stores;
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
        var vault = new CredentialVault(new MemorySecretStore(), new MemoryLoginMetadataStore(), Clock.Instance);
        var store = new VaultAccountStore(vault);
        foreach (var user in TenantsByUser.Keys)
            store.Save(Account(user));
        SeedLogins(vault);

        // One 0.6.0-style saved string, so the demo shows it moving into Saved logins on launch.
        var legacy = new DemoLegacy(new Dictionary<string, string>
        {
            ["sbns-demo-001 reader (demo)"] = "Endpoint=sb://sbns-demo-001.servicebus.windows.net/;" + "Shared" + "AccessKeyName=reader;" + "Shared" + "AccessKey=bm90LWEtcmVhbC1rZXk=",
        });
        return new AccountServices(new Authenticator(), store, new Arm(), vault, new SyntheticDemoProvider(), legacy);
    }

    /// <summary>Made-up logins for every system. All secrets are fake.</summary>
    private static void SeedLogins(CredentialVault vault)
    {
        static Dictionary<string, string> F(params (string Key, string Value)[] pairs) => pairs.ToDictionary(p => p.Key, p => p.Value);
        var alice = Account("alice@contoso.example");
        var bob = Account("bob@fabrikam.example");
        var seeds = new (LoginDraft Draft, string? Secret, int? UsedMinutesAgo)[]
        {
            (new("Orders namespace (demo)", LoginKind.ServiceBusEntra, F(("namespace", "sbns-demo-002.servicebus.windows.net"), ("accountName", alice.Username)), alice.Id), null, 42),
            (new("Telemetry hub (demo)", LoginKind.EventHubsEntra, F(("namespace", "evhns-demo-001.servicebus.windows.net"), ("accountName", bob.Username)), bob.Id), null, null),
            (new("Kafka dev cluster (demo)", LoginKind.KafkaSasl, F(("bootstrapServers", "kafka-demo.contoso.example:9093"), ("mechanism", "SCRAM-SHA-512"), ("username", "demo-reader"))), "fake-kafka-secret-demo", 1500),
            (new("Kafka mTLS lab (demo)", LoginKind.KafkaMutualTls, F(("bootstrapServers", "kafka-lab.contoso.example:9094"), ("certificatePath", "~/certs/demo-client.pem"), ("keyPath", "~/certs/demo-client.key"))), null, null),
            (new("RabbitMQ staging (demo)", LoginKind.RabbitMqPassword, F(("host", "rabbit-demo.fabrikam.example"), ("port", "5671"), ("virtualHost", "/"), ("username", "demo-user"))), "fake-rabbit-secret-demo", 4320),
            (new("AWS sandbox profile (demo)", LoginKind.AwsProfile, F(("profileName", "contoso-sandbox-demo"), ("region", "us-east-1"))), null, null),
            (new("AWS reader keys (demo)", LoginKind.AwsAccessKey, F(("region", "eu-west-1"), ("accessKeyId", "AKIAEXAMPLEDEMO00001"))), "fake-aws-secret-demo", null),
            (new("Local emulator (demo)", LoginKind.EmulatorConnectionString, F(("endpoint", "localhost"))), "Endpoint=sb://localhost;" + "Shared" + "AccessKeyName=RootManage" + "Shared" + "AccessKey;" + "Shared" + "AccessKey=ZmFrZS1lbXVsYXRvci1rZXk=;UseDevelopmentEmulator=true;", 10),
        };
        foreach (var (draft, secret, usedMinutesAgo) in seeds)
        {
            // The memory store finishes at once, so the demo seeds synchronously.
            var entry = vault.AddAsync(draft, secret is null ? null : new RuntimeSecret(secret)).GetAwaiter().GetResult();
            if (usedMinutesAgo is { } minutes)
            {
                Clock.Instance.Offset = TimeSpan.FromMinutes(-minutes);
                vault.MarkUsed(entry.Id);
                Clock.Instance.Offset = TimeSpan.Zero;
            }
        }
    }

    /// <summary>The wall clock, shifted while seeding so "last used" shows a spread of times.</summary>
    private sealed class Clock : TimeProvider
    {
        public static Clock Instance { get; } = new();

        public TimeSpan Offset { get; set; }

        public override DateTimeOffset GetUtcNow() => base.GetUtcNow() + Offset;
    }

    private sealed class DemoLegacy(Dictionary<string, string> items) : ILegacySavedConnections
    {
        public IReadOnlyDictionary<string, string> ReadAll() => new Dictionary<string, string>(items);

        public void Clear() => items.Clear();
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

        public async Task<SignedInAccount> SignInInteractiveAsync(string? tenant, string? loginHint, SignInPrompt prompt, CancellationToken cancellationToken)
        {
            // Synthetic walkthroughs of the failure paths: tenant "demo-wait" waits like a browser that never
            // returns (until Cancel sign-in or the timeout); "demo-fail" fails like a refused sign-in.
            prompt.LinkReady?.Invoke(new Uri("https://login.example/demo/authorize"));
            if (tenant == "demo-wait")
                await Task.Delay(Timeout.Infinite, cancellationToken).ConfigureAwait(true);
            if (tenant == "demo-fail")
                throw new Azure.Identity.AuthenticationFailedException("AADSTS50011: The redirect URI does not match (synthetic demo error).");
            var user = loginHint ?? "alice@contoso.example";
            return new SignedInAccount(Account(TenantsByUser.ContainsKey(user) ? user : "alice@contoso.example") with { Browser = prompt.Browser.Id }, new DemoCredential(user));
        }

        public async Task<SignedInAccount> SignInWithDeviceCodeAsync(string? tenant, SignInPrompt prompt, CancellationToken cancellationToken)
        {
            prompt.DeviceCode?.Invoke("To sign in, use a web browser to open the page https://login.example/device and enter the code DEMO-1234 to authenticate. (synthetic demo)");
            await Task.Delay(TimeSpan.FromSeconds(tenant == "demo-wait" ? 3600 : 2), cancellationToken).ConfigureAwait(true);
            return new SignedInAccount(Account("alice@contoso.example"), new DemoCredential("alice@contoso.example"));
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
}
