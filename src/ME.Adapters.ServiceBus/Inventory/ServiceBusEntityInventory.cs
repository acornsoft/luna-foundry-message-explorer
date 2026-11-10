// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;

/// <summary>
/// Read-only inventory over the Service Bus management plane. It pages runtime properties for counts and size,
/// pages entity descriptions for status, and joins them by name. Nothing here sends, receives, locks, settles,
/// creates, updates, or deletes.
/// </summary>
public sealed class ServiceBusEntityInventory : IEntityInventory
{
    private readonly IServiceBusAdminReader _reader;

    public ServiceBusEntityInventory(BrokerProviderDescriptor provider, IServiceBusAdminReader reader)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
    }

    public BrokerProviderDescriptor Provider { get; }

    public Task<InventoryResult> ListQueuesAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(_reader.QueueRuntimePagesAsync(cancellationToken), _reader.QueueStatusPagesAsync(cancellationToken), cancellationToken);

    public Task<InventoryResult> ListTopicsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(_reader.TopicRuntimePagesAsync(cancellationToken), _reader.TopicStatusPagesAsync(cancellationToken), cancellationToken);

    public Task<InventoryResult> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return ReadAsync(
            _reader.SubscriptionRuntimePagesAsync(topicName, cancellationToken),
            _reader.SubscriptionStatusPagesAsync(topicName, cancellationToken),
            cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<InventoryResult> ReadAsync(
        IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> runtimePages,
        IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> statusPages,
        CancellationToken cancellationToken)
    {
        try
        {
            var rows = new List<EntitySnapshot>();
            var pages = 0;
            await foreach (var page in runtimePages.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                pages++;
                rows.AddRange(page);
            }

            var status = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            await foreach (var page in statusPages.WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                foreach (var row in page)
                    status[row.Name] = row.Status;
            }

            var joined = rows
                .Select(row => status.TryGetValue(row.Name, out var value) ? row with { Status = value } : row)
                .OrderBy(row => row.Name, StringComparer.OrdinalIgnoreCase)
                .ToList();
            return new InventoryResult(joined, pages);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ServiceBusErrorMapper.Map(exception);
        }
    }
}
