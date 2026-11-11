// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Azure.Messaging.ServiceBus.Administration;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;

/// <summary>Maps SDK runtime properties to broker-neutral rows. Pure functions, tested with the SDK model factory.</summary>
public static class ServiceBusMapping
{
    public static EntitySnapshot ToSnapshot(QueueRuntimeProperties queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return new EntitySnapshot(
            EntityKind.Queue,
            queue.Name,
            Parent: null,
            new EntityCounts(
                queue.ActiveMessageCount,
                queue.DeadLetterMessageCount,
                queue.ScheduledMessageCount,
                queue.TransferDeadLetterMessageCount,
                queue.TotalMessageCount),
            Status: null,
            queue.SizeInBytes);
    }

    public static EntitySnapshot ToSnapshot(TopicRuntimeProperties topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return new EntitySnapshot(
            EntityKind.Topic,
            topic.Name,
            Parent: null,
            new EntityCounts(Active: null, DeadLetter: null, topic.ScheduledMessageCount, TransferDeadLetter: null, Total: null),
            Status: null,
            topic.SizeInBytes,
            topic.SubscriptionCount);
    }

    public static EntitySnapshot ToSnapshot(SubscriptionRuntimeProperties subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new EntitySnapshot(
            EntityKind.Subscription,
            subscription.SubscriptionName,
            subscription.TopicName,
            new EntityCounts(
                subscription.ActiveMessageCount,
                subscription.DeadLetterMessageCount,
                Scheduled: null,
                subscription.TransferDeadLetterMessageCount,
                subscription.TotalMessageCount),
            Status: null,
            SizeInBytes: null);
    }

    public static EntityStatusRow ToStatus(QueueProperties queue)
    {
        ArgumentNullException.ThrowIfNull(queue);
        return new EntityStatusRow(queue.Name, queue.Status.ToString());
    }

    public static EntityStatusRow ToStatus(TopicProperties topic)
    {
        ArgumentNullException.ThrowIfNull(topic);
        return new EntityStatusRow(topic.Name, topic.Status.ToString());
    }

    public static EntityStatusRow ToStatus(SubscriptionProperties subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);
        return new EntityStatusRow(subscription.SubscriptionName, subscription.Status.ToString());
    }
}
