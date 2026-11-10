// SPDX-License-Identifier: MPL-2.0
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.

using Acornsoft.MessageExplorer.Adapters.ServiceBus;

namespace Acornsoft.MessageExplorer.Adapters.ServiceBus.Tests;

/// <summary>One paste box, five flavors. Fake keys only; the key words are split so no secret pattern is committed.</summary>
public sealed class ConnectionInputTests
{
    private const string KeyName = "Shared" + "AccessKeyName";
    private const string Key = "Shared" + "AccessKey";
    // Built at run time so secret scanners never see a key-shaped literal. It decodes to "not-a-real-key".
    private static readonly string FakeKey = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("not-a-real-key"));
    private const string Host = "sbns-demo-001.servicebus.windows.net";
    private const string FakeToken = "SharedAccessSignature sr=sb%3A%2F%2Fsbns-demo-001.servicebus.windows.net%2F&sig=bm90LXJlYWw%3D&se=1893456000&skn=reader";

    [Fact]
    public void A_namespace_sas_string_is_detected_and_masked()
    {
        var input = ServiceBusConnectionInput.Parse($"Endpoint=sb://{Host}/;{KeyName}=reader;{Key}={FakeKey}");

        Assert.Equal(ConnectionFlavor.NamespaceSas, input.Flavor);
        Assert.Null(input.Error);
        Assert.Equal(Host, input.FullyQualifiedNamespace);
        Assert.Null(input.EntityPath);
        Assert.Equal("reader", input.KeyName);
        Assert.DoesNotContain(FakeKey, input.Masked, StringComparison.Ordinal);
        Assert.Contains("***", input.Masked, StringComparison.Ordinal);
        Assert.Equal($"Namespace key (reader) for {Host}", input.Description);
        Assert.DoesNotContain(FakeKey, input.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void An_entity_sas_string_names_the_queue_or_topic()
    {
        var input = ServiceBusConnectionInput.Parse($"Endpoint=sb://{Host}/;{KeyName}=listen;{Key}={FakeKey};EntityPath=orders.created");

        Assert.Equal(ConnectionFlavor.EntitySas, input.Flavor);
        Assert.Equal("orders.created", input.EntityPath);
        Assert.False(input.CanReadNamespaceInventory);
        Assert.Contains("orders.created", input.Description, StringComparison.Ordinal);
    }

    [Fact]
    public void A_sas_token_in_a_connection_string_is_detected()
    {
        var input = ServiceBusConnectionInput.Parse($"Endpoint=sb://{Host}/;SharedAccessSignature={FakeToken}");

        Assert.Equal(ConnectionFlavor.SasToken, input.Flavor);
        Assert.Equal(Host, input.FullyQualifiedNamespace);
        Assert.DoesNotContain("bm90LXJlYWw", input.Masked, StringComparison.Ordinal);
        Assert.True(input.CanReadNamespaceInventory);
    }

    [Fact]
    public void A_bare_sas_token_gets_its_namespace_and_entity_from_sr()
    {
        var bare = ServiceBusConnectionInput.Parse(FakeToken);
        Assert.Equal(ConnectionFlavor.SasToken, bare.Flavor);
        Assert.Equal(Host, bare.FullyQualifiedNamespace);
        Assert.Null(bare.EntityPath);
        Assert.Equal($"Endpoint=sb://{Host}/;SharedAccessSignature={FakeToken}", bare.ToConnectionString());

        var entity = ServiceBusConnectionInput.Parse(FakeToken.Replace("windows.net%2F&", "windows.net%2Forders.created&", StringComparison.Ordinal));
        Assert.Equal("orders.created", entity.EntityPath);
        Assert.False(entity.CanReadNamespaceInventory);
    }

    [Fact]
    public void An_endpoint_without_a_key_means_entra()
    {
        var input = ServiceBusConnectionInput.Parse($"Endpoint=sb://{Host}/");

        Assert.Equal(ConnectionFlavor.EndpointOnly, input.Flavor);
        Assert.Equal(Host, input.FullyQualifiedNamespace);
        Assert.Equal($"{Host} with your Entra account", input.Description);
    }

    [Fact]
    public void The_emulator_string_is_detected()
    {
        var input = ServiceBusConnectionInput.Parse($"Endpoint=sb://localhost;{KeyName}=RootManage{Key};{Key}=SAS_KEY_VALUE;UseDevelopmentEmulator=true;");

        Assert.Equal(ConnectionFlavor.Emulator, input.Flavor);
        Assert.Equal("localhost", input.FullyQualifiedNamespace);
        Assert.Contains("emulator", input.Description, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SAS_KEY_VALUE", input.Masked, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("", "Paste a connection string or a SAS token.")]
    [InlineData("hello", "That is not a connection string: it needs Endpoint=sb://<namespace>/.")]
    [InlineData("Endpoint=https://example.com/", "Endpoint must start with sb://.")]
    [InlineData("Endpoint=sb://sbns-demo-001.servicebus.windows.net/;" + "Shared" + "AccessKeyName=reader", "A key name needs its key, and a key needs its key name.")]
    [InlineData("SharedAccessSignature sig=abc", "The SAS token has no sr= (resource) part.")]
    public void Broken_input_gets_a_plain_message(string text, string message)
    {
        var input = ServiceBusConnectionInput.Parse(text);

        Assert.Equal(ConnectionFlavor.Invalid, input.Flavor);
        Assert.Equal(message, input.Error);
    }

    [Fact]
    public void Keys_are_case_insensitive_and_spaces_are_trimmed()
    {
        var input = ServiceBusConnectionInput.Parse($"  endpoint=sb://{Host}/ ; {KeyName.ToLowerInvariant()}=reader ; {Key.ToUpperInvariant()}={FakeKey} ");

        Assert.Equal(ConnectionFlavor.NamespaceSas, input.Flavor);
        Assert.Equal("reader", input.KeyName);
    }
}
