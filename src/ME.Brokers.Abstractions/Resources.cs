// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers.Browse;

namespace Acornsoft.MessageExplorer.Brokers;

/// <summary>What you send to.</summary>
public enum ResourceKind
{
    /// <summary>One implicit receiver: the queue itself (point to point, a "channel").</summary>
    Queue,

    /// <summary>N named receivers: its subscriptions (publish and subscribe).</summary>
    Topic,

    /// <summary>Later: receivers are consumer groups.</summary>
    EventHub,

    /// <summary>Later: receivers are consumer groups.</summary>
    KafkaTopic,
}

/// <summary>What you read from.</summary>
public enum ReceiverKind
{
    /// <summary>A queue's single receiver: the queue itself.</summary>
    Implicit,

    /// <summary>A topic subscription.</summary>
    Subscription,

    /// <summary>Later: an Event Hubs or Kafka consumer group.</summary>
    ConsumerGroup,
}

/// <summary>The one kind filter over the resources list: everything, single-receiver resources, or fan-out ones.</summary>
public enum ResourceKindFilter
{
    All,
    Queues,
    Topics,
}

/// <summary>
/// A resource: what a sender sends to. A queue is a resource with exactly one receiver, itself; a topic is a
/// resource that fans out to N named receivers. Counts follow the broker: on a queue every count is on the queue;
/// on a topic the scheduled count stays on the topic and dead letters are on each receiver.
/// </summary>
public sealed record Resource(ResourceKind Kind, string Name, EntityCounts Counts, string? Status, long? SizeInBytes, int? ReceiverCount)
{
    /// <summary>True when the resource is its own single receiver (a queue).</summary>
    public bool HasImplicitReceiver => Kind == ResourceKind.Queue;

    /// <summary>The queue as its own receiver; null for resources with named receivers.</summary>
    public Receiver? ImplicitReceiver => HasImplicitReceiver
        ? new Receiver(ReceiverKind.Implicit, Name, Name, Counts, Status)
        : null;

    public bool Matches(ResourceKindFilter filter) => filter switch
    {
        ResourceKindFilter.Queues => HasImplicitReceiver,
        ResourceKindFilter.Topics => !HasImplicitReceiver,
        _ => true,
    };

    public static Resource From(EntitySnapshot entity)
    {
        ArgumentNullException.ThrowIfNull(entity);
        return entity.Kind switch
        {
            EntityKind.Queue => new Resource(ResourceKind.Queue, entity.Name, entity.Counts, entity.Status, entity.SizeInBytes, 1),
            EntityKind.Topic => new Resource(ResourceKind.Topic, entity.Name, entity.Counts, entity.Status, entity.SizeInBytes, entity.SubscriptionCount),
            _ => throw new ArgumentException($"A {entity.Kind} is a receiver, not a resource.", nameof(entity)),
        };
    }

    /// <summary>The inventory row this resource stands for (the quick view's lanes read it).</summary>
    public EntitySnapshot ToSnapshot() => Kind switch
    {
        ResourceKind.Queue => new EntitySnapshot(EntityKind.Queue, Name, null, Counts, Status, SizeInBytes),
        _ => new EntitySnapshot(EntityKind.Topic, Name, null, Counts, Status, SizeInBytes, ReceiverCount),
    };
}

/// <summary>A receiver: what a reader reads from. A queue's is implicit (the queue); a topic's are its subscriptions.</summary>
public sealed record Receiver(ReceiverKind Kind, string Name, string ResourceName, EntityCounts Counts, string? Status)
{
    public bool IsImplicit => Kind == ReceiverKind.Implicit;

    /// <summary>Where a peek reads this receiver.</summary>
    public BrowseEntity Target => IsImplicit ? BrowseEntity.Queue(ResourceName) : BrowseEntity.Subscription(ResourceName, Name);

    public static Receiver From(EntitySnapshot subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        if (subscription.Kind != EntityKind.Subscription || subscription.Parent is null)
            throw new ArgumentException($"Only a subscription is a named receiver; a {subscription.Kind} is a resource.", nameof(subscription));
        return new Receiver(ReceiverKind.Subscription, subscription.Name, subscription.Parent, subscription.Counts, subscription.Status);
    }

    public EntitySnapshot ToSnapshot() => IsImplicit
        ? new EntitySnapshot(EntityKind.Queue, ResourceName, null, Counts, Status, null)
        : new EntitySnapshot(EntityKind.Subscription, Name, ResourceName, Counts, Status, null);
}

/// <summary>Every resource in a namespace, sorted by name, from one read.</summary>
public sealed record ResourceList(IReadOnlyList<Resource> Resources, string? Notice, int PagesRead)
{
    public int Count(ResourceKind kind) => Resources.Count(r => r.Kind == kind);
}

/// <summary>Reads resources and receivers from a read-only inventory. Nothing here sends, receives or changes anything.</summary>
public static class ResourceReader
{
    /// <summary>One list read per kind (queues, topics); receivers are not read here.</summary>
    public static async Task<ResourceList> ReadAsync(IEntityInventory inventory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var queues = await inventory.ListQueuesAsync(cancellationToken).ConfigureAwait(false);
        var topics = await inventory.ListTopicsAsync(cancellationToken).ConfigureAwait(false);
        var resources = queues.Entities.Where(e => e.Kind == EntityKind.Queue)
            .Concat(topics.Entities.Where(e => e.Kind == EntityKind.Topic))
            .Select(Resource.From)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(r => r.Kind)
            .ToList();
        return new ResourceList(resources, queues.Notice ?? topics.Notice, queues.PagesRead + topics.PagesRead);
    }

    /// <summary>A queue's one receiver needs no read; a topic's subscriptions are read.</summary>
    public static async Task<IReadOnlyList<Receiver>> ReceiversAsync(IEntityInventory inventory, Resource resource, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        ArgumentNullException.ThrowIfNull(resource);
        if (resource.ImplicitReceiver is { } implicitReceiver)
            return [implicitReceiver];
        if (resource.Kind != ResourceKind.Topic)
            return [];
        var result = await inventory.ListSubscriptionsAsync(resource.Name, cancellationToken).ConfigureAwait(false);
        return result.Entities.Where(e => e.Kind == EntityKind.Subscription).Select(Receiver.From).ToList();
    }
}
