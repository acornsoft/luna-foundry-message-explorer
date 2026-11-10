// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.ReadTools;

/// <summary>
/// A made-up namespace, in memory, so the headless tool, its container and CI examples can be tried with no
/// Azure account: queues <c>orders</c> and <c>payments</c> (3 dead-lettered), topic <c>orders-events</c> with
/// subscriptions <c>billing</c> (1 dead-lettered) and <c>shipping</c>.
/// </summary>
public sealed class SyntheticNamespace : IEntityInventory
{
    public const string Host = "sbns-demo-001.servicebus.windows.net";

    private static readonly EntitySnapshot[] Queues =
    [
        new(EntityKind.Queue, "orders", null, new EntityCounts(12, 0, 2, 0, 14), "Active", 40_960),
        new(EntityKind.Queue, "payments", null, new EntityCounts(4, 3, 0, 0, 7), "Active", 12_288),
    ];

    private static readonly EntitySnapshot[] Topics =
    [
        new(EntityKind.Topic, "orders-events", null, new EntityCounts(null, null, 0, null, null), "Active", 8_192, SubscriptionCount: 2),
    ];

    private static readonly EntitySnapshot[] Subscriptions =
    [
        new(EntityKind.Subscription, "billing", "orders-events", new EntityCounts(5, 1, null, 0, 6), "Active", null),
        new(EntityKind.Subscription, "shipping", "orders-events", new EntityCounts(0, 0, null, 0, 0), "Active", null),
    ];

    public BrokerProviderDescriptor Provider { get; } = new(
        "synthetic",
        "Synthetic namespace (in memory)",
        ProviderAvailability.Enabled,
        BrokerCapabilities.ListQueues | BrokerCapabilities.ListTopics | BrokerCapabilities.ListSubscriptions
            | BrokerCapabilities.ActiveCounts | BrokerCapabilities.DeadLetterCounts | BrokerCapabilities.ScheduledCounts,
        [AuthMode.Entra],
        "synthetic",
        "Made-up entities and counts for trying the tool. Nothing leaves the process.");

    public Task<InventoryResult> ListQueuesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new InventoryResult(Queues, 1));

    public Task<InventoryResult> ListTopicsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new InventoryResult(Topics, 1));

    public Task<InventoryResult> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new InventoryResult(Subscriptions.Where(s => s.Parent == topicName).ToList(), 1));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
