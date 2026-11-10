// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;
using Acornsoft.MessageExplorer.Brokers;
using Azure.Core;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;

/// <summary>
/// The queue, topic and subscription grids for a namespace found through Azure Resource Manager.
/// Counts come from ARM GETs, so the Reader role on the namespace is enough (no data-plane role).
/// </summary>
public sealed class ArmEntityInventory(IArmReader reader, TokenCredential credential, NamespaceRef serviceBusNamespace) : IEntityInventory
{
    public BrokerProviderDescriptor Provider => ServiceBusBrokerProvider.Descriptor;

    public NamespaceRef Namespace => serviceBusNamespace;

    /// <summary>The signed-in account this inventory reads with. Move mode opens its own client with it.</summary>
    public TokenCredential Credential => credential;

    public Task<InventoryResult> ListQueuesAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(reader.ListQueuesAsync(credential, serviceBusNamespace, cancellationToken), cancellationToken);

    public Task<InventoryResult> ListTopicsAsync(CancellationToken cancellationToken = default) =>
        ReadAsync(reader.ListTopicsAsync(credential, serviceBusNamespace, cancellationToken), cancellationToken);

    public Task<InventoryResult> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(topicName);
        return ReadAsync(reader.ListSubscriptionsAsync(credential, serviceBusNamespace, topicName, cancellationToken), cancellationToken);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    private static async Task<InventoryResult> ReadAsync(IAsyncEnumerable<EntitySnapshot> rows, CancellationToken cancellationToken)
    {
        try
        {
            var list = new List<EntitySnapshot>();
            await foreach (var row in rows.WithCancellation(cancellationToken).ConfigureAwait(false))
                list.Add(row);
            return new InventoryResult(list.OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase).ToList(), 1);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ServiceBusErrorMapper.Map(exception);
        }
    }
}
