// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using Acornsoft.MessageExplorer.Brokers;
using Azure.Core;
using Azure.ResourceManager;
using Azure.ResourceManager.ServiceBus;
using Azure.ResourceManager.ServiceBus.Models;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;

/// <summary>
/// <see cref="IArmReader"/> over Azure.ResourceManager. Only list and get calls (HTTP GET). No create,
/// update, delete, key listing or key regeneration; the no-mutation guard checks the compiled IL.
/// </summary>
public sealed class ArmReader(AzureCloud? cloud = null) : IArmReader
{
    /// <summary>The cloud this reader talks to. Public unless the account picked another.</summary>
    public AzureCloud Cloud { get; } = cloud ?? AzureClouds.Public;

    public IArmReader ForCloud(AzureCloud cloud) => ReferenceEquals(cloud, Cloud) || cloud.Id == Cloud.Id ? this : new ArmReader(cloud);

    /// <summary>ArmClient options for <paramref name="cloud"/>: its ARM endpoint and token audience.</summary>
    public static ArmClientOptions ClientOptions(AzureCloud cloud)
    {
        ArgumentNullException.ThrowIfNull(cloud);
        return new ArmClientOptions { Environment = new ArmEnvironment(cloud.ArmEndpoint, cloud.ArmAudience) };
    }

    private ArmClient Client(TokenCredential credential) => new(credential, default, ClientOptions(Cloud));

    public async IAsyncEnumerable<TenantRef> ListTenantsAsync(TokenCredential credential, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var arm = Client(credential);
        await foreach (var tenant in arm.GetTenants().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var id = tenant.Data.TenantId?.ToString();
            if (id is null)
                continue;
            yield return new TenantRef(id, tenant.Data.DisplayName ?? tenant.Data.DefaultDomain ?? id);
        }
    }

    public async IAsyncEnumerable<SubscriptionRef> ListSubscriptionsAsync(TokenCredential credential, string tenantId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var arm = Client(new TenantScopedCredential(credential, tenantId));
        await foreach (var subscription in arm.GetSubscriptions().GetAllAsync(cancellationToken).ConfigureAwait(false))
        {
            var data = subscription.Data;
            var subscriptionTenant = data.TenantId?.ToString() ?? tenantId;
            if (!string.Equals(subscriptionTenant, tenantId, StringComparison.OrdinalIgnoreCase))
                continue;
            yield return new SubscriptionRef(data.SubscriptionId, data.DisplayName ?? data.SubscriptionId, tenantId);
        }
    }

    public async IAsyncEnumerable<NamespaceRef> ListNamespacesAsync(TokenCredential credential, SubscriptionRef subscription, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var arm = Client(new TenantScopedCredential(credential, subscription.TenantId));
        var resource = arm.GetSubscriptionResource(Azure.ResourceManager.Resources.SubscriptionResource.CreateResourceIdentifier(subscription.SubscriptionId));
        await foreach (var ns in resource.GetServiceBusNamespacesAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var data = ns.Data;
            yield return new NamespaceRef(
                data.Name,
                HostOf(data.ServiceBusEndpoint) ?? Cloud.NamespaceHost(data.Name),
                ns.Id.ToString(),
                ns.Id.ResourceGroupName ?? string.Empty,
                data.Location.DisplayName ?? data.Location.Name,
                subscription.SubscriptionId,
                subscription.TenantId,
                Cloud.Id);
        }
    }

    public async IAsyncEnumerable<EntitySnapshot> ListQueuesAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var queue in Namespace(credential, serviceBusNamespace).GetServiceBusQueues().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var data = queue.Data;
            yield return new EntitySnapshot(EntityKind.Queue, data.Name, null, Counts(data.CountDetails, data.MessageCount), Status(data.Status), data.SizeInBytes);
        }
    }

    public async IAsyncEnumerable<EntitySnapshot> ListTopicsAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var topic in Namespace(credential, serviceBusNamespace).GetServiceBusTopics().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var data = topic.Data;
            yield return new EntitySnapshot(EntityKind.Topic, data.Name, null, Counts(data.CountDetails, null), Status(data.Status), data.SizeInBytes, data.SubscriptionCount);
        }
    }

    public async IAsyncEnumerable<EntitySnapshot> ListSubscriptionsAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, string topicName, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var topic = Namespace(credential, serviceBusNamespace).GetServiceBusTopics();
        var resource = (await topic.GetAsync(topicName, cancellationToken).ConfigureAwait(false)).Value;
        await foreach (var subscription in resource.GetServiceBusSubscriptions().GetAllAsync(cancellationToken: cancellationToken).ConfigureAwait(false))
        {
            var data = subscription.Data;
            yield return new EntitySnapshot(EntityKind.Subscription, data.Name, topicName, Counts(data.CountDetails, data.MessageCount), Status(data.Status), null);
        }
    }

    private static ServiceBusNamespaceResource Namespace(TokenCredential credential, NamespaceRef serviceBusNamespace) =>
        new ArmClient(new TenantScopedCredential(credential, serviceBusNamespace.TenantId), default, ClientOptions(AzureClouds.Find(serviceBusNamespace.Cloud)))
            .GetServiceBusNamespaceResource(new ResourceIdentifier(serviceBusNamespace.ResourceId));

    private static EntityCounts Counts(MessageCountDetails? details, long? total) => details is null
        ? EntityCounts.Unknown with { Total = total }
        : new EntityCounts(details.ActiveMessageCount, details.DeadLetterMessageCount, details.ScheduledMessageCount, details.TransferDeadLetterMessageCount, total);

    private static string? Status(ServiceBusMessagingEntityStatus? status) => status?.ToString();

    private static string? HostOf(string? endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host.ToLowerInvariant() : null;
}

/// <summary>Asks the account's credential for a token in one tenant, so one sign-in covers every tenant it belongs to.</summary>
public sealed class TenantScopedCredential(TokenCredential inner, string tenantId) : TokenCredential
{
    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        inner.GetToken(Scoped(requestContext), cancellationToken);

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        inner.GetTokenAsync(Scoped(requestContext), cancellationToken);

    private TokenRequestContext Scoped(TokenRequestContext context) =>
        new(context.Scopes, context.ParentRequestId, context.Claims, tenantId, context.IsCaeEnabled);
}
