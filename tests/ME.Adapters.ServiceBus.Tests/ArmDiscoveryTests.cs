// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Azure;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

/// <summary>Discovery walks every tenant and subscription of an account. Synthetic tenants and namespaces only.</summary>
public sealed class ArmDiscoveryTests
{
    private const string Contoso = "00000000-0000-0000-0000-00000000c001";
    private const string ContosoLab = "00000000-0000-0000-0000-00000000c002";

    private static NamespaceRef Ns(string name, string subscription, string tenant) => new(
        name,
        $"{name}.servicebus.windows.net",
        $"/subscriptions/{subscription}/resourceGroups/rg-demo/providers/Microsoft.ServiceBus/namespaces/{name}",
        "rg-demo",
        "East US",
        subscription,
        tenant);

    private static FakeArmReader TwoTenants()
    {
        var arm = new FakeArmReader();
        arm.Tenants["alice"] = [new TenantRef(Contoso, "Contoso"), new TenantRef(ContosoLab, "Contoso Lab")];
        arm.Subscriptions[Contoso] = [new SubscriptionRef("sub-demo-a", "Demo A", Contoso), new SubscriptionRef("sub-demo-b", "Demo B", Contoso)];
        arm.Subscriptions[ContosoLab] = [new SubscriptionRef("sub-lab", "Lab", ContosoLab)];
        arm.Namespaces["sub-demo-a"] = [Ns("sbns-demo-002", "sub-demo-a", Contoso), Ns("sbns-demo-001", "sub-demo-a", Contoso)];
        arm.Namespaces["sub-demo-b"] = [];
        arm.Namespaces["sub-lab"] = [Ns("sbns-lab-001", "sub-lab", ContosoLab)];
        return arm;
    }

    [Fact]
    public async Task Discovery_maps_every_tenant_and_subscription_to_its_namespaces()
    {
        var arm = TwoTenants();
        var credential = new FakeCredential("alice");

        var result = await NamespaceDiscovery.DiscoverAsync(arm, credential);

        Assert.Null(result.Error);
        Assert.Equal(["Contoso", "Contoso Lab"], result.Tenants.Select(t => t.Tenant.DisplayName));
        Assert.Equal(["Demo A", "Demo B"], result.Tenants[0].Subscriptions.Select(s => s.Subscription.DisplayName));
        Assert.Equal(["sbns-demo-001", "sbns-demo-002"], result.Tenants[0].Subscriptions[0].Namespaces.Select(n => n.Name));
        Assert.Equal(["sbns-demo-001", "sbns-demo-002", "sbns-lab-001"], result.Namespaces.Select(n => n.Name));
        Assert.Equal("sbns-lab-001.servicebus.windows.net", result.Namespaces.Last().FullyQualifiedNamespace);
        Assert.Contains(ContosoLab, credential.TenantsAsked);
    }

    [Fact]
    public async Task One_tenant_failing_is_reported_there_and_the_other_tenants_still_load()
    {
        var arm = TwoTenants();
        arm.FailingTenants.Add(ContosoLab);

        var result = await NamespaceDiscovery.DiscoverAsync(arm, new FakeCredential("alice"));

        Assert.Null(result.Error);
        var lab = Assert.Single(result.Tenants, t => t.Tenant.TenantId == ContosoLab);
        Assert.StartsWith("Sign-in to this tenant was refused.", lab.Error, StringComparison.Ordinal);
        Assert.Empty(lab.Subscriptions);
        Assert.Equal(["sbns-demo-001", "sbns-demo-002"], result.Namespaces.Select(n => n.Name));
    }

    [Fact]
    public async Task One_subscription_failing_keeps_its_siblings()
    {
        var arm = TwoTenants();
        arm.FailingSubscriptions.Add("sub-demo-a");

        var result = await NamespaceDiscovery.DiscoverAsync(arm, new FakeCredential("alice"));

        var contoso = result.Tenants[0];
        Assert.Null(contoso.Error);
        Assert.Equal("No permission to list here. Ask for the Reader role.", contoso.Subscriptions.Single(s => s.Subscription.SubscriptionId == "sub-demo-a").Error);
        Assert.Equal(["sbns-lab-001"], result.Namespaces.Select(n => n.Name));
    }

    [Fact]
    public async Task A_token_failure_for_the_whole_account_becomes_a_plain_error_not_an_exception()
    {
        var arm = TwoTenants();
        var credential = new FakeCredential("alice", new RequestFailedException(401, "token expired"));

        var result = await NamespaceDiscovery.DiscoverAsync(arm, credential);

        Assert.NotNull(result.Error);
        Assert.Empty(result.Tenants);
    }

    [Fact]
    public async Task A_subscription_returned_by_two_tenants_is_listed_once()
    {
        var arm = TwoTenants();
        arm.Subscriptions[ContosoLab].Add(new SubscriptionRef("sub-demo-a", "Demo A", Contoso));

        var result = await NamespaceDiscovery.DiscoverAsync(arm, new FakeCredential("alice"));

        Assert.Single(result.Tenants.SelectMany(t => t.Subscriptions), s => s.Subscription.SubscriptionId == "sub-demo-a");
    }

    [Fact]
    public async Task Arm_inventory_lists_queues_topics_and_subscriptions_with_counts()
    {
        var arm = TwoTenants();
        var ns = arm.Namespaces["sub-demo-a"][1];
        arm.Entities[ns.Name] =
        [
            new EntitySnapshot(EntityKind.Queue, "orders", null, new EntityCounts(4, 1, 0, 0, 5), "Active", 2048),
            new EntitySnapshot(EntityKind.Queue, "audit", null, new EntityCounts(0, 0, 0, 0, 0), "Active", 0),
            new EntitySnapshot(EntityKind.Topic, "events", null, EntityCounts.Unknown, "Active", 512, 1),
            new EntitySnapshot(EntityKind.Subscription, "all", "events", new EntityCounts(2, 0, 0, 0, 2), "Active", null),
        ];
        var credential = new FakeCredential("alice");
        await using var inventory = new ArmEntityInventory(arm, credential, ns);

        var queues = await inventory.ListQueuesAsync();
        var topics = await inventory.ListTopicsAsync();
        var subscriptions = await inventory.ListSubscriptionsAsync("events");

        Assert.Equal(["audit", "orders"], queues.Entities.Select(e => e.Name));
        Assert.Equal(1, queues.Entities[1].Counts.DeadLetter);
        Assert.Equal("events", Assert.Single(topics.Entities).Name);
        Assert.Equal("all", Assert.Single(subscriptions.Entities).Name);
        Assert.All(credential.TenantsAsked, t => Assert.Equal(Contoso, t));
        Assert.Equal(ServiceBusBrokerProvider.ProviderId, inventory.Provider.Id);
    }

    [Fact]
    public async Task Arm_inventory_maps_a_forbidden_read_to_a_broker_error()
    {
        var ns = Ns("sbns-demo-001", "sub-demo-a", Contoso);
        var credential = new FakeCredential("alice", new RequestFailedException(403, "no read"));
        await using var inventory = new ArmEntityInventory(new FakeArmReader(), credential, ns);

        var error = await Assert.ThrowsAsync<BrokerException>(() => inventory.ListQueuesAsync());
        Assert.Equal(BrokerErrorKind.Forbidden, error.Kind);
    }
}
