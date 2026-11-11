// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;

/// <summary>
/// What an entity-level key or token can show: just the one entity, with no counts. Such a key cannot
/// read the namespace's management endpoints, so this makes no broker call and says so in plain words.
/// </summary>
public sealed class EntityScopedInventory(BrokerProviderDescriptor provider, string fullyQualifiedNamespace, string entityPath) : IEntityInventory
{
    public BrokerProviderDescriptor Provider => provider;

    public string EntityPath => entityPath;

    public string Notice =>
        $"This connection string is for {entityPath} only, on {fullyQualifiedNamespace}. An entity-level key cannot read "
        + "the namespace's queue and topic lists or message counts. Use a namespace-level key, or sign in with Entra, to see counts.";

    public Task<InventoryResult> ListQueuesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new InventoryResult([new EntitySnapshot(EntityKind.Queue, entityPath, null, EntityCounts.Unknown, null, null)], 0, Notice));

    public Task<InventoryResult> ListTopicsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new InventoryResult([], 0, Notice));

    public Task<InventoryResult> ListSubscriptionsAsync(string topicName, CancellationToken cancellationToken = default) =>
        Task.FromResult(new InventoryResult([], 0, Notice));

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
