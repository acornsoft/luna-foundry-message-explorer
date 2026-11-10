// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using System.Text.RegularExpressions;
using Acornsoft.MessageExplorer.Adapters.ServiceBus;
using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;
using Azure.Core;
using Azure.Identity;

namespace Acornsoft.MessageExplorer.ReadTools;

/// <summary>Where the read-only tools get counts: exactly one of a synthetic namespace, an ARM resource ID, a namespace, or a connection string in an environment variable.</summary>
public sealed record InventorySourceSpec(
    bool Synthetic = false,
    string? Namespace = null,
    string? ResourceId = null,
    string? Cloud = null,
    string? ConnectionStringVariable = null)
{
    /// <summary>Null when the spec names exactly one source; otherwise what to fix.</summary>
    public string? Problem()
    {
        var sources = (Synthetic ? 1 : 0) + (Namespace is null ? 0 : 1) + (ResourceId is null ? 0 : 1) + (ConnectionStringVariable is null ? 0 : 1);
        if (sources != 1)
            return "Give exactly one source: --synthetic, --resource-id, --namespace or --connection-string-env.";
        if (Cloud is not null && Namespace is null && ResourceId is null)
            return "--cloud goes with --resource-id or --namespace.";
        if (Cloud is not null && !AzureClouds.All.Any(c => string.Equals(c.Id, Cloud, StringComparison.OrdinalIgnoreCase)))
            return $"--cloud is public, usgov or china, not '{Cloud}'.";
        return null;
    }
}

/// <summary>The source spec was not usable (missing variable, malformed resource ID). The message says what to fix.</summary>
public sealed class InventorySourceException(string message) : Exception(message);

/// <summary>A read-only inventory and a label for it (a namespace host, or "synthetic"). No secret is in the label.</summary>
public sealed record OpenedSource(IEntityInventory Inventory, string Label);

/// <summary>
/// Opens read-only inventories for the headless tool, the MCP server and the voice panel alike. Secrets come
/// only from an environment variable, never from arguments. Entra sign-in is non-interactive.
/// </summary>
public static partial class InventorySources
{
    /// <summary>The real management-API opener: <see cref="ServiceBusBrokerProvider"/>.</summary>
    public static IEntityInventory OpenServiceBus(BrokerConnection connection) => new ServiceBusBrokerProvider().OpenInventory(connection);

    /// <summary>The real ARM opener: counts through Resource Manager, so the Reader role is enough.</summary>
    public static IEntityInventory OpenArm(NamespaceRef ns, TokenCredential credential) =>
        new ArmEntityInventory(new ArmReader(AzureClouds.Find(ns.Cloud)), credential, ns);

    public static OpenedSource Open(
        InventorySourceSpec spec,
        Func<string, string?> environment,
        Func<BrokerConnection, IEntityInventory>? open = null,
        Func<NamespaceRef, TokenCredential, IEntityInventory>? openArm = null)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(environment);
        open ??= OpenServiceBus;
        openArm ??= OpenArm;
        if (spec.Problem() is { } problem)
            throw new InventorySourceException(problem);

        if (spec.Synthetic)
            return new OpenedSource(new SyntheticNamespace(), "synthetic");

        if (spec.ConnectionStringVariable is { } variable)
        {
            var value = environment(variable);
            if (string.IsNullOrWhiteSpace(value))
                throw new InventorySourceException($"Environment variable {variable} is not set or is empty.");
            var connection = BrokerConnection.WithConnectionString(ServiceBusBrokerProvider.ProviderId, new RuntimeSecret(value));
            var plan = ServiceBusBrokerProvider.Plan(connection);
            return new OpenedSource(open(connection), plan.FullyQualifiedNamespace);
        }

        var cloud = AzureClouds.Find(spec.Cloud);
        var credential = new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            AuthorityHost = cloud.AuthorityHost,
            ExcludeInteractiveBrowserCredential = true,
        });

        if (spec.ResourceId is { } resourceId)
        {
            var match = NamespaceResourceId().Match(resourceId.Trim());
            if (!match.Success)
                throw new InventorySourceException("--resource-id must be a Service Bus namespace: /subscriptions/<id>/resourceGroups/<group>/providers/Microsoft.ServiceBus/namespaces/<name>.");
            var name = match.Groups["name"].Value;
            var ns = new NamespaceRef(
                name,
                cloud.NamespaceHost(name),
                resourceId.Trim(),
                match.Groups["group"].Value,
                string.Empty,
                match.Groups["subscription"].Value,
                environment("AZURE_TENANT_ID") is { Length: > 0 } tenant ? tenant : null!,
                cloud.Id);
            return new OpenedSource(openArm(ns, credential), ns.FullyQualifiedNamespace);
        }

        var host = cloud.NamespaceHost(spec.Namespace!);
        return new OpenedSource(open(BrokerConnection.WithEntra(ServiceBusBrokerProvider.ProviderId, host, credential)), host);
    }

    /// <summary>Every queue, every topic, and each topic's subscriptions, in that order.</summary>
    public static async Task<IReadOnlyList<EntitySnapshot>> ReadAllAsync(IEntityInventory inventory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(inventory);
        var all = new List<EntitySnapshot>();
        all.AddRange((await inventory.ListQueuesAsync(cancellationToken).ConfigureAwait(false)).Entities);
        foreach (var topic in (await inventory.ListTopicsAsync(cancellationToken).ConfigureAwait(false)).Entities)
        {
            all.Add(topic);
            all.AddRange((await inventory.ListSubscriptionsAsync(topic.Name, cancellationToken).ConfigureAwait(false)).Entities);
        }

        return all;
    }

    [GeneratedRegex(@"^/subscriptions/(?<subscription>[^/]+)/resourceGroups/(?<group>[^/]+)/providers/Microsoft\.ServiceBus/namespaces/(?<name>[^/]+)/?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NamespaceResourceId();
}
