// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;

/// <summary>Name and status for one entity, from the entity description pages.</summary>
public sealed record EntityStatusRow(string Name, string Status);

/// <summary>
/// The only seam between the inventory and the Azure SDK. Every member is a paged GET.
/// The production implementation is <see cref="AdministrationClientReader"/>.
/// </summary>
public interface IServiceBusAdminReader
{
    IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> QueueRuntimePagesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> QueueStatusPagesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> TopicRuntimePagesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> TopicStatusPagesAsync(CancellationToken cancellationToken);

    IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> SubscriptionRuntimePagesAsync(string topicName, CancellationToken cancellationToken);

    IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> SubscriptionStatusPagesAsync(string topicName, CancellationToken cancellationToken);
}
