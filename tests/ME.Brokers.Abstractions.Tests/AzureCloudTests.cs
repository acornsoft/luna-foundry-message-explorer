// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

namespace Acornsoft.MessageExplorer.Brokers.Tests;

/// <summary>Per-account cloud choice: sign-in host, Resource Manager endpoint, and namespace suffix for each cloud.</summary>
public sealed class AzureCloudTests
{
    [Theory]
    [InlineData("public", "https://login.microsoftonline.com/", "https://management.azure.com/", ".servicebus.windows.net")]
    [InlineData("usgov", "https://login.microsoftonline.us/", "https://management.usgovcloudapi.net/", ".servicebus.usgovcloudapi.net")]
    [InlineData("china", "https://login.chinacloudapi.cn/", "https://management.chinacloudapi.cn/", ".servicebus.chinacloudapi.cn")]
    public void Each_cloud_has_its_own_authority_arm_endpoint_and_service_bus_suffix(string id, string authority, string arm, string suffix)
    {
        var cloud = AzureClouds.Find(id);

        Assert.Equal(id, cloud.Id);
        Assert.Equal(new Uri(authority), cloud.AuthorityHost);
        Assert.Equal(new Uri(arm), cloud.ArmEndpoint);
        Assert.Equal(arm + ".default", cloud.ArmScope);
        Assert.Equal(suffix, cloud.ServiceBusSuffix);
        Assert.Equal("https://servicebus.azure.net/.default", cloud.ServiceBusScope);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("mars")]
    public void Missing_or_unknown_cloud_ids_fall_back_to_public_so_old_config_keeps_working(string? id)
    {
        Assert.Same(AzureClouds.Public, AzureClouds.Find(id));
    }

    [Fact]
    public void Ids_match_case_insensitively()
    {
        Assert.Same(AzureClouds.UsGovernment, AzureClouds.Find(" USGov "));
    }

    [Theory]
    [InlineData("orders", "public", "orders.servicebus.windows.net")]
    [InlineData("orders", "usgov", "orders.servicebus.usgovcloudapi.net")]
    [InlineData("Orders", "china", "orders.servicebus.chinacloudapi.cn")]
    [InlineData("sbns-demo-001.servicebus.windows.net", "usgov", "sbns-demo-001.servicebus.windows.net")]
    public void A_short_namespace_name_gets_the_clouds_suffix_and_a_full_host_is_kept(string input, string cloud, string expected)
    {
        Assert.Equal(expected, AzureClouds.Find(cloud).NamespaceHost(input));
    }

    [Theory]
    [InlineData("payments.servicebus.windows.net", "public")]
    [InlineData("payments.servicebus.usgovcloudapi.net", "usgov")]
    [InlineData("sb://payments.servicebus.chinacloudapi.cn/", "china")]
    [InlineData("https://payments.servicebus.usgovcloudapi.net/", "usgov")]
    public void The_cloud_is_recognised_from_a_namespace_host(string host, string expected)
    {
        Assert.Equal(expected, AzureClouds.FromHost(host)?.Id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("localhost")]
    [InlineData("broker.example")]
    public void Non_azure_hosts_have_no_cloud(string? host)
    {
        Assert.Null(AzureClouds.FromHost(host));
    }

    [Fact]
    public void The_picker_lists_exactly_three_clouds_public_first()
    {
        Assert.Equal(["public", "usgov", "china"], AzureClouds.All.Select(c => c.Id));
    }
}
