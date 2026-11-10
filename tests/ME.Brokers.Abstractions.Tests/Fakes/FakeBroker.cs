// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Testing;

/// <summary>Synthetic queues. Names and counts are made up. No real namespace or customer data.</summary>
public static class SyntheticQueues
{
    public static EntitySnapshot Queue(string name, long active, long deadLetter, long scheduled = 0, long transferDeadLetter = 0, string? status = "Active", long? size = 2048) =>
        new(EntityKind.Queue, name, null, new EntityCounts(active, deadLetter, scheduled, transferDeadLetter, active + deadLetter + scheduled + transferDeadLetter), status, size);

    public static IReadOnlyList<EntitySnapshot> Standard() =>
    [
        Queue("gsl-disputes-intake", active: 12, deadLetter: 0, scheduled: 2),
        Queue("gsl-disputes-provisional-credit", active: 4, deadLetter: 3, scheduled: 0),
        Queue("gsl-notices-outbound", active: 0, deadLetter: 0, scheduled: 9, status: "SendDisabled", size: 512),
        Queue("gsl-case-sync", active: 40, deadLetter: 0, transferDeadLetter: 1, size: 5 * 1024 * 1024),
    ];

    public static EntitySnapshot Topic(string name, int subscriptions, long scheduled = 0, string? status = "Active", long? size = 4096) =>
        new(EntityKind.Topic, name, null, new EntityCounts(null, null, scheduled, null, null), status, size, subscriptions);

    public static EntitySnapshot Subscription(string topic, string name, long active, long deadLetter, long transferDeadLetter = 0, string? status = "Active") =>
        new(EntityKind.Subscription, name, topic, new EntityCounts(active, deadLetter, null, transferDeadLetter, active + deadLetter + transferDeadLetter), status, null);

    /// <summary>Two topics with subscriptions; one subscription has dead letters, one has transfer dead letters.</summary>
    public static IReadOnlyList<EntitySnapshot> WithTopics() =>
    [
        .. Standard(),
        Topic("gsl-dispute-events", subscriptions: 3, scheduled: 1),
        Topic("gsl-ledger-postings", subscriptions: 1),
        Subscription("gsl-dispute-events", "case-tracker", active: 5, deadLetter: 0),
        Subscription("gsl-dispute-events", "notice-writer", active: 1, deadLetter: 2),
        Subscription("gsl-dispute-events", "audit-copy", active: 0, deadLetter: 0, transferDeadLetter: 4),
        Subscription("gsl-ledger-postings", "reconciler", active: 7, deadLetter: 0),
    ];

    public static IReadOnlyList<EntitySnapshot> Many(int count) =>
        Enumerable.Range(1, count)
            .Select(i => Queue($"synthetic-q-{i:D3}", active: i, deadLetter: i % 7 == 0 ? 1 : 0, scheduled: i % 3))
            .ToList();
}

/// <summary>An in-memory inventory. Records calls and can be told to fail or to wait.</summary>
public sealed class FakeInventory : IEntityInventory
{
    private readonly IReadOnlyList<EntitySnapshot> _entities;
    private readonly int _pageSize;

    public FakeInventory(BrokerProviderDescriptor provider, IReadOnlyList<EntitySnapshot> entities, int pageSize = 100)
    {
        Provider = provider;
        _entities = entities;
        _pageSize = pageSize;
    }

    public BrokerProviderDescriptor Provider { get; }

    public int ListCalls { get; private set; }

    public bool Disposed { get; private set; }

    public Exception? FailWith { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public async Task<InventoryResult> ListQueuesAsync(CancellationToken cancellationToken = default)
    {
        ListCalls++;
        if (Gate is not null)
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (FailWith is not null)
            throw FailWith;
        var queues = _entities.Where(entity => entity.Kind == EntityKind.Queue).ToList();
        var pages = Math.Max(1, (queues.Count + _pageSize - 1) / _pageSize);
        return new InventoryResult(queues, pages);
    }

    public int TopicCalls { get; private set; }

    public List<string> SubscriptionCalls { get; } = [];

    public async Task<InventoryResult> ListTopicsAsync(CancellationToken cancellationToken = default)
    {
        TopicCalls++;
        if (Gate is not null)
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (FailWith is not null)
            throw FailWith;
        return new InventoryResult(_entities.Where(entity => entity.Kind == EntityKind.Topic).ToList(), 1);
    }

    public async Task<InventoryResult> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default)
    {
        SubscriptionCalls.Add(topicName);
        if (Gate is not null)
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        if (FailWith is not null)
            throw FailWith;
        return new InventoryResult(_entities.Where(entity => entity.Kind == EntityKind.Subscription && entity.Parent == topicName).ToList(), 1);
    }

    public ValueTask DisposeAsync()
    {
        Disposed = true;
        return ValueTask.CompletedTask;
    }
}

/// <summary>A broker-neutral fake. It proves the UI and catalog work without Azure.</summary>
public sealed class FakeBrokerProvider : IBrokerProvider
{
    public const string Id = "fake-broker";

    public FakeBrokerProvider(IReadOnlyList<EntitySnapshot>? entities = null)
    {
        Inventory = new FakeInventory(Descriptor, entities ?? SyntheticQueues.Standard());
    }

    public BrokerProviderDescriptor Descriptor { get; } = new(
        Id,
        "Fake broker",
        ProviderAvailability.Enabled,
        BrokerCapabilities.ListQueues | BrokerCapabilities.ListTopics | BrokerCapabilities.ListSubscriptions | BrokerCapabilities.ActiveCounts | BrokerCapabilities.DeadLetterCounts | BrokerCapabilities.TransferDeadLetterCounts,
        [AuthMode.Entra, AuthMode.ConnectionString],
        "anything",
        "Synthetic test provider.");

    public FakeInventory Inventory { get; }

    public BrokerConnection? LastConnection { get; private set; }

    public int OpenCalls { get; private set; }

    public string? Validate(BrokerConnection connection) =>
        connection.Endpoint == "bad-endpoint" ? "That endpoint is not valid." : null;

    public IEntityInventory OpenInventory(BrokerConnection connection)
    {
        OpenCalls++;
        LastConnection = connection;
        return Inventory;
    }
}
