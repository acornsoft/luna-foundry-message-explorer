// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Inventory;
using Azure.Core;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;

/// <param name="Namespaces">Service Bus namespaces: the ones the grids can open.</param>
/// <param name="EventHubs">Event Hubs namespaces, for browsing. Empty when the reader lists none.</param>
public sealed record SubscriptionDiscovery(SubscriptionRef Subscription, IReadOnlyList<NamespaceRef> Namespaces, string? Error)
{
    public IReadOnlyList<NamespaceRef> EventHubs { get; init; } = [];
}

public sealed record TenantDiscovery(TenantRef Tenant, IReadOnlyList<SubscriptionDiscovery> Subscriptions, string? Error);

/// <summary>Everything one account can read: every tenant, every subscription in it, every namespace in that.</summary>
public sealed record DiscoveryResult(IReadOnlyList<TenantDiscovery> Tenants, string? Error)
{
    public IEnumerable<NamespaceRef> Namespaces => Tenants.SelectMany(t => t.Subscriptions).SelectMany(s => s.Namespaces);
}

/// <summary>
/// Walks tenants, then subscriptions, then namespaces for one credential. A failure in one tenant or one
/// subscription is recorded there with a plain message; the rest still load.
/// </summary>
public static class NamespaceDiscovery
{
    public static async Task<DiscoveryResult> DiscoverAsync(IArmReader reader, TokenCredential credential, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        ArgumentNullException.ThrowIfNull(credential);
        List<TenantRef> tenants;
        try
        {
            tenants = await ToListAsync(reader.ListTenantsAsync(credential, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new DiscoveryResult([], Explain(exception));
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = await Task.WhenAll(tenants
            .DistinctBy(t => t.TenantId, StringComparer.OrdinalIgnoreCase)
            .Select(tenant => DiscoverTenantAsync(reader, credential, tenant, cancellationToken))).ConfigureAwait(false);

        // A subscription is listed once, under the first tenant that returned it.
        var unique = results
            .Select(t => t with { Subscriptions = t.Subscriptions.Where(s => seen.Add(s.Subscription.SubscriptionId)).ToList() })
            .OrderBy(t => t.Tenant.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new DiscoveryResult(unique, null);
    }

    private static async Task<TenantDiscovery> DiscoverTenantAsync(IArmReader reader, TokenCredential credential, TenantRef tenant, CancellationToken cancellationToken)
    {
        List<SubscriptionRef> subscriptions;
        try
        {
            subscriptions = await ToListAsync(reader.ListSubscriptionsAsync(credential, tenant.TenantId, cancellationToken), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return new TenantDiscovery(tenant, [], Explain(exception));
        }

        var found = await Task.WhenAll(subscriptions.Select(async subscription =>
        {
            try
            {
                var namespaces = await ToListAsync(reader.ListNamespacesAsync(credential, subscription, cancellationToken), cancellationToken).ConfigureAwait(false);
                var (hubs, hubsError) = await EventHubsAsync(reader, credential, subscription, cancellationToken).ConfigureAwait(false);
                return new SubscriptionDiscovery(subscription, namespaces.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList(), hubsError) { EventHubs = hubs };
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                return new SubscriptionDiscovery(subscription, [], Explain(exception));
            }
        })).ConfigureAwait(false);
        return new TenantDiscovery(tenant, found.OrderBy(s => s.Subscription.DisplayName, StringComparer.OrdinalIgnoreCase).ToList(), null);
    }

    /// <summary>Event Hubs namespaces are a bonus for browsing: a failure there is reported, and Service Bus still loads.</summary>
    private static async Task<(IReadOnlyList<NamespaceRef> Hubs, string? Error)> EventHubsAsync(IArmReader reader, TokenCredential credential, SubscriptionRef subscription, CancellationToken cancellationToken)
    {
        try
        {
            var hubs = await ToListAsync(reader.ListEventHubsNamespacesAsync(credential, subscription, cancellationToken), cancellationToken).ConfigureAwait(false);
            return (hubs.OrderBy(n => n.Name, StringComparer.OrdinalIgnoreCase).ToList(), null);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return ([], "Event Hubs namespaces could not be listed: " + Explain(exception));
        }
    }

    /// <summary>Plain words for a discovery failure. Discovery has no connection string, so it says what to do instead.</summary>
    public static string Explain(Exception exception) => exception switch
    {
        Azure.RequestFailedException { Status: 401 } =>
            "Sign-in to this tenant was refused. It may need multi-factor authentication or consent. Type the tenant in Tenant (optional) and add the account again.",
        Azure.RequestFailedException { Status: 403 } =>
            "No permission to list here. Ask for the Reader role.",
        Azure.Identity.AuthenticationFailedException failed =>
            "Sign-in to this tenant failed: " + FirstLine(failed.Message),
        _ => ServiceBusErrorMapper.Map(exception).Message,
    };

    private static string FirstLine(string text)
    {
        var line = text.Split('\n', 2)[0].Trim();
        return line.Length > 200 ? line[..200] + "..." : line;
    }

    private static async Task<List<T>> ToListAsync<T>(IAsyncEnumerable<T> source, CancellationToken cancellationToken)
    {
        var list = new List<T>();
        await foreach (var item in source.WithCancellation(cancellationToken).ConfigureAwait(false))
            list.Add(item);
        return list;
    }
}
