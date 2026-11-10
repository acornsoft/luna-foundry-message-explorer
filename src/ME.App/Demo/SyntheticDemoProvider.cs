// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Shell.Demo;

/// <summary>
/// A synthetic, in-memory provider for walkthroughs and screenshots. Off unless the app starts with
/// <c>--synthetic-demo</c> or <c>LFME_SYNTHETIC_DEMO=1</c>. Made-up names and counts only; no network.
/// </summary>
public sealed class SyntheticDemoProvider : IBrokerProvider
{
    public const string Id = "synthetic-demo";

    public BrokerProviderDescriptor Descriptor { get; } = new(
        Id,
        "Synthetic demo (in memory)",
        ProviderAvailability.Enabled,
        BrokerCapabilities.ListQueues | BrokerCapabilities.ListTopics | BrokerCapabilities.ListSubscriptions
            | BrokerCapabilities.ActiveCounts | BrokerCapabilities.DeadLetterCounts | BrokerCapabilities.ScheduledCounts
            | BrokerCapabilities.TransferDeadLetterCounts | BrokerCapabilities.EntityStatus | BrokerCapabilities.EntitySize,
        [AuthMode.Entra],
        "synthetic-demo",
        "Made-up queues, topics and subscriptions held in memory. Nothing leaves the app.");

    public string? Validate(BrokerConnection connection) => null;

    public IEntityInventory OpenInventory(BrokerConnection connection) => new Inventory(Descriptor);

    public static IReadOnlyList<EntitySnapshot> Entities { get; } =
    [
        Queue("gsl-disputes-intake", 12, 0, 2, 0, 18_432),
        Queue("gsl-disputes-provisional-credit", 4, 3, 0, 0, 6_144),
        Queue("gsl-notices-outbound", 0, 0, 9, 0, 512, "SendDisabled"),
        Queue("crew.ticket-changes", 31, 0, 0, 0, 40_960),
        Queue("stock.requests", 8, 5, 0, 0, 12_288),
        Topic("ops.ticket-events", 2, 0, 65_536),
        Topic("gsl-dispute-events", 3, 1, 24_576),
        Topic("billing.usage", 1, 0, 8_192),
        Topic("orders.lifecycle", 0, 0, 0),
        Subscription("ops.ticket-events", "request-stock", 3, 0, 0),
        Subscription("ops.ticket-events", "meter-usage", 1, 0, 2),
        Subscription("gsl-dispute-events", "case-tracker", 5, 0, 0),
        Subscription("gsl-dispute-events", "notice-writer", 1, 2, 0),
        Subscription("gsl-dispute-events", "audit-copy", 0, 0, 4),
        Subscription("billing.usage", "invoice-draft", 7, 0, 0),
    ];

    private static EntitySnapshot Queue(string name, long active, long deadLetter, long scheduled, long transfer, long size, string status = "Active") =>
        new(EntityKind.Queue, name, null, new EntityCounts(active, deadLetter, scheduled, transfer, active + deadLetter + scheduled + transfer), status, size);

    private static EntitySnapshot Topic(string name, int subscriptions, long scheduled, long size) =>
        new(EntityKind.Topic, name, null, new EntityCounts(null, null, scheduled, null, null), "Active", size, subscriptions);

    private static EntitySnapshot Subscription(string topic, string name, long active, long deadLetter, long transfer) =>
        new(EntityKind.Subscription, name, topic, new EntityCounts(active, deadLetter, null, transfer, active + deadLetter + transfer), "Active", null);

    private sealed class Inventory(BrokerProviderDescriptor provider) : IEntityInventory
    {
        public BrokerProviderDescriptor Provider { get; } = provider;

        public Task<InventoryResult> ListQueuesAsync(CancellationToken cancellationToken = default) => List(e => e.Kind == EntityKind.Queue);

        public Task<InventoryResult> ListTopicsAsync(CancellationToken cancellationToken = default) => List(e => e.Kind == EntityKind.Topic);

        public Task<InventoryResult> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default) =>
            List(e => e.Kind == EntityKind.Subscription && e.Parent == topicName);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private static Task<InventoryResult> List(Func<EntitySnapshot, bool> filter) =>
            Task.FromResult(new InventoryResult(Entities.Where(filter).ToList(), 1));
    }
}
