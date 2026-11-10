// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Brokers;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

/// <summary>Each pasted flavor opens the right way. Fake keys only, and no network.</summary>
public sealed class ProviderFlavorTests
{
    private const string KeyName = "Shared" + "AccessKeyName";
    private const string Key = "Shared" + "AccessKey";
    private const string FakeKey = "bm90LWEtcmVhbC1rZXk=";
    private const string Host = "sbns-demo-001.servicebus.windows.net";
    private const string FakeToken = "SharedAccessSignature sr=sb%3A%2F%2Fsbns-demo-001.servicebus.windows.net%2F&sig=bm90LXJlYWw%3D&se=1893456000&skn=reader";

    private int _defaultCredentialCalls;
    private int _adminReaders;

    private ServiceBusBrokerProvider Provider() => new(
        () =>
        {
            _defaultCredentialCalls++;
            return new FakeCredential("default");
        },
        _ =>
        {
            _adminReaders++;
            return new PagedReader(10);
        });

    private static BrokerConnection Pasted(string text, object? credential = null) =>
        BrokerConnection.WithConnectionString(ServiceBusBrokerProvider.ProviderId, new RuntimeSecret(text), credential);

    [Fact]
    public async Task An_entity_sas_opens_only_that_entity_and_says_why_counts_are_missing()
    {
        var connection = Pasted($"Endpoint=sb://{Host}/;{KeyName}=listen;{Key}={FakeKey};EntityPath=orders");

        var plan = ServiceBusBrokerProvider.Plan(connection);
        await using var inventory = Provider().OpenInventory(connection);
        var queues = await inventory.ListQueuesAsync();
        var topics = await inventory.ListTopicsAsync();

        Assert.Equal(ConnectionFlavor.EntitySas, plan.Flavor);
        Assert.Equal("orders", plan.EntityPath);
        Assert.Equal(0, _adminReaders);
        var row = Assert.Single(queues.Entities);
        Assert.Equal("orders", row.Name);
        Assert.Equal(EntityCounts.Unknown, row.Counts);
        Assert.NotNull(queues.Notice);
        Assert.Contains("orders", queues.Notice, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeKey, queues.Notice, StringComparison.Ordinal);
        Assert.Empty(topics.Entities);
        Assert.NotNull(topics.Notice);
    }

    [Fact]
    public void An_endpoint_only_string_signs_in_with_the_selected_account()
    {
        var account = new FakeCredential("alice");
        var connection = Pasted($"Endpoint=sb://{Host}/", account);

        var plan = ServiceBusBrokerProvider.Plan(connection);
        Provider().OpenInventory(connection);

        Assert.Equal(AuthMode.Entra, plan.Mode);
        Assert.Equal(ConnectionFlavor.EndpointOnly, plan.Flavor);
        Assert.Equal(Host, plan.FullyQualifiedNamespace);
        Assert.Equal(0, _defaultCredentialCalls);
        Assert.Equal(1, _adminReaders);
    }

    [Fact]
    public void A_typed_namespace_uses_the_account_credential_when_one_is_given()
    {
        var provider = Provider();
        provider.OpenInventory(BrokerConnection.WithEntra(ServiceBusBrokerProvider.ProviderId, "sbns-demo-001", new FakeCredential("alice")));
        Assert.Equal(0, _defaultCredentialCalls);

        provider.OpenInventory(BrokerConnection.WithEntra(ServiceBusBrokerProvider.ProviderId, "sbns-demo-001"));
        Assert.Equal(1, _defaultCredentialCalls);
    }

    [Theory]
    [InlineData(ConnectionFlavor.NamespaceSas)]
    [InlineData(ConnectionFlavor.SasToken)]
    [InlineData(ConnectionFlavor.Emulator)]
    public void Namespace_wide_flavors_open_the_admin_reader_without_a_credential(ConnectionFlavor flavor)
    {
        var text = flavor switch
        {
            ConnectionFlavor.NamespaceSas => $"Endpoint=sb://{Host}/;{KeyName}=reader;{Key}={FakeKey}",
            ConnectionFlavor.SasToken => FakeToken,
            _ => $"Endpoint=sb://localhost;{KeyName}=RootManage{Key};{Key}={FakeKey};UseDevelopmentEmulator=true",
        };

        var connection = Pasted(text);
        var plan = ServiceBusBrokerProvider.Plan(connection);
        Provider().OpenInventory(connection);

        Assert.Equal(flavor, plan.Flavor);
        Assert.Equal(AuthMode.ConnectionString, plan.Mode);
        Assert.Equal(1, _adminReaders);
        Assert.Equal(0, _defaultCredentialCalls);
    }

    [Fact]
    public void A_broken_string_is_refused_with_the_parser_message_and_never_echoes_the_key()
    {
        var connection = Pasted($"Endpoint=sb://{Host}/;{Key}={FakeKey}");

        var message = Provider().Validate(connection);

        Assert.Equal("A key name needs its key, and a key needs its key name.", message);
        Assert.DoesNotContain(FakeKey, connection.ToString(), StringComparison.Ordinal);
    }
}
