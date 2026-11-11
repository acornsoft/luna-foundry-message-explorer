// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus.Arm;
using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

/// <summary>The Resource Manager reader talks to the account's cloud. No network: only the client options are checked.</summary>
public sealed class ArmCloudTests
{
    [Theory]
    [InlineData("public", "https://management.azure.com/")]
    [InlineData("usgov", "https://management.usgovcloudapi.net/")]
    [InlineData("china", "https://management.chinacloudapi.cn/")]
    public void Arm_client_options_point_at_the_clouds_endpoint_and_audience(string id, string endpoint)
    {
        var options = ArmReader.ClientOptions(AzureClouds.Find(id));

        Assert.NotNull(options.Environment);
        Assert.Equal(new Uri(endpoint), options.Environment!.Value.Endpoint);
        Assert.Equal(endpoint.TrimEnd('/'), options.Environment.Value.Audience);
        Assert.Equal(endpoint + ".default", options.Environment.Value.DefaultScope);
    }

    [Fact]
    public void A_reader_defaults_to_public_and_switches_cloud_without_changing_the_original()
    {
        var reader = new ArmReader();

        var gov = Assert.IsType<ArmReader>(reader.ForCloud(AzureClouds.UsGovernment));

        Assert.Equal("public", reader.Cloud.Id);
        Assert.Equal("usgov", gov.Cloud.Id);
        Assert.Same(reader, reader.ForCloud(AzureClouds.Public));
    }

    [Fact]
    public void Namespaces_saved_before_clouds_existed_are_public()
    {
        var ns = new NamespaceRef("sbns-demo-001", "sbns-demo-001.servicebus.windows.net", "/subscriptions/sub-demo/resourceGroups/rg-demo/providers/Microsoft.ServiceBus/namespaces/sbns-demo-001", "rg-demo", "East US", "sub-demo", "00000000-0000-0000-0000-00000000c001");

        Assert.Equal(AzureClouds.PublicId, ns.Cloud);
    }

    [Fact]
    public void Readers_that_know_one_cloud_return_themselves_by_default()
    {
        IArmReader plain = new SingleCloudReader();

        Assert.Same(plain, plain.ForCloud(AzureClouds.China));
    }

    private sealed class SingleCloudReader : IArmReader
    {
        public IAsyncEnumerable<TenantRef> ListTenantsAsync(Azure.Core.TokenCredential credential, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<SubscriptionRef> ListSubscriptionsAsync(Azure.Core.TokenCredential credential, string tenantId, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<NamespaceRef> ListNamespacesAsync(Azure.Core.TokenCredential credential, SubscriptionRef subscription, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<EntitySnapshot> ListQueuesAsync(Azure.Core.TokenCredential credential, NamespaceRef serviceBusNamespace, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<EntitySnapshot> ListTopicsAsync(Azure.Core.TokenCredential credential, NamespaceRef serviceBusNamespace, CancellationToken cancellationToken) => throw new NotSupportedException();

        public IAsyncEnumerable<EntitySnapshot> ListSubscriptionsAsync(Azure.Core.TokenCredential credential, NamespaceRef serviceBusNamespace, string topicName, CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
