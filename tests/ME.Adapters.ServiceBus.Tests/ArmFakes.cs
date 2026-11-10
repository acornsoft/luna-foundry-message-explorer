// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Runtime.CompilerServices;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Azure;
using Azure.Core;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

/// <summary>A credential that hands out a fake token, or fails, and records the tenants it was asked for.</summary>
internal sealed class FakeCredential(string name, Exception? failure = null) : TokenCredential
{
    public string Name { get; } = name;

    public List<string?> TenantsAsked { get; } = [];

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        lock (TenantsAsked)
            TenantsAsked.Add(requestContext.TenantId);
        if (failure is not null)
            throw failure;
        return new AccessToken("fake-token-not-real", DateTimeOffset.UtcNow.AddHours(1));
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

/// <summary>An in-memory ARM. Each credential name maps to what that account can read.</summary>
internal sealed class FakeArmReader : IArmReader
{
    public Dictionary<string, List<TenantRef>> Tenants { get; } = [];

    public Dictionary<string, List<SubscriptionRef>> Subscriptions { get; } = [];

    public Dictionary<string, List<NamespaceRef>> Namespaces { get; } = [];

    public Dictionary<string, List<EntitySnapshot>> Entities { get; } = [];

    public HashSet<string> FailingTenants { get; } = [];

    public HashSet<string> FailingSubscriptions { get; } = [];

    /// <summary>Every cloud a caller switched this reader to, in order.</summary>
    public List<string> CloudsAsked { get; } = [];

    public IArmReader ForCloud(AzureCloud cloud)
    {
        lock (CloudsAsked)
            CloudsAsked.Add(cloud.Id);
        return this;
    }

    public async IAsyncEnumerable<TenantRef> ListTenantsAsync(TokenCredential credential, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Token(credential, null);
        foreach (var tenant in Tenants.GetValueOrDefault(NameOf(credential), []))
            yield return tenant;
    }

    public async IAsyncEnumerable<SubscriptionRef> ListSubscriptionsAsync(TokenCredential credential, string tenantId, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Token(credential, tenantId);
        if (FailingTenants.Contains(tenantId))
            throw new RequestFailedException(401, "AADSTS50076: multi-factor authentication is required for this tenant.");
        foreach (var subscription in Subscriptions.GetValueOrDefault(tenantId, []))
            yield return subscription;
    }

    public async IAsyncEnumerable<NamespaceRef> ListNamespacesAsync(TokenCredential credential, SubscriptionRef subscription, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await Token(credential, subscription.TenantId);
        if (FailingSubscriptions.Contains(subscription.SubscriptionId))
            throw new RequestFailedException(403, "The client does not have authorization to perform action 'read'.");
        foreach (var ns in Namespaces.GetValueOrDefault(subscription.SubscriptionId, []))
            yield return ns;
    }

    public IAsyncEnumerable<EntitySnapshot> ListQueuesAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, CancellationToken cancellationToken) =>
        Rows(credential, serviceBusNamespace, EntityKind.Queue, null);

    public IAsyncEnumerable<EntitySnapshot> ListTopicsAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, CancellationToken cancellationToken) =>
        Rows(credential, serviceBusNamespace, EntityKind.Topic, null);

    public IAsyncEnumerable<EntitySnapshot> ListSubscriptionsAsync(TokenCredential credential, NamespaceRef serviceBusNamespace, string topicName, CancellationToken cancellationToken) =>
        Rows(credential, serviceBusNamespace, EntityKind.Subscription, topicName);

    private async IAsyncEnumerable<EntitySnapshot> Rows(TokenCredential credential, NamespaceRef ns, EntityKind kind, string? parent)
    {
        await Token(credential, ns.TenantId);
        foreach (var row in Entities.GetValueOrDefault(ns.Name, []).Where(r => r.Kind == kind && r.Parent == parent))
            yield return row;
    }

    private static async Task Token(TokenCredential credential, string? tenantId) =>
        await credential.GetTokenAsync(new TokenRequestContext(["https://management.azure.com/.default"], tenantId: tenantId), default);

    private static string NameOf(TokenCredential credential) => ((FakeCredential)credential).Name;
}
