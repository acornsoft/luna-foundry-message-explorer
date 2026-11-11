// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;
using Azure.Core;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;

public sealed record TenantRef(string TenantId, string DisplayName);

public sealed record SubscriptionRef(string SubscriptionId, string DisplayName, string TenantId);

/// <summary>Which kind of messaging namespace an ARM resource is.</summary>
public enum NamespaceKind
{
    ServiceBus,
    EventHubs,
}

/// <summary>A Service Bus (or, for browsing, Event Hubs) namespace the account can read in Azure Resource Manager.</summary>
public sealed record NamespaceRef(
    string Name,
    string FullyQualifiedNamespace,
    string ResourceId,
    string ResourceGroup,
    string Location,
    string SubscriptionId,
    string TenantId,
    string Cloud = AzureClouds.PublicId,
    NamespaceKind Kind = NamespaceKind.ServiceBus);

/// <summary>
/// Read-only Azure Resource Manager calls: list tenants, subscriptions, namespaces, and a namespace's
/// queues, topics and subscriptions with counts. Every call is an HTTP GET. A list only returns what the
/// signed-in account is allowed to read, so a namespace appears only when the account can read it.
/// </summary>
public interface IArmReader
{
    IAsyncEnumerable<TenantRef> ListTenantsAsync(TokenCredential credential, CancellationToken cancellationToken);

    IAsyncEnumerable<SubscriptionRef> ListSubscriptionsAsync(TokenCredential credential, string tenantId, CancellationToken cancellationToken);

    IAsyncEnumerable<NamespaceRef> ListNamespacesAsync(TokenCredential credential, SubscriptionRef subscription, CancellationToken cancellationToken);

    /// <summary>
    /// Event Hubs namespaces in a subscription, for browsing only (a resource list GET filtered by type).
    /// Readers that know none (older fakes) return nothing.
    /// </summary>
    IAsyncEnumerable<NamespaceRef> ListEventHubsNamespacesAsync(TokenCredential credential, SubscriptionRef subscription, CancellationToken cancellationToken) =>
        NoNamespaces();

    IAsyncEnumerable<EntitySnapshot> ListQueuesAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, CancellationToken cancellationToken);

    IAsyncEnumerable<EntitySnapshot> ListTopicsAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, CancellationToken cancellationToken);

    IAsyncEnumerable<EntitySnapshot> ListSubscriptionsAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, string topicName, CancellationToken cancellationToken);

    /// <summary>
    /// The same reads against another cloud's Resource Manager endpoint. Readers that only know one
    /// cloud (in-memory fakes) return themselves.
    /// </summary>
    IArmReader ForCloud(AzureCloud cloud) => this;

    private static async IAsyncEnumerable<NamespaceRef> NoNamespaces()
    {
        await Task.CompletedTask.ConfigureAwait(false);
        yield break;
    }
}
