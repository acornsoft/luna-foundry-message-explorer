// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Acornsoft.MessageExplorer.Operations.InMemory;
using Acornsoft.MessageExplorer.Operations.Move;

namespace Acornsoft.MessageExplorer.Shell.Demo;

/// <summary>
/// A synthetic, in-memory provider for walkthroughs and screenshots. Off unless the app starts with
/// <c>--synthetic-demo</c> or <c>LFME_SYNTHETIC_DEMO=1</c>. Made-up names and messages only; no network.
/// The counts come from an in-memory broker, so a move in Move mode shows up after a refresh.
/// </summary>
public sealed class SyntheticDemoProvider(InMemoryMoveBroker? broker = null) : IBrokerProvider
{
    public const string Id = "synthetic-demo";
    public const string Namespace = "sbns-demo-001";

    private static readonly Lazy<InMemoryMoveBroker> Shared = new(CreateBroker);

    /// <summary>The broker shared by the demo's Connect panel and its Accounts tab.</summary>
    public static InMemoryMoveBroker SharedBroker => Shared.Value;

    public InMemoryMoveBroker Broker { get; } = broker ?? SharedBroker;

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

    public IEntityInventory OpenInventory(BrokerConnection connection) => new Inventory(Descriptor, Broker);

    private static readonly (string Name, long Scheduled, long Transfer, string Status, bool Sessions)[] Queues =
    [
        ("orders", 2, 0, "Active", false),
        ("payments", 0, 0, "Active", false),
        ("payments-retry", 0, 0, "Active", false),
        ("notifications-outbound", 9, 0, "SendDisabled", false),
        ("shipments-by-customer", 0, 0, "Active", true),
        ("invoices", 0, 1, "Active", false),
    ];

    private static readonly (string Topic, string[] Subscriptions, long Scheduled)[] Topics =
    [
        ("order-events", ["billing", "audit", "shipping"], 1),
        ("payment-events", ["ledger", "fraud-check"], 0),
        ("usage", ["invoice-draft"], 0),
    ];

    /// <summary>The current rows, counted from the broker.</summary>
    public static IReadOnlyList<EntitySnapshot> Snapshot(InMemoryMoveBroker broker)
    {
        ArgumentNullException.ThrowIfNull(broker);
        var rows = new List<EntitySnapshot>();
        foreach (var (name, scheduled, transfer, status, _) in Queues)
        {
            var active = broker.Count(MoveEndpoint.Queue(broker.Namespace, name));
            var dead = broker.Count(MoveEndpoint.Queue(broker.Namespace, name, deadLetter: true));
            rows.Add(new(EntityKind.Queue, name, null, new EntityCounts(active, dead, scheduled, transfer, active + dead + scheduled + transfer), status, 512 * (active + dead + scheduled + 1)));
        }

        foreach (var (topic, subscriptions, scheduled) in Topics)
        {
            rows.Add(new(EntityKind.Topic, topic, null, new EntityCounts(null, null, scheduled, null, null), "Active", 8_192L * subscriptions.Length, subscriptions.Length));
            foreach (var subscription in subscriptions)
            {
                var active = broker.Count(MoveEndpoint.Subscription(broker.Namespace, topic, subscription));
                var dead = broker.Count(MoveEndpoint.Subscription(broker.Namespace, topic, subscription, deadLetter: true));
                rows.Add(new(EntityKind.Subscription, subscription, topic, new EntityCounts(active, dead, null, 0, active + dead), "Active", null));
            }
        }

        return rows;
    }

    /// <summary>The rows of the shared demo broker (the Accounts demo reads these).</summary>
    public static IReadOnlyList<EntitySnapshot> Entities => Snapshot(SharedBroker);

    /// <summary>A fresh demo namespace with made-up messages: dead letters with reasons, sessions, and subscription backlogs.</summary>
    public static InMemoryMoveBroker CreateBroker()
    {
        var broker = new InMemoryMoveBroker(Namespace);
        foreach (var (name, _, _, _, sessions) in Queues)
            broker.AddQueue(name, sessions: sessions, duplicateDetection: name == "payments");
        foreach (var (topic, subscriptions, _) in Topics)
        {
            broker.AddTopic(topic);
            foreach (var subscription in subscriptions)
                broker.AddSubscription(topic, subscription);
        }

        var ns = broker.Namespace;
        Fill(broker, MoveEndpoint.Queue(ns, "orders"), "ord", 12);
        Fill(broker, MoveEndpoint.Queue(ns, "orders", true), "ord-dl", 4, "MaxDeliveryCountExceeded", "SchemaValidationFailed");
        Fill(broker, MoveEndpoint.Queue(ns, "payments"), "pay", 6);
        Fill(broker, MoveEndpoint.Queue(ns, "payments", true), "pay-dl", 3, "TTLExpiredException", "PartnerTimeout");
        Fill(broker, MoveEndpoint.Queue(ns, "invoices"), "inv", 5);
        var sessionQueue = MoveEndpoint.Queue(ns, "shipments-by-customer");
        foreach (var (i, customer) in new[] { (1, "cust-a"), (2, "cust-b"), (3, "cust-a"), (4, "cust-c"), (5, "cust-b"), (6, "cust-a"), (7, "cust-c"), (8, "cust-b") })
            broker.Seed(sessionQueue, InMemoryMoveBroker.Message($"shp-{i:000}", customer, $"corr-shp-{i:000}"));
        Fill(broker, MoveEndpoint.Queue(ns, "shipments-by-customer", true), "shp-dl", 2, "MaxDeliveryCountExceeded");
        Fill(broker, MoveEndpoint.Subscription(ns, "order-events", "billing"), "oe-bill", 3);
        Fill(broker, MoveEndpoint.Subscription(ns, "order-events", "billing", true), "oe-bill-dl", 2, "Poison");
        Fill(broker, MoveEndpoint.Subscription(ns, "order-events", "audit"), "oe-aud", 1);
        Fill(broker, MoveEndpoint.Subscription(ns, "order-events", "shipping", true), "oe-shp-dl", 1, "MaxDeliveryCountExceeded");
        Fill(broker, MoveEndpoint.Subscription(ns, "payment-events", "ledger"), "pe-led", 4);
        Fill(broker, MoveEndpoint.Subscription(ns, "payment-events", "fraud-check"), "pe-frd", 1);
        Fill(broker, MoveEndpoint.Subscription(ns, "payment-events", "fraud-check", true), "pe-frd-dl", 1, "RuleTimeout");
        Fill(broker, MoveEndpoint.Subscription(ns, "usage", "invoice-draft"), "use", 7);
        return broker;
    }

    private static void Fill(InMemoryMoveBroker broker, MoveEndpoint endpoint, string prefix, int count, params string[] reasons)
    {
        for (var i = 1; i <= count; i++)
        {
            var properties = new Dictionary<string, object?>(StringComparer.Ordinal) { ["source"] = "synthetic-demo" };
            broker.Seed(endpoint, InMemoryMoveBroker.Message($"{prefix}-{i:000}", correlation: $"corr-{prefix}-{i:000}", properties: properties),
                reasons.Length == 0 ? null : reasons[(i - 1) % reasons.Length]);
            broker.Now = broker.Now.AddMinutes(3);
        }
    }

    private sealed class Inventory(BrokerProviderDescriptor provider, InMemoryMoveBroker broker) : IEntityInventory
    {
        public BrokerProviderDescriptor Provider { get; } = provider;

        public Task<InventoryResult> ListQueuesAsync(CancellationToken cancellationToken = default) => List(e => e.Kind == EntityKind.Queue);

        public Task<InventoryResult> ListTopicsAsync(CancellationToken cancellationToken = default) => List(e => e.Kind == EntityKind.Topic);

        public Task<InventoryResult> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default) =>
            List(e => e.Kind == EntityKind.Subscription && e.Parent == topicName);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private Task<InventoryResult> List(Func<EntitySnapshot, bool> filter) =>
            Task.FromResult(new InventoryResult(Snapshot(broker).Where(filter).ToList(), 1));
    }
}
