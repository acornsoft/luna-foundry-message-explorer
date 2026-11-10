// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using Acornsoft.MessageExplorer.Brokers;
using Azure.Messaging.ServiceBus.Administration;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;

/// <summary>
/// Pages the management plane with GET calls only. This is the single class that touches
/// <see cref="ServiceBusAdministrationClient"/>. A test scans the compiled adapter and fails if any
/// other administration call becomes reachable.
/// </summary>
public sealed class AdministrationClientReader : IServiceBusAdminReader
{
    private readonly ServiceBusAdministrationClient _client;

    public AdministrationClientReader(ServiceBusAdministrationClient client)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
    }

    public async IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> QueueRuntimePagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in _client.GetQueuesRuntimePropertiesAsync(cancellationToken).AsPages().WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return page.Values.Select(ServiceBusMapping.ToSnapshot).ToList();
    }

    public async IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> QueueStatusPagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in _client.GetQueuesAsync(cancellationToken).AsPages().WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return page.Values.Select(ServiceBusMapping.ToStatus).ToList();
    }

    public async IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> TopicRuntimePagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in _client.GetTopicsRuntimePropertiesAsync(cancellationToken).AsPages().WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return page.Values.Select(ServiceBusMapping.ToSnapshot).ToList();
    }

    public async IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> TopicStatusPagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in _client.GetTopicsAsync(cancellationToken).AsPages().WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return page.Values.Select(ServiceBusMapping.ToStatus).ToList();
    }

    public async IAsyncEnumerable<IReadOnlyList<EntitySnapshot>> SubscriptionRuntimePagesAsync(
        string topicName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in _client.GetSubscriptionsRuntimePropertiesAsync(topicName, cancellationToken).AsPages().WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return page.Values.Select(ServiceBusMapping.ToSnapshot).ToList();
    }

    public async IAsyncEnumerable<IReadOnlyList<EntityStatusRow>> SubscriptionStatusPagesAsync(
        string topicName,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in _client.GetSubscriptionsAsync(topicName, cancellationToken).AsPages().WithCancellation(cancellationToken).ConfigureAwait(false))
            yield return page.Values.Select(ServiceBusMapping.ToStatus).ToList();
    }
}
