// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Brokers;

/// <summary>
/// One Azure cloud: where Entra sign-in happens (authority host), where Azure Resource Manager answers
/// (ARM endpoint and token audience), and the DNS suffix of its Service Bus and Event Hubs namespaces.
/// Each saved account picks one. Values are the public endpoints Microsoft documents for each cloud.
/// </summary>
/// <param name="Id">Stable ID kept in config: "public", "usgov" or "china".</param>
/// <param name="ServiceBusAudience">Data-plane token resource. Microsoft documents https://servicebus.azure.net for every cloud.</param>
public sealed record AzureCloud(
    string Id,
    string DisplayName,
    Uri AuthorityHost,
    Uri ArmEndpoint,
    string ArmAudience,
    string ServiceBusSuffix,
    string ServiceBusAudience)
{
    /// <summary>The scope for Azure Resource Manager tokens in this cloud.</summary>
    public string ArmScope => ArmAudience.TrimEnd('/') + "/.default";

    /// <summary>The scope for Service Bus and Event Hubs data-plane tokens.</summary>
    public string ServiceBusScope => ServiceBusAudience.TrimEnd('/') + "/.default";

    /// <summary>"orders-ns" becomes "orders-ns" plus this cloud's suffix; a full host is kept as it is.</summary>
    public string NamespaceHost(string nameOrHost)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(nameOrHost);
        var value = nameOrHost.Trim().TrimEnd('/');
        return value.Contains('.', StringComparison.Ordinal) ? value.ToLowerInvariant() : value.ToLowerInvariant() + ServiceBusSuffix;
    }

    public override string ToString() => DisplayName;
}

/// <summary>The clouds the app knows. Public is the default for every account saved before clouds existed.</summary>
public static class AzureClouds
{
    public const string PublicId = "public";
    public const string UsGovernmentId = "usgov";
    public const string ChinaId = "china";

    public static AzureCloud Public { get; } = new(
        PublicId,
        "Azure (public)",
        new Uri("https://login.microsoftonline.com/"),
        new Uri("https://management.azure.com/"),
        "https://management.azure.com",
        ".servicebus.windows.net",
        "https://servicebus.azure.net");

    public static AzureCloud UsGovernment { get; } = new(
        UsGovernmentId,
        "Azure US Government",
        new Uri("https://login.microsoftonline.us/"),
        new Uri("https://management.usgovcloudapi.net/"),
        "https://management.usgovcloudapi.net",
        ".servicebus.usgovcloudapi.net",
        "https://servicebus.azure.net");

    public static AzureCloud China { get; } = new(
        ChinaId,
        "Azure China (21Vianet)",
        new Uri("https://login.chinacloudapi.cn/"),
        new Uri("https://management.chinacloudapi.cn/"),
        "https://management.chinacloudapi.cn",
        ".servicebus.chinacloudapi.cn",
        "https://servicebus.azure.net");

    public static IReadOnlyList<AzureCloud> All { get; } = [Public, UsGovernment, China];

    /// <summary>The cloud with this ID. Null, empty or unknown IDs are the public cloud, so old config keeps working.</summary>
    public static AzureCloud Find(string? id) =>
        All.FirstOrDefault(c => string.Equals(c.Id, id?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Public;

    /// <summary>The cloud a namespace host belongs to, by its DNS suffix. Null when the suffix is not an Azure one.</summary>
    public static AzureCloud? FromHost(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
            return null;
        var value = host.Trim().TrimEnd('/');
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri) && !string.IsNullOrEmpty(uri.Host))
            value = uri.Host;
        return All.FirstOrDefault(c => value.EndsWith(c.ServiceBusSuffix, StringComparison.OrdinalIgnoreCase));
    }
}
