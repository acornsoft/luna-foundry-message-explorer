// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Brokers.Browse;
using Acornsoft.MessageExplorer.Testing;

namespace Acornsoft.MessageExplorer.Brokers.Tests;

/// <summary>
/// Resources (what you send to) and receivers (what you read from). A queue is a resource with one implicit
/// receiver, itself; a topic is a resource with N named receivers, its subscriptions.
/// </summary>
public class ResourceModelTests
{
    private static FakeInventory Inventory(IReadOnlyList<EntitySnapshot>? entities = null) =>
        new FakeBrokerProvider(entities ?? SyntheticQueues.WithTopics()).Inventory;

    [Fact]
    public void A_queue_maps_to_a_queue_resource_that_is_its_own_single_receiver()
    {
        var snapshot = SyntheticQueues.Queue("orders", active: 5, deadLetter: 2, scheduled: 3, transferDeadLetter: 1);

        var resource = Resource.From(snapshot);

        Assert.Equal((ResourceKind.Queue, "orders", 1), (resource.Kind, resource.Name, resource.ReceiverCount));
        Assert.True(resource.HasImplicitReceiver);
        var receiver = Assert.IsType<Receiver>(resource.ImplicitReceiver);
        Assert.Equal((ReceiverKind.Implicit, "orders", "orders"), (receiver.Kind, receiver.Name, receiver.ResourceName));
        Assert.True(receiver.IsImplicit);
        // On a queue every count lives on the one entity: scheduled and dead letters alike.
        Assert.Equal(snapshot.Counts, resource.Counts);
        Assert.Equal(snapshot.Counts, receiver.Counts);
        Assert.Equal(BrowseEntity.Queue("orders"), receiver.Target);
    }

    [Fact]
    public void A_topic_maps_to_a_topic_resource_with_named_receivers()
    {
        var topic = SyntheticQueues.Topic("gsl-dispute-events", subscriptions: 3, scheduled: 4);

        var resource = Resource.From(topic);

        Assert.Equal((ResourceKind.Topic, 3), (resource.Kind, resource.ReceiverCount));
        Assert.False(resource.HasImplicitReceiver);
        Assert.Null(resource.ImplicitReceiver);
        Assert.Equal(4, resource.Counts.Scheduled); // scheduled stays at the topic

        var subscription = Receiver.From(SyntheticQueues.Subscription("gsl-dispute-events", "notice-writer", active: 1, deadLetter: 2));
        Assert.Equal((ReceiverKind.Subscription, "notice-writer", "gsl-dispute-events", false), (subscription.Kind, subscription.Name, subscription.ResourceName, subscription.IsImplicit));
        Assert.Equal(2, subscription.Counts.DeadLetter); // dead letters live on the receiver
        Assert.Equal(BrowseEntity.Subscription("gsl-dispute-events", "notice-writer"), subscription.Target);
    }

    [Fact]
    public void Resources_round_trip_to_the_inventory_rows_they_came_from()
    {
        foreach (var snapshot in SyntheticQueues.WithTopics().Where(e => e.Kind != EntityKind.Subscription))
            Assert.Equal(snapshot, Resource.From(snapshot).ToSnapshot());
        var sub = SyntheticQueues.Subscription("t", "s", 1, 0);
        Assert.Equal(sub, Receiver.From(sub).ToSnapshot());
        Assert.Equal(EntityKind.Queue, Resource.From(SyntheticQueues.Queue("q", 0, 0)).ImplicitReceiver!.ToSnapshot().Kind);
    }

    [Fact]
    public void A_subscription_is_not_a_resource_and_a_queue_is_not_a_named_receiver()
    {
        Assert.Throws<ArgumentException>(() => Resource.From(SyntheticQueues.Subscription("t", "s", 1, 0)));
        Assert.Throws<ArgumentException>(() => Receiver.From(SyntheticQueues.Queue("q", 0, 0)));
    }

    [Fact]
    public async Task One_read_lists_every_resource_sorted_by_name_with_its_kind()
    {
        var inventory = Inventory();

        var list = await ResourceReader.ReadAsync(inventory);

        Assert.Equal((1, 1), (inventory.ListCalls, inventory.TopicCalls));
        Assert.Empty(inventory.SubscriptionCalls);
        Assert.Equal(6, list.Resources.Count);
        Assert.Equal(list.Resources.Select(r => r.Name).Order(StringComparer.OrdinalIgnoreCase), list.Resources.Select(r => r.Name));
        Assert.Equal(4, list.Resources.Count(r => r.Kind == ResourceKind.Queue));
        Assert.Equal(2, list.Resources.Count(r => r.Kind == ResourceKind.Topic));
        Assert.Equal((4, 2), (list.Count(ResourceKind.Queue), list.Count(ResourceKind.Topic)));
    }

    [Fact]
    public async Task A_queue_needs_no_read_for_its_receiver_and_a_topic_reads_its_subscriptions()
    {
        var inventory = Inventory();
        var list = await ResourceReader.ReadAsync(inventory);
        var queue = list.Resources.First(r => r.Kind == ResourceKind.Queue);
        var topic = list.Resources.Single(r => r.Name == "gsl-dispute-events");

        var queueReceivers = await ResourceReader.ReceiversAsync(inventory, queue);
        Assert.Equal(queue.Name, Assert.Single(queueReceivers).Name);
        Assert.Empty(inventory.SubscriptionCalls);

        var topicReceivers = await ResourceReader.ReceiversAsync(inventory, topic);
        Assert.Equal(["case-tracker", "notice-writer", "audit-copy"], topicReceivers.Select(r => r.Name));
        Assert.All(topicReceivers, r => Assert.Equal(ReceiverKind.Subscription, r.Kind));
        Assert.Equal(["gsl-dispute-events"], inventory.SubscriptionCalls);
    }

    [Theory]
    [InlineData(ResourceKindFilter.All, 6)]
    [InlineData(ResourceKindFilter.Queues, 4)]
    [InlineData(ResourceKindFilter.Topics, 2)]
    public async Task The_kind_filter_keeps_all_queues_or_topics(ResourceKindFilter filter, int expected)
    {
        var list = await ResourceReader.ReadAsync(Inventory());

        Assert.Equal(expected, list.Resources.Count(r => r.Matches(filter)));
    }

    [Fact]
    public void Later_resource_kinds_are_named_but_have_their_own_receivers()
    {
        Assert.Contains(ResourceKind.EventHub, Enum.GetValues<ResourceKind>());
        Assert.Contains(ResourceKind.KafkaTopic, Enum.GetValues<ResourceKind>());
        Assert.Contains(ReceiverKind.ConsumerGroup, Enum.GetValues<ReceiverKind>());
        Assert.False(new Resource(ResourceKind.EventHub, "hub", EntityCounts.Unknown, null, null, 2).HasImplicitReceiver);
        Assert.True(new Resource(ResourceKind.EventHub, "hub", EntityCounts.Unknown, null, null, 2).Matches(ResourceKindFilter.Topics));
    }

    [Fact]
    public async Task Errors_and_notices_pass_through()
    {
        var inventory = Inventory();
        inventory.FailWith = new BrokerException(BrokerErrorKind.Unreachable, "unreachable");
        await Assert.ThrowsAsync<BrokerException>(() => ResourceReader.ReadAsync(inventory));
    }
}
